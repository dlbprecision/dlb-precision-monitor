using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DlbPrecision.Updater
{
    internal enum SetupOutcome { Updated, UpdatedRestartNeeded, UpdatedReenableStartup, RestartBeforeInstall, Cancelled, Failed }

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

    internal static class SetupRunner
    {
        public const string MonitorExecutable = "DlbPrecision.Monitor.exe";
        private const int RestartExitCode = 3010;
        private const int StartupNotEnabledExitCode = 20;   // DLB setup's GetCustomSetupExitCode
        private const int RestartBeforeInstallExitCode = 8; // Inno: preparation needs a restart
        private const int CannotProceedExitCode = 7;        // Inno: preparation refused to continue

        public static string Arguments(string logPath) =>
            "/SILENT /SUPPRESSMSGBOXES /NOCANCEL /NORESTART /RESTARTEXITCODE=" + RestartExitCode + " /DLBUPDATE=1 /LOG=\"" + logPath + "\"";

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
                default: return "The update didn't finish (code " + exitCode + "). Your monitor was reopened. Show details has the setup log.";
            }
        }

        public static SetupResult Run(string setupPath, string installDirectory, string workingFolder, string keptLogPath)
        {
            string monitor = Path.Combine(installDirectory, MonitorExecutable);
            string versionBefore = InstalledVersion(monitor);
            int[] widgetsBefore = RunningWidgets();
            string log = Path.Combine(workingFolder, "setup.log");
            int exitCode;
            try
            {
                // Started normally, never "Run as administrator": Inno Setup elevates itself and so still
                // knows the original user, which it needs to reopen the widget as that user.
                using (Process? process = Process.Start(new ProcessStartInfo(setupPath, Arguments(log)) { UseShellExecute = true, WorkingDirectory = workingFolder }))
                {
                    if (process == null) return new SetupResult(SetupOutcome.Failed, -1, null);
                    process.WaitForExit();
                    exitCode = process.ExitCode;
                }
            }
            catch (Win32Exception error)
            {
                exitCode = error.NativeErrorCode;
            }

            bool versionChanged = !string.Equals(InstalledVersion(monitor), versionBefore, StringComparison.Ordinal);
            bool widgetClosed = widgetsBefore.Any(id => !IsRunning(id));
            SetupOutcome outcome = Interpret(exitCode, versionChanged, widgetClosed);
            if (widgetsBefore.Length > 0 && RunningWidgets().Length == 0 && File.Exists(monitor)) ReopenWidget(monitor);
            string? kept = null;
            if ((outcome == SetupOutcome.Failed || outcome == SetupOutcome.RestartBeforeInstall) && File.Exists(log))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(keptLogPath));
                    File.Copy(log, keptLogPath, true);
                    kept = keptLogPath;
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    // Reported without the details link; the result message still explains what happened.
                }
            }
            return new SetupResult(outcome, exitCode, kept);
        }

        public static string InstalledVersion(string monitorPath) =>
            File.Exists(monitorPath) ? FileVersionInfo.GetVersionInfo(monitorPath).FileVersion?.Trim() ?? "" : "";

        private static int[] RunningWidgets()
        {
            int session = Process.GetCurrentProcess().SessionId;
            Process[] processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(MonitorExecutable));
            try { return processes.Where(process => process.SessionId == session).Select(process => process.Id).ToArray(); }
            finally { foreach (Process process in processes) process.Dispose(); }
        }

        private static bool IsRunning(int id)
        {
            try
            {
                using (Process process = Process.GetProcessById(id)) return !process.HasExited;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private static void ReopenWidget(string monitor)
        {
            try { using (Process.Start(new ProcessStartInfo(monitor) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(monitor) })) { } }
            catch (Win32Exception)
            {
                // The result message still tells the person the update didn't finish; they can open the monitor from its shortcut.
            }
        }
    }
}
