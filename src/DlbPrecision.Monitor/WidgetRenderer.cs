using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using DlbPrecision.Shared;

namespace DlbPrecision.Monitor
{
    internal static class WidgetRenderer
    {
        public static readonly Color Background = Color.FromArgb(17, 17, 22);
        public static readonly Color Surface = Color.FromArgb(25, 25, 31);
        public static readonly Color Blue = ColorTranslator.FromHtml("#2DA4F4");
        public static readonly Color Purple = ColorTranslator.FromHtml("#7B00FF");
        private static readonly Color Label = Color.FromArgb(189, 184, 201);
        private static readonly Color Muted = Color.FromArgb(131, 125, 145);
        private static readonly Color Border = Color.FromArgb(53, 49, 63);
        private static readonly string[] Labels = { "CPU Temp", "CPU Load", "CPU Clock", "GPU Temp", "GPU Load", "GPU Clock", "RAM Load" };

        internal readonly struct Reading
        {
            public readonly string Number;
            public readonly string Unit;
            public Reading(string number, string unit) { Number = number; Unit = unit; }
        }

        public static Reading Format(double? value, string unit, int decimals = 0)
        {
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value.Value < 0)
                return new Reading("—", unit);
            return new Reading(value.Value.ToString(decimals == 1 ? "0.0" : "0", CultureInfo.CurrentCulture), unit);
        }

        public static Reading Temperature(double? value, bool fahrenheit)
        {
            if (value.HasValue && fahrenheit) value = value.Value * 9.0 / 5.0 + 32.0;
            return Format(value, fahrenheit ? "°F" : "°C");
        }

        public static GpuSnapshot? SelectGpu(SensorSnapshot? snapshot, string id)
        {
            if (snapshot?.Gpus == null || snapshot.Gpus.Count == 0) return null;
            return snapshot.Gpus.FirstOrDefault(g => g.Id == id) ?? snapshot.Gpus[0];
        }

        public static Reading[] Readings(SensorSnapshot? snapshot, MonitorSettings settings)
        {
            GpuSnapshot? gpu = SelectGpu(snapshot, settings.GpuId);
            return new[] {
                Temperature(snapshot?.CpuTemperatureC, settings.Fahrenheit),
                Format(snapshot?.CpuLoadPercent, "%"), Format(snapshot?.CpuClockMhz, "MHz"),
                Temperature(gpu?.TemperatureC, settings.Fahrenheit),
                Format(gpu?.LoadPercent, "%"), Format(gpu?.ClockMhz, "MHz"),
                Format(snapshot?.RamUsedGb, "GB", 1)
            };
        }

