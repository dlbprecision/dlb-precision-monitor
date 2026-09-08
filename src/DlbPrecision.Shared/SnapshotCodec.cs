using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace DlbPrecision.Shared
{
    public static class SnapshotCodec
    {
        public const int MaximumBytes = 65536;

        public static byte[] Encode(SensorSnapshot snapshot)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(SensorSnapshot)).WriteObject(stream, snapshot);
                if (stream.Length > MaximumBytes) throw new SerializationException("Sensor response is too large.");
                return stream.ToArray();
            }
        }

        public static string ToJson(SensorSnapshot snapshot) => Encoding.UTF8.GetString(Encode(snapshot));

        public static SensorSnapshot Decode(byte[] bytes)
        {
            if (bytes.Length == 0 || bytes.Length > MaximumBytes)
                throw new SerializationException("Invalid sensor response size.");
            using (var stream = new MemoryStream(bytes, false))
            {
                var snapshot = (SensorSnapshot?)new DataContractJsonSerializer(typeof(SensorSnapshot)).ReadObject(stream);
                if (snapshot == null || snapshot.ProtocolVersion != 1)
                    throw new SerializationException("Unsupported sensor protocol; reinstall the matching app and service.");
                snapshot.Gpus = snapshot.Gpus ?? new System.Collections.Generic.List<GpuSnapshot>();
                snapshot.Warnings = snapshot.Warnings ?? new System.Collections.Generic.List<string>();
                snapshot.CpuName = snapshot.CpuName ?? "";
                snapshot.Status = snapshot.Status ?? "";
                // Never pass invalid numeric values from IPC into the display.
                snapshot.CpuTemperatureC = Valid(snapshot.CpuTemperatureC, -40, 150);
                snapshot.CpuLoadPercent = Valid(snapshot.CpuLoadPercent, 0, 100);
                snapshot.CpuClockMhz = Valid(snapshot.CpuClockMhz, 0, 15000);
                snapshot.RamUsedGb = Valid(snapshot.RamUsedGb, 0, 1048576);
                snapshot.Gpus.RemoveAll(gpu => gpu == null);
                foreach (var gpu in snapshot.Gpus)
                {
                    gpu.Id = gpu.Id ?? "";
                    gpu.Name = gpu.Name ?? "";
                    gpu.TemperatureC = Valid(gpu.TemperatureC, -40, 150);
                    gpu.LoadPercent = Valid(gpu.LoadPercent, 0, 100);
                    gpu.ClockMhz = Valid(gpu.ClockMhz, 0, 15000);
                }
                return snapshot;
            }
        }

        private static double? Valid(double? value, double minimum, double maximum) =>
            value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)
            && value.Value >= minimum && value.Value <= maximum ? value : null;
    }
}
