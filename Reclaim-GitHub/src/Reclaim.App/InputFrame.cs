using System.Drawing.Drawing2D;

namespace Reclaim.App;

// Keep native text editing and dropdown behavior inside a consistently sized field surface.
internal sealed class InputFrame : Panel
{
    private readonly Control editor;
    private readonly Panel viewport = new() { BackColor = Color.White, Margin = Padding.Empty };

    public InputFrame(Control editor)
    {
        this.editor = editor;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White; Margin = Padding.Empty; Size = new Size(200, 40);
        editor.Dock = DockStyle.None; editor.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        editor.Margin = Padding.Empty; editor.BackColor = Color.White;
        if (editor is TextBox text) text.BorderStyle = BorderStyle.None;
        if (editor is ComboBox combo) combo.FlatStyle = FlatStyle.Flat;
        viewport.Controls.Add(editor); Controls.Add(viewport);
        editor.Enter += (_, _) => Invalidate(); editor.Leave += (_, _) => Invalidate();
        editor.FontChanged += (_, _) => PerformLayout();
        Click += (_, _) => { editor.Focus(); if (editor is ComboBox combo) combo.DroppedDown = true; };
        viewport.Click += (_, _) => editor.Focus();
    }
    public override Size GetPreferredSize(Size proposedSize) => new(
        LogicalToDeviceUnits(160), Math.Max(LogicalToDeviceUnits(40), editor.GetPreferredSize(Size.Empty).Height + LogicalToDeviceUnits(14)));

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (editor is null) return;
        int inset = LogicalToDeviceUnits(12);
        // Clip the native dropdown's thin border; the outer frame supplies the shared border.
        int trim = editor is ComboBox ? LogicalToDeviceUnits(2) : 0;
        int editorHeight = editor.GetPreferredSize(Size.Empty).Height;
        int visibleHeight = Math.Max(1, editorHeight - trim * 2);
        viewport.Bounds = new Rectangle(inset, Math.Max(0, (Height - visibleHeight) / 2), Math.Max(1, Width - inset * 2), visibleHeight);
        editor.Bounds = new Rectangle(-trim, -trim, viewport.Width + trim * 2, editorHeight);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 4 || Height < 4) return;
        Control? surface = Parent;
        while (surface is not null && surface.BackColor.A < 255) surface = surface.Parent;
        e.Graphics.Clear(surface?.BackColor ?? Theme.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float stroke = Math.Max(1, DeviceDpi / 96f);
        using var path = Theme.Round(new RectangleF(stroke, stroke, Width - stroke * 2 - 1, Height - stroke * 2 - 1), LogicalToDeviceUnits(8));
        using var fill = new SolidBrush(Color.White); e.Graphics.FillPath(fill, path);
        using var border = new Pen(ContainsFocus ? Theme.Accent : Color.FromArgb(212, 223, 229), stroke);
        e.Graphics.DrawPath(border, path);
    }
}
