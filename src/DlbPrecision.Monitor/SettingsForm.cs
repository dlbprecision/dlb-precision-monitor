using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DlbPrecision.Shared;

namespace DlbPrecision.Monitor
{
    internal sealed class SettingsForm : Form
    {
        private readonly MonitorSettings settings;
        private readonly ComboBox layout = new ComboBox();
        private readonly ComboBox units = new ComboBox();
        private readonly ComboBox refresh = new ComboBox();
        private readonly ComboBox gpu = new ComboBox();
        private readonly TrackBar transparency = new TrackBar();
        private readonly Label transparencyValue = new Label();
        private readonly CheckBox branding = new CheckBox { Text = "Show small DLB Precision branding" };
        private readonly CheckBox startup = new CheckBox { Text = "Launch when I sign in to Windows" };
        private readonly CheckBox locked = new CheckBox { Text = "Lock position and size" };
        private readonly CheckBox onTop = new CheckBox { Text = "Keep above desktop windows" };
        private readonly TextBox shortcut = new TextBox { ReadOnly = true };
        private readonly Label validation = new Label();
        private readonly ToolTip diagnosticsTip = new ToolTip { AutoPopDelay = 30000 };
        private readonly Font bodyFont = new Font("Segoe UI", 9f);
        private readonly Font titleFont = new Font("Segoe UI Semibold", 17);
        private bool ownedResourcesDisposed;
        private uint shortcutModifiers;
        private int shortcutKey;
        public Func<MonitorSettings, bool, string>? ApplySettings { get; set; }

        public SettingsForm(MonitorSettings current, SensorSnapshot? snapshot, string diagnostics)
        {
            settings = current.Clone();
            Text = "DLB Precision Monitor · Settings";
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = bodyFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(490, 667);
            BackColor = Color.FromArgb(23, 23, 30);
            ForeColor = Color.FromArgb(232, 229, 240);

            var title = new Label { Text = "Make it yours.", Font = titleFont, AutoSize = true, Location = new Point(24, 20) };
            var intro = new Label { Text = "Right-click the widget or use its tray icon to return here.", AutoSize = true, ForeColor = Color.FromArgb(163, 158, 178), Location = new Point(26, 57) };
            Controls.Add(title); Controls.Add(intro);

            AddChoice("Layout", layout, 98, new[] { "Horizontal · seven tiles", "Vertical · seven tiles" });
            layout.SelectedIndex = current.Vertical ? 1 : 0;
            AddChoice("Temperature", units, 138, new[] { "Celsius (°C)", "Fahrenheit (°F)" });
            units.SelectedIndex = current.Fahrenheit ? 1 : 0;
            AddChoice("Refresh", refresh, 178, new[] { "Every second", "Every 2 seconds · economy" });
            refresh.SelectedIndex = current.RefreshMilliseconds == 2000 ? 1 : 0;

            AddChoice("Graphics card", gpu, 218, Array.Empty<string>());
            gpu.Items.Add(new GpuChoice("", "Automatic (first detected GPU)"));
            if (snapshot?.Gpus != null) foreach (GpuSnapshot card in snapshot.Gpus) gpu.Items.Add(new GpuChoice(card.Id, card.Name));
            gpu.SelectedIndex = 0;
            for (int index = 0; index < gpu.Items.Count; index++) if (((GpuChoice)gpu.Items[index]).Id == current.GpuId) gpu.SelectedIndex = index;
            if (current.GpuId.Length > 0 && gpu.SelectedIndex == 0)
            {
                gpu.Items.Add(new GpuChoice(current.GpuId, "Previously selected GPU (disconnected)"));
                gpu.SelectedIndex = gpu.Items.Count - 1;
            }

            Controls.Add(new Label { Text = "Opacity", Location = new Point(26, 266), Size = new Size(122, 22) });
            transparency.SetBounds(157, 255, 238, 42);
            transparency.Minimum = 30; transparency.Maximum = 100; transparency.TickFrequency = 10; transparency.Value = current.OpacityPercent;
            transparencyValue.SetBounds(402, 265, 61, 24);
            transparencyValue.Text = current.OpacityPercent + "%";
            transparency.ValueChanged += (sender, args) => transparencyValue.Text = transparency.Value + "%";
            Controls.Add(transparency); Controls.Add(transparencyValue);

            branding.Checked = current.Branding; startup.Checked = StartupRegistration.IsEnabled(); locked.Checked = current.PositionLocked; onTop.Checked = current.AlwaysOnTop;
            AddCheck(branding, 302); AddCheck(startup, 330); AddCheck(locked, 358); AddCheck(onTop, 386);

            Controls.Add(new Label { Text = "Show / hide shortcut", Location = new Point(26, 428), Size = new Size(139, 23) });
            shortcut.SetBounds(171, 424, 291, 27);
            shortcutModifiers = current.HotkeyModifiers; shortcutKey = current.HotkeyKey;
            shortcut.Text = Hotkey.Display(shortcutModifiers, shortcutKey);
            shortcut.KeyDown += ShortcutOnKeyDown;
            Controls.Add(shortcut);
            Controls.Add(new Label { Text = "Click the shortcut field and press Ctrl or Alt plus a key.", ForeColor = Color.FromArgb(163, 158, 178), Location = new Point(26, 460), Size = new Size(437, 22) });

            var diagnosticsLabel = new Label { Text = diagnostics, ForeColor = Color.FromArgb(163, 158, 178), Location = new Point(26, 489), Size = new Size(436, 51), AutoEllipsis = true };
            Controls.Add(diagnosticsLabel);
            diagnosticsTip.SetToolTip(diagnosticsLabel, diagnostics);
            validation.SetBounds(26, 547, 435, 44); validation.ForeColor = Color.FromArgb(245, 169, 179);
            Controls.Add(validation);

            var apply = new Button { Text = "Apply", DialogResult = DialogResult.None, BackColor = WidgetRenderer.Blue, ForeColor = Color.FromArgb(10, 15, 22), FlatStyle = FlatStyle.Flat };
            apply.FlatAppearance.BorderSize = 0;
            apply.SetBounds(247, 607, 104, 34);
            apply.Click += Apply;
            var close = new Button { Name = "CloseSettings", Text = "Close", DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat };
            close.SetBounds(361, 607, 102, 34);
            // This form is modeless: DialogResult alone only closes ShowDialog windows.
            close.Click += (sender, args) => Close();
            Controls.Add(apply); Controls.Add(close);
            AcceptButton = apply; CancelButton = close;
        }

