using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Security;
using System.ServiceProcess;
using System.Threading;
using DlbPrecision.Shared;

namespace DlbPrecision.Service
{
    internal static class PawnIoDriver
    {
        internal static string? EnsureRunning(CancellationToken cancellationToken)
        {
            using (var driver = new ServiceController("PawnIO"))
                return EnsureRunning(() => { driver.Refresh(); return driver.Status; }, driver.Start,
                    milliseconds => cancellationToken.WaitHandle.WaitOne(milliseconds), cancellationToken);
        }

        // The delegates keep the same start/wait/error policy testable without touching a real driver.
        internal static string? EnsureRunning(Func<ServiceControllerStatus> status, Action start,
            Func<int, bool> wait, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = status();
                if (current == ServiceControllerStatus.Stopped)
                {
                    try { start(); }
                    catch (InvalidOperationException error) when (NativeError(error) == 1056) { }
                    catch (Win32Exception error) when (error.NativeErrorCode == 1056) { }
                    current = status();
                }
                for (int waited = 0; current == ServiceControllerStatus.StartPending && waited < 1500; waited += 100)
                {
                    if (wait(100)) cancellationToken.ThrowIfCancellationRequested();
                    cancellationToken.ThrowIfCancellationRequested();
                    current = status();
                }
                if (current == ServiceControllerStatus.Running) return null;
                return "PawnIO sensor driver is " + current + ". CPU temperature and clock may be unavailable. " +
                    "If this persists, inspect the sensor report and Windows driver status.";
            }
            catch (Exception error) when (error is InvalidOperationException || error is Win32Exception
                || error is UnauthorizedAccessException || error is SecurityException)
            {
                int code = NativeError(error);
                return code == 1060
                    ? "PawnIO sensor driver is not installed. Run the DLB Precision installer to install the bundled driver."
                    : "PawnIO sensor driver could not start" + (code == 0 ? " (" + error.GetType().Name + ")" : " (Windows error " + code + ")") +
                        ". Other available readings remain active. Inspect Windows driver status or repair the DLB installation if this persists.";
            }
        }