        public static void Paint(Graphics graphics, Rectangle bounds, MonitorSettings settings, SensorSnapshot? snapshot, string status, float dpiScale, bool sample = false)
        {
            graphics.Clear(Background);
            graphics.SmoothingMode = SmoothingMode.None;
            float margin = 6 * dpiScale;
            float gap = 6 * dpiScale;
            bool showFooter = settings.Branding || !string.IsNullOrEmpty(status) || sample;
            float footer = showFooter ? 18 * dpiScale : 0;
            float width = Math.Max(1, bounds.Width - margin * 2);
            float height = Math.Max(1, bounds.Height - margin * 2 - footer);
            float tileWidth = settings.Vertical ? width : (width - gap * 6) / 7;
            float tileHeight = settings.Vertical ? (height - gap * 6) / 7 : height;
            Reading[] readings = Readings(snapshot, settings);
            using (var surfaceBrush = new SolidBrush(Surface))
            using (var borderPen = new Pen(Border))
            using (var bluePen = new Pen(Blue, Math.Max(1, 2 * dpiScale)))
            using (var purplePen = new Pen(Purple, Math.Max(1, 2 * dpiScale)))
            {
                for (int index = 0; index < 7; index++)
                {
                    float x = bounds.Left + margin + (settings.Vertical ? 0 : index * (tileWidth + gap));
                    float y = bounds.Top + margin + (settings.Vertical ? index * (tileHeight + gap) : 0);
                    var tile = new RectangleF(x, y, Math.Max(1, tileWidth), Math.Max(1, tileHeight));
                    graphics.FillRectangle(surfaceBrush, tile);
                    graphics.DrawRectangle(borderPen, tile.X + .5f, tile.Y + .5f, tile.Width - 1, tile.Height - 1);
                    graphics.DrawLine(index >= 3 && index <= 5 ? purplePen : bluePen, x + 1, y + 1, x + tileWidth - 1, y + 1);
                    DrawReading(graphics, tile, readings[index], Labels[index], dpiScale);
                }
            }
            if (showFooter)
            {
                string footerText = sample ? "SAMPLE DATA" : !string.IsNullOrEmpty(status) ? status : "DLB PRECISION";
                var textBounds = new Rectangle((int)margin, bounds.Bottom - (int)(footer + margin) + (int)(3 * dpiScale), (int)width, (int)footer);
                using (var actualFont = new Font("Segoe UI", 9.2f * dpiScale, FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    // Pixel fonts keep this quiet footer consistent with the custom DPI-scaled tile layout.
                    TextRenderer.DrawText(graphics, footerText, actualFont, textBounds, Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                }
            }
            if (!settings.PositionLocked)
            {
                using (var gripPen = new Pen(Border, Math.Max(1, dpiScale)))
                    for (int index = 1; index <= 3; index++)
                    {
                        float inset = 3 * index * dpiScale;
                        graphics.DrawLine(gripPen, bounds.Right - 2 * dpiScale - inset, bounds.Bottom - 2 * dpiScale,
                            bounds.Right - 2 * dpiScale, bounds.Bottom - 2 * dpiScale - inset);
                    }
            }
        }

        private static void DrawReading(Graphics graphics, RectangleF tile, Reading value, string label, float dpiScale)
        {
            float scale = Math.Min(tile.Width / 113f, tile.Height / 98f);
            scale = Math.Max(.55f * dpiScale, Math.Min(2.5f * dpiScale, scale));
            float numberSize = 28f * scale;
            float unitSize = 14f * scale;
            float labelSize = 12f * scale;
            const TextFormatFlags measureFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            using (var probeNumber = new Font("Segoe UI Semibold", numberSize, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var probeUnit = new Font("Segoe UI", unitSize, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                float total = TextRenderer.MeasureText(graphics, value.Number, probeNumber, Size.Empty, measureFlags).Width + TextRenderer.MeasureText(graphics, value.Unit, probeUnit, Size.Empty, measureFlags).Width + 2 * scale;
                float fit = Math.Min(1, (tile.Width - 10 * dpiScale) / Math.Max(total, 1));
                numberSize *= fit;
                unitSize *= fit;
            }
            using (var numberFont = new Font("Segoe UI Semibold", Math.Max(8, numberSize), FontStyle.Regular, GraphicsUnit.Pixel))
            using (var unitFont = new Font("Segoe UI", Math.Max(7, unitSize), FontStyle.Regular, GraphicsUnit.Pixel))
            using (var labelFont = new Font("Segoe UI", Math.Max(8, labelSize), FontStyle.Regular, GraphicsUnit.Pixel))
            {
                Size number = TextRenderer.MeasureText(graphics, value.Number, numberFont, Size.Empty, measureFlags);
                Size unit = TextRenderer.MeasureText(graphics, value.Unit, unitFont, Size.Empty, measureFlags);
                float readingY = tile.Top + tile.Height * .47f - number.Height / 2f;
                float start = tile.Left + (tile.Width - number.Width - unit.Width - 2 * scale) / 2;
                TextRenderer.DrawText(graphics, value.Number, numberFont, new Point((int)start, (int)readingY), value.Number == "—" ? Muted : Blue, measureFlags);
                TextRenderer.DrawText(graphics, value.Unit, unitFont, new Point((int)(start + number.Width + 2 * scale), (int)(readingY + number.Height - unit.Height - 3 * scale)), value.Number == "—" ? Muted : Blue, measureFlags);
                var labelBounds = new Rectangle((int)tile.Left + 3, (int)(tile.Top + tile.Height * .68f), (int)tile.Width - 6, (int)(tile.Height * .24f));
                TextRenderer.DrawText(graphics, label, labelFont, labelBounds, Label, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }
}
