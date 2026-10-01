using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DlbPrecision.Monitor
{
    internal static class NativeMethods
    {
        public const uint ModAlt = 1, ModControl = 2, ModShift = 4, ModWin = 8, ModNoRepeat = 0x4000;
        public const int WmHotkey = 0x0312, WmNcHitTest = 0x0084, WmNcCalcSize = 0x0083, WmNcActivate = 0x0086, WmDisplayChange = 0x007E;
        public const int WsThickFrame = 0x00040000, WsExTopmost = 0x00000008;
        private const int GwlExStyle = -20;
        private static readonly IntPtr HwndTopmost = new IntPtr(-1), HwndNoTopmost = new IntPtr(-2);
        public const int HtClient = 1, HtCaption = 2, HtLeft = 10, HtRight = 11, HtTop = 12, HtTopLeft = 13, HtTopRight = 14, HtBottom = 15, HtBottomLeft = 16, HtBottomRight = 17;
        public const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010;
        private const uint MonitorDefaultToNearest = 2;
        private const int MdtEffectiveDpi = 0;
        public delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnregisterHotKey(IntPtr handle, int id);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyIcon(IntPtr handle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PostMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr handle, int index);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr handle, StringBuilder text, int maximumCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr handle);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        // Changes stay-on-top without activating the window; only acts when the state differs, so the
        // widget's place among other windows is not disturbed.
        public static void SetAlwaysOnTop(IntPtr handle, bool alwaysOnTop)
        {
            bool current = (GetWindowLong(handle, GwlExStyle) & WsExTopmost) != 0;
            if (current != alwaysOnTop)
                SetWindowPos(handle, alwaysOnTop ? HwndTopmost : HwndNoTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
        }

        // The display's own scale, available before a window exists on it.
        public static float? DpiScaleFor(Rectangle bounds)
        {
            var rect = new NativeRect { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom };
            IntPtr monitor = MonitorFromRect(ref rect, MonitorDefaultToNearest);
            return monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MdtEffectiveDpi, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96f : (float?)null;
        }
    }

    internal static class Hotkey
    {
        public static bool IsValid(uint modifiers, int key)
        {
            if ((modifiers & ~(NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModShift | NativeMethods.ModWin)) != 0) return false;
            if ((modifiers & (NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModWin)) == 0) return false;
            if (key <= 0 || key > 255) return false;
            // Microsoft reserves F12 for debuggers at all times, including with modifiers.
            // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey
            switch ((Keys)key)
            {
                case Keys.F12:
                case Keys.ControlKey:
                case Keys.LControlKey:
                case Keys.RControlKey:
                case Keys.ShiftKey:
                case Keys.LShiftKey:
                case Keys.RShiftKey:
                case Keys.Menu:
                case Keys.LMenu:
                case Keys.RMenu:
                case Keys.LWin:
                case Keys.RWin:
                    return false;
                default:
                    return true;
            }
        }

        public static string Display(uint modifiers, int key)
        {
            string text = "";
            if ((modifiers & NativeMethods.ModControl) != 0) text += "Ctrl + ";
            if ((modifiers & NativeMethods.ModAlt) != 0) text += "Alt + ";
            if ((modifiers & NativeMethods.ModShift) != 0) text += "Shift + ";
            if ((modifiers & NativeMethods.ModWin) != 0) text += "Win + ";
            return text + new KeysConverter().ConvertToString((Keys)key);
        }
    }
}
