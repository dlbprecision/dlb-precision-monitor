using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DlbPrecision.Shared;

namespace DlbPrecision.Monitor
{
    internal sealed class MonitorForm : Form
    {
        private MonitorSettings settings;
        private SensorSnapshot? snapshot;
        private readonly SensorClient client = new SensorClient();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer { Interval = 600 };
        private readonly NotifyIcon tray;
        private readonly Icon brandIcon = BrandIcon.Load();
        private readonly Icon trayIcon = BrandIcon.Load(SystemInformation.SmallIconSize.Width);
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem showItem = new ToolStripMenuItem("Hide monitor");
        private readonly ToolStripMenuItem lockItem = new ToolStripMenuItem("Lock position and size");
        private readonly ToolStripMenuItem orientationItem = new ToolStripMenuItem("Use vertical layout");
        private readonly ToolStripMenuItem sensorItem = new ToolStripMenuItem("Connecting to sensor service…") { Enabled = false };
        private readonly ToolTip tooltip = new ToolTip { InitialDelay = 600, AutoPopDelay = 20000 };
        private SettingsForm? settingsForm;
        private string sensorStatus = "Connecting to sensors…";
        private string settingsWarning;
        private bool polling;
        private bool closing;
        private bool restoring;
        private bool ready;
        private readonly bool firstRun;
        private int hotkeyId = 101;
        private bool hotkeyRegistered;
        private bool ownedResourcesDisposed;
        internal const int ActivateMessage = 0x8000 + 72;

