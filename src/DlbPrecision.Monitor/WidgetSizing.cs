using System;
using System.Drawing;

namespace DlbPrecision.Monitor
{
    internal static class WidgetSizing
    {
        // 50% is the smallest size whose readings stay legible; 150% keeps the largest setting
        // usable on a 1080p display.
        public const int MinPercent = 50;
        public const int MaxPercent = 150;

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

        // Edge-dragging stops at the same size as the smallest slider setting.
        public static Size MinimumSize(bool vertical, float dpiScale) => GetSize(vertical, MinPercent, dpiScale);

        // How far the widget is scaled from its 100% size, for drawing; custom edge-dragged shapes use the tighter side.
        public static float Zoom(bool vertical, Size size, float dpiScale)
        {
            Size reference = DefaultSize(vertical);
            double ratio = Math.Min(size.Width / (reference.Width * (double)dpiScale), size.Height / (reference.Height * (double)dpiScale));
            return (float)Math.Max(MinPercent / 100.0, Math.Min(MaxPercent / 100.0, ratio));
        }

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
