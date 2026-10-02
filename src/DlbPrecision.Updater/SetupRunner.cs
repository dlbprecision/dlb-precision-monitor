using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using Microsoft.Win32;

namespace DlbPrecision.Updater
{
    internal enum SetupOutcome
    {
        Updated, UpdatedRestartNeeded, UpdatedReenableStartup, UpdatedServiceNotRunning,
        RestartBeforeInstall, CannotProceed, InUseByAnotherUser, Cancelled, CouldNotStart, Failed
    }

    internal sealed class SetupResult
    {
        public SetupResult(SetupOutcome outcome, int exitCode, string? keptLog, bool reopened = false, string detail = "")
        {
            Outcome = outcome;
            ExitCode = exitCode;
            KeptLog = keptLog;
            Reopened = reopened;
            Detail = detail;
        }

        public SetupOutcome Outcome { get; }
        public int ExitCode { get; }
        public string? KeptLog { get; }
        // True only when the updater itself started the widget again after setup.
        public bool Reopened { get; }
        // Setup's own explanation, when it refused to start the update.
        public string Detail { get; }
    }

    // The operating-system side of running setup, replaceable so the whole run can be tested with fakes.
    internal sealed class SetupHost
    {
        public Func<string, string, string, int> StartAndWait { get; set; } = SetupRunner.StartAndWait;
        public Func<int[]> RunningWidgets { get; set; } = SetupRunner.RunningWidgets;
        public Func<int> OtherSessionWidgets { get; set; } = SetupRunner.OtherSessionWidgets;
        public Func<int, bool> IsRunning { get; set; } = SetupRunner.IsRunning;
        public Func<string, bool> Reopen { get; set; } = SetupRunner.ReopenWidget;
        public Func<string, string> InstalledVersion { get; set; } = SetupRunner.InstalledVersion;
        public Func<bool> ServiceRunning { get; set; } = SetupRunner.SensorServiceRunning;
        public Func<string, bool> StartupRegistered { get; set; } = SetupRunner.StartupRegisteredFor;
    }

    internal static class SetupRunner
    {
        public const string MonitorExecutable = "DlbPrecision.Monitor.exe";
        private const string SensorService = "DlbPrecisionSensors";
        private const string StartupValue = "DLBPrecisionMonitor";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string PrepareFailedPrefix = "PrepareToInstall failed: "; // Inno Setup's log line
        private const int RestartExitCode = 3010;
        private const int StartupNotEnabledExitCode = 20;   // DLB setup's GetCustomSetupExitCode
        private const int ServiceNotRunningExitCode = 21;   // DLB setup's GetCustomSetupExitCode
        private const int RestartBeforeInstallExitCode = 8; // Inno: preparation needs a restart
        private const int CannotProceedExitCode = 7;        // Inno: preparation refused to continue
        private const int NotStarted = -1;                  // setup never ran; not one of setup's exit codes
        private const int ErrorCancelled = 1223;            // Windows: the person declined the prompt
        private const int MaximumDetail = 400;
        private static readonly TimeSpan ServiceStartWait = TimeSpan.FromSeconds(10);

        public static string Arguments(string logPath, string taskOptions) =>
            "/SILENT /SUPPRESSMSGBOXES /NOCANCEL /NORESTART /RESTARTEXITCODE=" + RestartExitCode + " /DLBUPDATE=1 /LOG=\"" + logPath + "\""
            + (taskOptions.Length > 0 ? " " + taskOptions : "");

        // A silent setup re-applies the tasks chosen at first install. Deselect the ones the person has
        // since turned off, so an update never turns launch at sign-in or the desktop shortcut back on.
        public static string TaskOptions(bool startupEnabled, bool desktopShortcut)
        {
            var off = new List<string>();
            if (!startupEnabled) off.Add("!startup");
            if (!desktopShortcut) off.Add("!desktopicon");
            return off.Count == 0 ? "" : "/MERGETASKS=\"" + string.Join(",", off) + "\"";
        }

        public static bool StartupEnabled()
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key?.GetValue(StartupValue) is string;
        }

