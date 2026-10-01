using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace DlbPrecision.Monitor
{
    internal static class AppVersion
    {
        // Three parts for releases; a local test build keeps its fourth part so it is recognizable.
        public static string Display
        {
            get
            {
                Version version = typeof(AppVersion).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
                return version.Revision > 0 ? version.ToString(4) : version.ToString(3);
            }
        }
    }

    // The widget only starts the separate updater program. All internet, download and install work
    // happens there, so the widget itself never loads that code and its resource use is unchanged.
    internal static class UpdateLauncher
    {
        public const string UpdaterExecutable = "DlbPrecision.Updater.exe";

        public static string? Start(string installDirectory, Action<ProcessStartInfo> start)
        {
            string updater = Path.Combine(installDirectory, UpdaterExecutable);
            if (!File.Exists(updater)) return "The updater is missing. Reinstall DLB Precision Monitor to restore it.";
            try
            {
                start(new ProcessStartInfo(updater) { UseShellExecute = false, WorkingDirectory = installDirectory });
                return null;
            }
            catch (Win32Exception error)
            {
                return "The updater could not start: " + error.Message;
            }
        }
    }
}
