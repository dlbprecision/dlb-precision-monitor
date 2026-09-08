using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DlbPrecision.Monitor
{
    internal static class NativeMethods
    {
        public const uint ModAlt = 1, ModControl = 2, ModShift = 4, ModWin = 8, ModNoRepeat = 0x4000;
        public const int WmHotkey = 0x0312, WmNcHitTest = 0x0084, WmDisplayChange = 0x007E;
        public const int HtClient = 1, HtCaption = 2, HtLeft = 10, HtRight = 11, HtTop = 12, HtTopLeft = 13, HtTopRight = 14, HtBottom = 15, HtBottomLeft = 16, HtBottomRight = 17;
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnregisterHotKey(IntPtr handle, int id);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyIcon(IntPtr handle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? className, string windowName);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PostMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
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