        // The exact entry the monitor writes when launch at sign-in is turned on.
        internal static bool StartupRegisteredFor(string monitorPath)
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey))
                return string.Equals(key?.GetValue(StartupValue) as string, "\"" + monitorPath + "\"", StringComparison.OrdinalIgnoreCase);
        }

        // Setup installs for all users, so its shortcut lives on the shared Public desktop. A person's own
        // copy on their desktop is theirs to manage and says nothing about setup's shortcut.
        public static bool DesktopShortcutExists() =>
            File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "DLB Precision Monitor.lnk"));

        public static bool IsUpdated(SetupOutcome outcome) =>
            outcome == SetupOutcome.Updated || outcome == SetupOutcome.UpdatedRestartNeeded
            || outcome == SetupOutcome.UpdatedReenableStartup || outcome == SetupOutcome.UpdatedServiceNotRunning;

        // Inno Setup does not document the code for a declined Windows prompt, so the outcome is also
        // judged by what changed: a declined or never-started update leaves the version and widget alone.
        public static SetupOutcome Interpret(int exitCode, bool versionChanged, bool widgetClosed)
        {
            bool untouched = !versionChanged && !widgetClosed;
            switch (exitCode)
            {
                case 0: return versionChanged ? SetupOutcome.Updated : SetupOutcome.Failed;
                case RestartExitCode: return versionChanged ? SetupOutcome.UpdatedRestartNeeded : SetupOutcome.Failed;
                case StartupNotEnabledExitCode: return versionChanged ? SetupOutcome.UpdatedReenableStartup : SetupOutcome.Failed;
                case ServiceNotRunningExitCode: return versionChanged ? SetupOutcome.UpdatedServiceNotRunning : SetupOutcome.Failed;
                case RestartBeforeInstallExitCode: return SetupOutcome.RestartBeforeInstall;
                case CannotProceedExitCode: return SetupOutcome.CannotProceed;
                default: return untouched ? SetupOutcome.Cancelled : SetupOutcome.Failed;
            }
        }

        public static string Message(SetupResult result)
        {
            string details = result.KeptLog != null ? " Show details has the setup log." : "";
            switch (result.Outcome)
            {
                case SetupOutcome.Updated: return "The update is installed and the monitor has reopened.";
                case SetupOutcome.UpdatedRestartNeeded: return "The update is installed. Restart Windows to finish.";
                case SetupOutcome.UpdatedReenableStartup:
                    return "The update is installed, but launching at sign-in could not be turned back on. Turn on \"Launch when I sign in to Windows\" again in Settings.";
                case SetupOutcome.UpdatedServiceNotRunning:
                    return "The update is installed, but the DLB sensor service didn't start, so readings may be missing. Restart Windows." + details;
                case SetupOutcome.RestartBeforeInstall: return "Windows needs to restart before this update can be installed. Restart, then check for updates again.";
                case SetupOutcome.CannotProceed:
                    return "The update wasn't installed. " + (result.Detail.Length > 0 ? "Setup said: " + result.Detail : "Setup couldn't start it (code " + result.ExitCode + ").") + details;
                case SetupOutcome.InUseByAnotherUser:
                    return "DLB Precision Monitor is also open for another Windows user (or another sign-in) on this PC. Close it there or sign that user out, then try again. Nothing was changed.";
                case SetupOutcome.Cancelled: return "The update was cancelled or didn't start. Nothing was changed.";
                case SetupOutcome.CouldNotStart: return "Setup couldn't start, so nothing was changed. Try again, or install the update by hand from the DLB download page.";
                default: return "The update didn't finish (code " + result.ExitCode + ")." + (result.Reopened ? " Your monitor was reopened." : "") + details;
            }
        }

        public static SetupResult Run(string setupPath, string installDirectory, string workingFolder, string keptLogPath, string taskOptions, SetupHost? host = null)
        {
            host = host ?? new SetupHost();
            // Windows can't close a program another user has open, so setup would fail halfway and roll back.
            if (host.OtherSessionWidgets() > 0) return new SetupResult(SetupOutcome.InUseByAnotherUser, NotStarted, null);
            string monitor = Path.Combine(installDirectory, MonitorExecutable);
            string versionBefore = host.InstalledVersion(monitor);
            int[] widgetsBefore = host.RunningWidgets();
            string log = Path.Combine(workingFolder, "setup.log");
            int exitCode;
            try
            {
                exitCode = host.StartAndWait(setupPath, Arguments(log, taskOptions), workingFolder);
            }
            catch (Win32Exception error)
            {
                return new SetupResult(error.NativeErrorCode == ErrorCancelled ? SetupOutcome.Cancelled : SetupOutcome.CouldNotStart, NotStarted, null);
            }

            bool versionChanged = !string.Equals(host.InstalledVersion(monitor), versionBefore, StringComparison.Ordinal);
            bool widgetClosed = widgetsBefore.Any(id => !host.IsRunning(id));
            SetupOutcome outcome = Interpret(exitCode, versionChanged, widgetClosed);
            // Setup reports 20 when its own attempt was refused, but the entry from before the update may still be right.
            if (outcome == SetupOutcome.UpdatedReenableStartup && host.StartupRegistered(monitor)) outcome = SetupOutcome.Updated;
            // Checked here too, because an older or interrupted setup may not report it.
            if ((outcome == SetupOutcome.Updated || outcome == SetupOutcome.UpdatedReenableStartup) && !host.ServiceRunning())
                outcome = SetupOutcome.UpdatedServiceNotRunning;
            bool reopened = widgetsBefore.Length > 0 && host.RunningWidgets().Length == 0 && host.Reopen(monitor);
            bool keep = outcome == SetupOutcome.Failed || outcome == SetupOutcome.CannotProceed || outcome == SetupOutcome.UpdatedServiceNotRunning
                || (!IsUpdated(outcome) && exitCode != 0);
            string detail = outcome == SetupOutcome.CannotProceed ? PrepareFailure(log) : "";
            return new SetupResult(outcome, exitCode, keep ? KeepLog(log, keptLogPath) : null, reopened, detail);
        }

        // Inno Setup logs the reason it refused to start; the person sees it without opening the log.
        internal static string PrepareFailure(string log)
        {
            try
            {
                if (!File.Exists(log) || new FileInfo(log).Length > 4 * 1024 * 1024) return "";
                foreach (string line in File.ReadLines(log))
                {
                    int at = line.IndexOf(PrepareFailedPrefix, StringComparison.Ordinal);
                    if (at < 0) continue;
                    string reason = line.Substring(at + PrepareFailedPrefix.Length).Trim();
                    return reason.Length > MaximumDetail ? reason.Substring(0, MaximumDetail - 1).TrimEnd() + "…" : reason;
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                // The result still shows the exit code, and Show details opens the kept log.
            }
            return "";
        }

        private static string? KeepLog(string log, string keptLogPath)
        {
            if (!File.Exists(log)) return null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(keptLogPath));
                File.Copy(log, keptLogPath, true);
                return keptLogPath;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                return null; // The result message still explains what happened, just without the details link.
            }
        }

        // Started normally, never "Run as administrator": Inno Setup elevates itself and so still knows the
        // original user, which it needs to reopen the widget as that user.
        internal static int StartAndWait(string setupPath, string arguments, string workingFolder)
        {
            using (Process? process = Process.Start(new ProcessStartInfo(setupPath, arguments) { UseShellExecute = true, WorkingDirectory = workingFolder }))
            {
                if (process == null) throw new Win32Exception(2);
                process.WaitForExit();
                return process.ExitCode;
            }
        }

        public static string InstalledVersion(string monitorPath) =>
            File.Exists(monitorPath) ? FileVersionInfo.GetVersionInfo(monitorPath).FileVersion?.Trim() ?? "" : "";

        internal static int[] RunningWidgets() => Widgets(sameSession: true);

        internal static int OtherSessionWidgets() => Widgets(sameSession: false).Length;

        private static int[] Widgets(bool sameSession)
        {
            int session = Process.GetCurrentProcess().SessionId;
            Process[] processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(MonitorExecutable));
            try { return processes.Where(process => (process.SessionId == session) == sameSession).Select(process => process.Id).ToArray(); }
            finally { foreach (Process process in processes) process.Dispose(); }
        }

        internal static bool IsRunning(int id)
        {
            try
            {
                using (Process process = Process.GetProcessById(id)) return !process.HasExited;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        internal static bool SensorServiceRunning()
        {
            try
            {
                using (var service = new ServiceController(SensorService))
                {
                    if (service.Status == ServiceControllerStatus.StartPending) service.WaitForStatus(ServiceControllerStatus.Running, ServiceStartWait);
                    return service.Status == ServiceControllerStatus.Running;
                }
            }
            catch (Exception error) when (error is InvalidOperationException || error is Win32Exception || error is System.ServiceProcess.TimeoutException)
            {
                return false; // Not installed, not readable or still not running: the person is told to restart.
            }
        }

        internal static bool ReopenWidget(string monitor)
        {
            if (!File.Exists(monitor)) return false;
            try
            {
                using (Process.Start(new ProcessStartInfo(monitor) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(monitor) })) { }
                return true;
            }
            catch (Win32Exception)
            {
                return false; // The result message still says the update didn't finish; the monitor's shortcut opens it.
            }
        }
    }
}
