using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DlbPrecision.Shared;

internal static class Program
{
    private static int assertions;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            CodecTests();
            await ClientTests();
            if (Array.IndexOf(args, "--integration") >= 0)
                await IntegrationTests(Array.IndexOf(args, "--allow-console-host") >= 0);
            Console.WriteLine("PASS: " + assertions + " assertions (serialization, unavailable data, protocol, IPC timeouts/cancellation" +
                (Array.IndexOf(args, "--integration") >= 0 ? ", live service" : "") + ").");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CodecTests()
    {
        var data = new SensorSnapshot
        {
            CpuName = "CPU \"test\"", CpuTemperatureC = 51.25, CpuLoadPercent = 0,
            CpuClockMhz = 5590, RamUsedGb = 20.2, Status = "Live",
            Gpus = new List<GpuSnapshot> { new GpuSnapshot { Id = "gpu-0", Name = "GPU", TemperatureC = 32, LoadPercent = 1, ClockMhz = 510 } }
        };
        var restored = SnapshotCodec.Decode(SnapshotCodec.Encode(data));
        Check(restored.CpuName == data.CpuName, "Names must survive JSON escaping.");
        Check(restored.CpuTemperatureC == data.CpuTemperatureC, "Celsius must retain precision before display conversion.");
        Check(restored.CpuLoadPercent == 0, "Genuine zero usage must remain zero.");
        Check(restored.Gpus.Count == 1 && restored.Gpus[0].ClockMhz == 510, "GPU core clock must round-trip.");
        var unavailable = SnapshotCodec.Decode(SnapshotCodec.Encode(SensorSnapshot.Unavailable("Missing driver")));
        Check(unavailable.CpuTemperatureC == null && unavailable.CpuClockMhz == null, "Missing sensors cannot become zero.");
        data.CpuLoadPercent = 101; data.CpuTemperatureC = double.NaN; data.CpuClockMhz = -1;
        data.Gpus[0].LoadPercent = -1;
        restored = SnapshotCodec.Decode(SnapshotCodec.Encode(data));
        Check(restored.CpuLoadPercent == null && restored.CpuTemperatureC == null && restored.CpuClockMhz == null,
            "Invalid readings must be rejected.");
        Check(restored.Gpus[0].LoadPercent == null, "Invalid GPU usage must be rejected.");
        bool rejected = false;
        try { SnapshotCodec.Decode(new byte[SnapshotCodec.MaximumBytes + 1]); } catch (SerializationException) { rejected = true; }
        Check(rejected, "Oversize response must be rejected before parse.");
        data.ProtocolVersion = 999; rejected = false;
        try { SnapshotCodec.Decode(SnapshotCodec.Encode(data)); } catch (SerializationException) { rejected = true; }
        Check(rejected, "Protocol mismatch must fail explicitly.");
    }

    private static async Task<SensorSnapshot> ReadFixture(byte[] payload, int delay = 0, string? pipeName = null, bool allowUninstalledHost = false)
    {
        var name = pipeName ?? "DLBPrecision.Test." + Guid.NewGuid().ToString("N");
        using (var server = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        using (var client = new SensorClient(name, allowUninstalledHost))
        {
            var serverTask = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync();
                if (delay > 0) await Task.Delay(delay);
                try { await server.WriteAsync(payload, 0, payload.Length); }
                catch (System.IO.IOException) { /* An unauthenticated server is disconnected before its payload is read. */ }
                server.Dispose();
            });
            var response = await client.ReadAsync(1000, CancellationToken.None);
            await serverTask;
            return response;
        }
    }

    private static async Task ClientTests()
    {
        var data = new SensorSnapshot { CpuTemperatureC = 55, CpuLoadPercent = 17, RamUsedGb = 12.4, Status = "Live" };
        var response = await ReadFixture(SnapshotCodec.Encode(data));
        Check(response.CpuTemperatureC == 55, "Client must read output-only pipe response.");
        response = await ReadFixture(SnapshotCodec.Encode(data), pipeName: "DLBPrecision.Untrusted." + Guid.NewGuid().ToString("N"));
        Check(response.CpuTemperatureC == null && response.Status.Contains("identity"),
            "A fresh-looking response from a process that is not the installed service must be rejected.");
        response = await ReadFixture(SnapshotCodec.Encode(data), pipeName: "DLBPrecision.Untrusted." + Guid.NewGuid().ToString("N"), allowUninstalledHost: true);
        Check(response.CpuTemperatureC == 55, "Diagnostic console-host bypass must require explicit opt-in.");
        data.TimestampUtc = DateTime.UtcNow.AddMinutes(-2);
        response = await ReadFixture(SnapshotCodec.Encode(data));
        Check(response.CpuTemperatureC == null && response.Status.IndexOf("stale", StringComparison.OrdinalIgnoreCase) >= 0,
            "Stale service values must not look live.");
        data.TimestampUtc = DateTime.UtcNow.AddMinutes(3);
        response = await ReadFixture(SnapshotCodec.Encode(data));
        Check(response.CpuLoadPercent == null, "Future dated response must be rejected.");
        response = await ReadFixture(Encoding.UTF8.GetBytes("not valid JSON"));
        Check(response.CpuTemperatureC == null, "Malformed service response must not crash widget.");
        using (var client = new SensorClient("DLBPrecision.Missing." + Guid.NewGuid().ToString("N")))
        {
            var timer = Stopwatch.StartNew();
            response = await client.ReadAsync(1000, CancellationToken.None);
            Check(response.CpuTemperatureC == null && response.Status.Contains("unavailable"), "Missing service must be explicit.");
            Check(timer.Elapsed < TimeSpan.FromSeconds(4), "Missing service should fail quickly.");
        }
        using (var client = new SensorClient("DLBPrecision.Missing." + Guid.NewGuid().ToString("N")))
        using (var cancel = new CancellationTokenSource(50))
        {
            bool cancelled = false;
            try { await client.ReadAsync(1000, cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Widget shutdown must cancel outstanding IPC.");
        }
    }

    private static async Task IntegrationTests(bool allowConsoleHost)
    {
        using (var client = new SensorClient(allowUninstalledHost: allowConsoleHost))
        {
            SensorSnapshot? current = null;
            for (int i = 0; i < 5; i++)
            {
                current = await client.ReadAsync(1000, CancellationToken.None);
                if (current.RamUsedGb.HasValue) break;
                await Task.Delay(1000);
            }
            Check(current != null && current.RamUsedGb > 0, "Running service must provide physical memory.");
            Check(current!.CpuName.Length > 0, "Running service must identify CPU.");
            await Task.Delay(1100);
            var next = await client.ReadAsync(1000, CancellationToken.None);
            Check(next.TimestampUtc > current.TimestampUtc, "Requested service samples must advance.");
            Check(next.CpuLoadPercent >= 0 && next.CpuLoadPercent <= 100, "CPU usage must be valid after warm-up.");
            Console.WriteLine(SnapshotCodec.ToJson(next));
        }
    }
}
