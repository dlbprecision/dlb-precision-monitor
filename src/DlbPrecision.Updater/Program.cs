using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

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

                string? feed = Value(args, "--feed");
                string installDirectory = Value(args, "--install-dir") ?? AppDomain.CurrentDomain.BaseDirectory;
                string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
                string mutexName = @"Local\DLBPrecision.Updater." + user;
                if (!args.Contains("--relocated"))
                {
                    if (Mutex.TryOpenExisting(mutexName, out Mutex? existing)) { existing.Dispose(); FocusExistingWindow(); return 0; }
                    Relocate(installDirectory, feed);
                    return 0;
                }
                using (var instance = new Mutex(true, mutexName, out bool created))
                {
                    if (!created) { FocusExistingWindow(); return 0; }
                    using (var form = new UpdaterForm(installDirectory, feed, AppDomain.CurrentDomain.BaseDirectory)) Application.Run(form);
                    instance.ReleaseMutex();
                }
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
            string arguments = "--relocated --install-dir " + Quote(installDirectory) + (feed != null ? " --feed " + Quote(feed) : "");
            using (Process.Start(new ProcessStartInfo(copy, arguments) { UseShellExecute = false, WorkingDirectory = folder })) { }
        }

        // A running copy cannot delete itself, so each check removes copies left by earlier checks.
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
        internal static string Quote(string value) => "\"" + value.TrimEnd('\\') + "\"";

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
            string report = results.Count + " checks passed. No network, setup or install folder was used.\n" + string.Join("\n", results);
            if (reportPath != null) File.WriteAllText(reportPath, report);
        }
    }
}
