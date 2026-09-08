using System;
using System.Drawing;

namespace DlbPrecision.Monitor
{
    internal static class WidgetSizing
    {
        public const int MinPercent = 75;
        public const int MaxPercent = 200;

        private static Size DefaultSize(bool vertical) => vertical ? new Size(144, 716) : new Size(861, 128);

        public static Size GetSize(bool vertical, int percent, float dpiScale)
        {
            Size reference = DefaultSize(vertical);
            double scale = Math.Max(MinPercent, Math.Min(MaxPercent, percent)) / 100.0 * dpiScale;
            return new Size((int)Math.Round(reference.Width * scale), (int)Math.Round(reference.Height * scale));
        }

        public static int GetPercent(bool vertical, Size size, float dpiScale)
        {
            Size reference = DefaultSize(vertical);
            double ratio = Math.Min(size.Width / (reference.Width * (double)dpiScale), size.Height / (reference.Height * (double)dpiScale));
            return Math.Max(MinPercent, Math.Min(MaxPercent, (int)Math.Round(ratio * 100)));
        }

        public static Size MinimumSize(bool vertical, float dpiScale) => vertical
            ? new Size((int)(108 * dpiScale), (int)(486 * dpiScale))
            : new Size((int)(590 * dpiScale), (int)(96 * dpiScale));

        public static Rectangle Apply(Rectangle current, bool wasVertical, bool vertical, int? requestedPercent, float dpiScale, Rectangle[] workingAreas)
        {
            // Preserve custom edge-dragged dimensions unless size or layout was changed explicitly.
            Size target = requestedPercent.HasValue || wasVertical != vertical
                ? GetSize(vertical, requestedPercent ?? GetPercent(wasVertical, current.Size, dpiScale), dpiScale)
                : current.Size;
            Rectangle fitted = WindowPlacement.Fit(new Rectangle(current.Location, target), workingAreas, MinimumSize(vertical, dpiScale));
            if ((requestedPercent.HasValue || wasVertical != vertical) && fitted.Size != target)
            {
                // A large slider setting should shrink uniformly to fit its display.
                double fit = Math.Min(fitted.Width / (double)target.Width, fitted.Height / (double)target.Height);
                target = new Size((int)Math.Round(target.Width * fit), (int)Math.Round(target.Height * fit));
                fitted = WindowPlacement.Fit(new Rectangle(current.Location, target), workingAreas, MinimumSize(vertical, dpiScale));
            }
            return fitted;
        }
    }
}
