using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DlbPrecision.Updater
{
    // Windows' themed progress bar is always green; this keeps the DLB blue.
    internal sealed class ProgressStrip : Control
    {
        private int value;

        public ProgressStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public int Value
        {
            get => value;
            set { this.value = Math.Max(0, Math.Min(1000, value)); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            args.Graphics.Clear(BackColor);
            using (var fill = new SolidBrush(ForeColor))
                args.Graphics.FillRectangle(fill, 0, 0, Width * value / 1000, Height);
        }
    }

    internal sealed class UpdaterForm : Form
    {
        public const string WindowTitle = "DLB Precision Monitor · Update";
        private static readonly Color Background = Color.FromArgb(23, 23, 30);
        private static readonly Color Foreground = Color.FromArgb(232, 229, 240);
        private static readonly Color Muted = Color.FromArgb(163, 158, 178);
        private static readonly Color Blue = ColorTranslator.FromHtml("#2DA4F4");
        private static readonly Color Warning = Color.FromArgb(245, 169, 179);
        private static readonly string UserAgent = "DLBPrecisionMonitor-Updater/" + typeof(UpdaterForm).Assembly.GetName().Version.ToString(3);

        private readonly string installDirectory;
        private readonly string? feed;
        private readonly string workingFolder;
        private readonly bool startCheck;
        private readonly Font bodyFont = new Font("Segoe UI", 9.5f);
        private readonly Font titleFont = new Font("Segoe UI Semibold", 15);
        private readonly Label status = new Label { Name = "Status", AutoSize = false };
        private readonly TextBox notes = new TextBox { Name = "Notes", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle };
        private readonly ProgressStrip progress = new ProgressStrip { Name = "Progress" };
        private readonly Label progressText = new Label { Name = "ProgressText", AutoSize = false };
        private readonly LinkLabel details = new LinkLabel { Name = "Details", Text = "Show details", AutoSize = true };
        private readonly Button primary = new Button { Name = "Primary", FlatStyle = FlatStyle.Flat };
        private readonly Button secondary = new Button { Name = "Secondary", FlatStyle = FlatStyle.Flat };
        private readonly System.Windows.Forms.Timer closeTimer = new System.Windows.Forms.Timer { Interval = 4000 };
        private Action? primaryAction;
        private Action? secondaryAction;
        private CancellationTokenSource? cancellation;
        private UpdateOffer? offer;
        private string? keptLog;
        private bool installing;
        private bool ownedResourcesDisposed;

        public UpdaterForm(string installDirectory, string? feed, string workingFolder, bool startCheck = true)
        {
            this.installDirectory = installDirectory;
            this.feed = feed;
            this.workingFolder = workingFolder;
            this.startCheck = startCheck;
            Text = WindowTitle;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = bodyFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(480, 330);
            BackColor = Background;
            ForeColor = Foreground;

            Controls.Add(new Label { Text = "DLB Precision Monitor update", Font = titleFont, AutoSize = true, Location = new Point(24, 18) });
            status.SetBounds(26, 58, 430, 46);
            notes.SetBounds(26, 108, 428, 150);
            notes.BackColor = Color.FromArgb(17, 17, 22);
            notes.ForeColor = Foreground;
            progress.SetBounds(26, 112, 428, 12);
            progress.BackColor = Color.FromArgb(53, 49, 63);
            progress.ForeColor = Blue;
            progressText.SetBounds(26, 136, 428, 22);
            progressText.ForeColor = Muted;
            details.SetBounds(26, 272, 120, 22);
            details.LinkColor = Blue;
            details.ActiveLinkColor = Blue;
            details.LinkClicked += (sender, args) => OpenLog();
            primary.SetBounds(242, 276, 104, 34);
            primary.BackColor = Blue;
            primary.ForeColor = Color.FromArgb(10, 15, 22);
            primary.FlatAppearance.BorderSize = 0;
            primary.Click += (sender, args) => primaryAction?.Invoke();
            secondary.SetBounds(356, 276, 102, 34);
            secondary.Click += (sender, args) => secondaryAction?.Invoke();
            Controls.AddRange(new Control[] { status, notes, progress, progressText, details, primary, secondary });
            closeTimer.Tick += (sender, args) => { closeTimer.Stop(); Close(); };
            ShowChecking();
        }

        public static string KeptLogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DLBPrecision", "Monitor", "update-setup.log");

        protected override void OnShown(EventArgs args)
        {
            base.OnShown(args);
            if (startCheck) StartCheck();
        }

        protected override void OnFormClosing(FormClosingEventArgs args)
        {
            // Setup is already running; closing now would only lose its result.
            if (installing && args.CloseReason == CloseReason.UserClosing) { args.Cancel = true; return; }
            cancellation?.Cancel();
            base.OnFormClosing(args);
        }

        internal void ShowChecking() =>
            Present("Checking for updates…", Foreground, showNotes: false, showProgress: false, primaryText: null, secondaryText: "Close", secondaryClick: Close);

        internal void ShowUpToDate(string installedVersion) =>
            Present("You're up to date (" + installedVersion + ").", Foreground, false, false, null, "Close", Close);

        internal void ShowAvailable(UpdateOffer available)
        {
            offer = available;
            notes.Text = available.Title + (available.Notes.Length > 0 ? "\r\n\r\n" + available.Notes : "");
            Present("Version " + available.VersionText + " is available. Your monitor will close and reopen while it installs.", Foreground,
                true, false, "Update now", "Not now", Close, StartUpdate);
        }

        internal void ShowDownloading(long done, long total)
        {
            progress.Value = total > 0 ? (int)Math.Min(1000, done * 1000 / total) : 0;
            progressText.Text = (done / 1048576.0).ToString("0.0") + " of " + (total / 1048576.0).ToString("0.0") + " MB";
            Present("Downloading the update…", Foreground, false, true, null, "Cancel", () => cancellation?.Cancel());
        }

        internal void ShowVerifying()
        {
            Present("Checking the download is genuine…", Foreground, false, false, null, "Cancel", null);
            secondary.Enabled = false;
        }

        internal void ShowInstalling()
        {
            Present("Installing the update. If Windows asks for permission, click Yes. Your monitor will close and reopen.", Foreground,
                false, false, null, "Close", null);
            secondary.Enabled = false;
        }

        internal void ShowResult(SetupResult result)
        {
            bool updated = result.Outcome == SetupOutcome.Updated || result.Outcome == SetupOutcome.UpdatedRestartNeeded
                || result.Outcome == SetupOutcome.UpdatedReenableStartup;
            keptLog = result.KeptLog;
            Present(SetupRunner.Message(result.Outcome, result.ExitCode), updated ? Blue : Warning, false, false, null, "Close", Close);
            details.Visible = keptLog != null;
            if (result.Outcome == SetupOutcome.Updated) closeTimer.Start();
        }

        internal void ShowError(string message, Action? retry) =>
            Present(message, Warning, false, false, retry != null ? "Try again" : null, "Close", Close, retry);

        private void Present(string message, Color color, bool showNotes, bool showProgress, string? primaryText, string secondaryText,
            Action? secondaryClick, Action? primaryClick = null)
        {
            if (IsDisposed) return;
            status.Text = message;
            status.ForeColor = color;
            notes.Visible = showNotes;
            progress.Visible = progressText.Visible = showProgress;
            details.Visible = false;
            primary.Visible = primaryText != null;
            primary.Text = primaryText ?? "";
            primary.Enabled = true;
            primaryAction = primaryClick;
            secondary.Text = secondaryText;
            secondary.Enabled = true;
            secondaryAction = secondaryClick;
            AcceptButton = primary.Visible ? primary : secondary;
        }

        private async void StartCheck()
        {
            ShowChecking();
            if (!TryInstalledVersion(out Version installed))
            {
                ShowError("DLB Precision Monitor was not found in " + installDirectory + ". Reinstall it, then try again.", null);
                return;
            }
            string source = feed ?? ReleaseFeed.LatestUrl;
            FeedResult result = await Task.Run(() => ReleaseFeed.Fetch(source, UserAgent));
            if (IsDisposed) return;
            if (result.Status == FeedStatus.NoRelease) { ShowUpToDate(Display(installed)); return; }
            if (result.Status != FeedStatus.Release) { ShowError(result.Message, StartCheck); return; }
            bool testFeed = feed != null;
            UpdateDecision decision = UpdateOffer.Decide(result.Release, installed, allowPrerelease: testFeed, allowFileUrls: testFeed && ReleaseFeed.IsLocal(source));
            if (decision.Status == OfferStatus.UpToDate) ShowUpToDate(Display(installed));
            else if (decision.Offer == null) ShowError("This update isn't available yet. Try again later.", StartCheck);
            else ShowAvailable(decision.Offer);
        }

        private async void StartUpdate()
        {
            UpdateOffer? available = offer;
            if (available == null) return;
            cancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            string installer = Path.Combine(workingFolder, available.Installer.Name);
            string checksum = Path.Combine(workingFolder, available.Checksum.Name);
            bool localFeed = feed != null && ReleaseFeed.IsLocal(feed);
            FileStream? package = null;
            try
            {
                DeleteDownloads(installer, checksum);
                ShowDownloading(0, available.Installer.Size);
                int shown = -1;
                await Task.Run(() =>
                {
                    Downloader.Download(new Uri(available.Checksum.DownloadUrl), checksum, available.Checksum.Size, localFeed, null, token, UserAgent);
                    Downloader.Download(new Uri(available.Installer.DownloadUrl), installer, available.Installer.Size, localFeed, bytes =>
                    {
                        int permille = (int)(bytes * 1000 / available.Installer.Size);
                        if (permille / 5 == shown / 5) return;
                        shown = permille;
                        PostToWindow(() => { if (progress.Visible) ShowDownloading(bytes, available.Installer.Size); });
                    }, token, UserAgent);
                }, token);

                ShowVerifying();
                if (!ChecksumFile.TryParse(File.ReadAllText(checksum), available.Installer.Name, out string expected))
                {
                    ShowError("The update's checksum file couldn't be read, so it wasn't installed.", null);
                    return;
                }
                package = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
                FileStream locked = package;
                VerificationResult verdict = await Task.Run(() => PackageVerifier.Verify(locked, installer, expected, available.VersionText));
                if (!verdict.Ok) { ShowError(verdict.Reason, null); return; }

                installing = true;
                ShowInstalling();
                SetupResult result = await Task.Run(() => SetupRunner.Run(installer, installDirectory, workingFolder, KeptLogPath));
                installing = false;
                ShowResult(result);
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed) ShowError("Download cancelled. Nothing was changed.", StartUpdate);
            }
            catch (WebException)
            {
                ShowError("The download stopped. Check your internet connection, then try again. Nothing was changed.", StartUpdate);
            }
            catch (InvalidDataException error)
            {
                ShowError(error.Message + " Nothing was changed.", StartUpdate);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                ShowError("The update couldn't be saved on this PC (" + error.Message + "). Nothing was changed.", StartUpdate);
            }
            finally
            {
                installing = false;
                package?.Dispose();
                DeleteDownloads(installer, checksum);
            }
        }

        // Progress arrives on a download thread; the window may already be closing.
        private void PostToWindow(Action action)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(() => { if (!IsDisposed) action(); })); }
            catch (Exception error) when (error is InvalidOperationException || error is ObjectDisposedException)
            {
                // The window closed between the check and the post; the download is being cancelled.
            }
        }

        private bool TryInstalledVersion(out Version installed)
        {
            installed = new Version(0, 0, 0, 0);
            string text = SetupRunner.InstalledVersion(Path.Combine(installDirectory, SetupRunner.MonitorExecutable));
            if (text.Length == 0 || !Version.TryParse(text, out Version? parsed) || parsed == null) return false;
            installed = parsed;
            return true;
        }

        internal static string Display(Version version) =>
            version.Revision > 0 ? version.ToString(4) : version.ToString(3);

        private static void DeleteDownloads(params string[] files)
        {
            foreach (string file in files)
            {
                try { if (File.Exists(file)) File.Delete(file); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    // Left in the private temporary folder, which the next check removes.
                }
            }
        }

        private void OpenLog()
        {
            if (keptLog == null || !File.Exists(keptLog)) return;
            try { using (Process.Start(new ProcessStartInfo("notepad.exe", "\"" + keptLog + "\"") { UseShellExecute = false })) { } }
            catch (Win32Exception error) { ShowError("The setup log couldn't be opened: " + error.Message, null); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !ownedResourcesDisposed)
            {
                ownedResourcesDisposed = true;
                closeTimer.Dispose();
                cancellation?.Dispose();
                try { base.Dispose(disposing); }
                finally { bodyFont.Dispose(); titleFont.Dispose(); }
            }
            else base.Dispose(disposing);
        }
    }
}