        public MonitorForm(bool resetPosition)
        {
            settings = SettingsStore.Load(SettingsStore.DefaultPath, out settingsWarning);
            firstRun = settings.Left == int.MinValue;
            if (resetPosition) { settings.Left = int.MinValue; settings.Top = int.MinValue; }
            Text = "DLB Precision Monitor";
            Name = "DlbPrecisionMonitorWidget";
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = WidgetRenderer.Background;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Icon = brandIcon;
            // Set before the handle exists so the window is created with it, with no repositioning or flash.
            Opacity = settings.OpacityPercent / 100.0;
            PlaceBeforeFirstShow();

            showItem.Click += (sender, args) => ToggleVisible();
            lockItem.Click += (sender, args) => { settings.PositionLocked = !settings.PositionLocked; ApplyWindowOptions(); Save(); SyncSettingsWindow(); };
            orientationItem.Click += (sender, args) => { ChangeOrientation(!settings.Vertical); Save(); SyncSettingsWindow(); };
            menu.Items.Add(new ToolStripMenuItem("DLB Precision Monitor") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(showItem);
            menu.Items.Add(new ToolStripMenuItem("Settings…", null, (sender, args) => OpenSettings()));
            menu.Items.Add(new ToolStripMenuItem("Check for updates…", null, (sender, args) => CheckForUpdates()));
            menu.Items.Add(orientationItem);
            menu.Items.Add(lockItem);
            menu.Items.Add(new ToolStripMenuItem("Move to primary monitor", null, (sender, args) => RecoverPosition()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(sensorItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (sender, args) => { closing = true; Close(); }));
            menu.Opening += (sender, args) => RefreshMenu();
            ContextMenuStrip = menu;
            tray = new NotifyIcon { Icon = trayIcon, Text = "DLB Precision Monitor", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += (sender, args) => ToggleVisible();
            timer.Tick += async (sender, args) => await PollAsync();
            saveTimer.Tick += (sender, args) => { saveTimer.Stop(); Save(); };
            Shown += async (sender, args) =>
            {
                ready = true;
                ApplyWindowOptions();
                RestoreSavedBounds();
                hotkeyRegistered = NativeMethods.RegisterHotKey(Handle, hotkeyId, settings.HotkeyModifiers | NativeMethods.ModNoRepeat, (uint)settings.HotkeyKey);
                if (!hotkeyRegistered)
                {
                    tray.BalloonTipTitle = "Show / hide shortcut is in use";
                    tray.BalloonTipText = "Use the tray icon to open Settings and choose a different shortcut.";
                    tray.ShowBalloonTip(7000);
                }
                timer.Start();
                await PollAsync();
            };
            MouseDown += (sender, args) =>
            {
                if (args.Button == MouseButtons.Left && !settings.PositionLocked)
                {
                    NativeMethods.ReleaseCapture();
                    NativeMethods.SendMessage(Handle, 0x00A1, new IntPtr(NativeMethods.HtCaption), IntPtr.Zero);
                }
            };
            SizeChanged += (sender, args) => QueueSave();
            LocationChanged += (sender, args) => QueueSave();
        }

        private float DpiScale => DeviceDpi / 96f;
        private Size WidgetMinimum => WidgetSizing.MinimumSize(settings.Vertical, DpiScale);

        // A monitoring widget must never pull focus from a game, including when the shortcut shows it.
        protected override bool ShowWithoutActivation => true;

        // Primary first, so a first launch or a widget whose display was removed lands on the main display.
        private static Rectangle[] WorkingAreas() => Screen.AllScreens.OrderByDescending(s => s.Primary).Select(s => s.WorkingArea).ToArray();

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                // Windows needs the sizing style as well as edge hit tests to resize the widget.
                parameters.Style |= NativeMethods.WsThickFrame;
                // Stay-on-top is a creation style, never the TopMost property: WinForms applies that property
                // with a SetWindowPos that activates the window, defeating ShowWithoutActivation on every show.
                if (settings != null && settings.AlwaysOnTop) parameters.ExStyle |= NativeMethods.WsExTopmost;
                return parameters;
            }
        }

        // Creating the window at its saved place avoids a flash at the main display's top-left and
        // gives it that display's DPI from the start, so Windows never rescales it on the way there.
        private void PlaceBeforeFirstShow()
        {
            Rectangle[] areas = WorkingAreas();
            var saved = new Rectangle(settings.Left, settings.Top, settings.Width, settings.Height);
            Rectangle target = WindowPlacement.Fit(saved, areas, WidgetMinimum);
            float scale = NativeMethods.DpiScaleFor(target) ?? DpiScale;
            if (firstRun)
            {
                saved.Size = WidgetSizing.GetSize(settings.Vertical, 100, scale);
                settings.Width = saved.Width; settings.Height = saved.Height;
            }
            MinimumSize = WidgetSizing.MinimumSize(settings.Vertical, scale);
            Bounds = WindowPlacement.Fit(saved, areas, MinimumSize);
        }

        private void RestoreSavedBounds()
        {
            restoring = true;
            try
            {
                MinimumSize = WidgetMinimum;
                Bounds = WindowPlacement.Fit(new Rectangle(settings.Left, settings.Top, settings.Width, settings.Height), WorkingAreas(), MinimumSize);
            }
            finally { restoring = false; }
        }

        private void ApplyWindowOptions()
        {
            if (IsHandleCreated) NativeMethods.SetAlwaysOnTop(Handle, settings.AlwaysOnTop);
            Opacity = settings.OpacityPercent / 100.0;
            timer.Interval = settings.RefreshMilliseconds;
            Cursor = settings.PositionLocked ? Cursors.Default : Cursors.SizeAll;
            MinimumSize = WidgetMinimum;
            RefreshMenu();
            Invalidate();
        }

        private void RefreshMenu()
        {
            showItem.Text = Visible ? "Hide monitor" : "Show monitor";
            lockItem.Checked = settings.PositionLocked;
            orientationItem.Text = settings.Vertical ? "Use horizontal layout" : "Use vertical layout";
            sensorItem.Text = sensorStatus.Length > 70 ? sensorStatus.Substring(0, 67) + "…" : sensorStatus.Length == 0 ? "Sensors connected" : sensorStatus;
        }

        private void ChangeOrientation(bool vertical)
        {
            if (vertical == settings.Vertical) return;
            restoring = true;
            try
            {
                Rectangle nextBounds = WidgetSizing.Apply(Bounds, settings.Vertical, vertical, null, DpiScale, WorkingAreas());
                settings.Vertical = vertical;
                MinimumSize = WidgetMinimum;
                Bounds = nextBounds;
                UpdateBoundsSettings();
                ApplyWindowOptions();
            }
            finally { restoring = false; }
        }

        private async Task PollAsync()
        {
            if (polling || closing || !Visible) return;
            polling = true;
            try
            {
                SensorSnapshot received = await client.ReadAsync(cancellation.Token);
                if (closing || IsDisposed) return;
                snapshot = received;
                WidgetRenderer.Reading[] values = WidgetRenderer.Readings(snapshot, settings);
                sensorStatus = values.All(value => value.Number == "—") ? "Sensors unavailable · right-click for settings" : values.Any(value => value.Number == "—") ? "Some sensors unavailable" : "";
                tooltip.SetToolTip(this, DiagnosticsText());
                RefreshMenu();
                Invalidate();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException)
            {
                snapshot = null;
                sensorStatus = "Sensor connection lost · right-click for settings";
                RefreshMenu(); Invalidate();
            }
            finally { polling = false; }
        }

        private string DiagnosticsText()
        {
            string text = snapshot == null ? "Waiting for sensor service. Install or repair DLB Precision Monitor if readings stay unavailable." : snapshot.Status;
            if (snapshot != null)
            {
                if (!string.IsNullOrEmpty(snapshot.CpuName)) text += "\nCPU: " + snapshot.CpuName;
                GpuSnapshot? gpu = WidgetRenderer.SelectGpu(snapshot, settings.GpuId);
                if (gpu != null) text += "\nGPU: " + gpu.Name;
                if (snapshot.Warnings != null && snapshot.Warnings.Count > 0)
                {
                    string[] warnings = snapshot.Warnings.Where(w => !string.Equals(w, snapshot.Status, StringComparison.Ordinal)).Distinct().ToArray();
                    if (warnings.Length > 0) text += "\n" + string.Join("\n", warnings);
                }
            }
            if (!hotkeyRegistered) text += "\nShortcut unavailable: another application may be using it.";
            if (!string.IsNullOrEmpty(settingsWarning)) text += "\n" + settingsWarning;
            text += settings.PositionLocked ? "\nDragging is locked. Use Size in Settings to resize." : "\nResize: drag an edge or use Size in Settings.";
            return text;
        }

        private void SyncSettingsWindow()
        {
            if (settingsForm == null || settingsForm.IsDisposed) return;
            settingsForm.SyncWidgetState(settings.Vertical, settings.PositionLocked, WidgetSizing.GetPercent(settings.Vertical, Size, DpiScale));
        }

        private void CheckForUpdates()
        {
            string? problem = UpdateLauncher.Start(Path.GetDirectoryName(Application.ExecutablePath), info => { using (Process.Start(info)) { } });
            if (problem == null) return;
            tray.BalloonTipTitle = "Check for updates";
            tray.BalloonTipText = problem;
            tray.ShowBalloonTip(7000);
        }

        private void OpenSettings()
        {
            if (settingsForm != null && !settingsForm.IsDisposed) { settingsForm.Activate(); return; }
            UpdateBoundsSettings();
            settingsForm = new SettingsForm(settings, snapshot, DiagnosticsText(), DpiScale) { Icon = Icon, TopMost = settings.AlwaysOnTop };
            settingsForm.ApplySettings = ApplySettings;
            settingsForm.CheckForUpdates = CheckForUpdates;
            settingsForm.FormClosed += (sender, args) => settingsForm = null;
            settingsForm.Show();
        }

        private SettingsApplyResult ApplySettings(MonitorSettings next, bool enableStartup, int? requestedSizePercent)
        {
            next.Normalize();
            bool hotkeyRequested = settings.HotkeyKey != next.HotkeyKey || settings.HotkeyModifiers != next.HotkeyModifiers;
            bool hotkeyNeeded = hotkeyRequested || !hotkeyRegistered;
            int nextHotkeyId = hotkeyId == 101 ? 102 : 101;
            bool hotkeyAcquired = hotkeyNeeded
                && NativeMethods.RegisterHotKey(Handle, nextHotkeyId, next.HotkeyModifiers | NativeMethods.ModNoRepeat, (uint)next.HotkeyKey);
            string hotkeyWarning = "";
            if (hotkeyNeeded && !hotkeyAcquired)
            {
                // Another application owns the shortcut. Save everything else and keep the current shortcut.
                next.HotkeyModifiers = settings.HotkeyModifiers; next.HotkeyKey = settings.HotkeyKey;
                hotkeyWarning = hotkeyRequested && hotkeyRegistered
                    ? "Saved, but that shortcut is in use by another app. Your previous shortcut still works."
                    : "Saved, but the show / hide shortcut is in use by another app. Choose another key combination.";
            }

            try
            {
                if (StartupRegistration.IsEnabled() != enableStartup) StartupRegistration.SetEnabled(enableStartup);
                Rectangle nextBounds = WidgetSizing.Apply(Bounds, settings.Vertical, next.Vertical, requestedSizePercent, DpiScale, WorkingAreas());
                // Save first so a filesystem error leaves the running configuration intact.
                next.Left = nextBounds.Left; next.Top = nextBounds.Top; next.Width = nextBounds.Width; next.Height = nextBounds.Height;
                SettingsStore.Save(next, SettingsStore.DefaultPath);
                if (hotkeyAcquired)
                {
                    if (hotkeyRegistered) NativeMethods.UnregisterHotKey(Handle, hotkeyId);
                    hotkeyId = nextHotkeyId; hotkeyRegistered = true;
                }
                settings = next;
                restoring = true;
                try
                {
                    MinimumSize = WidgetMinimum;
                    Bounds = nextBounds;
                    ApplyWindowOptions();
                }
                finally { restoring = false; }
                if (Bounds != nextBounds) Save();
                settingsWarning = "";
                return new SettingsApplyResult(true, hotkeyWarning);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                if (hotkeyAcquired) NativeMethods.UnregisterHotKey(Handle, nextHotkeyId);
                return new SettingsApplyResult(false, "Could not save settings: " + ex.Message);
            }
        }

        private async void ToggleVisible()
        {
            if (Visible) { Hide(); timer.Stop(); }
            else { Show(); RestoreVisibleBounds(); BringToTopWithoutFocus(); timer.Start(); await PollAsync(); }
            RefreshMenu();
        }

        private void RecoverPosition()
        {
            if (!Visible) Show();
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Bounds = WindowPlacement.Fit(new Rectangle(area.Left + 24, area.Top + 24, Width, Height), new[] { area }, MinimumSize);
            timer.Start();
            Save();
        }

        // Opening the app again shows the widget where the user put it; only the menu moves it.
        private void RevealInPlace()
        {
            RestoreVisibleBounds();
            BringToTopWithoutFocus();
        }

        private void BringToTopWithoutFocus() =>
            NativeMethods.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, NativeMethods.SwpNoSize | NativeMethods.SwpNoMove | NativeMethods.SwpNoActivate);

