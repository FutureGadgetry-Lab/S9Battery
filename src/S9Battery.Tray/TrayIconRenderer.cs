using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using S9Battery.Core;

namespace S9Battery.Tray;

internal readonly record struct TrayIconStyle(string Text, int? Level, bool Charging, Color Accent)
{
    public static TrayIconStyle From(MouseStatus status, int threshold)
    {
        var color = !status.Fresh ? Color.FromArgb(158, 169, 187) : status.Battery?.Charging == true ?
            Color.FromArgb(100, 231, 171) : status.Battery is { HasReliableLevel: true } b && b.Level < threshold ?
            Color.FromArgb(255, 182, 107) : Color.FromArgb(233, 241, 251);
        return new(status.IconText, status.IconText is "--" or "充" ? null : status.Battery?.Level,
            status.Fresh && status.Battery?.Charging == true, color);
    }
}

internal static class TrayIconRenderer
{
    public static Icon Create(TrayIconStyle style, int size)
    {
        // Supersampling only on a visual change, not on every polling tick.
        using var large = new Bitmap(size * 4, size * 4, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(large))
        {
            g.Clear(Color.Transparent);
            g.ScaleTransform(large.Width / 16f, large.Height / 16f);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var body = Rounded(new(.4f, .5f, 15.2f, 15), 3.4f);
            using var background = new LinearGradientBrush(new RectangleF(0, 0, 16, 16),
                Color.FromArgb(49, 59, 74), Color.FromArgb(26, 33, 44), 90);
            g.FillPath(background, body);
            using var outline = new Pen(Color.FromArgb(110, style.Accent), .65f);
            g.DrawPath(outline, body);
            using var accent = new SolidBrush(style.Accent);
            if (style.Text == "充")
            {
                g.FillPolygon(accent, new[] { new PointF(9.3f, 2.1f), new PointF(4.6f, 8.8f),
                    new PointF(7.5f, 8.8f), new PointF(6.6f, 13.8f), new PointF(11.8f, 6.6f), new PointF(8.6f, 6.6f) });
            }
            else
            {
                using var format = new StringFormat(StringFormat.GenericTypographic)
                { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.None };
                float fontSize = style.Text.Length >= 3 ? 8.6f : 10.2f;
                Font font = new("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
                try
                {
                    while (fontSize > 6 && g.MeasureString(style.Text, font, 100, format).Width > 14)
                    { font.Dispose(); font = new("Segoe UI", fontSize -= .3f, FontStyle.Bold, GraphicsUnit.Pixel); }
                    g.DrawString(style.Text, font, accent, new RectangleF(.4f, 0, 15.2f, 13.5f), format);
                }
                finally { font.Dispose(); }
                using var track = new Pen(Color.FromArgb(65, 78, 96), 1.15f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(track, 3, 13.7f, 13, 13.7f);
                if (style.Level is > 0)
                {
                    using var fill = new Pen(style.Accent, 1.15f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawLine(fill, 3, 13.7f, 3 + 10 * Math.Clamp(style.Level.Value, 0, 100) / 100f, 13.7f);
                }
            }
        }
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(large, new Rectangle(0, 0, size, size));
        }
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { Native.DestroyIcon(handle); }
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
}
