using System;
using System.Collections.Generic;
using System.Security.Principal;
using DlbPrecision.Shared;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace DlbPrecision.Sensors
{
    /// <summary>
    /// Opens only CPU and GPU monitoring once. Read is serialized and performs one sensor update.
    /// No driver installation, fan control, WMI queries, logging, or network calls are performed here.
    /// </summary>
    public sealed class SensorReader : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Computer _computer;
        private readonly WindowsMetrics _windows = new WindowsMetrics();
        private readonly List<HardwareEntry> _entries = new List<HardwareEntry>();
        private readonly List<string> _startupWarnings = new List<string>();
        private readonly string _fallbackCpuName;
        private readonly bool _isAdministrator;
        private readonly bool _pawnIoRegistered;
        private bool _disposed;

        public SensorReader()
        {
            _isAdministrator = IsAdministrator();
            _pawnIoRegistered = IsPawnIoRegistered();
            _fallbackCpuName = ReadCpuName();
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = false,
                IsMotherboardEnabled = false,
                IsControllerEnabled = false,
                IsStorageEnabled = false,
                IsNetworkEnabled = false,
                IsPowerMonitorEnabled = false
            };

            try
            {
                _computer.Open();
            }
            catch (Exception error)
            {
                _startupWarnings.Add("Hardware sensor initialization failed (" + error.GetType().Name
                    + "). Windows CPU load and memory readings remain available.");
            }

            // A partially initialized library can still contain useful devices after one device fails.
            foreach (IHardware hardware in _computer.Hardware)
            {
                if (hardware.HardwareType == HardwareType.Cpu || IsGpu(hardware.HardwareType))
                    _entries.Add(new HardwareEntry(hardware));
            }
        }

        public SensorSnapshot Read()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return ReadCore();
            }
        }

        private SensorSnapshot ReadCore()
        {
            var snapshot = new SensorSnapshot
            {
                ProtocolVersion = 1,
                TimestampUtc = DateTime.UtcNow,
                CpuName = _fallbackCpuName,
                CpuLoadPercent = _windows.ReadCpuLoad(),
                RamUsedGb = WindowsMetrics.ReadRamUsedGb(),
                Gpus = new List<GpuSnapshot>(),
                Warnings = new List<string>(_startupWarnings)
            };
            bool firstCpu = true;
            int cpuCount = 0;

            foreach (HardwareEntry entry in _entries)
            {
                bool updated = entry.Update();
                IHardware hardware = entry.Hardware;
                if (!updated)
                    snapshot.Warnings.Add(hardware.Name + ": sensor update failed (" + entry.LastError
                        + "); old readings were discarded.");

                if (hardware.HardwareType == HardwareType.Cpu)
                {
                    cpuCount++;
                    if (!firstCpu) continue;
                    firstCpu = false;
                    snapshot.CpuName = hardware.Name;
                    if (!updated) continue;
                    snapshot.CpuTemperatureC = SensorSelection.CpuTemperature(entry.Readings);
                    snapshot.CpuClockMhz = SensorSelection.CpuClock(entry.Readings);
                    snapshot.CpuLoadPercent = snapshot.CpuLoadPercent ?? SensorSelection.CpuLoad(entry.Readings);
                }
                else
                {
                    var gpu = new GpuSnapshot
                    {
                        Id = hardware.Identifier.ToString(),
                        Name = hardware.Name,
                        TemperatureC = updated ? SensorSelection.GpuTemperature(entry.Readings) : null,
                        LoadPercent = updated ? SensorSelection.GpuLoad(entry.Readings) : null,
                        ClockMhz = updated ? SensorSelection.GpuClock(entry.Readings) : null
                    };
                    snapshot.Gpus.Add(gpu);
                    if (!gpu.TemperatureC.HasValue) snapshot.Warnings.Add(gpu.Name + ": GPU core temperature unavailable.");
                    if (!gpu.LoadPercent.HasValue) snapshot.Warnings.Add(gpu.Name + ": GPU load unavailable.");
                    if (!gpu.ClockMhz.HasValue) snapshot.Warnings.Add(gpu.Name + ": GPU core clock unavailable.");
                }
            }

            if (cpuCount > 1)
                snapshot.Warnings.Add("Multiple CPU packages detected: temperature and clock describe the first package; Windows load covers the system.");
            if (!snapshot.CpuTemperatureC.HasValue)
            {
                if (!_pawnIoRegistered)
                    snapshot.Warnings.Add("CPU temperature unavailable: the PawnIO sensor driver is not registered. Run the DLB Precision installer to configure sensor access.");
                else if (!_isAdministrator)
                    snapshot.Warnings.Add("CPU temperature unavailable in this non-elevated process. Use the installed DLB Precision sensor service; the registered driver alone may not grant access.");
                else
                    snapshot.Warnings.Add("CPU temperature unavailable. The driver may be blocked, incompatible, or unable to read this processor; inspect the sensor report.");
            }
            if (!snapshot.CpuLoadPercent.HasValue) snapshot.Warnings.Add("CPU load unavailable; the next timed sample may establish a valid interval.");
            if (!snapshot.CpuClockMhz.HasValue) snapshot.Warnings.Add("CPU core clock unavailable.");
            if (!snapshot.RamUsedGb.HasValue) snapshot.Warnings.Add("Physical RAM usage unavailable.");
            if (snapshot.Gpus.Count == 0) snapshot.Warnings.Add("No supported GPU was detected. Install the graphics manufacturer's display driver if it is missing.");
            snapshot.TimestampUtc = DateTime.UtcNow;
            snapshot.Status = snapshot.Warnings.Count == 0 ? "ok" : "partial";
            return snapshot;
        }

        /// <summary>On-demand diagnosis only: no serial numbers, usernames, or full LHM machine dump.</summary>
        public SensorReport GetReport()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                var report = new SensorReport
                {
                    Snapshot = ReadCore(),
                    SensorLibraryVersion = typeof(Computer).Assembly.GetName().Version?.ToString() ?? "unknown",
                    IsAdministrator = _isAdministrator,
                    PawnIoDriverRegistered = _pawnIoRegistered,
                    Definitions = new[]
                    {
                        "CPU temperature: package/die temperature; control temperature or hottest core is used only if the preferred sensor is unavailable.",
                        "CPU load: Windows busy CPU time over the sampling interval; library total/thread average is a fallback. It can differ from Task Manager's frequency-adjusted utilization.",
                        "CPU clock: arithmetic mean of reported core clocks, excluding bus, SoC, and effective clocks. On multiple sockets the first package is shown.",
                        "GPU temperature and clock: core metrics; memory, shader, and hot-spot sensors are not substituted.",
                        "GPU load: vendor core load, or the busiest Windows D3D engine when core load is unavailable.",
                        "RAM: physical total minus physical available, divided by 2^30 (Windows-style GB).",
                        "Null means unavailable or rejected as invalid. Zero percent load and zero MHz on a sleeping GPU can be valid readings.",
                        "Exactly zero Celsius is rejected because inaccessible hardware can report this placeholder; inspect raw sensors if using sub-zero cooling.",
                        "Historical sensor retention is disabled to keep memory use bounded."
                    },
                    Hardware = new List<HardwareReport>()
                };
                foreach (HardwareEntry entry in _entries)
                {
                    var item = new HardwareReport
                    {
                        Id = entry.Hardware.Identifier.ToString(),
                        Name = entry.Hardware.Name,
                        Type = entry.Hardware.HardwareType.ToString(),
                        LastUpdateError = entry.LastError,
                        Sensors = new List<RawSensorReport>()
                    };
                    foreach (Reading sensor in entry.Readings)
                        item.Sensors.Add(new RawSensorReport
                        {
                            Name = sensor.Name,
                            Type = sensor.Type.ToString(),
                            Value = sensor.Value.HasValue && !double.IsNaN(sensor.Value.Value) && !double.IsInfinity(sensor.Value.Value)
                                ? sensor.Value : null
                        });
                    report.Hardware.Add(item);
                }
                return report;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                try { _computer.Close(); }
                catch (Exception) { /* Shutdown must not keep the service process alive. */ }
                _entries.Clear();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SensorReader));
        }

        private static bool IsGpu(HardwareType type)
        {
            return type == HardwareType.GpuNvidia || type == HardwareType.GpuAmd || type == HardwareType.GpuIntel;
        }

        private static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static bool IsPawnIoRegistered()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO"))
                    return key != null;
            }
            catch (System.Security.SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static string ReadCpuName()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
            }
            catch (System.Security.SecurityException) { return "CPU"; }
            catch (UnauthorizedAccessException) { return "CPU"; }
        }

        private sealed class HardwareEntry
        {
            internal HardwareEntry(IHardware hardware)
            {
                Hardware = hardware;
                DisableHistory(hardware);
            }

            internal IHardware Hardware { get; }
            internal List<Reading> Readings { get; } = new List<Reading>(64);
            internal string? LastError { get; private set; }

            internal bool Update()
            {
                Readings.Clear();
                LastError = null;
                try
                {
                    UpdateAndCollect(Hardware);
                    return true;
                }
                catch (Exception error)
                {
                    LastError = error.GetType().Name;
                    Readings.Clear();
                    return false;
                }
            }

            private void UpdateAndCollect(IHardware hardware)
            {
                hardware.Update();
                // Sensors can activate after initialization; read the small sensor collection,
                // without repeating hardware discovery or enabling other categories.
                foreach (ISensor sensor in hardware.Sensors)
                {
                    // LHM otherwise retains up to a day of samples for every sensor.
                    if (sensor.ValuesTimeWindow != TimeSpan.Zero) sensor.ValuesTimeWindow = TimeSpan.Zero;
                    if (sensor.SensorType == SensorType.Temperature || sensor.SensorType == SensorType.Load || sensor.SensorType == SensorType.Clock)
                        Readings.Add(new Reading(sensor.Name, sensor.SensorType, sensor.Value));
                }
                foreach (IHardware child in hardware.SubHardware) UpdateAndCollect(child);
            }

            private static void DisableHistory(IHardware hardware)
            {
                foreach (ISensor sensor in hardware.Sensors) sensor.ValuesTimeWindow = TimeSpan.Zero;
                foreach (IHardware child in hardware.SubHardware) DisableHistory(child);
            }
        }
    }

    public sealed class SensorReport
    {
        public SensorSnapshot Snapshot { get; set; } = new SensorSnapshot();
        public string SensorLibraryVersion { get; set; } = "";
        public bool IsAdministrator { get; set; }
        public bool PawnIoDriverRegistered { get; set; }
        public string[] Definitions { get; set; } = Array.Empty<string>();
        public List<HardwareReport> Hardware { get; set; } = new List<HardwareReport>();
    }

    public sealed class HardwareReport
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string? LastUpdateError { get; set; }
        public List<RawSensorReport> Sensors { get; set; } = new List<RawSensorReport>();
    }

    public sealed class RawSensorReport
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public double? Value { get; set; }
    }
}
