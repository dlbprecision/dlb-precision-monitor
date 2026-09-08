using System;
using System.Collections.Generic;
using LibreHardwareMonitor.Hardware;

namespace DlbPrecision.Sensors
{
    // Selection is deliberately independent of hardware so naming and fallback rules can be verified.
    internal readonly struct Reading
    {
        public Reading(string name, SensorType type, double? value)
        {
            Name = name ?? string.Empty;
            Type = type;
            Value = value;
        }

        public string Name { get; }
        public SensorType Type { get; }
        public double? Value { get; }
    }

    internal static class SensorSelection
    {
        internal static double? Valid(double? value, double minimum, double maximum)
        {
            return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)
                && value.Value >= minimum && value.Value <= maximum ? value : null;
        }

        internal static double? CpuTemperature(IReadOnlyList<Reading> sensors)
        {
            // Prefer a physical die temperature over AMD's offset fan-control temperature.
            double? result = FirstNamed(sensors, SensorType.Temperature, -30, 130,
                "CPU Package", "Core (Tdie)", "Core (Tctl/Tdie)", "CPU (Tctl/Tdie)",
                "Core (Tctl)", "CPU (Tctl)", "Core Max", "CCDs Max (Tdie)", "Core Average");
            if (result.HasValue) return result;

            // Older processors expose only individual cores. Never use Distance to TjMax.
            double? hottest = null;
            foreach (Reading sensor in sensors)
            {
                if (sensor.Type != SensorType.Temperature || !IsCore(sensor.Name)
                    || Contains(sensor.Name, "Distance") || Contains(sensor.Name, "TjMax")) continue;
                double? value = Valid(sensor.Value, -30, 130);
                if (value == 0) continue; // A zero temperature is also returned for inaccessible AMD sensors.
                if (value.HasValue && (!hottest.HasValue || value > hottest)) hottest = value;
            }
            return hottest;
        }

        internal static double? CpuLoad(IReadOnlyList<Reading> sensors)
        {
            double? total = FirstNamed(sensors, SensorType.Load, 0, 100, "CPU Total");
            if (total.HasValue) return total;
            return CoreAverage(sensors, SensorType.Load, 0, 100);
        }

        internal static double? CpuClock(IReadOnlyList<Reading> sensors)
        {
            double? reportedAverage = FirstNamed(sensors, SensorType.Clock, 1, 15000, "Cores (Average)");
            return reportedAverage ?? CoreAverage(sensors, SensorType.Clock, 1, 15000);
        }

        internal static double? GpuTemperature(IReadOnlyList<Reading> sensors)
        {
            // A hot spot or memory junction is a different metric, not a core fallback.
            return FirstNamed(sensors, SensorType.Temperature, -30, 130, "GPU Core", "GPU Temperature", "GPU");
        }

        internal static double? GpuLoad(IReadOnlyList<Reading> sensors)
        {
            double? core = FirstNamed(sensors, SensorType.Load, 0, 100, "GPU Core", "GPU Total");
            if (core.HasValue) return core;
            // Intel iGPUs expose Windows D3D engines instead of a vendor aggregate.
            // Busiest engine avoids adding concurrently active engine percentages beyond 100%.
            double? busiest = null;
            foreach (Reading sensor in sensors)
            {
                if (sensor.Type != SensorType.Load || !sensor.Name.StartsWith("D3D ", StringComparison.OrdinalIgnoreCase)
                    || Contains(sensor.Name, "Memory")) continue;
                double? value = Valid(sensor.Value, 0, 100);
                if (value.HasValue && (!busiest.HasValue || value > busiest)) busiest = value;
            }
            return busiest;
        }

        internal static double? GpuClock(IReadOnlyList<Reading> sensors)
        {
            return FirstNamed(sensors, SensorType.Clock, 0, 15000, "GPU Core", "GPU Graphics");
        }

        private static double? FirstNamed(IReadOnlyList<Reading> sensors, SensorType type,
            double minimum, double maximum, params string[] names)
        {
            foreach (string name in names)
                foreach (Reading sensor in sensors)
                    if (sensor.Type == type && string.Equals(sensor.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        double? value = Valid(sensor.Value, minimum, maximum);
                        if (type == SensorType.Temperature && value == 0) continue;
                        if (value.HasValue) return value;
                    }
            return null;
        }

        private static double? CoreAverage(IReadOnlyList<Reading> sensors, SensorType type, double minimum, double maximum)
        {
            double sum = 0;
            int count = 0;
            foreach (Reading sensor in sensors)
            {
                if (sensor.Type != type || !IsCore(sensor.Name) || Contains(sensor.Name, "Effective")) continue;
                double? value = Valid(sensor.Value, minimum, maximum);
                if (!value.HasValue) continue;
                sum += value.Value;
                count++;
            }
            return count > 0 ? sum / count : (double?)null;
        }

        private static bool IsCore(string name)
        {
            return string.Equals(name, "CPU Core", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("CPU Core Thread #", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("P-Core #", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("E-Core #", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("CPU P-Core #", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("CPU E-Core #", StringComparison.OrdinalIgnoreCase);
        }

        private static bool Contains(string source, string value)
        {
            return source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
