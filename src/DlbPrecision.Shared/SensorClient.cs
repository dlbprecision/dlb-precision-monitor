using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Serialization;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace DlbPrecision.Shared
{
    public sealed class SensorClient : IDisposable
    {
        public const string PipeName = "DLBPrecision.Sensors.v1";
        public const string ServiceName = "DlbPrecisionSensors";
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly string pipeName;
        private readonly bool verifyServiceIdentity;

        public SensorClient(string? pipeName = null, bool allowUninstalledHost = false)
        {
            this.pipeName = pipeName ?? PipeName;
            // Production always authenticates the installed service. The explicit opt-out is
            // only for the diagnostic console host. Isolated GUID fixtures cannot address the
            // production pipe and do not require a service installation to run unit tests.
            const string fixturePrefix = "DLBPrecision.Test.";
            bool isolatedFixture = this.pipeName.StartsWith(fixturePrefix, StringComparison.Ordinal)
                && Guid.TryParseExact(this.pipeName.Substring(fixturePrefix.Length), "N", out _);
            verifyServiceIdentity = !allowUninstalledHost && !isolatedFixture;
        }

        public async Task<SensorSnapshot> ReadAsync(int refreshMilliseconds, CancellationToken cancellationToken)
        {
            // Only data flows from the service. The client cannot send commands or file paths.
            // Polling frequency is owned by the widget; a 2s UI interval causes 2s sensor work.
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token))
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.Asynchronous))
            {
                timeout.CancelAfter(4000);
                try
                {
                    await pipe.ConnectAsync(1500, timeout.Token).ConfigureAwait(false);
                    using (timeout.Token.Register(() => pipe.Dispose()))
                    using (var buffer = new MemoryStream())
                    {
                        if (verifyServiceIdentity && !PipeServerIdentity.IsInstalledService(pipe, ServiceName))
                            return SensorSnapshot.Unavailable("Sensor service identity could not be verified. Install or repair DLB Precision Monitor.");
                        var chunk = new byte[4096];
                        int count;
                        while ((count = await pipe.ReadAsync(chunk, 0, chunk.Length, timeout.Token).ConfigureAwait(false)) > 0)
                        {
                            if (buffer.Length + count > SnapshotCodec.MaximumBytes)
                                throw new SerializationException("Sensor response is too large.");
                            buffer.Write(chunk, 0, count);
                        }
                        var snapshot = SnapshotCodec.Decode(buffer.ToArray());
                        var age = DateTime.UtcNow - snapshot.TimestampUtc.ToUniversalTime();
                        if (age > TimeSpan.FromSeconds(10) || age < TimeSpan.FromMinutes(-1))
                            return SensorSnapshot.Unavailable("Sensor readings are stale. Check the sensor service.");
                        return snapshot;
                    }
                }
                catch (Exception exception) when (exception is IOException || exception is TimeoutException
                    || exception is UnauthorizedAccessException || exception is SecurityException
                    || exception is OperationCanceledException || exception is ObjectDisposedException
                    || exception is SerializationException || exception is System.Xml.XmlException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lifetime.Token.ThrowIfCancellationRequested();
                    return SensorSnapshot.Unavailable(exception is SerializationException
                        ? "Sensor response could not be read. Repair the installation."
                        : "Sensor service unavailable. Install or repair DLB Precision Monitor.");
                }
            }
        }

        public void Dispose() { lifetime.Cancel(); }
    }
}
