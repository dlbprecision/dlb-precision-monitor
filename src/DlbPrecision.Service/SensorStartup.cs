using System;
using System.Collections.Generic;
using System.ComponentModel;
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
        // Bounded rediscovery; unsupported hardware stays partial until readings recover or the service
        // restarts. Separate budgets keep a GPU problem from ever using up the CPU's recovery. GPU retries
        // run longer (about 21 minutes in all), because a graphics-driver install can keep the card
        // unlisted for several minutes, and a card is only forgotten once they are spent.
        private static readonly long[] CpuDelays = { 10000, 30000, 60000 };
        private static readonly long[] GpuDelays = { 10000, 60000, 300000, 900000 };
        private readonly HashSet<string> reportingGpus = new HashSet<string>(StringComparer.Ordinal);
        private int cpuRetries;
        private int gpuRetries;
        private bool cpuReady = true;
        private bool gpusReady = true;
        // Each kind keeps its own due time, so a CPU problem never moves or spends a GPU retry and the other way round.
        private long cpuDueAt = -1;
        private long gpuDueAt = -1;
        private long lastReopenAt = -1;

        internal bool Pending => cpuDueAt >= 0 || gpuDueAt >= 0;
        private long RetryAt => cpuDueAt < 0 ? gpuDueAt : gpuDueAt < 0 ? cpuDueAt : Math.Min(cpuDueAt, gpuDueAt);
        internal bool CpuExhausted => !cpuReady && cpuRetries == CpuDelays.Length;
        internal bool GpuExhausted => !gpusReady && gpuRetries == GpuDelays.Length;

        // One reopen serves both, but only a kind whose own retry was due spends it. Reopening is expensive, so
        // reopens are at least the shortest retry delay apart even when the two schedules fall close together.
        internal bool TryBeginRetry(long milliseconds)
        {
            if (!Pending || milliseconds < RetryAt) return false;
            if (lastReopenAt >= 0 && milliseconds < lastReopenAt + CpuDelays[0]) return false;
            lastReopenAt = milliseconds;
            if (cpuDueAt >= 0 && milliseconds >= cpuDueAt) { cpuRetries++; cpuDueAt = -1; }
            if (gpuDueAt >= 0 && milliseconds >= gpuDueAt) { gpuRetries++; gpuDueAt = -1; }
            return true;
        }

        internal static bool CpuReady(SensorSnapshot snapshot) => snapshot.CpuTemperatureC.HasValue && snapshot.CpuClockMhz.HasValue;

        // Shown with the sample; a sample that carries a recovery warning is not "ok".
        internal static void AddWarning(SensorSnapshot snapshot, string warning)
        {
            snapshot.Warnings.Add(warning);
            if (snapshot.Status == "ok") snapshot.Status = "partial";
        }

        // A GPU is watched only after it has reported, so hardware that never exposes GPU sensors
        // does not trigger rediscovery, while one that goes blank later (for example after a
        // graphics-driver update) does. Reopening sensors re-initializes the vendor libraries.
        internal bool Observe(SensorSnapshot snapshot, long milliseconds)
        {
            // Once the GPU retries are spent, a watched GPU that sensors no longer list at all was
            // unplugged or disabled; stop waiting for it.
            if (gpuRetries == GpuDelays.Length) reportingGpus.RemoveWhere(id => !IsListed(snapshot, id));
            gpusReady = true;
            foreach (string id in reportingGpus)
                if (!IsReporting(snapshot, id)) gpusReady = false;
            foreach (GpuSnapshot gpu in snapshot.Gpus)
                if (HasReading(gpu)) reportingGpus.Add(gpu.Id);

            cpuReady = CpuReady(snapshot);
            if (cpuReady) cpuRetries = 0;
            if (gpusReady) gpuRetries = 0;
            // A due time is set once and kept while the problem lasts, so repeated failed samples never postpone it.
            if (cpuReady || cpuRetries == CpuDelays.Length) cpuDueAt = -1;
            else if (cpuDueAt < 0) cpuDueAt = milliseconds + CpuDelays[cpuRetries];
            // While a CPU retry is pending it reopens everything (GPU libraries included), so a GPU retry is
            // scheduled only when none is: the CPU reads, or its retries are used up. One already scheduled is kept.
            if (gpusReady || gpuRetries == GpuDelays.Length) gpuDueAt = -1;
            else if (gpuDueAt < 0 && cpuDueAt < 0) gpuDueAt = milliseconds + GpuDelays[gpuRetries];
            return cpuReady && gpusReady;
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
