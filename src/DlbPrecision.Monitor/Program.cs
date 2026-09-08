using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using DlbPrecision.Shared;

namespace DlbPrecision.Monitor
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            NativeMethods.SetProcessDpiAwarenessContext(new IntPtr(-4));
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                // Inno Setup cannot recover an unelevated original user when setup itself was
                // explicitly launched with "Run as administrator". Keep that case out of the UI.
                if (args.Contains("--from-installer") && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
                if (args.Contains("--enable-startup") || args.Contains("--disable-startup"))
                {
                    // Startup belongs to the signed-in user. The installer must run this action as its original unelevated user.
                    if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
                    StartupRegistration.SetEnabled(args.Contains("--enable-startup"));
                    return 0;
                }
                if (args.Length >= 2 && args[0] == "--render-preview") { RenderPreviews(args[1]); return 0; }
                if (args.Length >= 1 && args[0] == "--smoke-test") { RunSmokeTests(args.Length >= 2 ? args[1] : null); return 0; }

                string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
                using (var instance = new Mutex(true, @"Local\DLBPrecision.Monitor." + user, out bool created))
                {
                    if (!created)
                    {
                        IntPtr existing = NativeMethods.FindWindow(null, "DLB Precision Monitor");
                        if (existing != IntPtr.Zero) NativeMethods.PostMessage(existing, 0x8000 + 72, IntPtr.Zero, IntPtr.Zero);
                        return 0;
                    }
                    var monitor = new MonitorForm(args.Contains("--reset-position"));
                    // Diagnostic launch exposes the tool window to accessibility test tools.
                    // Regular widget launches remain absent from the taskbar.
                    if (args.Contains("--test-window")) monitor.ShowInTaskbar = true;
                    Application.Run(monitor);
                    instance.ReleaseMutex();
                }
                return 0;
            }
            catch (Exception ex)
            {
                if (args.Length == 0 || args[0] == "--reset-position") MessageBox.Show("DLB Precision Monitor could not start.\n\n" + ex.Message, "DLB Precision Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
                else if (args.Length >= 2 && args[0] == "--smoke-test") File.WriteAllText(args[1], "FAILED\n" + ex);
                return 1;
            }
        }

        private static SensorSnapshot SampleSnapshot() => new SensorSnapshot
        {
            TimestampUtc = DateTime.UtcNow,
            CpuName = "Preview CPU (sample values)",
            CpuTemperatureC = 51,
            CpuLoadPercent = 5,
            CpuClockMhz = 5590,
            RamUsedGb = 20.2,
            Status = "Sample data for appearance preview. These are not live hardware readings.",
            Gpus = new List<GpuSnapshot> { new GpuSnapshot { Id = "sample-gpu", Name = "Preview GPU (sample values)", TemperatureC = 32, LoadPercent = 1, ClockMhz = 510 } }
        };

        private static void RenderPreviews(string directory)
        {
            Directory.CreateDirectory(directory);
            var settings = new MonitorSettings();
            SensorSnapshot sample = SampleSnapshot();
            Render(Path.Combine(directory, "widget-horizontal-sample.png"), new Size(861, 128), settings, sample, "", 1, true);
            Render(Path.Combine(directory, "widget-horizontal-150dpi-sample.png"), new Size(1292, 192), settings, sample, "", 1.5f, true);
            Render(Path.Combine(directory, "widget-minimum-sample.png"), new Size(590, 96), settings, sample, "", 1, true);
            Render(Path.Combine(directory, "widget-unavailable.png"), new Size(861, 128), settings, null, "Sensors unavailable · right-click for settings", 1, false);
            settings.Fahrenheit = true;
            Render(Path.Combine(directory, "widget-fahrenheit-sample.png"), new Size(861, 128), settings, sample, "", 1, true);
            settings.Fahrenheit = false; settings.Vertical = true;
            Render(Path.Combine(directory, "widget-vertical-sample.png"), new Size(144, 716), settings, sample, "", 1, true);
            using (var previewIcon = BrandIcon.Load())
            using (var form = new SettingsForm(settings, sample, "Sample data for appearance preview. Sensor service is not being queried."))
            {
                form.Icon = previewIcon;
                // Force child handle creation without displaying a window on the user's desktop.
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-32000, -32000);
                form.Opacity = 0;
                form.Show();
                form.PerformLayout();
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    bitmap.Save(Path.Combine(directory, "settings-sample.png"), ImageFormat.Png);
                }
            }
        }

        private static void Render(string path, Size size, MonitorSettings settings, SensorSnapshot? snapshot, string status, float dpiScale, bool sample)
        {
            using (var bitmap = new Bitmap(size.Width, size.Height))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                WidgetRenderer.Paint(graphics, new Rectangle(Point.Empty, size), settings, snapshot, status, dpiScale, sample);
                bitmap.Save(path, ImageFormat.Png);
            }
        }

        private static void RunSmokeTests(string? reportPath)
        {
            var results = new List<string>();
            Action<bool, string> verify = (passed, description) => { if (!passed) throw new InvalidOperationException(description); results.Add("PASS " + description); };
            verify(WidgetRenderer.Temperature(100, true).Number == "212", "Celsius-to-Fahrenheit conversion at boiling point");
            verify(WidgetRenderer.Temperature(0, true).Number == "32", "Zero temperature remains a valid reading");
            verify(WidgetRenderer.Temperature(null, true).Number == "—", "Missing temperature stays unavailable after unit conversion");
            verify(WidgetRenderer.Format(null, "%").Number == "—", "Missing usage does not become zero");
            verify(WidgetRenderer.Format(0, "%").Number == "0", "Actual zero usage remains visible");
            verify(WidgetRenderer.Format(double.NaN, "MHz").Number == "—", "Invalid sensor numbers remain unavailable");
            verify(WidgetRenderer.Format(double.PositiveInfinity, "MHz").Number == "—", "Infinite sensor numbers remain unavailable");
            verify(!Hotkey.IsValid(0, (int)Keys.A), "Unmodified letter shortcuts cannot intercept normal typing");
            verify(!Hotkey.IsValid(NativeMethods.ModControl, (int)Keys.ControlKey), "Modifier alone is not a usable shortcut");
            verify(!Hotkey.IsValid(NativeMethods.ModControl | NativeMethods.ModAlt, (int)Keys.F12), "Debugger-reserved F12 is rejected even with modifiers");
            Keys[] modifierKeys = { Keys.ControlKey, Keys.LControlKey, Keys.RControlKey, Keys.ShiftKey, Keys.LShiftKey, Keys.RShiftKey, Keys.Menu, Keys.LMenu, Keys.RMenu, Keys.LWin, Keys.RWin };
            verify(modifierKeys.All(key => !Hotkey.IsValid(NativeMethods.ModControl | NativeMethods.ModAlt, (int)key)), "All generic and left/right modifier-only keys are rejected");
            verify(Hotkey.IsValid(NativeMethods.ModControl | NativeMethods.ModAlt, (int)Keys.F10), "Default Ctrl+Alt+F10 show-hide shortcut is valid");
            var oldHotkeySettings = new MonitorSettings { HotkeyKey = (int)Keys.F12, Fahrenheit = true, OpacityPercent = 73 };
            oldHotkeySettings.Normalize();
            verify(oldHotkeySettings.HotkeyKey == (int)Keys.F10 && oldHotkeySettings.Fahrenheit && oldHotkeySettings.OpacityPercent == 73, "Previously saved F12 setting recovers to F10 without losing display preferences");
            using (var icon = BrandIcon.Load(16))
                verify(icon.Width == 16 && icon.Height == 16, "Embedded DLB tray icon resolves at native tray size");
            using (var modelessSettings = new SettingsForm(new MonitorSettings(), null, "Modeless close regression; no settings are applied."))
            {
                modelessSettings.ShowInTaskbar = false;
                modelessSettings.StartPosition = FormStartPosition.Manual;
                modelessSettings.Location = new Point(-32000, -32000);
                modelessSettings.Opacity = 0;
                modelessSettings.Show();
                verify(!modelessSettings.Modal && modelessSettings.IsHandleCreated, "Close regression exercises a real modeless settings window");
                ((Button)modelessSettings.Controls["CloseSettings"]).PerformClick();
                verify(modelessSettings.IsDisposed, "Close button disposes the modeless settings window");
                modelessSettings.Dispose();
                verify(modelessSettings.IsDisposed, "Repeated settings disposal is safe after modeless Close");
            }
            Rectangle area = new Rectangle(0, 0, 1920, 1080);
            Rectangle recovered = WindowPlacement.Fit(new Rectangle(3000, -1000, 861, 128), new[] { area }, new Size(590, 96));
            verify(area.Contains(recovered), "Widget is recoverable after a monitor is disconnected");
            Rectangle leftDisplay = new Rectangle(-1920, 0, 1920, 1080);
            Rectangle leftWidget = new Rectangle(-1500, 300, 861, 128);
            verify(WindowPlacement.Fit(leftWidget, new[] { area, leftDisplay }, new Size(590, 96)) == leftWidget, "Valid negative monitor coordinates are preserved");
            SensorSnapshot sample = SampleSnapshot();
            sample.Gpus.Add(new GpuSnapshot { Id = "second", Name = "Second GPU", LoadPercent = 73 });
            verify(WidgetRenderer.SelectGpu(sample, "second")?.LoadPercent == 73, "GPU selection chooses the requested hardware");
            verify(WidgetRenderer.SelectGpu(sample, "removed")?.Id == "sample-gpu", "Removed GPU selection falls back to detected hardware");
            var settings = new MonitorSettings { Fahrenheit = true, Vertical = true, OpacityPercent = 68, GpuId = "second", HotkeyKey = (int)Keys.F9 };
            string tempDirectory = Path.Combine(Path.GetTempPath(), "DlbPrecisionMonitorTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            try
            {
                string path = Path.Combine(tempDirectory, "settings.json");
                SettingsStore.Save(settings, path);
                MonitorSettings loaded = SettingsStore.Load(path, out string warning);
                verify(warning == "" && loaded.Fahrenheit && loaded.Vertical && loaded.OpacityPercent == 68 && loaded.GpuId == "second" && loaded.HotkeyKey == (int)Keys.F9, "Display preferences and programmable shortcut persist together");
                settings.OpacityPercent = 80;
                SettingsStore.Save(settings, path);
                verify(SettingsStore.Load(path, out warning).OpacityPercent == 80, "Existing preferences are atomically replaced");
                File.WriteAllText(path, "malformed settings");
                verify(SettingsStore.Load(path, out warning).RefreshMilliseconds == 1000 && warning.Length > 0, "Corrupted preferences recover with a visible warning");
                Render(Path.Combine(tempDirectory, "missing.png"), new Size(590, 96), new MonitorSettings(), null, "Sensors unavailable", 1, false);
                verify(new FileInfo(Path.Combine(tempDirectory, "missing.png")).Length > 1000, "Unavailable readings render at minimum widget size");
            }
            finally
            {
                // Only the unique temporary test directory created by this invocation is removed.
                foreach (string path in Directory.GetFiles(tempDirectory)) File.Delete(path);
                Directory.Delete(tempDirectory);
            }
            string report = results.Count + " checks passed. No startup registry entries, global shortcuts, services, or drivers were changed.\n" + string.Join("\n", results);
            if (reportPath != null) File.WriteAllText(reportPath, report);
        }
    }
}
