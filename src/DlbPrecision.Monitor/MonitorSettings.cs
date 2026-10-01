using System;
using System.Drawing;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DlbPrecision.Monitor
{
    [DataContract]
    internal sealed class MonitorSettings
    {
        [DataMember] public int Version { get; set; }
        [DataMember] public bool Vertical { get; set; }
        [DataMember] public bool Fahrenheit { get; set; }
        [DataMember] public bool Branding { get; set; }
        [DataMember] public bool PositionLocked { get; set; }
        [DataMember] public bool AlwaysOnTop { get; set; }
        [DataMember] public int OpacityPercent { get; set; }
        [DataMember] public int RefreshMilliseconds { get; set; }
        [DataMember] public string GpuId { get; set; } = "";
        [DataMember] public uint HotkeyModifiers { get; set; }
        [DataMember] public int HotkeyKey { get; set; }
        [DataMember] public int Left { get; set; }
        [DataMember] public int Top { get; set; }
        [DataMember] public int Width { get; set; }
        [DataMember] public int Height { get; set; }

        public MonitorSettings() => SetDefaults();

        // The JSON serializer skips constructors, so a setting missing from an older file would
        // otherwise load as false or zero instead of its default.
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context) => SetDefaults();

        private void SetDefaults()
        {
            Version = 1;
            Vertical = false;
            Fahrenheit = false;
            Branding = true;
            PositionLocked = false;
            AlwaysOnTop = true;
            OpacityPercent = 100;
            RefreshMilliseconds = 1000;
            GpuId = "";
            HotkeyModifiers = NativeMethods.ModControl | NativeMethods.ModAlt;
            HotkeyKey = (int)Keys.F10;
            Left = int.MinValue;
            Top = int.MinValue;
            Width = 861;
            Height = 128;
        }

        public MonitorSettings Clone() => (MonitorSettings)MemberwiseClone();

        public void Normalize()
        {
            OpacityPercent = Math.Max(30, Math.Min(100, OpacityPercent));
            RefreshMilliseconds = RefreshMilliseconds == 2000 ? 2000 : 1000;
            // Loose sanity bounds only; the window enforces the real 50% minimum for its display.
            Width = Math.Max(50, Math.Min(6000, Width));
            Height = Math.Max(40, Math.Min(6000, Height));
            GpuId = GpuId ?? "";
            if (!Hotkey.IsValid(HotkeyModifiers, HotkeyKey))
            {
                HotkeyModifiers = NativeMethods.ModControl | NativeMethods.ModAlt;
                HotkeyKey = (int)Keys.F10;
            }
        }
    }

    internal static class SettingsStore
    {
        public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLBPrecision", "Monitor", "settings.json");

        public static MonitorSettings Load(string path, out string warning)
        {
            warning = "";
            if (!File.Exists(path)) return new MonitorSettings();
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var result = (MonitorSettings)new DataContractJsonSerializer(typeof(MonitorSettings)).ReadObject(stream);
                    if (result == null || result.Version != 1) throw new SerializationException("Unknown settings format.");
                    result.Normalize();
                    return result;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SerializationException || ex is ArgumentException)
            {
                warning = "Saved settings could not be read. Default settings are in use.";
                return new MonitorSettings();
            }
        }

        public static void Save(MonitorSettings settings, string path)
        {
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temp = path + ".tmp";
            using (var stream = File.Create(temp))
                new DataContractJsonSerializer(typeof(MonitorSettings)).WriteObject(stream, settings);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
    }

    internal static class StartupRegistration
    {
        private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "DLBPrecisionMonitor";
        private static string Command => "\"" + Application.ExecutablePath + "\"";

        public static bool IsEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }

        public static void SetEnabled(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
                else if (string.Equals(key.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase)) key.DeleteValue(ValueName, false);
            }
        }
    }

    internal static class WindowPlacement
    {
        // Keep the entire widget within a working area, including after a monitor is disconnected.
        public static Rectangle Fit(Rectangle desired, Rectangle[] workingAreas, Size minimum)
        {
            Rectangle best = workingAreas.Length == 0 ? new Rectangle(0, 0, 1920, 1080) : workingAreas[0];
            long largest = -1;
            foreach (var area in workingAreas)
            {
                Rectangle overlap = Rectangle.Intersect(desired, area);
                long size = (long)Math.Max(0, overlap.Width) * Math.Max(0, overlap.Height);
                if (size > largest) { largest = size; best = area; }
            }
            int width = Math.Min(best.Width, Math.Max(minimum.Width, desired.Width));
            int height = Math.Min(best.Height, Math.Max(minimum.Height, desired.Height));
            int x = Math.Max(best.Left, Math.Min(desired.Left, best.Right - width));
            int y = Math.Max(best.Top, Math.Min(desired.Top, best.Bottom - height));
            if (desired.X == int.MinValue || desired.Y == int.MinValue) { x = best.Left + Math.Min(24, Math.Max(0, best.Width - width)); y = best.Top + Math.Min(24, Math.Max(0, best.Height - height)); }
            return new Rectangle(x, y, width, height);
        }
    }
}
