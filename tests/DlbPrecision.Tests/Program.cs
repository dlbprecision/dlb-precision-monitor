using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Serialization;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DlbPrecision.Shared;
using DlbPrecision.Service;

internal static class Program
{
    private static int assertions;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            CodecTests();
            SensorStartupTests();
            GpuRecoveryTests();
            SamplePolicyTests();
            UpdaterTests.Run(Check, Array.IndexOf(args, "--integration") >= 0);
            await ClientTests();
            if (Array.IndexOf(args, "--integration") >= 0)
                await IntegrationTests(Array.IndexOf(args, "--allow-console-host") >= 0);
            Console.WriteLine("PASS: " + assertions + " assertions (serialization, unavailable data, driver startup/recovery, GPU recovery, sample sharing, updater, protocol, IPC timeouts/cancellation" +
                (Array.IndexOf(args, "--integration") >= 0 ? ", live service" : "") + ").");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CodecTests()
    {
        var data = new SensorSnapshot
        {
            CpuName = "CPU \"test\"", CpuTemperatureC = 51.25, CpuLoadPercent = 0,
            CpuClockMhz = 5590, RamUsedGb = 20.2, Status = "Live",
            Gpus = new List<GpuSnapshot> { new GpuSnapshot { Id = "gpu-0", Name = "GPU", TemperatureC = 32, LoadPercent = 1, ClockMhz = 510 } }
        };
        var restored = SnapshotCodec.Decode(SnapshotCodec.Encode(data));
        Check(restored.CpuName == data.CpuName, "Names must survive JSON escaping.");
        Check(restored.CpuTemperatureC == data.CpuTemperatureC, "Celsius must retain precision before display conversion.");
        Check(restored.CpuLoadPercent == 0, "Genuine zero usage must remain zero.");
        Check(restored.Gpus.Count == 1 && restored.Gpus[0].ClockMhz == 510, "GPU core clock must round-trip.");
        var unavailable = SnapshotCodec.Decode(SnapshotCodec.Encode(SensorSnapshot.Unavailable("Missing driver")));
        Check(unavailable.CpuTemperatureC == null && unavailable.CpuClockMhz == null, "Missing sensors cannot become zero.");
        data.CpuLoadPercent = 101; data.CpuTemperatureC = double.NaN; data.CpuClockMhz = -1;
        data.Gpus[0].LoadPercent = -1;
        restored = SnapshotCodec.Decode(SnapshotCodec.Encode(data));
        Check(restored.CpuLoadPercent == null && restored.CpuTemperatureC == null && restored.CpuClockMhz == null,
            "Invalid readings must be rejected.");
        Check(restored.Gpus[0].LoadPercent == null, "Invalid GPU usage must be rejected.");
        bool rejected = false;
        try { SnapshotCodec.Decode(new byte[SnapshotCodec.MaximumBytes + 1]); } catch (SerializationException) { rejected = true; }
        Check(rejected, "Oversize response must be rejected before parse.");
        data.ProtocolVersion = 999; rejected = false;
        try { SnapshotCodec.Decode(SnapshotCodec.Encode(data)); } catch (SerializationException) { rejected = true; }
        Check(rejected, "Protocol mismatch must fail explicitly.");
    }

