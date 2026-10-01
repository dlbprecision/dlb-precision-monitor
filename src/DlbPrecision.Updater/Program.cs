using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

// The updater runs from a temporary folder; Windows libraries must only ever come from System32.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace DlbPrecision.Updater
{
    internal static class Program
    {
        private const string FolderPrefix = "DLBPrecision-Update-";
        private static readonly TimeSpan StaleFolderAge = TimeSpan.FromMinutes(10);

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (args.Length >= 1 && args[0] == "--smoke-test") { RunSmokeTests(args.Length >= 2 ? args[1] : null); return 0; }
                if (args.Length >= 2 && args[0] == "--render-preview") { RenderPreviews(args[1]); return 0; }
                if (args.Length >= 3 && args[0] == "--cleanup") { RemoveAfterExit(args[1], args[2]); return 0; }

                string? feed = Value(args, "--feed");
                string ownFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string installDirectory = Value(args, "--install-dir") ?? ownFolder;
                string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
                string mutexName = @"Local\DLBPrecision.Updater." + user;
                if (!IsRelocatedFolder(ownFolder))
                {
                    if (Mutex.TryOpenExisting(mutexName, out Mutex? existing)) { existing.Dispose(); FocusExistingWindow(); return 0; }
                    Relocate(installDirectory, feed);
                    return 0;
                }
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (sender, error) => MessageBox.Show("The updater hit an unexpected problem.\n\n" + error.Exception.Message,
                    UpdaterForm.WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                using (var instance = new Mutex(true, mutexName, out bool created))
                {
                    if (created)
                    {
                        using (var form = new UpdaterForm(installDirectory, feed, ownFolder)) Application.Run(form);
                        instance.ReleaseMutex();
                    }
                    else FocusExistingWindow();
                }
                ScheduleRemoval(installDirectory, ownFolder);
                return 0;
            }
            catch (Exception error)
            {
                if (args.Length >= 2 && args[0] == "--smoke-test") File.WriteAllText(args[1], "FAILED\n" + error);
                else MessageBox.Show("DLB Precision Monitor could not check for updates.\n\n" + error.Message, UpdaterForm.WindowTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        // Setup replaces every file in the install folder, so the updater waits for it from a private copy.
        private static void Relocate(string installDirectory, string? feed)
        {
            RemoveStaleFolders();
            string folder = Path.Combine(Path.GetTempPath(), FolderPrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string self = Assembly.GetExecutingAssembly().Location;
            string copy = Path.Combine(folder, Path.GetFileName(self));
            File.Copy(self, copy);
            if (File.Exists(self + ".config")) File.Copy(self + ".config", copy + ".config");
            string arguments = "--install-dir " + Quote(installDirectory) + (feed != null ? " --feed " + Quote(feed) : "");
            using (Process.Start(new ProcessStartInfo(copy, arguments) { UseShellExecute = false, WorkingDirectory = folder })) { }
        }

        internal static bool IsRelocatedFolder(string folder)
        {
            string full = Path.GetFullPath(folder).TrimEnd('\\');
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
            return string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(Path.GetFileName(full), "^" + FolderPrefix + "[0-9a-f]{32}$", RegexOptions.CultureInvariant);
        }

        // A running copy cannot delete its own folder. The installed updater removes it once this copy has exited.
        private static void ScheduleRemoval(string installDirectory, string folder)
        {
            string cleaner = Path.Combine(installDirectory, Path.GetFileName(Assembly.GetExecutingAssembly().Location));
            if (!File.Exists(cleaner) || IsRelocatedFolder(Path.GetDirectoryName(cleaner))) return;
            try
            {
                using (Process.Start(new ProcessStartInfo(cleaner, "--cleanup " + Quote(folder) + " " + Process.GetCurrentProcess().Id)
                    { UseShellExecute = false, WorkingDirectory = installDirectory })) { }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The next check removes the folder instead.
            }
        }

        // Deletes only a DLB update folder in this user's temporary folder, after the copy using it has exited.
        private static void RemoveAfterExit(string folderArgument, string processArgument)
        {
            string folder = Path.GetFullPath(folderArgument);
            if (!IsRelocatedFolder(folder) || !int.TryParse(processArgument, out int processId)) return;
            try
            {
                using (Process process = Process.GetProcessById(processId)) process.WaitForExit(120000);
            }
            catch (ArgumentException) { /* Already exited. */ }
            catch (InvalidOperationException) { /* Already exited. */ }
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (Directory.Exists(folder)) Directory.Delete(folder, true);
                    return;
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    Thread.Sleep(500); // Antivirus or Windows may briefly hold a file just written.
                }
            }
        }

        // A copy that could not be removed at exit is removed by a later check.
        private static void RemoveStaleFolders()
        {
            foreach (string folder in Directory.EnumerateDirectories(Path.GetTempPath(), FolderPrefix + "*"))
            {
                try
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) > StaleFolderAge) Directory.Delete(folder, true);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    // Still in use or locked by antivirus; the next check tries again.
                }
            }
        }

        private static void FocusExistingWindow()
        {
            int self = Process.GetCurrentProcess().Id;
            NativeMethods.EnumWindows((window, parameter) =>
            {
                var title = new StringBuilder(UpdaterForm.WindowTitle.Length + 1);
                NativeMethods.GetWindowText(window, title, title.Capacity);
                if (title.ToString() != UpdaterForm.WindowTitle) return true;
                NativeMethods.GetWindowThreadProcessId(window, out uint owner);
                if (owner == self) return true;
                NativeMethods.ShowWindow(window, 9); // SW_RESTORE
                NativeMethods.SetForegroundWindow(window);
                return false;
            }, IntPtr.Zero);
        }

        private static string? Value(string[] args, string name)
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        // A trailing backslash before the closing quote would escape it on the command line.
        internal static string Quote(string value)
        {
            if (value.IndexOf('"') >= 0) throw new ArgumentException("Quotes are not allowed in update paths or feed addresses.");
            return "\"" + value.TrimEnd('\\') + "\"";
        }

        private static UpdateOffer SampleOffer()
        {
            var release = new ReleaseInfo
            {
                Tag = "v0.1.9",
                Name = "DLB Precision Monitor v0.1.9 - Sample Fixes",
                Body = "Sample release notes for appearance testing.\n\n- **Faster** startup\n- Fixes the [example](https://example.test) issue"
            };
            foreach (string name in new[] { "DLB-Precision-Monitor-0.1.9-Setup.exe", "DLB-Precision-Monitor-0.1.9-Setup.exe.sha256" })
                release.Assets.Add(new ReleaseAsset { Name = name, Size = name.EndsWith(".sha256") ? 104 : 7_434_112, DownloadUrl = "https://example.test/" + name });
            UpdateDecision decision = UpdateOffer.Decide(release, new Version(0, 1, 8, 0), false, false);
            return decision.Offer ?? throw new InvalidOperationException("Sample offer was not accepted.");
        }

        private static IEnumerable<KeyValuePair<string, Action<UpdaterForm>>> States()
        {
            UpdateOffer offer = SampleOffer();
            yield return new KeyValuePair<string, Action<UpdaterForm>>("checking", form => form.ShowChecking());
            yield return new KeyValuePair<string, Action<UpdaterForm>>("up-to-date", form => form.ShowUpToDate("0.1.8"));
            yield return new KeyValuePair<string, Action<UpdaterForm>>("available", form => form.ShowAvailable(offer));
            yield return new KeyValuePair<string, Action<UpdaterForm>>("downloading", form => form.ShowDownloading(3_355_443, 7_434_112));
            yield return new KeyValuePair<string, Action<UpdaterForm>>("verifying", form => form.ShowVerifying());
            yield return new KeyValuePair<string, Action<UpdaterForm>>("installing", form => form.ShowInstalling());
            yield return new KeyValuePair<string, Action<UpdaterForm>>("updated", form => form.ShowResult(new SetupResult(SetupOutcome.Updated, 0, null)));
            yield return new KeyValuePair<string, Action<UpdaterForm>>("cancelled", form => form.ShowResult(new SetupResult(SetupOutcome.Cancelled, 1, null)));
            yield return new KeyValuePair<string, Action<UpdaterForm>>("failed", form => form.ShowResult(new SetupResult(SetupOutcome.Failed, 7, @"C:\example\update-setup.log")));
            yield return new KeyValuePair<string, Action<UpdaterForm>>("error", form => form.ShowError("Couldn't reach the update server. Check your internet connection.", () => { }));
        }

        private static UpdaterForm OffscreenForm()
        {
            var form = new UpdaterForm(@"C:\Program Files\DLB Precision Monitor", null, Path.GetTempPath(), startCheck: false)
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000),
                Opacity = 0
            };
            form.Show();
            return form;
        }

        private static void RenderPreviews(string directory)
        {
            Directory.CreateDirectory(directory);
            foreach (KeyValuePair<string, Action<UpdaterForm>> state in States())
                using (UpdaterForm form = OffscreenForm())
                {
                    state.Value(form);
                    form.PerformLayout();
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                        bitmap.Save(Path.Combine(directory, "updater-" + state.Key + ".png"), ImageFormat.Png);
                    }
                }
        }

        private static void RunSmokeTests(string? reportPath)
        {
            var results = new List<string>();
            Action<bool, string> verify = (passed, description) => { if (!passed) throw new InvalidOperationException(description); results.Add("PASS " + description); };
            Control Find(Form form, string name) => form.Controls.Find(name, true).Single();

            using (UpdaterForm form = OffscreenForm())
            {
                form.ShowChecking();
                verify(!Find(form, "Primary").Visible && Find(form, "Secondary").Enabled && Find(form, "Secondary").Text == "Close",
                    "Checking can be closed and offers nothing to install");
                form.ShowUpToDate("0.1.8");
                verify(Find(form, "Status").Text.Contains("up to date (0.1.8)") && !Find(form, "Notes").Visible, "Up to date names the installed version");
                form.ShowAvailable(SampleOffer());
                verify(Find(form, "Primary").Visible && Find(form, "Primary").Text == "Update now" && Find(form, "Secondary").Text == "Not now"
                    && Find(form, "Notes").Visible && Find(form, "Notes").Text.Contains("• Faster startup") && Find(form, "Status").Text.Contains("0.1.9"),
                    "An available update shows its version and plain-text notes with Update now and Not now");
                form.ShowDownloading(3_717_056, 7_434_112);
                verify(Find(form, "Progress").Visible && ((ProgressStrip)Find(form, "Progress")).Value == 500 && Find(form, "ProgressText").Text == "3.5 of 7.1 MB"
                    && Find(form, "Secondary").Text == "Cancel" && Find(form, "Secondary").Enabled, "Downloading shows progress and can be cancelled");
                form.ShowVerifying();
                verify(!Find(form, "Secondary").Enabled && !Find(form, "Primary").Visible, "Verification cannot be interrupted halfway");
                form.ShowInstalling();
                verify(!Find(form, "Secondary").Enabled && Find(form, "Status").Text.Contains("click Yes"), "Installing explains the Windows prompt and cannot be closed");
                form.ShowResult(new SetupResult(SetupOutcome.Failed, 7, @"C:\example\update-setup.log"));
                verify(Find(form, "Details").Visible && Find(form, "Status").Text.Contains("code 7") && Find(form, "Secondary").Enabled,
                    "A failed update offers the setup log");
                form.ShowResult(new SetupResult(SetupOutcome.Cancelled, 1, null));
                verify(!Find(form, "Details").Visible && Find(form, "Status").Text.Contains("Nothing was changed"), "A cancelled update says nothing changed");
                form.ShowError("Couldn't reach the update server.", () => { });
                verify(Find(form, "Primary").Visible && Find(form, "Primary").Text == "Try again", "Errors offer Try again");
            }
            verify(Quote(@"C:\Program Files\DLB Precision Monitor\") == "\"C:\\Program Files\\DLB Precision Monitor\"",
                "Install folders are quoted without a trailing backslash that would break the command line");
            verify(UpdaterForm.Display(new Version(0, 1, 8, 0)) == "0.1.8" && UpdaterForm.Display(new Version(0, 1, 7, 9)) == "0.1.7.9",
                "Versions display as three parts, keeping a test build's fourth part");
            verify(UpdaterForm.KeptLogPath.EndsWith(@"DLBPrecision\Monitor\update-setup.log", StringComparison.OrdinalIgnoreCase),
                "A failed update's setup log is kept with the monitor's settings");
            verify(IsRelocatedFolder(Path.Combine(Path.GetTempPath(), FolderPrefix + Guid.NewGuid().ToString("N")))
                && !IsRelocatedFolder(@"C:\Program Files\DLB Precision Monitor")
                && !IsRelocatedFolder(Path.Combine(Path.GetTempPath(), FolderPrefix + "evil"))
                && !IsRelocatedFolder(Path.Combine(Path.GetTempPath(), "nested", FolderPrefix + Guid.NewGuid().ToString("N"))),
                "Only this user's own DLB update folders count as temporary copies, so cleanup can never delete anything else");
            bool quoteRefused = false;
            try { Quote("C:\\x\" --other"); } catch (ArgumentException) { quoteRefused = true; }
            verify(quoteRefused, "A quote in a path or feed address cannot add command-line arguments");
            string report = results.Count + " checks passed. No network, setup or install folder was used.\n" + string.Join("\n", results);
            if (reportPath != null) File.WriteAllText(reportPath, report);
        }
    }
}
