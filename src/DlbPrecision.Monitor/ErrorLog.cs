using System;
using System.IO;
using System.Security;

namespace DlbPrecision.Monitor
{
    // Written only when something unexpected fails, so normal running never touches the disk here.
    internal static class ErrorLog
    {
        private const long MaximumBytes = 64 * 1024;
        private static readonly object Gate = new object();
        private static string lastEntry = "";

        public static string DefaultPath => Path.Combine(Path.GetDirectoryName(SettingsStore.DefaultPath), "error.log");

        public static void Write(Exception? error)
        {
            if (error == null) return;
            lock (Gate)
            {
                // A fault in painting would otherwise repeat every refresh; one entry is enough.
                string entry = error.ToString();
                if (entry == lastEntry) return;
                lastEntry = entry;
                try
                {
                    string path = DefaultPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    string line = DateTime.UtcNow.ToString("u") + " " + entry + Environment.NewLine;
                    var existing = new FileInfo(path);
                    if (existing.Exists && existing.Length > MaximumBytes) File.WriteAllText(path, line);
                    else File.AppendAllText(path, line);
                }
                catch (Exception logError) when (logError is IOException || logError is UnauthorizedAccessException || logError is SecurityException)
                {
                    // The log is the last resort for reporting; if it cannot be written there is nowhere left to report.
                }
            }
        }
    }
}
