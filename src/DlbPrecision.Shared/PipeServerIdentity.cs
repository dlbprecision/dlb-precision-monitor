using System;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DlbPrecision.Shared
{
    internal static class PipeServerIdentity
    {
        // Query the OS on each connection instead of trusting a process name, executable path,
        // claimed PID in the response, or a stale PID cache. No WMI or process enumeration.
        internal static bool IsInstalledService(NamedPipeClientStream pipe, string serviceName)
        {
            if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pipeServerPid) || pipeServerPid == 0)
                return false;

            using (SafeServiceHandle manager = Native.OpenSCManager(null, null, 0x0001)) // SC_MANAGER_CONNECT
            {
                if (manager.IsInvalid) return false;
                using (SafeServiceHandle service = Native.OpenService(manager, serviceName, 0x0004)) // SERVICE_QUERY_STATUS
                {
                    if (service.IsInvalid) return false;
                    if (!Native.QueryServiceStatusEx(service, 0, out ServiceStatusProcess status,
                        (uint)Marshal.SizeOf(typeof(ServiceStatusProcess)), out _)) return false;

                    // SCM does not guarantee a valid PID while a service is starting or stopping.
                    return status.CurrentState == 4 && status.ProcessId != 0 && status.ProcessId == pipeServerPid;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatusProcess
        {
            internal uint ServiceType;
            internal uint CurrentState;
            internal uint ControlsAccepted;
            internal uint Win32ExitCode;
            internal uint ServiceSpecificExitCode;
            internal uint CheckPoint;
            internal uint WaitHint;
            internal uint ProcessId;
            internal uint ServiceFlags;
        }

        private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private SafeServiceHandle() : base(true) { }
            protected override bool ReleaseHandle() => Native.CloseServiceHandle(handle);
        }

        private static class Native
        {
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern SafeServiceHandle OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern SafeServiceHandle OpenService(SafeServiceHandle manager, string serviceName, uint desiredAccess);

            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool QueryServiceStatusEx(SafeServiceHandle service, int informationLevel,
                out ServiceStatusProcess status, uint bufferSize, out uint bytesNeeded);

            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CloseServiceHandle(IntPtr serviceHandle);
        }
    }
}
