using System;
using DlbPrecision.Sensors;
using LibreHardwareMonitor.Hardware;

namespace DlbPrecision.Probe
{
    internal static class SelectionTests
    {
        internal static int Run()
        {
            Expect("package preferred over hottest core", 65, SensorSelection.CpuTemperature(new[]
            {
                R("Core Max", SensorType.Temperature, 90), R("CPU Package", SensorType.Temperature, 65)
            }));
            Expect("AMD physical die preferred over control offset", 55, SensorSelection.CpuTemperature(new[]
            {
                R("Core (Tctl)", SensorType.Temperature, 75), R("Core (Tdie)", SensorType.Temperature, 55)
            }));
            Expect("invalid package falls back without using distance", 48, SensorSelection.CpuTemperature(new[]
            {
                R("CPU Package", SensorType.Temperature, double.NaN),
                R("CPU Core #1 Distance to TjMax", SensorType.Temperature, 99),
                R("CPU Core #1", SensorType.Temperature, 48)
            }));
            Expect("hybrid CPU core mean excludes bus and effective clock", 3000, SensorSelection.CpuClock(new[]
            {
                R("Bus Speed", SensorType.Clock, 100), R("P-Core #1", SensorType.Clock, 4000),
                R("E-Core #1", SensorType.Clock, 2000), R("Core #1 (Effective)", SensorType.Clock, 150)
            }));
            Expect("AMD reported aggregate excludes effective aggregate", 4500, SensorSelection.CpuClock(new[]
            {
                R("Cores (Average)", SensorType.Clock, 4500), R("Cores (Average Effective)", SensorType.Clock, 500)
            }));
            Expect("GPU does not substitute memory clock", null, SensorSelection.GpuClock(new[]
            {
                R("GPU Memory", SensorType.Clock, 10500), R("GPU Shader", SensorType.Clock, 2100)
            }));
            Expect("GPU does not substitute hot spot", null, SensorSelection.GpuTemperature(new[]
            {
                R("GPU Hot Spot", SensorType.Temperature, 80), R("GPU Memory Junction", SensorType.Temperature, 90)
            }));
            Expect("idle GPU load is valid", 0, SensorSelection.GpuLoad(new[] { R("GPU Core", SensorType.Load, 0) }));
            Expect("idle GPU clock is valid", 0, SensorSelection.GpuClock(new[] { R("GPU Core", SensorType.Clock, 0) }));
            Expect("busiest D3D engine does not add percentages", 70, SensorSelection.GpuLoad(new[]
            {
                R("D3D 3D", SensorType.Load, 70), R("D3D Copy", SensorType.Load, 60),
                R("GPU Memory", SensorType.Load, 95)
            }));
            Expect("invalid vendor load permits valid D3D fallback", 12, SensorSelection.GpuLoad(new[]
            {
                R("GPU Core", SensorType.Load, 110), R("D3D 3D", SensorType.Load, 12)
            }));
            Expect("non-finite readings rejected", null, SensorSelection.Valid(double.PositiveInfinity, 0, 100));
            Expect("missing value stays null", null, SensorSelection.Valid(null, 0, 100));
            Expect("inaccessible AMD temperature zero is unavailable", null, SensorSelection.CpuTemperature(new[]
            {
                R("Core (Tctl/Tdie)", SensorType.Temperature, 0)
            }));
            Expect("inaccessible AMD clock zero is unavailable", null, SensorSelection.CpuClock(new[]
            {
                R("Cores (Average)", SensorType.Clock, 0), R("Core #1", SensorType.Clock, null)
            }));
            Console.WriteLine("PASS: 15 sensor selection checks.");
            return 0;
        }

        private static Reading R(string name, SensorType type, double? value) => new Reading(name, type, value);

        private static void Expect(string name, double? expected, double? actual)
        {
            if (expected.HasValue != actual.HasValue || (expected.HasValue && actual.HasValue && Math.Abs(expected.Value - actual.Value) > 0.001))
                throw new InvalidOperationException(name + ": expected " + expected + ", actual " + actual);
        }
    }
}
