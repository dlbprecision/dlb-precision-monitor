using System;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;

namespace DlbPrecision.Service
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
            if (args.Contains("--console", StringComparer.OrdinalIgnoreCase))
            {
                using (var host = new SensorHost())
                using (var stop = new ManualResetEventSlim())
                {
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
                    host.Start();
                    Console.WriteLine("DLB Precision sensor host. Press Ctrl+C to stop. Readings use current account permissions.");
                    int seconds = 0;
                    var argument = args.FirstOrDefault(a => a.StartsWith("--seconds=", StringComparison.OrdinalIgnoreCase));
                    if (argument != null) int.TryParse(argument.Substring(10), out seconds);
                    if (seconds > 0) stop.Wait(TimeSpan.FromSeconds(seconds));
                    else stop.Wait();
                }
                return 0;
            }
            if (Environment.UserInteractive)
            {
                Console.Error.WriteLine("This component runs as a Windows service installed by DLB setup. For diagnostics use --console.");
                return 2;
            }
            ServiceBase.Run(new MonitorService());
            return 0;
        }
    }

    internal sealed class MonitorService : ServiceBase
    {
        private SensorHost? host;
        public MonitorService()
        {
            ServiceName = Shared.SensorClient.ServiceName;
            CanStop = true;
            CanShutdown = true;
            AutoLog = true;
        }
        protected override void OnStart(string[] args) { host = new SensorHost(); host.Start(); }
        protected override void OnStop() { host?.Dispose(); host = null; }
        protected override void OnShutdown() => OnStop();
    }
}