    private static void SensorStartupTests()
    {
        int starts = 0, waits = 0, reads = 0;
        string? warning = PawnIoDriver.EnsureRunning(() => { reads++; return ServiceControllerStatus.Running; },
            () => starts++, _ => { waits++; return false; }, CancellationToken.None);
        Check(warning == null && starts == 0 && waits == 0 && reads == 1, "A running driver must not be started or polled repeatedly.");

        var order = new List<string>();
        var state = ServiceControllerStatus.Stopped;
        warning = PawnIoDriver.EnsureRunning(() => { order.Add("status"); return state; },
            () => { order.Add("start"); state = ServiceControllerStatus.StartPending; },
            _ => { order.Add("wait"); state = ServiceControllerStatus.Running; return false; }, CancellationToken.None);
        Check(warning == null && string.Join(",", order) == "status,start,status,wait,status",
            "A stopped driver must be started and confirmed running before sensor initialization can proceed.");

        starts = waits = 0;
        state = ServiceControllerStatus.StartPending;
        warning = PawnIoDriver.EnsureRunning(() => state, () => starts++, _ =>
        { waits++; state = ServiceControllerStatus.Running; return false; }, CancellationToken.None);
        Check(warning == null && starts == 0 && waits == 1, "An already-starting driver must be awaited without a duplicate start.");

        waits = 0;
        warning = PawnIoDriver.EnsureRunning(() => ServiceControllerStatus.StartPending,
            () => { throw new InvalidOperationException("Must not start twice"); }, milliseconds =>
            { waits += milliseconds; return false; }, CancellationToken.None);
        Check(warning != null && warning.Contains("StartPending") && waits == 1500,
            "A stalled driver must have a bounded startup wait and an explicit warning.");

        state = ServiceControllerStatus.Stopped;
        warning = PawnIoDriver.EnsureRunning(() => state, () =>
        { state = ServiceControllerStatus.Running; throw new InvalidOperationException("Already running", new Win32Exception(1056)); },
            _ => false, CancellationToken.None);
        Check(warning == null, "Another utility starting PawnIO concurrently must be treated as success after checking status.");

        warning = PawnIoDriver.EnsureRunning(() => throw new InvalidOperationException("Missing", new Win32Exception(1060)),
            () => { throw new Exception("Unexpected start"); }, _ => false, CancellationToken.None);
        Check(warning != null && warning.Contains("not installed") && warning.Contains("bundled driver"),
            "A missing driver must produce an actionable warning instead of aborting partial sensor initialization.");

        warning = PawnIoDriver.EnsureRunning(() => ServiceControllerStatus.Stopped,
            () => throw new InvalidOperationException("Blocked", new Win32Exception(577)), _ => false, CancellationToken.None);
        Check(warning != null && warning.Contains("577"), "A blocked driver must preserve the Windows error for diagnosis.");

        warning = PawnIoDriver.EnsureRunning(() => throw new UnauthorizedAccessException(), () => { }, _ => false, CancellationToken.None);
        Check(warning != null && warning.Contains("UnauthorizedAccessException"), "Console-host access failures must not suppress other readings.");

        using (var cancellation = new CancellationTokenSource())
        {
            bool cancelled = false;
            try
            {
                PawnIoDriver.EnsureRunning(() => ServiceControllerStatus.StartPending, () => { },
                    _ => { cancellation.Cancel(); return true; }, cancellation.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Stopping the service must interrupt a driver startup wait.");
        }

        var recovery = new SensorRecovery();
        var partial = new SensorSnapshot
        {
            CpuLoadPercent = 20, RamUsedGb = 12,
            Gpus = new List<GpuSnapshot> { new GpuSnapshot { ClockMhz = 510 } }
        };
        Check(!recovery.Observe(partial, 0) && recovery.Pending, "Missing CPU readings must schedule on-demand recovery.");
        for (int second = 1; second < 10; second++)
        {
            recovery.Observe(partial, second * 1000);
            if (recovery.TryBeginRetry(second * 1000)) throw new InvalidOperationException("Recovery ignored its cooldown.");
        }
        Check(recovery.TryBeginRetry(10000) && !recovery.TryBeginRetry(10000),
            "Repeated failed samples must not postpone recovery or permit duplicate reopen attempts.");
        recovery.Observe(SensorSnapshot.Unavailable("Constructor failed"), 10000);
        Check(!recovery.TryBeginRetry(39999) && recovery.TryBeginRetry(40000),
            "Even a failed reader constructor must respect recovery backoff.");
        recovery.Observe(partial, 40000);
        Check(!recovery.TryBeginRetry(99999) && recovery.TryBeginRetry(100000), "The last recovery attempt must use the longer cooldown.");
        recovery.Observe(partial, 100000);
        recovery.Observe(partial, 86400000);
        Check(recovery.CpuExhausted && !recovery.GpuExhausted && !recovery.TryBeginRetry(86400000), "Unsupported hardware must not trigger endless expensive rediscovery.");
        Check(partial.CpuLoadPercent == 20 && partial.RamUsedGb == 12 && partial.Gpus[0].ClockMhz == 510,
            "Recovery scheduling must preserve available Windows and GPU measurements.");

        var healthy = new SensorSnapshot { CpuTemperatureC = 55, CpuClockMhz = 4800, Gpus = ReportingGpu() };
        Check(recovery.Observe(healthy, 86401000) && !recovery.CpuExhausted && !recovery.Pending,
            "Successful CPU measurements must clear failed-startup state, regardless of unavailable GPU metrics.");
        Check(!recovery.TryBeginRetry(long.MaxValue), "A healthy reader must not trigger periodic driver checks or reopen attempts.");
        healthy.CpuClockMhz = null;
        Check(!recovery.Observe(healthy, 86402000) && recovery.CpuExhausted && !recovery.Pending,
            "A fault that returns within minutes continues its old retry budget instead of restarting it.");
        healthy.CpuClockMhz = 4800;
        recovery.Observe(healthy, 86403000);
        recovery.Observe(healthy, 86703000);
        healthy.CpuClockMhz = null;
        recovery.Observe(healthy, 86704000);
        Check(recovery.Pending && !recovery.TryBeginRetry(86713999) && recovery.TryBeginRetry(86714000),
            "After readings have stayed good for 5 minutes, a new fault gets a fresh bounded retry budget.");
        recovery.Observe(healthy, 86714000);
        var back = new SensorSnapshot { CpuTemperatureC = 56, CpuClockMhz = 4700, Gpus = ReportingGpu() };
        recovery.Observe(back, 86715000);
        Check(recovery.Pending && !recovery.Retrying, "Readings that have just come back show no 'will retry' message while the retry winds down.");
        recovery.Observe(back, 86720000);
        Check(!recovery.Pending && !recovery.TryBeginRetry(long.MaxValue), "Readings that stay back for 5 seconds cancel pending recovery.");
    }

    private static List<GpuSnapshot> ReportingGpu() => new List<GpuSnapshot> { new GpuSnapshot { ClockMhz = 510 } };

    private static void GpuRecoveryTests()
    {
        var recovery = new SensorRecovery();
        SensorSnapshot Sample(params GpuSnapshot[] gpus) =>
            new SensorSnapshot { CpuTemperatureC = 50, CpuClockMhz = 4000, Gpus = new List<GpuSnapshot>(gpus) };
        var blank = new GpuSnapshot { Id = "gpu-0", Name = "GPU" };
        var live = new GpuSnapshot { Id = "gpu-0", Name = "GPU", TemperatureC = 40, LoadPercent = 3, ClockMhz = 300 };

        Check(recovery.Observe(Sample(blank), 0) && !recovery.Pending,
            "A GPU that has never exposed sensors must not trigger expensive rediscovery.");
        Check(recovery.Observe(Sample(live), 1000) && !recovery.Pending, "Healthy CPU and GPU readings need no recovery.");
        Check(!recovery.Observe(Sample(blank), 2000) && recovery.Pending
            && !recovery.TryBeginRetry(11999) && recovery.TryBeginRetry(12000),
            "A GPU that stops reporting, as after a graphics-driver update, must schedule bounded recovery.");
        Check(!recovery.Observe(Sample(), 12000) && recovery.Pending && !recovery.TryBeginRetry(41999),
            "A GPU still missing after reopening sensors must use the next, longer retry delay.");
        recovery.Observe(Sample(live), 13000);
        Check(recovery.Observe(Sample(live), 18000) && !recovery.Pending && !recovery.GpuExhausted,
            "GPU readings that return and stay back must clear recovery state.");
        Check(SensorRecovery.CpuReady(Sample()) && !SensorRecovery.CpuReady(new SensorSnapshot { CpuTemperatureC = 50 }),
            "CPU readiness still requires both temperature and clock for the driver warning.");

        // A GPU that is unplugged or disabled is retried within the GPU budget, then forgotten.
        recovery = new SensorRecovery();
        var second = new GpuSnapshot { Id = "gpu-1", Name = "eGPU", TemperatureC = 35, LoadPercent = 1, ClockMhz = 200 };
        recovery.Observe(Sample(live, second), 0);
        long now = 1000;
        Check(!recovery.Observe(Sample(live), now) && recovery.Pending, "A GPU that disappears is first treated as a GPU that stopped reporting.");
        while (recovery.Pending) now = RetryAndObserve(recovery, now, Sample(live));
        Check(recovery.Observe(Sample(live), now + 1000) && !recovery.Pending && !recovery.CpuExhausted && !recovery.GpuExhausted,
            "A GPU still not listed after its retries is treated as removed, so healthy sensors stop reporting a problem.");
        var cpuLost = new SensorSnapshot { CpuTemperatureC = 50, Gpus = new List<GpuSnapshot> { live } };
        Check(!recovery.Observe(cpuLost, now + 2000) && recovery.Pending && recovery.TryBeginRetry(now + 12000),
            "CPU readings lost after a GPU was removed still get their own recovery.");

        // A GPU that stays listed but blank uses up only the GPU budget.
        recovery = new SensorRecovery();
        recovery.Observe(Sample(live), 0);
        now = 1000;
        recovery.Observe(Sample(blank), now);
        while (recovery.Pending) now = RetryAndObserve(recovery, now, Sample(blank));
        Check(!recovery.Pending && recovery.GpuExhausted && !recovery.CpuExhausted,
            "A listed GPU that never reports again exhausts only the GPU retries.");
        cpuLost = new SensorSnapshot { CpuTemperatureC = 50, Gpus = new List<GpuSnapshot> { blank } };
        Check(!recovery.Observe(cpuLost, now + 1000) && recovery.Pending && !recovery.TryBeginRetry(now + 10999) && recovery.TryBeginRetry(now + 11000),
            "Exhausted GPU retries never disable CPU recovery.");
        Check(recovery.Observe(Sample(live), now + 12000) && !recovery.Pending && !recovery.GpuExhausted && !recovery.CpuExhausted,
            "Readings that come back clear both warnings.");

        // A graphics-driver install can keep the card unlisted for minutes; it must still be found again.
        recovery = new SensorRecovery();
        recovery.Observe(Sample(live), 0);
        now = 1000;
        recovery.Observe(Sample(blank), now);
        const long driverInstalled = 8 * 60 * 1000;
        int reopens = 0;
        while (recovery.Pending)
        {
            now = RetryAndObserve(recovery, now, null);
            reopens++;
            recovery.Observe(now < driverInstalled ? Sample() : Sample(live), now);
        }
        Check(now >= driverInstalled && recovery.Observe(Sample(live), now + 1000) && !recovery.GpuExhausted && reopens <= 4,
            "A graphics driver that takes 8 minutes to install is found again by the slower GPU retries, after at most four sensor reopens (" + reopens + ").");
        recovery.Observe(Sample(live), now + 301000);
        Check(!recovery.Observe(Sample(blank), now + 302000) && recovery.Pending, "That GPU is still watched afterwards, with a fresh budget once it has reported for 5 minutes.");

        // A long GPU wait never holds up the CPU's own, quicker recovery.
        recovery = new SensorRecovery();
        recovery.Observe(Sample(live), 0);
        now = 1000;
        recovery.Observe(Sample(blank), now);
        now = RetryAndObserve(recovery, now, Sample(blank));
        now = RetryAndObserve(recovery, now, Sample(blank));
        var cpuGone = new SensorSnapshot { CpuTemperatureC = 50, Gpus = new List<GpuSnapshot> { blank } };
        Check(!recovery.Observe(cpuGone, now + 1000) && recovery.Pending && recovery.TryBeginRetry(now + 11000),
            "CPU readings lost while a 5-minute GPU retry is waiting are retried after 10 seconds, not after the GPU wait.");

        // A CPU that can't be read at all (unsupported, or its driver blocked) uses up its retries; a GPU that
        // stops reporting later still gets its own retries and warning.
        recovery = new SensorRecovery();
        var cpuless = new SensorSnapshot { Gpus = new List<GpuSnapshot> { live } };
        now = 0;
        recovery.Observe(cpuless, now);
        while (recovery.Pending) now = RetryAndObserve(recovery, now, cpuless);
        Check(recovery.CpuExhausted && !recovery.Pending, "An unreadable CPU uses up its retries.");
        var cpulessBlank = new SensorSnapshot { Gpus = new List<GpuSnapshot> { blank } };
        now += 60_000;
        Check(!recovery.Observe(cpulessBlank, now) && recovery.Pending && !recovery.TryBeginRetry(now + 9_999) && recovery.TryBeginRetry(now + 10_000),
            "With the CPU's retries used up, a GPU that stops reporting is still retried after 10 seconds.");
        Check(GpuFoundAfterDriverInstall(cpuGlitchAt: 0, cpuGlitchSamples: 10_000),
            "On a PC whose CPU sensors never read, a graphics card is still found again after a driver install.");

        // Separate CPU and GPU schedules never reopen sensors in quick succession.
        recovery = new SensorRecovery();
        recovery.Observe(Sample(live), 0);
        recovery.Observe(Sample(blank), 1000);                       // GPU retry due at 11 s
        var cpuAndGpuDown = new SensorSnapshot { CpuTemperatureC = 50, Gpus = new List<GpuSnapshot> { blank } };
        recovery.Observe(cpuAndGpuDown, 8000);                        // CPU retry due at 18 s
        Check(recovery.TryBeginRetry(11000) && !recovery.Observe(cpuAndGpuDown, 11000) && !recovery.TryBeginRetry(18000)
            && !recovery.TryBeginRetry(20999) && recovery.TryBeginRetry(21000),
            "Sensors are reopened at most once every 10 seconds, even when CPU and GPU retries fall close together.");

        // A single bad CPU sample during a long GPU wait must not use up the GPU's own retries. Sensors are only
        // listed again by a reopen: here the driver finishes after 8 minutes, and one CPU sample fails at 400 s.
        Check(GpuFoundAfterDriverInstall(cpuGlitchAt: 400_000, cpuGlitchSamples: 1) && GpuFoundAfterDriverInstall(cpuGlitchAt: 400_000, cpuGlitchSamples: 15)
            && GpuFoundAfterDriverInstall(cpuGlitchAt: -1, cpuGlitchSamples: 0),
            "CPU readings that fail during a graphics-driver install never make the service give up on the graphics card early.");

        // Each graphics card has its own schedule: a second card that drops out while another card's retries
        // run (or are spent) gets its own retries and is never forgotten with the other one.
        var eGpu = new SimCard { Id = "egpu", Gone = 600_000 };                        // unplugged for good
        var dGpu = new SimCard { Id = "dgpu", Gone = 1_850_000, Back = 1_880_000 };      // short outage across the eGPU's last retry
        var pair = Simulate(new[] { eGpu, dGpu }, _ => false, 3600);
        Check(pair.liveAtEnd[1] && !pair.recovery.Pending && !pair.recovery.GpuExhausted,
            "A card whose driver blinks while another, unplugged card uses its last retry is found again.");
        pair = Simulate(new[] { new SimCard { Id = "b", Gone = 100_000 }, new SimCard { Id = "a", Gone = 1_360_000, Back = 1_480_000 } }, _ => false, 3600);
        Check(pair.liveAtEnd[1], "A card that drops out after another card's retries are spent still gets its own retries.");
        pair = Simulate(new[] { new SimCard { Id = "b", Gone = 300_000, Back = 780_000 }, new SimCard { Id = "a", Gone = 1_110_000, Back = 1_590_000 } }, _ => false, 3600);
        Check(pair.liveAtEnd[0] && pair.liveAtEnd[1], "Two driver installs one after the other both end with their cards found again.");

        // A fault that keeps coming back is bounded: one good sample neither refills the budget nor cancels a retry.
        foreach (int badSeconds in new[] { 2, 9, 10, 11, 59 })
        {
            int cycle = badSeconds + 1;
            var flap = Simulate(new[] { new SimCard { Id = "g" } }, t => t >= 300_000 && (t / 1000 - 300) % cycle != badSeconds, 3600);
            Check(flap.reopens >= 1 && flap.reopens <= 3,
                "CPU readings that fail for " + badSeconds + " s at a time reopen sensors a bounded number of times (" + flap.reopens + "), never forever or never.");
        }
        long afterReopen = 0;
        var glitch = Simulate(new[] { new SimCard { Id = "g" } }, t => t >= 300_000 && t != afterReopen, 3600, onReopen: t => afterReopen = t);
        Check(glitch.reopens <= 3, "A fault that each reopen clears for only a moment is retried a bounded number of times (" + glitch.reopens + ").");

        // A failed read says nothing about the cards: it never makes the service forget one.
        recovery = new SensorRecovery();
        recovery.Observe(Sample(live), 0);
        now = 1000;
        recovery.Observe(Sample(blank), now);
        while (recovery.Pending) now = RetryAndObserve(recovery, now, Sample(blank));
        recovery.Observe(SensorSnapshot.Unavailable("Sensor update failed (IOException)."), now + 1000);
        Check(!recovery.Observe(Sample(blank), now + 2000) && recovery.GpuExhausted,
            "A failed read after a card's retries are spent keeps its 'restart the service' warning.");

        var reported = new SensorSnapshot { Status = "ok" };
        SensorRecovery.AddWarning(reported, "Retrying sensors.");
        Check(reported.Status == "partial" && reported.Warnings.Contains("Retrying sensors."),
            "A sample that carries a recovery warning is not labelled ok.");
        var unavailable = SensorSnapshot.Unavailable("Missing driver");
        SensorRecovery.AddWarning(unavailable, "Retrying sensors.");
        Check(unavailable.Status == "Missing driver", "An explicit failure status is kept when a warning is added.");
    }

    private sealed class SimCard
    {
        public string Id = "";
        public long Gone = long.MaxValue;   // the card's driver is missing during [Gone, Back)
        public long Back = long.MaxValue;   // MaxValue: unplugged or disabled for good
    }

    // One sample a second, read the way the service reads: the sensor library lists a card only if its driver
    // was present when sensors were last (re)opened, and a card whose driver left after that open stays blank
    // until the next reopen, even once the driver is back.
    private static (int reopens, bool[] liveAtEnd, SensorRecovery recovery) Simulate(SimCard[] cards, Func<long, bool> cpuBad, long seconds,
        Action<long>? onReopen = null)
    {
        var recovery = new SensorRecovery();
        long openedAt = 0;
        int reopens = 0;
        var live = new bool[cards.Length];
        for (long now = 0; now <= seconds * 1000; now += 1000)
        {
            if (now > 0 && recovery.TryBeginRetry(now)) { openedAt = now; reopens++; onReopen?.Invoke(now); }
            var gpus = new List<GpuSnapshot>();
            for (int i = 0; i < cards.Length; i++)
            {
                SimCard card = cards[i];
                bool presentAtOpen = !(openedAt >= card.Gone && openedAt < card.Back);
                bool presentNow = !(now >= card.Gone && now < card.Back);
                bool staleHandles = openedAt < card.Gone && now >= card.Gone;
                live[i] = presentAtOpen && presentNow && !staleHandles;
                if (presentAtOpen)
                    gpus.Add(live[i] ? new GpuSnapshot { Id = card.Id, Name = card.Id, TemperatureC = 40, LoadPercent = 2, ClockMhz = 300 }
                                     : new GpuSnapshot { Id = card.Id, Name = card.Id });
            }
            recovery.Observe(new SensorSnapshot { CpuTemperatureC = 50, CpuClockMhz = cpuBad(now) ? (double?)null : 4000, Gpus = gpus }, now);
        }
        return (reopens, live, recovery);
    }

    // Simulates one sample a second while a graphics driver installs. Reopening sensors lists the card again only
    // once the driver has finished; until the first reopen the card is still listed, but blank.
    private static bool GpuFoundAfterDriverInstall(long cpuGlitchAt, int cpuGlitchSamples)
    {
        const long driverInstalled = 480_000;
        var recovery = new SensorRecovery();
        var card = new GpuSnapshot { Id = "gpu-0", Name = "GPU", TemperatureC = 40, LoadPercent = 3, ClockMhz = 300 };
        var blankCard = new GpuSnapshot { Id = "gpu-0", Name = "GPU" };
        recovery.Observe(new SensorSnapshot { CpuTemperatureC = 50, CpuClockMhz = 4000, Gpus = new List<GpuSnapshot> { card } }, 0);
        long lastReopen = -1;
        bool found = false;
        for (long now = 1000; now <= 2_400_000; now += 1000)
        {
            if (recovery.TryBeginRetry(now)) lastReopen = now;
            var gpus = new List<GpuSnapshot>();
            if (lastReopen < 0) gpus.Add(blankCard);
            else if (lastReopen >= driverInstalled) gpus.Add(card);
            bool cpuBad = cpuGlitchAt >= 0 && now >= cpuGlitchAt && now < cpuGlitchAt + cpuGlitchSamples * 1000;
            var sample = new SensorSnapshot { CpuTemperatureC = 50, CpuClockMhz = cpuBad ? (double?)null : 4000, Gpus = gpus };
            recovery.Observe(sample, now);
            found = gpus.Count == 1 && gpus[0].ClockMhz.HasValue;
        }
        return found && !recovery.Pending && !recovery.GpuExhausted;
    }

    // Waits for the next permitted retry, then reports the sample read after reopening sensors (if given).
    private static long RetryAndObserve(SensorRecovery recovery, long now, SensorSnapshot? afterReopen)
    {
        long limit = now + 3_600_000;
        while (!recovery.TryBeginRetry(now))
        {
            now += 1000;
            if (now > limit) throw new InvalidOperationException("Recovery stopped retrying early.");
        }
        if (afterReopen != null) recovery.Observe(afterReopen, now);
        return now;
    }

    private static void SamplePolicyTests()
    {
        // Intervals measured from the widget's WinForms 1-second timer; a fresh sample took ~51 ms.
        long[] intervals = { 995, 997, 1003, 992, 1003, 1005, 994 };
        long now = 0, lastStarted = 0;
        int fresh = 0;
        foreach (long interval in intervals)
        {
            now += interval;
            if (SamplePolicy.CanReuse(now, lastStarted)) continue;
            fresh++;
            lastStarted = now;
        }
        Check(fresh == intervals.Length, "Every 1-second widget poll must receive a new hardware sample.");
        Check(SamplePolicy.CanReuse(10100, 10000), "Widgets polling moments apart must share one sample.");
        Check(!SamplePolicy.CanReuse(10800, 10000), "An early timer tick after a late one must still get a new sample.");
        Check(!SamplePolicy.CanReuse(12000, 10000), "Economy 2-second polling must always get a new sample.");
    }

    private static async Task<SensorSnapshot> ReadFixture(byte[] payload, int delay = 0, string? pipeName = null, bool allowUninstalledHost = false)
    {
        var name = pipeName ?? "DLBPrecision.Test." + Guid.NewGuid().ToString("N");
        using (var server = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        using (var client = new SensorClient(name, allowUninstalledHost))
        {
            var serverTask = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync();
                if (delay > 0) await Task.Delay(delay);
                try { await server.WriteAsync(payload, 0, payload.Length); }
                catch (System.IO.IOException) { /* An unauthenticated server is disconnected before its payload is read. */ }
                server.Dispose();
            });
            var response = await client.ReadAsync(CancellationToken.None);
            await serverTask;
            return response;
        }
    }

    private static async Task ClientTests()
    {
        var data = new SensorSnapshot { CpuTemperatureC = 55, CpuLoadPercent = 17, RamUsedGb = 12.4, Status = "Live" };
        var response = await ReadFixture(SnapshotCodec.Encode(data));
        Check(response.CpuTemperatureC == 55, "Client must read output-only pipe response.");
        response = await ReadFixture(SnapshotCodec.Encode(data), pipeName: "DLBPrecision.Untrusted." + Guid.NewGuid().ToString("N"));
        Check(response.CpuTemperatureC == null && response.Status.Contains("identity"),
            "A fresh-looking response from a process that is not the installed service must be rejected.");
        response = await ReadFixture(SnapshotCodec.Encode(data), pipeName: "DLBPrecision.Untrusted." + Guid.NewGuid().ToString("N"), allowUninstalledHost: true);
        Check(response.CpuTemperatureC == 55, "Diagnostic console-host bypass must require explicit opt-in.");
        data.TimestampUtc = DateTime.UtcNow.AddMinutes(-2);
        response = await ReadFixture(SnapshotCodec.Encode(data));
        Check(response.CpuTemperatureC == null && response.Status.IndexOf("stale", StringComparison.OrdinalIgnoreCase) >= 0,
            "Stale service values must not look live.");
        data.TimestampUtc = DateTime.UtcNow.AddMinutes(3);
        response = await ReadFixture(SnapshotCodec.Encode(data));
        Check(response.CpuLoadPercent == null, "Future dated response must be rejected.");
        response = await ReadFixture(Encoding.UTF8.GetBytes("not valid JSON"));
        Check(response.CpuTemperatureC == null, "Malformed service response must not crash widget.");
        using (var client = new SensorClient("DLBPrecision.Missing." + Guid.NewGuid().ToString("N")))
        {
            var timer = Stopwatch.StartNew();
            response = await client.ReadAsync(CancellationToken.None);
            Check(response.CpuTemperatureC == null && response.Status.Contains("unavailable"), "Missing service must be explicit.");
            Check(timer.Elapsed < TimeSpan.FromSeconds(4), "Missing service should fail quickly.");
        }
        using (var client = new SensorClient("DLBPrecision.Missing." + Guid.NewGuid().ToString("N")))
        using (var cancel = new CancellationTokenSource(50))
        {
            bool cancelled = false;
            try { await client.ReadAsync(cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Widget shutdown must cancel outstanding IPC.");
        }
    }

    private static async Task IntegrationTests(bool allowConsoleHost)
    {
        using (var client = new SensorClient(allowUninstalledHost: allowConsoleHost))
        {
            SensorSnapshot? current = null;
            for (int i = 0; i < 5; i++)
            {
                current = await client.ReadAsync(CancellationToken.None);
                if (current.RamUsedGb.HasValue) break;
                await Task.Delay(1000);
            }
            Check(current != null && current.RamUsedGb > 0, "Running service must provide physical memory.");
            Check(current!.CpuName.Length > 0, "Running service must identify CPU.");
            await Task.Delay(1100);
            var next = await client.ReadAsync(CancellationToken.None);
            Check(next.TimestampUtc > current.TimestampUtc, "Requested service samples must advance.");
            Check(next.CpuLoadPercent >= 0 && next.CpuLoadPercent <= 100, "CPU usage must be valid after warm-up.");
            Console.WriteLine(SnapshotCodec.ToJson(next));
        }
    }
}
