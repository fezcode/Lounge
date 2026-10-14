using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Lounge.Core.Input;

namespace Lounge.Agent;

/// <summary>"Would you like to go to Lounge?" A borderless, rounded, topmost card at the
/// top of the screen that never takes focus from what you are doing. Answered with the
/// controller: A for yes, B for no. It leaves by itself after a while.</summary>
internal sealed class PromptForm : Form
{
    private readonly XInputPad _pad;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 16 };
    private readonly DateTime _until;
    private readonly double _seconds;
    private readonly Lounge.Core.Launch.PromptAnswer _answer;
    private double _appear;

    /// <summary>true = yes, false = no, null = timed out.</summary>
    public bool? Answer { get; private set; }

    public PromptForm(XInputPad pad, int timeoutSeconds)
    {
        _pad = pad;
        _seconds = Math.Max(3, timeoutSeconds);
        _until = DateTime.Now.AddSeconds(_seconds);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 24, 30);
        DoubleBuffered = true;
        var scale = DeviceDpi / 96f;
        Size = new Size((int)(620 * scale), (int)(150 * scale));
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (int)(36 * scale));
        Opacity = 0;
        _poll.Tick += (_, _) => Tick();
        _poll.Start();
        // Buttons already down when the prompt appears (the press that woke the pad) don't count.
        _answer = new Lounge.Core.Launch.PromptAnswer(_pad.Read(Environment.TickCount64).Buttons);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int dark = 1;
        DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    private void Tick()
    {
        _appear = Math.Min(1, _appear + 0.08);
        Opacity = 0.97 * (1 - Math.Pow(1 - _appear, 3));
        if (_answer.Update(_pad.Read(Environment.TickCount64).Buttons) is { } decided) { Answer = decided; Close(); return; }
        if (DateTime.Now >= _until) { Answer = null; Close(); return; }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var s = DeviceDpi / 96f;

        // Brand tile.
        var tile = new RectangleF(24 * s, 27 * s, 96 * s, 96 * s);
        using (var path = Rounded(tile, 22 * s))
        using (var brush = new LinearGradientBrush(tile, Color.FromArgb(10, 132, 255), Color.FromArgb(219, 39, 119), 45f))
            g.FillPath(brush, path);
        using (var pen = new Pen(Color.White, 3.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            // The sofa glyph from the app icon.
            float x = tile.X + 18 * s, y = tile.Y + 20 * s, u = 2.5f * s;
            g.DrawLines(pen, new[] { P(x, y, u, 5, 11), P(x, y, u, 5, 8), P(x, y, u, 8, 5), P(x, y, u, 16, 5), P(x, y, u, 19, 8), P(x, y, u, 19, 11) });
            g.DrawLines(pen, new[] { P(x, y, u, 3, 17), P(x, y, u, 3, 12), P(x, y, u, 7, 12), P(x, y, u, 7, 14), P(x, y, u, 17, 14), P(x, y, u, 17, 12), P(x, y, u, 21, 12), P(x, y, u, 21, 17), P(x, y, u, 3, 17) });
        }

        using var title = new Font("Segoe UI Semibold", 17f, FontStyle.Regular, GraphicsUnit.Point);
        using var body = new Font("Segoe UI", 12.5f, FontStyle.Regular, GraphicsUnit.Point);
        using var glyph = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point);
        g.DrawString("Would you like to go to Lounge?", title, Brushes.White, 140 * s, 30 * s);

        float hx = 142 * s, hy = 82 * s;
        hx = Hint(g, hx, hy, "A", Color.FromArgb(93, 194, 30), "Yes", body, glyph, s);
        Hint(g, hx + 26 * s, hy, "B", Color.FromArgb(230, 57, 70), "Not now", body, glyph, s);

        // Time left.
        var left = Math.Clamp((_until - DateTime.Now).TotalSeconds / _seconds, 0, 1);
        using var track = new SolidBrush(Color.FromArgb(50, 255, 255, 255));
        using var fill = new SolidBrush(Color.FromArgb(10, 132, 255));
        var bar = new RectangleF(140 * s, Height - 14 * s, Width - 170 * s, 4 * s);
        g.FillRectangle(track, bar);
        g.FillRectangle(fill, bar.X, bar.Y, (float)(bar.Width * left), bar.Height);
    }

    private static PointF P(float x, float y, float u, float gx, float gy) => new(x + gx * u, y + gy * u);

    private static float Hint(Graphics g, float x, float y, string letter, Color c, string label, Font body, Font glyph, float s)
    {
        var d = 34 * s;
        using (var pen = new Pen(c, 2.2f * s)) g.DrawEllipse(pen, x, y, d, d);
        using (var brush = new SolidBrush(c))
        {
            var size = g.MeasureString(letter, glyph);
            g.DrawString(letter, glyph, brush, x + (d - size.Width) / 2 + 1, y + (d - size.Height) / 2 + 1);
        }
        using var text = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
        var ls = g.MeasureString(label, body);
        g.DrawString(label, body, text, x + d + 10 * s, y + (d - ls.Height) / 2);
        return x + d + 10 * s + ls.Width;
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _poll.Stop();
        base.OnFormClosed(e);
    }

    private const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_TOPMOST = 0x00000008;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2, DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
