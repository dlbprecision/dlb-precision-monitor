using System;
using System.Threading;
using System.Web.Script.Serialization;
using DlbPrecision.Sensors;
using DlbPrecision.Shared;

namespace DlbPrecision.Probe
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                string mode = args.Length > 0 ? args[0] : "--sample";
                if (mode == "--self-test") return SelectionTests.Run();
                if (mode != "--sample" && mode != "--report")
                {
                    Console.Error.WriteLine("Usage: DlbPrecision.Probe.exe [--sample|--report|--self-test]");
                    return 2;
                }
                using (var reader = new SensorReader())
                {
                    reader.Read();
                    Thread.Sleep(1000);
                    Console.WriteLine(mode == "--report"
                        ? new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 }.Serialize(reader.GetReport())
                        : SnapshotCodec.ToJson(reader.Read()));
                }
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
                return 1;
            }
        }
    }
}
