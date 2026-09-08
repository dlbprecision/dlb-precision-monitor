using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using DlbPrecision.Sensors;
using DlbPrecision.Shared;

namespace DlbPrecision.Service
{
    internal sealed class SensorHost : IDisposable
    {
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly object sampling = new object();
        private readonly object pipeGate = new object();
        private readonly HashSet<NamedPipeServerStream> pipes = new HashSet<NamedPipeServerStream>();
        private readonly List<Task> workers = new List<Task>();
        private readonly Stopwatch cacheClock = Stopwatch.StartNew();
        private SensorReader? reader;
        private byte[]? cache;
        private long lastSample = -1000;
        private bool disposed;

        public void Start()
        {
            // Multiple local widgets share a maximum of one hardware sample per second.
            // No periodic timer: if every widget exits, sensor updates stop entirely.
            for (int i = 0; i < 3; i++) workers.Add(Task.Run(() => ServeAsync(stop.Token)));
        }

        private static PipeSecurity CreateSecurity()
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            // Explicitly exclude network logons. Clients are local and can read only.
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            var current = WindowsIdentity.GetCurrent().User;
            if (current != null)
                security.AddAccessRule(new PipeAccessRule(current, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.Read | PipeAccessRights.Synchronize, AccessControlType.Allow));
            return security;
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(SensorClient.PipeName, PipeDirection.Out, 4,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 8192, CreateSecurity());
                    lock (pipeGate)
                    {
                        if (cancellationToken.IsCancellationRequested) { pipe.Dispose(); return; }
                        pipes.Add(pipe);
                    }
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    var response = ReadCached();
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        timeout.CancelAfter(2000);
                        using (timeout.Token.Register(() => pipe.Dispose()))
                            await pipe.WriteAsync(response, 0, response.Length, timeout.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException
                    || e is ObjectDisposedException || e is OperationCanceledException)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        try { await Task.Delay(1000, cancellationToken).ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                    }
                }
                finally
                {
                    if (pipe != null)
                    {
                        lock (pipeGate) pipes.Remove(pipe);
                        pipe.Dispose();
                    }
                }
            }
        }

        private byte[] ReadCached()
        {
            lock (sampling)
            {
                if (stop.IsCancellationRequested) return SnapshotCodec.Encode(SensorSnapshot.Unavailable("Sensor service is stopping."));
                if (cache != null && cacheClock.ElapsedMilliseconds - lastSample < 1000) return cache;
                SensorSnapshot snapshot;
                try
                {
                    if (reader == null) reader = new SensorReader();
                    snapshot = reader.Read();
                }
                catch (Exception e)
                {
                    snapshot = SensorSnapshot.Unavailable("Sensor update failed (" + e.GetType().Name + "). Restart the sensor service.");
                }
                snapshot.TimestampUtc = DateTime.UtcNow;
                cache = SnapshotCodec.Encode(snapshot);
                lastSample = cacheClock.ElapsedMilliseconds;
                return cache;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            stop.Cancel();
            lock (pipeGate) foreach (var pipe in pipes) pipe.Dispose();
            try { Task.WaitAll(workers.ToArray(), 5000); }
            catch (AggregateException) { }
            // Both lock acquisition and native driver cleanup can stall. Run cleanup on a
            // background worker and bound the wait so SCM always receives a stop response.
            var cleanup = Task.Run(() =>
            {
                lock (sampling) { reader?.Dispose(); reader = null; }
            });
            try { cleanup.Wait(1000); } catch (AggregateException) { }
        }
    }
}