        private void AddChoice(string text, ComboBox choice, int top, string[] options)
        {
            Controls.Add(new Label { Text = text, Location = new Point(26, top + 3), Size = new Size(124, 23) });
            choice.DropDownStyle = ComboBoxStyle.DropDownList;
            choice.SetBounds(158, top, 304, 27);
            choice.Items.AddRange(options);
            Controls.Add(choice);
        }

        private void AddCheck(CheckBox checkbox, int top) { checkbox.SetBounds(26, top, 436, 26); Controls.Add(checkbox); }

        private void ShortcutOnKeyDown(object? sender, KeyEventArgs args)
        {
            args.SuppressKeyPress = true;
            uint mods = (args.Control ? NativeMethods.ModControl : 0) | (args.Alt ? NativeMethods.ModAlt : 0) | (args.Shift ? NativeMethods.ModShift : 0);
            if (args.KeyCode == Keys.F12)
            {
                validation.Text = "Windows reserves F12 for debugging. Choose another key.";
                return;
            }
            if (!Hotkey.IsValid(mods, args.KeyValue)) return;
            shortcutModifiers = mods; shortcutKey = args.KeyValue;
            shortcut.Text = Hotkey.Display(mods, shortcutKey);
            validation.Text = "";
        }

        private void Apply(object? sender, EventArgs args)
        {
            settings.Vertical = layout.SelectedIndex == 1;
            settings.Fahrenheit = units.SelectedIndex == 1;
            settings.RefreshMilliseconds = refresh.SelectedIndex == 1 ? 2000 : 1000;
            settings.GpuId = ((GpuChoice)gpu.SelectedItem).Id;
            settings.OpacityPercent = transparency.Value;
            settings.Branding = branding.Checked; settings.PositionLocked = locked.Checked; settings.AlwaysOnTop = onTop.Checked;
            settings.HotkeyModifiers = shortcutModifiers; settings.HotkeyKey = shortcutKey;
            string error = ApplySettings?.Invoke(settings.Clone(), startup.Checked) ?? "Unable to apply settings.";
            validation.ForeColor = error.Length == 0 ? WidgetRenderer.Blue : Color.FromArgb(245, 169, 179);
            validation.Text = error.Length == 0 ? "Saved." : error;
        }

        private sealed class GpuChoice
        {
            public string Id { get; }
            private string Name { get; }
            public GpuChoice(string id, string name) { Id = id; Name = name; }
            public override string ToString() => Name;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !ownedResourcesDisposed)
            {
                ownedResourcesDisposed = true;
                diagnosticsTip.Dispose();
                try { base.Dispose(disposing); }
                finally { bodyFont.Dispose(); titleFont.Dispose(); }
            }
            else base.Dispose(disposing);
        }
    }
}
