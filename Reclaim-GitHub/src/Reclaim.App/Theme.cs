using System.Drawing.Drawing2D;

namespace Reclaim.App;

internal static class Theme
{
    public static readonly Color Ink = Color.FromArgb(28, 45, 59);
    public static readonly Color Muted = Color.FromArgb(105, 120, 134);
    public static readonly Color Accent = Color.FromArgb(0, 128, 116);
    public static readonly Color Pale = Color.FromArgb(228, 245, 240);
    public static readonly Color Background = Color.FromArgb(244, 246, 248);
    public static readonly Color Line = Color.FromArgb(225, 231, 235);
    public static Font Font(float size = 10, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);
    public static Label Label(string text, float size = 10, Color? color = null, FontStyle style = FontStyle.Regular) => new()
    {
        Text = text, Font = Font(size, style), ForeColor = color ?? Ink, AutoSize = false,
        TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, Margin = new Padding(0), UseMnemonic = false,
        AutoEllipsis = true
    };
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var path = new GraphicsPath(); float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
    }
}

internal sealed class Card : Panel
{
    public Card() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); BackColor = Color.White; Padding = new Padding(20); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Theme.Line); e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

internal sealed class BrandPanel : Panel
{
    public BrandPanel() { Height = 96; Dock = DockStyle.Top; DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var accent = new Pen(Color.FromArgb(50, 203, 169), 4);
        g.DrawArc(accent, 24, 32, 26, 26, 40, 285);
        using var brush = new SolidBrush(Color.FromArgb(50, 203, 169));
        g.FillPolygon(brush, [new Point(49, 31), new Point(51, 42), new Point(40, 38)]);
        TextRenderer.DrawText(g, "Reclaim", Theme.Font(18, FontStyle.Bold), new Point(56, 26), Color.White);
        TextRenderer.DrawText(g, "FILE RECOVERY", Theme.Font(8, FontStyle.Bold), new Point(58, 60), Color.FromArgb(132, 155, 171));
    }
}
