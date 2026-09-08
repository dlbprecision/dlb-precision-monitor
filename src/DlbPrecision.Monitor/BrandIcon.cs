using System;
using System.Drawing;
using System.Reflection;

namespace DlbPrecision.Monitor
{
    internal static class BrandIcon
    {
        public static Icon Load(int size = 32)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DlbPrecision.Monitor.monitor.ico"))
            {
                if (stream == null) throw new InvalidOperationException("The DLB Precision icon resource is missing.");
                using (var icon = new Icon(stream, size, size)) return (Icon)icon.Clone();
            }
        }
    }
}
