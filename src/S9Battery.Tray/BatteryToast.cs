using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Diagnostics;
using System.Drawing.Imaging;

namespace S9Battery.Tray;

// A small non-activating application window, independent of Windows notification banners.
internal sealed class BatteryToast : Form
{
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 30 };
    private readonly Stopwatch clock = new();
    private TimeSpan previous;
    private double remaining;
    private bool low;
    private int level;
    private string detail = "";
    private Rectangle target;
    private Rectangle workArea;
    private LayeredSurface? surface;
    private bool positioning;
    private bool entering;
    private const int LogicalWidth = 380, LogicalHeight = 204;

    public BatteryToast()
    {
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.FromArgb(24, 28, 35);
        AccessibleRole = AccessibleRole.Alert;
        animation.Tick += (_, _) => TickAnimation();
        MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) Dismiss(); };
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x00000080 | 0x00080000; return p; }
    }

    public void Present(bool lowBattery, int percent, string message)
    {
        low = lowBattery; level = percent; detail = message;
        Text = low ? "S9 Pro · 该充电了" : "S9 Pro · 电量达到 100%";
        AccessibleName = Text + $"，{level}%，" + detail;
        remaining = low ? 12 : 8;
        clock.Restart(); previous = TimeSpan.Zero;
        entering = true;
        Reposition();
        BuildSurface();
        surface!.Present(Handle, target.Location, 0);
        Show();
        // Showing the window may select its monitor's DPI, so measure again afterwards.
        Reposition();
        if (surface.Image.Size != target.Size) BuildSurface();
        surface.Present(Handle, target.Location, 0);
        animation.Interval = 30;
        animation.Start();
    }

    private void Reposition()
    {
        if (positioning) return;
        positioning = true;
        try
        {
            var work = (Screen.PrimaryScreen ?? Screen.FromPoint(Cursor.Position)).WorkingArea;
            workArea = work;
            float scale = DeviceDpi / 96f;
            int margin = (int)Math.Round(16 * scale);
            int width = Math.Min((int)Math.Round(LogicalWidth * scale), work.Width);
            int height = Math.Min((int)Math.Round(LogicalHeight * scale), work.Height);
            target = new(Math.Max(work.Left, work.Right - width - margin),
                Math.Max(work.Top, work.Bottom - height - margin), width, height);
            Bounds = target;
        }
        finally { positioning = false; }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (positioning || !Visible) return;
        Reposition(); BuildSurface();
        surface!.Present(Handle, target.Location, 255);
    }

    private void TickAnimation()
    {
        var elapsed = clock.Elapsed;
        double delta = (elapsed - previous).TotalSeconds;
        previous = elapsed;
        if (entering)
        {
            double entrance = Math.Clamp(elapsed.TotalMilliseconds / 180, 0, 1);
            var position = new Point(target.Left, target.Top + (int)((1 - entrance) * 12 * DeviceDpi / 96f));
            surface!.Present(Handle, position, (byte)Math.Round(entrance * 255));
            if (entrance >= 1) { entering = false; animation.Interval = 250; }
        }
        if (!ClientRectangle.Contains(PointToClient(Cursor.Position))) remaining -= delta;
        if (remaining <= 0) { Dismiss(); return; }
        // Handle a moved taskbar or a disconnected display while the popup is visible.
        if ((Screen.PrimaryScreen ?? Screen.FromControl(this)).WorkingArea != workArea)
        {
            Reposition();
            if (surface!.Image.Size != target.Size) BuildSurface();
            surface!.Present(Handle, target.Location, 255);
        }
    }

    public void Dismiss()
    {
        animation.Stop(); clock.Stop(); Hide();
        surface?.Dispose(); surface = null;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0021) { m.Result = (IntPtr)3; return; } // MA_NOACTIVATE
        base.WndProc(ref m);
    }

    // The layered surface owns all painting; do not allocate a second WinForms back buffer.
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    // Render text and curves once. Two-times supersampling smooths fractional DPI edges.
    private void BuildSurface()
    {
        surface?.Dispose(); surface = null;
        using var canvas = new Bitmap(target.Width * 2, target.Height * 2, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.Transparent);
            g.ScaleTransform(canvas.Width / (float)LogicalWidth, canvas.Height / (float)LogicalHeight);
            PaintCard(g);
        }
        var replacement = new LayeredSurface(target.Width, target.Height);
        try
        {
            using var g = Graphics.FromImage(replacement.Image);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(canvas, new Rectangle(0, 0, target.Width, target.Height));
            g.Flush(FlushIntention.Sync);
            surface = replacement;
        }
        catch { replacement.Dispose(); throw; }
    }

    private void PaintCard(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        Color accent = low ? Color.FromArgb(255, 182, 107) : Color.FromArgb(100, 231, 171);
        using var border = new Pen(Color.FromArgb(61, 69, 82));
        using var outline = Rounded(new RectangleF(.5f, .5f, LogicalWidth - 1, LogicalHeight - 1), 18);
        using var background = new SolidBrush(BackColor);
        g.FillPath(background, outline);
        g.DrawPath(border, outline);
        Write(g, "S9 PRO", 12, FontStyle.Bold, Color.FromArgb(153, 166, 185), new(23, 18, 290, 24));
        Write(g, "×", 22, FontStyle.Regular, Color.FromArgb(153, 166, 185), new(340, 10, 30, 32));
        using var track = new Pen(Color.FromArgb(44, 52, 63), 5);
        using var arc = new Pen(accent, 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawEllipse(track, 26, 60, 72, 72);
        if (level > 0) g.DrawArc(arc, 26, 60, 72, 72, -90, 360 * Math.Clamp(level, 0, 100) / 100f);
        Write(g, level + "%", 21, FontStyle.Bold, accent, new(25, 80, 74, 32), StringAlignment.Center);
        Write(g, low ? "该充电了" : "电量达到 100%", 20, FontStyle.Bold, Color.FromArgb(241, 245, 250),
            new(120, 57, 237, 32));
        Write(g, detail, 12, FontStyle.Regular, Color.FromArgb(172, 183, 200), new(120, 99, 237, 55));
        Write(g, "点击关闭 · 悬停暂停", 11, FontStyle.Regular, Color.FromArgb(126, 141, 161), new(24, 166, 300, 22));
    }

    private static void Write(Graphics g, string text, float size, FontStyle style, Color color,
        RectangleF bounds, StringAlignment alignment = StringAlignment.Near)
    {
        using var font = new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = alignment, Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(text, font, brush, bounds, format);
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure(); return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { animation.Dispose(); surface?.Dispose(); surface = null; }
        base.Dispose(disposing);
    }
}