        private static int NativeError(Exception error) =>
            (error as Win32Exception ?? error.InnerException as Win32Exception)?.NativeErrorCode ?? 0;
    }

    internal sealed class SensorRecovery
    {
        // Bounded rediscovery: unsupported or broken sensors stay partial until readings recover or the service
        // restarts. The CPU and every graphics card keep their own retry schedule. One reopen serves them all,
        // but only spends the retries that were due, so one problem never uses up another's recovery. GPU retries
        // run longer (about 21 minutes in all) because a graphics-driver install can keep a card unlisted for
        // several minutes; a card is forgotten only once its own retries are spent and it is no longer listed
        // (unplugged or disabled).
        private static readonly long[] CpuDelays = { 10000, 30000, 60000 };
        private static readonly long[] GpuDelays = { 10000, 60000, 300000, 900000 };
        // A fault counts only once it has two bad samples without readings settling in between, so an isolated
        // dropout costs nothing.
        // A brief good reading neither cancels a planned retry nor refills a budget. Readings must stay good for
        // SettleTime to cancel a retry, and for RefillTime before a new fault gets a fresh budget. A fault that
        // returns within RefillTime of such a natural recovery gets a firm retry that good readings no longer
        // cancel. Otherwise a fault that keeps returning would reopen sensors forever, or keep promising
        // retries that never happen.
        private const long SettleTime = 5000;
        private const long RefillTime = 300000;
        // Reopening is expensive, so reopens are at least this far apart even when schedules fall close together.
        private const long MinimumGap = 10000;

        private sealed class Track
        {
            public int Retries;
            public long DueAt = -1;
            public bool Firm;            // the planned retry happens even if readings come back first
            public long GoodSince = -1;  // -1 while failing
            public long BadSince;        // start of the fault, or of the wait since the last reopen
            public int BadSamples;
            public bool Reopened;        // a reopen spent this track's retry; the next wait starts when it is read
            public long CancelledAt = long.MinValue / 2;
        }

        private readonly Track cpu = new Track();
        // Cards are watched only after they have reported, so hardware that never exposes GPU sensors does not
        // trigger rediscovery, while one that goes blank later (for example after a graphics-driver update) does.
        private readonly Dictionary<string, Track> gpus = new Dictionary<string, Track>(StringComparer.Ordinal);
        private bool cpuReady = true;
        private bool gpusReady = true;
        private long lastReopenAt = -1;

        internal bool Pending => cpu.DueAt >= 0 || gpus.Values.Any(card => card.DueAt >= 0);
        // A retry is planned for a reading that is missing now: what the "will retry" message promises.
        internal bool Retrying => (!cpuReady && cpu.DueAt >= 0) || gpus.Values.Any(card => card.GoodSince < 0 && card.DueAt >= 0);
        internal bool CpuExhausted => !cpuReady && cpu.Retries == CpuDelays.Length;
        internal bool GpuExhausted => gpus.Values.Any(card => card.GoodSince < 0 && card.Retries == GpuDelays.Length);

        internal bool TryBeginRetry(long milliseconds)
        {
            long retryAt = gpus.Values.Where(card => card.DueAt >= 0).Select(card => card.DueAt).DefaultIfEmpty(-1).Min();
            if (cpu.DueAt >= 0 && (retryAt < 0 || cpu.DueAt < retryAt)) retryAt = cpu.DueAt;
            if (retryAt < 0 || milliseconds < retryAt) return false;
            if (lastReopenAt >= 0 && milliseconds < lastReopenAt + MinimumGap) return false;
            lastReopenAt = milliseconds;
            Spend(cpu, milliseconds);
            foreach (Track card in gpus.Values) Spend(card, milliseconds);
            return true;
        }

        private static void Spend(Track track, long milliseconds)
        {
            if (track.DueAt < 0 || milliseconds < track.DueAt) return;
            track.Retries++;
            track.DueAt = -1;
            track.Firm = false;
            track.Reopened = true;
        }

        internal static bool CpuReady(SensorSnapshot snapshot) => snapshot.CpuTemperatureC.HasValue && snapshot.CpuClockMhz.HasValue;

        // Shown with the sample; a sample that carries a recovery warning is not "ok".
        internal static void AddWarning(SensorSnapshot snapshot, string warning)
        {
            snapshot.Warnings.Add(warning);
            if (snapshot.Status == "ok") snapshot.Status = "partial";
        }

        // Reopening sensors re-initializes the vendor libraries, which brings back readings that a driver
        // update or a sensor fault took away.
        internal bool Observe(SensorSnapshot snapshot, long milliseconds)
        {
            cpuReady = CpuReady(snapshot);
            Update(cpu, cpuReady, CpuDelays, milliseconds);
            // A failed read lists nothing, which says nothing about the cards: none starts or stops failing, and
            // none is forgotten. Cards already failing keep their retries going, since nothing else may reopen.
            if (snapshot.ReadFailed)
            {
                foreach (Track card in gpus.Values)
                    if (card.GoodSince < 0) Update(card, false, GpuDelays, milliseconds);
            }
            else
            {
                foreach (GpuSnapshot gpu in snapshot.Gpus)
                    if (HasReading(gpu) && !gpus.ContainsKey(gpu.Id)) gpus[gpu.Id] = new Track();
                foreach (string id in gpus.Keys.ToList())
                {
                    Track card = gpus[id];
                    bool reporting = IsReporting(snapshot, id);
                    // Its own retries are spent and it is not even listed: unplugged or disabled. Stop waiting.
                    if (!reporting && card.Retries == GpuDelays.Length && !IsListed(snapshot, id)) { gpus.Remove(id); continue; }
                    Update(card, reporting, GpuDelays, milliseconds);
                }
            }
            gpusReady = gpus.Values.All(card => card.GoodSince >= 0);
            return cpuReady && gpusReady;
        }

        private static void Update(Track track, bool good, long[] delays, long milliseconds)
        {
            if (good)
            {
                if (track.GoodSince < 0) track.GoodSince = milliseconds;
                long goodFor = milliseconds - track.GoodSince;
                // A fault is over once readings have settled, not at the first good sample, so a reading that is
                // blank on every other sample still counts as one fault.
                if (goodFor >= SettleTime) track.BadSamples = 0;
                if (goodFor >= SettleTime && track.DueAt >= 0 && !track.Firm) { track.DueAt = -1; track.CancelledAt = milliseconds; }
                if (goodFor >= RefillTime) { track.Retries = 0; track.DueAt = -1; track.Firm = false; }
                return;
            }
            track.GoodSince = -1;
            // The first sample after a reopen comes once it has finished, however long that took.
            if (track.BadSamples++ == 0 || track.Reopened) track.BadSince = milliseconds;
            track.Reopened = false;
            if (track.BadSamples < 2 || track.DueAt >= 0 || track.Retries == delays.Length) return;
            track.DueAt = track.BadSince + delays[track.Retries];
            track.Firm = milliseconds - track.CancelledAt < RefillTime;
        }

        private static bool IsListed(SensorSnapshot snapshot, string id)
        {
            foreach (GpuSnapshot gpu in snapshot.Gpus)
                if (gpu.Id == id) return true;
            return false;
        }

        private static bool IsReporting(SensorSnapshot snapshot, string id)
        {
            foreach (GpuSnapshot gpu in snapshot.Gpus)
                if (gpu.Id == id && HasReading(gpu)) return true;
            return false;
        }

        private static bool HasReading(GpuSnapshot gpu) => gpu.TemperatureC.HasValue || gpu.LoadPercent.HasValue || gpu.ClockMhz.HasValue;
    }
}
