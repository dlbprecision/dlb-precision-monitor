using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace DlbPrecision.Shared
{
    [DataContract]
    public sealed class SensorSnapshot
    {
        [DataMember(Order = 0)] public int ProtocolVersion { get; set; } = 1;
        [DataMember(Order = 1)] public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        [DataMember(Order = 2)] public string CpuName { get; set; } = "";
        [DataMember(Order = 3)] public double? CpuTemperatureC { get; set; }
        [DataMember(Order = 4)] public double? CpuLoadPercent { get; set; }
        [DataMember(Order = 5)] public double? CpuClockMhz { get; set; }
        [DataMember(Order = 6)] public double? RamUsedGb { get; set; }
        [DataMember(Order = 7)] public List<GpuSnapshot> Gpus { get; set; } = new List<GpuSnapshot>();
        [DataMember(Order = 8)] public string Status { get; set; } = "Starting";
        [DataMember(Order = 9)] public List<string> Warnings { get; set; } = new List<string>();

        public static SensorSnapshot Unavailable(string reason) => new SensorSnapshot
        {
            Status = reason,
            Warnings = new List<string> { reason }
        };
    }

    [DataContract]
    public sealed class GpuSnapshot
    {
        [DataMember(Order = 0)] public string Id { get; set; } = "";
        [DataMember(Order = 1)] public string Name { get; set; } = "";
        [DataMember(Order = 2)] public double? TemperatureC { get; set; }
        [DataMember(Order = 3)] public double? LoadPercent { get; set; }
        [DataMember(Order = 4)] public double? ClockMhz { get; set; }
    }
}