        private void RestoreVisibleBounds()
        {
            Bounds = WindowPlacement.Fit(Bounds, WorkingAreas(), MinimumSize);
        }

        private void UpdateBoundsSettings() { settings.Left = Left; settings.Top = Top; settings.Width = Width; settings.Height = Height; }
        private void QueueSave() { if (!ready || restoring || closing) return; saveTimer.Stop(); saveTimer.Start(); }
        private void Save()
        {
            UpdateBoundsSettings();
            try { SettingsStore.Save(settings, SettingsStore.DefaultPath); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { settingsWarning = "Settings could not be saved: " + ex.Message; }
        }

        protected override void OnPaint(PaintEventArgs args) => WidgetRenderer.Paint(args.Graphics, ClientRectangle, settings, snapshot, sensorStatus, DpiScale);

        protected override void OnDpiChanged(DpiChangedEventArgs args)
        {
            base.OnDpiChanged(args);
            MinimumSize = WidgetMinimum;
            BeginInvoke(new Action(RestoreVisibleBounds));
            Invalidate();
        }

        protected override void WndProc(ref Message message)
        {
            // Keep the full client area and DLB's borderless appearance with native resizing enabled.
            if (message.Msg == NativeMethods.WmNcCalcSize) { message.Result = IntPtr.Zero; return; }
            // On a focus change Windows repaints the hidden resize frame over the widget's edges until
            // the next refresh; -1 asks DefWindowProc to track the state change without that repaint.
            if (message.Msg == NativeMethods.WmNcActivate) message.LParam = new IntPtr(-1);
            if (message.Msg == NativeMethods.WmHotkey && message.WParam.ToInt32() == hotkeyId) { ToggleVisible(); return; }
            if (message.Msg == ActivateMessage) { if (!Visible) ToggleVisible(); else RevealInPlace(); return; }
            if (message.Msg == NativeMethods.WmDisplayChange && ready) BeginInvoke(new Action(RestoreVisibleBounds));
            if (message.Msg == NativeMethods.WmNcHitTest)
            {
                if (settings.PositionLocked) { message.Result = new IntPtr(NativeMethods.HtClient); return; }
                long raw = message.LParam.ToInt64();
                Point point = PointToClient(new Point(unchecked((short)(raw & 0xffff)), unchecked((short)((raw >> 16) & 0xffff))));
                int edge = Math.Max(5, (int)(6 * DpiScale));
                bool left = point.X < edge, right = point.X >= ClientSize.Width - edge, top = point.Y < edge, bottom = point.Y >= ClientSize.Height - edge;
                int hit = top && left ? NativeMethods.HtTopLeft : top && right ? NativeMethods.HtTopRight : bottom && left ? NativeMethods.HtBottomLeft : bottom && right ? NativeMethods.HtBottomRight : left ? NativeMethods.HtLeft : right ? NativeMethods.HtRight : top ? NativeMethods.HtTop : bottom ? NativeMethods.HtBottom : NativeMethods.HtClient;
                message.Result = new IntPtr(hit); return;
            }
            base.WndProc(ref message);
        }

        protected override void OnFormClosing(FormClosingEventArgs args)
        {
            if (!closing && args.CloseReason == CloseReason.UserClosing)
            {
                args.Cancel = true;
                Hide(); timer.Stop();
                base.OnFormClosing(args);
                return;
            }
            closing = true;
            timer.Stop(); saveTimer.Stop();
            Save();
            cancellation.Cancel(); client.Dispose();
            if (hotkeyRegistered) NativeMethods.UnregisterHotKey(Handle, hotkeyId);
            tray.Visible = false;
            settingsForm?.Close();
            base.OnFormClosing(args);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !ownedResourcesDisposed)
            {
                ownedResourcesDisposed = true;
                cancellation.Cancel(); client.Dispose();
                timer.Dispose(); saveTimer.Dispose();
                settingsForm?.Dispose(); settingsForm = null;
                tray.Dispose(); menu.Dispose(); tooltip.Dispose(); cancellation.Dispose();
                try { base.Dispose(disposing); }
                finally { brandIcon.Dispose(); trayIcon.Dispose(); }
            }
            else base.Dispose(disposing);
        }
    }
}
