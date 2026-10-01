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
        // Cap rediscovery at three retries; unsupported hardware stays partial until readings recover or the service restarts.
        private static readonly long[] Delays = { 10000, 30000, 60000 };
        private readonly HashSet<string> reportingGpus = new HashSet<string>(StringComparer.Ordinal);
        private int retries;
        private long retryAt = -1;

        internal bool Pending => retryAt >= 0;
        internal bool Exhausted => retries == Delays.Length && !Pending;
        internal bool TryBeginRetry(long milliseconds)
        {
            if (!Pending || milliseconds < retryAt) return false;
            retries++;
            retryAt = -1;
            return true;
        }

        internal static bool CpuReady(SensorSnapshot snapshot) => snapshot.CpuTemperatureC.HasValue && snapshot.CpuClockMhz.HasValue;

        // A GPU is watched only after it has reported, so hardware that never exposes GPU sensors
        // does not trigger rediscovery, while one that goes blank later (for example after a
        // graphics-driver update) does. Reopening sensors re-initializes the vendor libraries.
        internal bool Observe(SensorSnapshot snapshot, long milliseconds)
        {
            bool gpusReady = true;
            foreach (string id in reportingGpus)
                if (!IsReporting(snapshot, id)) gpusReady = false;
            foreach (GpuSnapshot gpu in snapshot.Gpus)
                if (HasReading(gpu)) reportingGpus.Add(gpu.Id);

            bool ready = CpuReady(snapshot) && gpusReady;
            if (ready) { retries = 0; retryAt = -1; }
            else if (!Pending && retries < Delays.Length) retryAt = milliseconds + Delays[retries];
            return ready;
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
