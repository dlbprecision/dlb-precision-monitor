using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace DlbPrecision.Updater
{
    internal enum SetupOutcome { Updated, UpdatedRestartNeeded, UpdatedReenableStartup, RestartBeforeInstall, Cancelled, CouldNotStart, Failed }

    internal sealed class SetupResult
    {
        public SetupResult(SetupOutcome outcome, int exitCode, string? keptLog)
        {
            Outcome = outcome;
            ExitCode = exitCode;
            KeptLog = keptLog;
        }

        public SetupOutcome Outcome { get; }
        public int ExitCode { get; }
        public string? KeptLog { get; }
    }

    // The operating-system side of running setup, replaceable so the whole run can be tested with fakes.
    internal sealed class SetupHost
    {
        public Func<string, string, string, int> StartAndWait { get; set; } = SetupRunner.StartAndWait;
        public Func<int[]> RunningWidgets { get; set; } = SetupRunner.RunningWidgets;
        public Func<int, bool> IsRunning { get; set; } = SetupRunner.IsRunning;
        public Action<string> Reopen { get; set; } = SetupRunner.ReopenWidget;
        public Func<string, string> InstalledVersion { get; set; } = SetupRunner.InstalledVersion;
    }

    internal static class SetupRunner
    {
        public const string MonitorExecutable = "DlbPrecision.Monitor.exe";
        private const int RestartExitCode = 3010;
        private const int StartupNotEnabledExitCode = 20;   // DLB setup's GetCustomSetupExitCode
        private const int RestartBeforeInstallExitCode = 8; // Inno: preparation needs a restart
        private const int CannotProceedExitCode = 7;        // Inno: preparation refused to continue
        private const int NotStarted = -1;                  // setup never ran; not one of setup's exit codes
        private const int ErrorCancelled = 1223;            // Windows: the person declined the prompt

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
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                return key?.GetValue("DLBPrecisionMonitor") is string;
        }

        // Setup installs for all users, so its shortcut lives on the shared Public desktop. A person's own
        // copy on their desktop is theirs to manage and says nothing about setup's shortcut.
        public static bool DesktopShortcutExists() =>
            File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "DLB Precision Monitor.lnk"));

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
                case RestartBeforeInstallExitCode: return SetupOutcome.RestartBeforeInstall;
                case CannotProceedExitCode: return SetupOutcome.Failed;
                default: return untouched ? SetupOutcome.Cancelled : SetupOutcome.Failed;
            }
        }

        public static string Message(SetupOutcome outcome, int exitCode)
        {
            switch (outcome)
            {
                case SetupOutcome.Updated: return "The update is installed and the monitor has reopened.";
                case SetupOutcome.UpdatedRestartNeeded: return "The update is installed. Restart Windows to finish.";
                case SetupOutcome.UpdatedReenableStartup:
                    return "The update is installed, but launching at sign-in could not be turned back on. Turn on \"Launch when I sign in to Windows\" again in Settings.";
                case SetupOutcome.RestartBeforeInstall: return "Windows needs to restart before this update can be installed. Restart, then check for updates again.";
                case SetupOutcome.Cancelled: return "The update was cancelled or didn't start. Nothing was changed.";
                case SetupOutcome.CouldNotStart: return "Setup couldn't start, so nothing was changed. Try again, or install the update by hand from the DLB download page.";
                default: return "The update didn't finish (code " + exitCode + "). Your monitor was reopened. Show details has the setup log.";
            }
        }

        public static SetupResult Run(string setupPath, string installDirectory, string workingFolder, string keptLogPath, string taskOptions, SetupHost? host = null)
        {
            host = host ?? new SetupHost();
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
            if (widgetsBefore.Length > 0 && host.RunningWidgets().Length == 0) host.Reopen(monitor);
            bool updated = outcome == SetupOutcome.Updated || outcome == SetupOutcome.UpdatedRestartNeeded || outcome == SetupOutcome.UpdatedReenableStartup;
            bool keep = outcome == SetupOutcome.Failed || (!updated && exitCode != 0);
            return new SetupResult(outcome, exitCode, keep ? KeepLog(log, keptLogPath) : null);
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

        internal static int[] RunningWidgets()
        {
            int session = Process.GetCurrentProcess().SessionId;
            Process[] processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(MonitorExecutable));
            try { return processes.Where(process => process.SessionId == session).Select(process => process.Id).ToArray(); }
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

        internal static void ReopenWidget(string monitor)
        {
            if (!File.Exists(monitor)) return;
            try { using (Process.Start(new ProcessStartInfo(monitor) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(monitor) })) { } }
            catch (Win32Exception)
            {
                // The result message still says the update didn't finish; the monitor's shortcut opens it.
            }
        }
    }
}
