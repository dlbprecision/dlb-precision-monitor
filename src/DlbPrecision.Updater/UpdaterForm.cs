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
        // The layout below is drawn for Segoe UI 9.5 pt at 100% display scaling, which is 17 pixels tall.
        private const float DesignFontHeight = 17f;
        private const float BodyPoints = 9.5f, TitlePoints = 15f;

        private readonly string installDirectory;
        private readonly string? feed;
        private readonly string workingFolder;
        private readonly bool startCheck;
        private readonly Font bodyFont = new Font("Segoe UI", BodyPoints);
        private Font titleFont = new Font("Segoe UI Semibold", TitlePoints);
        private readonly Icon? windowIcon = LoadIcon();
        private readonly Label title = new Label { Name = "Title", Text = "DLB Precision Monitor update", AutoSize = false };
        private readonly Label status = new Label { Name = "Status", AutoSize = false };
        private readonly TextBox notes = new TextBox { Name = "Notes", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle };
        private readonly ProgressStrip progress = new ProgressStrip { Name = "Progress" };
        private readonly Label progressText = new Label { Name = "ProgressText", AutoSize = false };
        private readonly LinkLabel details = new LinkLabel { Name = "Details", Text = "Show details", AutoSize = false };
        private readonly Button primary = new Button { Name = "Primary", FlatStyle = FlatStyle.Flat };
        private readonly Button secondary = new Button { Name = "Secondary", FlatStyle = FlatStyle.Flat };
        private readonly System.Windows.Forms.Timer closeTimer = new System.Windows.Forms.Timer { Interval = 4000 };
        private Action? primaryAction;
        private Action? secondaryAction;
        private CancellationTokenSource? cancellation;
        private UpdateOffer? offer;
        private string? keptLog;
        private bool installing;
        private bool downloading;
        private bool ready;
        private bool arranging;
        private bool ownedResourcesDisposed;

        // What Windows actually shows, which is what matters for being above other windows.
        internal bool AlwaysOnTop => IsHandleCreated ? (NativeMethods.GetWindowLong(Handle, -20) & 0x8) != 0 : TopMost;

        public UpdaterForm(string installDirectory, string? feed, string workingFolder, bool startCheck = true)
        {
            this.installDirectory = installDirectory;
            this.feed = feed;
            this.workingFolder = workingFolder;
            this.startCheck = startCheck;
            Text = WindowTitle;
            if (windowIcon != null) Icon = windowIcon;
            // Arrange() sizes everything from the font, which Windows already scales for the display,
            // so WinForms must not scale the bounds a second time.
            AutoScaleMode = AutoScaleMode.None;
            Font = bodyFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            // A short window the person just asked for. Settings can be always on top, and the updater must
            // never open hidden behind it.
            TopMost = true;
            BackColor = Background;
            ForeColor = Foreground;

            title.Font = titleFont;
            notes.BackColor = Color.FromArgb(17, 17, 22);
            notes.ForeColor = Foreground;
            progress.BackColor = Color.FromArgb(53, 49, 63);
            progress.ForeColor = Blue;
            progressText.ForeColor = Muted;
            details.LinkColor = Blue;
            details.ActiveLinkColor = Blue;
            details.LinkClicked += (sender, args) => OpenLog();
            primary.BackColor = Blue;
            primary.ForeColor = Color.FromArgb(10, 15, 22);
            primary.FlatAppearance.BorderSize = 0;
            primary.Click += (sender, args) => primaryAction?.Invoke();
            secondary.Click += (sender, args) => secondaryAction?.Invoke();
            Controls.AddRange(new Control[] { title, status, notes, progress, progressText, details, primary, secondary });
            closeTimer.Tick += (sender, args) => { closeTimer.Stop(); Close(); };
            ready = true;
            ShowChecking();
        }

        public static string KeptLogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DLBPrecision", "Monitor", "update-setup.log");

        protected override void OnShown(EventArgs args)
        {
            base.OnShown(args);
            if (WindowState == FormWindowState.Normal) Bounds = KeepOnScreen(Bounds, Screen.FromControl(this).WorkingArea);
            if (startCheck) StartCheck();
        }

        // Enter only ever runs the screen's main action. While checking, downloading or installing there is
        // none, so Enter does nothing even if a button has keyboard focus: a repeated or stray Enter can't
        // cancel a download or close the window.
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Enter && AcceptButton == null) return true;
            return base.ProcessDialogKey(keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs args)
        {
            // Setup is already running; closing now would only lose its result.
            if (installing && args.CloseReason == CloseReason.UserClosing) { args.Cancel = true; return; }
            cancellation?.Cancel();
            base.OnFormClosing(args);
        }

        // Moving to a display with different scaling changes the font; everything else follows it.
        protected override void OnFontChanged(EventArgs args)
        {
            base.OnFontChanged(args);
            Arrange();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs args)
        {
            base.OnDpiChanged(args);
            Arrange();
        }

        internal void ShowChecking() =>
            Present("Checking for updates…", Foreground, showNotes: false, showProgress: false, primaryText: null, secondaryText: "Close", secondaryClick: Close);

        internal void ShowUpToDate(string installedVersion) =>
            Present("You're up to date (" + installedVersion + ").", Foreground, false, false, null, "Close", Close, enterCloses: true);

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
            if (downloading) return;
            Present("Downloading the update…", Foreground, false, true, null, "Cancel", () => cancellation?.Cancel());
            downloading = true;
        }

        internal void ShowVerifying()
        {
            Present("Checking the download is genuine…", Foreground, false, false, null, "Cancel", null);
            secondary.Enabled = false;
        }

        internal void ShowInstalling()
        {
            // Setup's own windows must not be covered while it runs.
            SetAlwaysOnTop(false);
            Present("Installing the update. If Windows asks for permission, click Yes. Your monitor will close and reopen.", Foreground,
                false, false, null, "Close", null);
            secondary.Enabled = false;
        }

        internal void ShowResult(SetupResult result)
        {
            SetAlwaysOnTop(true);
            if (result.Outcome == SetupOutcome.InUseByAnotherUser) { ShowError(SetupRunner.Message(result), StartUpdate); return; }
            bool good = SetupRunner.IsUpdated(result.Outcome) && result.Outcome != SetupOutcome.UpdatedServiceNotRunning;
            keptLog = result.KeptLog;
            Present(SetupRunner.Message(result), good ? Blue : Warning, false, false, null, "Close", Close, enterCloses: true);
            details.Visible = keptLog != null;
            if (result.Outcome == SetupOutcome.Updated) closeTimer.Start();
        }

        internal void ShowError(string message, Action? retry)
        {
            SetAlwaysOnTop(true);
            Present(message, Warning, false, false, retry != null ? "Try again" : null, "Close", Close, retry, enterCloses: true);
        }

        // Once the window exists, z-order changes never activate it: WinForms' TopMost property would, and
        // could pull the result in front of a game the person went back to during the install.
        private void SetAlwaysOnTop(bool on)
        {
            if (!IsHandleCreated) { TopMost = on; return; }
            if (AlwaysOnTop != on)
                NativeMethods.SetWindowPos(Handle, on ? NativeMethods.TopMostWindow : NativeMethods.NotTopMostWindow, 0, 0, 0, 0, NativeMethods.ZOrderOnly);
        }

        // Enter only ever means the visible main action, or Close on a finished screen. While checking or
        // downloading nothing has focus, so a repeated or stray Enter cannot cancel or close anything.
        private void Present(string message, Color color, bool showNotes, bool showProgress, string? primaryText, string secondaryText,
            Action? secondaryClick, Action? primaryClick = null, bool enterCloses = false)
        {
            if (IsDisposed) return;
            downloading = false;
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
            Arrange();
            AcceptButton = primary.Visible ? primary : enterCloses ? secondary : null;
            ActiveControl = (Control?)AcceptButton;
        }

        // Every size comes from the current font, so the window keeps its proportions at any display scaling,
        // and a long message makes the window taller instead of being cut off.
        private void Arrange()
        {
            if (!ready || arranging || IsDisposed) return;
            arranging = true;
            SuspendLayout();
            try
            {
                float scale = Math.Max(Font.Height / DesignFontHeight, DeviceDpi / 96f);
                int Scaled(float value) => (int)Math.Round(value * scale);
                float titlePoints = Font.SizeInPoints * TitlePoints / BodyPoints;
                if (Math.Abs(title.Font.SizeInPoints - titlePoints) > 0.01f)
                {
                    Font previous = titleFont;
                    titleFont = new Font("Segoe UI Semibold", titlePoints);
                    title.Font = titleFont;
                    previous.Dispose();
                }

                int left = Scaled(26), width = Scaled(428), clientWidth = left + width + Scaled(26);
                int line = TextRenderer.MeasureText("Ag", Font).Height;
                Size titleSize = TextRenderer.MeasureText(title.Text, title.Font);
                title.SetBounds(Scaled(24), Scaled(18), titleSize.Width + Scaled(4), titleSize.Height + Scaled(2));
                int statusTop = Math.Max(Scaled(58), title.Bottom + Scaled(6));
                int statusText = TextRenderer.MeasureText(status.Text, status.Font, new Size(width, 0), TextFormatFlags.WordBreak).Height;
                status.SetBounds(left, statusTop, width, Math.Max(Scaled(46), statusText + Scaled(4)));
                int contentTop = status.Bottom + Scaled(4);
                int buttonHeight = Math.Max(Scaled(34), line + Scaled(14));
                int chrome = Height - ClientSize.Height;
                // Before the window exists, use the screen Windows will centre it on; asking for this window's
                // own screen would create it early, at the default size, and leave it off-centre.
                Rectangle area = (IsHandleCreated ? Screen.FromControl(this) : Screen.FromPoint(Cursor.Position)).WorkingArea;
                int notesHeight = Scaled(150);
                int overflow = contentTop + notesHeight + Scaled(18) + buttonHeight + Scaled(20) + chrome - area.Height;
                if (overflow > 0) notesHeight = Math.Max(Scaled(60), notesHeight - overflow);

                notes.SetBounds(left, contentTop, width, notesHeight);
                progress.SetBounds(left, contentTop + Scaled(4), width, Scaled(12));
                progressText.SetBounds(left, progress.Bottom + Scaled(12), width, line + Scaled(5));
                int buttonTop = contentTop + notesHeight + Scaled(18);
                int secondaryWidth = Math.Max(Scaled(102), TextRenderer.MeasureText(secondary.Text, secondary.Font).Width + Scaled(28));
                int primaryWidth = Math.Max(Scaled(104), TextRenderer.MeasureText(primary.Text, primary.Font).Width + Scaled(28));
                secondary.SetBounds(clientWidth - Scaled(22) - secondaryWidth, buttonTop, secondaryWidth, buttonHeight);
                primary.SetBounds(secondary.Left - Scaled(10) - primaryWidth, buttonTop, primaryWidth, buttonHeight);
                Size link = TextRenderer.MeasureText(details.Text, details.Font);
                details.SetBounds(left, buttonTop + (buttonHeight - link.Height) / 2, link.Width + Scaled(4), link.Height + Scaled(2));
                ClientSize = new Size(clientWidth, buttonTop + buttonHeight + Scaled(20));
                // A minimized window is parked off-screen by Windows; moving it would become its restored size.
                if (IsHandleCreated && Visible && WindowState == FormWindowState.Normal) Bounds = KeepOnScreen(Bounds, area);
            }
            finally
            {
                ResumeLayout(false);
                arranging = false;
            }
        }

        private async void StartCheck()
        {
            try
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
                if (result.Status != FeedStatus.Release) { ShowError(result.Message, StartCheck); return; }
                bool testFeed = feed != null;
                UpdateDecision decision = UpdateOffer.Decide(result.Release, installed, allowPrerelease: testFeed,
                    allowFileUrls: testFeed && ReleaseFeed.IsLocal(source), requiredDownloadPrefix: testFeed ? null : ReleaseFeed.DownloadPrefix);
                if (decision.Status == OfferStatus.UpToDate) ShowUpToDate(Display(installed));
                else if (decision.Offer == null) ShowError("This update isn't available yet. Try again later.", StartCheck);
                else ShowAvailable(decision.Offer);
            }
            catch (Exception error)
            {
                // Shown, not swallowed: an unexpected problem must not end in the .NET crash dialog.
                ShowError("The updater hit an unexpected problem: " + error.Message, StartCheck);
            }
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
                // Checked before downloading too, so nobody waits for a download that can't be installed.
                if (SetupRunner.OtherSessionWidgets() > 0)
                {
                    ShowResult(new SetupResult(SetupOutcome.InUseByAnotherUser, -1, null));
                    return;
                }
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
                        PostToWindow(() => { if (downloading) ShowDownloading(bytes, available.Installer.Size); });
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
                if (!verdict.Ok) { ShowError(verdict.Reason, verdict.Retryable ? StartUpdate : (Action?)null); return; }

                installing = true;
                ShowInstalling();
                string tasks = SetupRunner.TaskOptions(SetupRunner.StartupEnabled(), SetupRunner.DesktopShortcutExists());
                SetupResult result = await Task.Run(() => SetupRunner.Run(installer, installDirectory, workingFolder, KeptLogPath, tasks));
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
            catch (Exception error)
            {
                // Shown, not swallowed. If setup had already run, the version in Settings tells the person where things stand.
                ShowError("The updater hit an unexpected problem: " + error.Message + " Check the version shown in the monitor's Settings.", null);
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

        // Left and top win when the window is larger than the room left, so its title and text stay visible.
        internal static Rectangle KeepOnScreen(Rectangle window, Rectangle area)
        {
            int x = Math.Max(area.Left, Math.Min(window.Left, area.Right - window.Width));
            int y = Math.Max(area.Top, Math.Min(window.Top, area.Bottom - window.Height));
            return new Rectangle(x, y, window.Width, window.Height);
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
            // Full path: the updater runs from a temporary folder, which must not be able to supply its own "notepad".
            string notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            SetAlwaysOnTop(false); // the log the person asked for must be able to open above this window
            try { using (Process.Start(new ProcessStartInfo(notepad, "\"" + keptLog + "\"") { UseShellExecute = false })) { } }
            catch (Win32Exception error) { ShowError("The setup log couldn't be opened: " + error.Message, null); }
        }

        private static Icon? LoadIcon()
        {
            using (Stream? stream = typeof(UpdaterForm).Assembly.GetManifestResourceStream("DlbPrecision.Updater.monitor.ico"))
                return stream == null ? null : new Icon(stream);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !ownedResourcesDisposed)
            {
                ownedResourcesDisposed = true;
                closeTimer.Dispose();
                cancellation?.Dispose();
                try { base.Dispose(disposing); }
                finally { bodyFont.Dispose(); titleFont.Dispose(); windowIcon?.Dispose(); }
            }
            else base.Dispose(disposing);
        }
    }
}
