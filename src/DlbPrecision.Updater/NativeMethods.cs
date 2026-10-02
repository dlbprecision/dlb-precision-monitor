using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DlbPrecision.Updater
{
    internal static class NativeMethods
    {
        private const uint WtdUiNone = 2, WtdRevokeWholeChain = 1, WtdChoiceFile = 1, WtdStateActionVerify = 1, WtdStateActionClose = 2;
        private const uint WtdRevocationCheckChain = 0x40;
        private static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustFileInfo
        {
            public uint Size;
            public IntPtr FilePath;
            public IntPtr File;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint Size;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
            public IntPtr SignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern int WinVerifyTrust(IntPtr window, [In] ref Guid action, [In, Out] ref WinTrustData data);

        // Windows' own Authenticode check of the open file, including revocation of the whole chain.
        // Returns 0 when the signature is valid and trusted.
        public static int VerifyEmbeddedSignature(string path, SafeFileHandle file)
        {
            IntPtr pathText = Marshal.StringToCoTaskMemUni(path);
            IntPtr fileInfo = IntPtr.Zero;
            try
            {
                var info = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)), FilePath = pathText, File = file.DangerousGetHandle() };
                fileInfo = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WinTrustFileInfo)));
                Marshal.StructureToPtr(info, fileInfo, false);
                var data = new WinTrustData
                {
                    Size = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                    UiChoice = WtdUiNone,
                    RevocationChecks = WtdRevokeWholeChain,
                    UnionChoice = WtdChoiceFile,
                    FileInfo = fileInfo,
                    StateAction = WtdStateActionVerify,
                    ProviderFlags = WtdRevocationCheckChain
                };
                Guid action = GenericVerifyV2;
                int result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                data.StateAction = WtdStateActionClose;
                WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                return result;
            }
            finally
            {
                if (fileInfo != IntPtr.Zero) Marshal.FreeCoTaskMem(fileInfo);
                Marshal.FreeCoTaskMem(pathText);
                GC.KeepAlive(file);
            }
        }

        public delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        public static readonly IntPtr TopMostWindow = new IntPtr(-1);           // HWND_TOPMOST
        public static readonly IntPtr NotTopMostWindow = new IntPtr(-2);        // HWND_NOTOPMOST
        public const uint ZOrderOnly = 0x0001 | 0x0002 | 0x0010;                // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr window, int index);
        public const uint KeepPositionAndSize = 0x0001 | 0x0002 | 0x0040;       // SWP_NOSIZE | SWP_NOMOVE | SWP_SHOWWINDOW
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    }
}
