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
            await ClientTests();
            if (Array.IndexOf(args, "--integration") >= 0)
                await IntegrationTests(Array.IndexOf(args, "--allow-console-host") >= 0);
            Console.WriteLine("PASS: " + assertions + " assertions (serialization, unavailable data, driver startup/recovery, GPU recovery, sample sharing, protocol, IPC timeouts/cancellation" +
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
        Check(recovery.Exhausted && !recovery.TryBeginRetry(86400000), "Unsupported hardware must not trigger endless expensive rediscovery.");
        Check(partial.CpuLoadPercent == 20 && partial.RamUsedGb == 12 && partial.Gpus[0].ClockMhz == 510,
            "Recovery scheduling must preserve available Windows and GPU measurements.");

        var healthy = new SensorSnapshot { CpuTemperatureC = 55, CpuClockMhz = 4800, Gpus = ReportingGpu() };
        Check(recovery.Observe(healthy, 86401000) && !recovery.Exhausted && !recovery.Pending,
            "Successful CPU measurements must clear failed-startup state, regardless of unavailable GPU metrics.");
        Check(!recovery.TryBeginRetry(long.MaxValue), "A healthy reader must not trigger periodic driver checks or reopen attempts.");
        healthy.CpuClockMhz = null;
        recovery.Observe(healthy, 86402000);
        Check(recovery.Pending && !recovery.TryBeginRetry(86411999) && recovery.TryBeginRetry(86412000),
            "Losing one CPU measurement after recovery must start a fresh bounded retry budget.");
        recovery.Observe(new SensorSnapshot { CpuTemperatureC = 56, CpuClockMhz = 4700, Gpus = ReportingGpu() }, 86412001);
        Check(!recovery.Pending && !recovery.TryBeginRetry(long.MaxValue), "Readings that recover naturally must cancel pending recovery.");
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
        Check(recovery.Observe(Sample(live), 13000) && !recovery.Pending && !recovery.Exhausted,
            "GPU readings that return must clear recovery state.");
        Check(SensorRecovery.CpuReady(Sample()) && !SensorRecovery.CpuReady(new SensorSnapshot { CpuTemperatureC = 50 }),
            "CPU readiness still requires both temperature and clock for the driver warning.");
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
