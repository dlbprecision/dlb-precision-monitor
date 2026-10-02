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
                if (args.Length >= 4 && args[0] == "--verify-package") return VerifyForRelease(args[1], args[2], args[3], args.Contains("--test-build"));
                // Keep this exact form in every future version: after an update, the old temporary copy
                // asks the newly installed updater to remove the copy's folder with these arguments.
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
                else if (args.Length >= 1 && args[0] == "--verify-package") return 3;
                else MessageBox.Show("DLB Precision Monitor could not check for updates.\n\n" + error.Message, UpdaterForm.WindowTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        // The release build runs the updater's own checks on the signed setup, so a release that every
        // installed copy would refuse fails the build instead of reaching customers.
        // Exit 0: accepted. 2: installed updaters would refuse the setup. 3: the check itself failed. Never shows a window.
        internal static int VerifyForRelease(string setupPath, string version, string reportPath, bool testBuild)
        {
            string outcome;
            int code;
            try
            {
                VerificationResult result = PackageVerifier.VerifyPackage(Path.GetFullPath(setupPath), version, testBuild);
                outcome = result.Ok ? "ACCEPTED " + Path.GetFileName(setupPath) + " as version " + version : "REJECTED " + result.Reason;
                code = result.Ok ? 0 : 2;
            }
            catch (Exception error)
            {
                outcome = "ERROR " + error.GetType().Name + ": " + error.Message;
                code = 3;
            }
            try { File.WriteAllText(reportPath, outcome); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is NotSupportedException)
            {
                return 3; // The build reports the missing report.
            }
            return code;
        }

        // Setup replaces every file in the install folder, so the updater waits for it from a private copy.
        private static void Relocate(string installDirectory, string? feed)
        {
            string ownFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (Path.GetFileName(ownFolder.TrimEnd('\\')).StartsWith(FolderPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The updater's temporary copy is in an unexpected place, so it will not copy itself again.");
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
            if (Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return; // never follow a link
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
                // Above an always-on-top Settings window too, not just in front of normal windows.
                NativeMethods.SetWindowPos(window, NativeMethods.TopMostWindow, 0, 0, 0, 0, NativeMethods.KeepPositionAndSize);
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
            yield return new KeyValuePair<string, Action<UpdaterForm>>("failed", form => form.ShowResult(new SetupResult(SetupOutcome.Failed, 4, @"C:\example\update-setup.log")));
            yield return new KeyValuePair<string, Action<UpdaterForm>>("error", form => form.ShowError("Couldn't reach the update server. Check your internet connection.", () => { }));
            yield return new KeyValuePair<string, Action<UpdaterForm>>("cannot-proceed", form => form.ShowResult(new SetupResult(SetupOutcome.CannotProceed, 7,
                @"C:\example\update-setup.log", detail: "The bundled sensor driver installer could not start. Restart Windows and retry this DLB installer. If it still fails, send DLB the setup log.")));
            yield return new KeyValuePair<string, Action<UpdaterForm>>("service-down", form => form.ShowResult(new SetupResult(SetupOutcome.UpdatedServiceNotRunning, 21, @"C:\example\update-setup.log")));
        }

        // Every visible control sits inside the window, no two overlap, and no text is cut off.
        internal static List<string> LayoutProblems(Form form)
        {
            var problems = new List<string>();
            var visible = form.Controls.Cast<Control>().Where(control => control.Visible).ToList();
            Rectangle client = form.ClientRectangle;
            foreach (Control control in visible)
            {
                string name = control.Name.Length > 0 ? control.Name : control.Text;
                if (!client.Contains(control.Bounds)) problems.Add(name + " " + control.Bounds + " is outside the window " + client.Size);
                foreach (Control other in visible)
                    if (string.CompareOrdinal(control.Name + control.Text, other.Name + other.Text) < 0 && control.Bounds.IntersectsWith(other.Bounds))
                        problems.Add(name + " overlaps " + (other.Name.Length > 0 ? other.Name : other.Text));
                if (control.Text.Length == 0 || control is TextBox) continue;
                if (control is Button)
                {
                    Size text = TextRenderer.MeasureText(control.Text, control.Font);
                    if (text.Width + 12 > control.Width || text.Height + 4 > control.Height) problems.Add("button " + name + " " + control.Size + " cuts off its text " + text);
                }
                else if (!control.AutoSize)
                {
                    Size text = TextRenderer.MeasureText(control.Text, control.Font, new Size(control.Width, 0), TextFormatFlags.WordBreak);
                    if (text.Height > control.Height) problems.Add(name + " " + control.Size + " needs " + text.Height + "px of height");
                }
            }
            return problems;
        }

        private static void ScaleFonts(Form form, float scale)
        {
            form.Font = new Font("Segoe UI", 9.5f * scale);
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

        // Presses Enter the way a person does: through the window's message loop, on the focused control.
        private static void PressEnter(Form form)
        {
            Control? focused = form.ActiveControl;
            IntPtr target = focused != null ? focused.Handle : form.Handle;
            NativeMethods.PostMessage(target, 0x0100, new IntPtr(0x0D), new IntPtr(0x001C0001)); // WM_KEYDOWN VK_RETURN
            NativeMethods.PostMessage(target, 0x0101, new IntPtr(0x0D), new IntPtr(unchecked((int)0xC01C0001))); // WM_KEYUP
            for (int pump = 0; pump < 10; pump++) { Application.DoEvents(); Thread.Sleep(10); }
        }

        private static void RunSmokeTests(string? reportPath)
        {
            var results = new List<string>();
            Action<bool, string> verify = (passed, description) => { if (!passed) throw new InvalidOperationException(description); results.Add("PASS " + description); };
            Control Find(Form form, string name) => form.Controls.Find(name, true).Single();

            // The real window: built, then shown centred by Windows, as the updater does.
            using (var form = new UpdaterForm(@"C:\Program Files\DLB Precision Monitor", null, Path.GetTempPath(), startCheck: false) { ShowInTaskbar = false, Opacity = 0 })
            {
                verify(!form.IsHandleCreated, "The window is not created before it is shown, so Windows centres it at its real size");
                form.Show();
                Application.DoEvents();
                Rectangle area = Screen.FromControl(form).WorkingArea;
                Point centre = new Point(form.Left + form.Width / 2, form.Top + form.Height / 2);
                Point expected = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
                verify(Math.Abs(centre.X - expected.X) <= 2 && Math.Abs(centre.Y - expected.Y) <= 2,
                    "The updater opens centred on its screen (centre " + centre + ", screen centre " + expected + ")");
                form.ShowChecking();
                Find(form, "Secondary").Focus();
                PressEnter(form);
                verify(!form.IsDisposed && form.Visible, "Enter while checking does nothing, even when Close has keyboard focus in the shown window");
                form.ShowAvailable(SampleOffer());
                verify(form.ActiveControl == Find(form, "Primary"), "In the shown window, Update now has focus when an update is offered");
                form.ShowDownloading(1_000_000, 7_434_112);
                Find(form, "Secondary").Focus();
                PressEnter(form);
                verify(!form.IsDisposed && Find(form, "Status").Text.StartsWith("Downloading"), "Enter during a download never presses Cancel, even when it has focus");
                form.ShowInstalling();
                verify(!form.TopMost, "While setup runs, the updater is not always on top, so setup's own windows can appear above it");
                form.ShowResult(new SetupResult(SetupOutcome.Failed, 4, @"C:\example\update-setup.log"));
                verify(form.TopMost, "The result is always on top again, so it is not hidden behind an always-on-top Settings window");
                form.Close();
            }
            Rectangle narrow = new Rectangle(1000, 0, 480, 1920);
            verify(UpdaterForm.KeepOnScreen(new Rectangle(1090, 100, 496, 369), narrow) == new Rectangle(1000, 100, 496, 369)
                && UpdaterForm.KeepOnScreen(new Rectangle(1000, 1700, 400, 369), narrow) == new Rectangle(1000, 1551, 400, 369)
                && UpdaterForm.KeepOnScreen(new Rectangle(1010, 20, 300, 200), narrow) == new Rectangle(1010, 20, 300, 200),
                "A window wider or taller than the room left on a narrow display is moved back on screen, left edge first");
            string verifyFolder = Path.Combine(Path.GetTempPath(), "DlbVerifyRelease-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(verifyFolder);
            try
            {
                string setup = Path.Combine(verifyFolder, "DLB-Precision-Monitor-0.1.9-Setup.exe");
                File.WriteAllText(setup, "MZ not a real setup");
                File.WriteAllText(setup + ".sha256", new string('0', 64) + "  DLB-Precision-Monitor-0.1.9-Setup.exe\n");
                string verifyReport = Path.Combine(verifyFolder, "report.txt");
                verify(VerifyForRelease(setup, "0.1.9", verifyReport, testBuild: false) == 2 && File.ReadAllText(verifyReport).StartsWith("REJECTED"),
                    "The release check reports a refused setup with exit code 2");
                int crashed;
                using (new FileStream(setup, FileMode.Open, FileAccess.Read, FileShare.None))
                    crashed = VerifyForRelease(setup, "0.1.9", verifyReport, testBuild: false);
                verify(crashed == 3 && File.ReadAllText(verifyReport).StartsWith("ERROR"),
                    "A release check that cannot read the setup reports its own error (exit 3) instead of blaming the setup or showing a window");
                verify(VerifyForRelease(setup, "0.1.9", Path.Combine(verifyFolder, "missing", "report.txt"), testBuild: false) == 3,
                    "A release check that cannot write its report still exits without showing a window");
            }
            finally { Directory.Delete(verifyFolder, true); }

            using (UpdaterForm form = OffscreenForm())
            {
                verify(form.TopMost, "The updater opens above an always-on-top Settings window instead of hidden behind it");
                form.ShowChecking();
                verify(!Find(form, "Primary").Visible && Find(form, "Secondary").Enabled && Find(form, "Secondary").Text == "Close",
                    "Checking can be closed and offers nothing to install");
                verify(form.AcceptButton == null && form.ActiveControl == null, "Enter does nothing while checking, so a key meant for another window can't close it");
                form.ShowUpToDate("0.1.8");
                verify(Find(form, "Status").Text.Contains("up to date (0.1.8)") && !Find(form, "Notes").Visible, "Up to date names the installed version");
                form.ShowAvailable(SampleOffer());
                verify(Find(form, "Primary").Visible && Find(form, "Primary").Text == "Update now" && Find(form, "Secondary").Text == "Not now"
                    && Find(form, "Notes").Visible && Find(form, "Notes").Text.Contains("• Faster startup") && Find(form, "Status").Text.Contains("0.1.9"),
                    "An available update shows its version and plain-text notes with Update now and Not now");
                verify(form.AcceptButton == Find(form, "Primary") && form.ActiveControl == Find(form, "Primary"), "Enter on an offered update means Update now");
                form.ShowDownloading(3_717_056, 7_434_112);
                verify(Find(form, "Progress").Visible && ((ProgressStrip)Find(form, "Progress")).Value == 500 && Find(form, "ProgressText").Text == "3.5 of 7.1 MB"
                    && Find(form, "Secondary").Text == "Cancel" && Find(form, "Secondary").Enabled, "Downloading shows progress and can be cancelled");
                verify(form.AcceptButton == null && form.ActiveControl == null, "A second Enter after Update now can't cancel the download");
                form.ShowVerifying();
                verify(!Find(form, "Secondary").Enabled && !Find(form, "Primary").Visible, "Verification cannot be interrupted halfway");
                form.ShowInstalling();
                verify(!Find(form, "Secondary").Enabled && Find(form, "Status").Text.Contains("click Yes"), "Installing explains the Windows prompt and cannot be closed");
                form.ShowResult(new SetupResult(SetupOutcome.Failed, 4, @"C:\example\update-setup.log"));
                verify(Find(form, "Details").Visible && Find(form, "Status").Text.Contains("code 4") && Find(form, "Secondary").Enabled
                    && form.AcceptButton == Find(form, "Secondary"), "A failed update offers the setup log, and Enter closes the result");
                form.ShowResult(new SetupResult(SetupOutcome.CannotProceed, 7, @"C:\example\update-setup.log", detail: "DLB Precision Sensors did not stop."));
                verify(Find(form, "Status").Text.Contains("DLB Precision Sensors did not stop.") && !Find(form, "Status").Text.Contains("reopened"),
                    "Setup's own reason for refusing is shown");
                form.ShowResult(new SetupResult(SetupOutcome.Cancelled, 1, null));
                verify(!Find(form, "Details").Visible && Find(form, "Status").Text.Contains("Nothing was changed"), "A cancelled update says nothing changed");
                form.ShowError("Couldn't reach the update server.", () => { });
                verify(Find(form, "Primary").Visible && Find(form, "Primary").Text == "Try again", "Errors offer Try again");
            }
            foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
                foreach (KeyValuePair<string, Action<UpdaterForm>> state in States())
                    using (UpdaterForm form = OffscreenForm())
                    {
                        ScaleFonts(form, scale);
                        state.Value(form);
                        List<string> problems = LayoutProblems(form);
                        verify(problems.Count == 0, "At " + scale * 100 + "% the " + state.Key + " window fits its text" + (problems.Count > 0 ? ": " + string.Join("; ", problems) : ""));
                        verify(form.ClientSize.Width >= 460 * scale, "At " + scale * 100 + "% the " + state.Key + " window grows with its text (" + form.ClientSize + ")");
                    }
            using (UpdaterForm form = OffscreenForm())
            {
                form.ShowError(string.Join(" ", Enumerable.Repeat("A very long message that has to wrap onto several lines.", 6)), () => { });
                List<string> problems = LayoutProblems(form);
                verify(problems.Count == 0, "A long message makes the window taller instead of cutting it off" + (problems.Count > 0 ? ": " + string.Join("; ", problems) : ""));
                verify(form.Height <= Screen.FromControl(form).WorkingArea.Height || Find(form, "Notes").Visible, "The window stays within the screen");
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
