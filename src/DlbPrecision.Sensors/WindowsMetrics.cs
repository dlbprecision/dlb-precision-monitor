using System;
using System.Runtime.InteropServices;

namespace DlbPrecision.Sensors
{
    internal sealed class WindowsMetrics
    {
        private ulong _previousIdle;
        private ulong _previousKernel;
        private ulong _previousUser;
        private bool _hasBaseline;
        private readonly bool _supportsAllProcessors;

        internal WindowsMetrics()
        {
            // GetSystemTimes only includes the calling group on machines with >64 logical CPUs.
            _supportsAllProcessors = Native.GetActiveProcessorCount(0xffff) <= 64;
            ReadCpuLoad();
        }

        internal double? ReadCpuLoad()
        {
            if (!_supportsAllProcessors || !Native.GetSystemTimes(out ulong idle, out ulong kernel, out ulong user)) return null;
            bool canCalculate = _hasBaseline && idle >= _previousIdle && kernel >= _previousKernel && user >= _previousUser;
            double total = canCalculate ? (double)(kernel - _previousKernel) + (user - _previousUser) : 0;
            double idleDelta = canCalculate ? idle - _previousIdle : 0;
            _previousIdle = idle;
            _previousKernel = kernel;
            _previousUser = user;
            _hasBaseline = true;
            return total > 0 && idleDelta <= total
                ? SensorSelection.Valid(100 * (total - idleDelta) / total, 0, 100) : null;
        }

        internal static double? ReadRamUsedGb()
        {
            var state = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
            if (!Native.GlobalMemoryStatusEx(ref state) || state.TotalPhysical == 0 || state.AvailablePhysical > state.TotalPhysical) return null;
            // Match Windows convention: binary gigabytes, displayed as GB in the compact widget.
            return (state.TotalPhysical - state.AvailablePhysical) / 1073741824.0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            internal uint Length;
            internal uint MemoryLoad;
            internal ulong TotalPhysical;
            internal ulong AvailablePhysical;
            internal ulong TotalPageFile;
            internal ulong AvailablePageFile;
            internal ulong TotalVirtual;
            internal ulong AvailableVirtual;
            internal ulong AvailableExtendedVirtual;
        }

        private static class Native
        {
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

            [DllImport("kernel32.dll")]
            internal static extern uint GetActiveProcessorCount(ushort groupNumber);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GlobalMemoryStatusEx(ref MemoryStatus memoryStatus);
        }
    }
}
