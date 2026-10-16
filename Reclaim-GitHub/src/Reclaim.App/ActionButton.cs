using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Reclaim.App;

// Use a single painted surface rather than mixing native button borders with rounded artwork.
internal sealed class ActionButton : Control, IButtonControl
{
    private bool selected, hovered, mousePressed, spacePressed, defaultButton;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Primary { get; init; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Dark { get; init; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Navigation { get; init; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => selected;
        set { if (selected != value) { selected = value; Invalidate(); } }
    }
    [DefaultValue(DialogResult.None)]
    public DialogResult DialogResult { get; set; }

    public ActionButton(string text, bool primary = false)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
            ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
        SetAutoSizeMode(AutoSizeMode.GrowAndShrink);
        Text = text; Primary = primary; Font = Theme.Font(10, FontStyle.Bold);
        BackColor = Color.Transparent; Cursor = Cursors.Hand; TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        Size = new Size(140, 40); Margin = Padding.Empty; AutoSize = true;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        return new Size(text.Width + LogicalToDeviceUnits(28), Math.Max(LogicalToDeviceUnits(40), text.Height + LogicalToDeviceUnits(14)));
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Table layouts can have a transparent BackColor. Clearing a native buffer with that
        // color leaves black corners; find the solid surface beneath the entire button instead.
        Control? surface = Parent;
        while (surface is not null && surface.BackColor.A < 255) surface = surface.Parent;
        e.Graphics.Clear(surface?.BackColor ?? Theme.Background);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 4 || Height < 4) return;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        bool down = (mousePressed && hovered) || spacePressed;
        Color fill, border, foreground;
        if (!Enabled)
        {
            fill = Dark ? Color.FromArgb(32, 49, 62) : Color.FromArgb(237, 241, 244);
            border = Dark ? fill : Color.FromArgb(229, 235, 239);
            foreground = Dark ? Color.FromArgb(141, 162, 177) : Color.FromArgb(130, 146, 158);
        }
        else if (Navigation)
        {
            fill = Selected ? Color.FromArgb(29, 72, 72) : hovered ? Color.FromArgb(31, 51, 65) : Color.FromArgb(18, 34, 47);
            border = fill; foreground = Selected ? Color.FromArgb(107, 232, 207) : Color.FromArgb(187, 203, 214);
            if (down) fill = Color.FromArgb(37, 84, 82);
        }
        else if (Primary)
        {
            fill = down ? Color.FromArgb(0, 93, 85) : hovered ? Color.FromArgb(0, 113, 103) : Theme.Accent;
            border = fill; foreground = Color.White;
        }
        else if (Dark)
        {
            fill = down ? Color.FromArgb(45, 66, 81) : hovered ? Color.FromArgb(46, 68, 83) : Color.FromArgb(35, 54, 68);
            border = fill; foreground = Color.FromArgb(235, 243, 248);
        }
        else
        {
            fill = Selected ? (down ? Color.FromArgb(198, 231, 222) : hovered ? Color.FromArgb(216, 239, 231) : Theme.Pale)
                : down ? Color.FromArgb(229, 237, 241) : hovered ? Color.FromArgb(245, 249, 250) : Color.White;
            border = Selected ? Color.FromArgb(168, 215, 203) : hovered ? Color.FromArgb(177, 196, 205) : Color.FromArgb(212, 223, 229);
            foreground = Selected ? Theme.Accent : Theme.Ink;
        }

        float stroke = Math.Max(1, DeviceDpi / 96f);
        using var path = Theme.Round(new RectangleF(stroke, stroke, Width - stroke * 2 - 1, Height - stroke * 2 - 1), LogicalToDeviceUnits(8));
        using var brush = new SolidBrush(fill); g.FillPath(brush, path);
        using var pen = new Pen(border, stroke); g.DrawPath(pen, path);
        var textBounds = Rectangle.Inflate(ClientRectangle, -LogicalToDeviceUnits(12), -LogicalToDeviceUnits(3));
        if (down) textBounds.Offset(0, LogicalToDeviceUnits(1));
        TextRenderer.DrawText(g, Text, Font, textBounds, foreground,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix | (Navigation ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter));
        if (Enabled && ((Focused && ShowFocusCues) || defaultButton))
        {
            float inset = LogicalToDeviceUnits(4);
            using var focus = Theme.Round(new RectangleF(inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1), LogicalToDeviceUnits(5));
            using var focusPen = new Pen(Primary || Dark || Navigation ? Color.FromArgb(126, 222, 202) : Theme.Accent, stroke);
            g.DrawPath(focusPen, focus);
        }
        base.OnPaint(e);
    }

    public void NotifyDefault(bool value) { defaultButton = value; Invalidate(); }
    public void PerformClick()
    {
        if (!CanSelect) return;
        if (CausesValidation && FindForm() is Form owner && !owner.Validate()) return;
        OnClick(EventArgs.Empty);
    }
    protected override void OnClick(EventArgs e)
    {
        if (DialogResult != DialogResult.None && FindForm() is Form owner) owner.DialogResult = DialogResult;
        base.OnClick(e);
    }
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Enter) { PerformClick(); return true; }
        return base.ProcessDialogKey(keyData);
    }
    protected override bool IsInputKey(Keys keyData) => keyData == Keys.Space || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyData == Keys.Space) { spacePressed = true; e.Handled = e.SuppressKeyPress = true; Invalidate(); }
        base.OnKeyDown(e);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && spacePressed)
        {
            spacePressed = false; Invalidate(); e.Handled = e.SuppressKeyPress = true; PerformClick();
        }
        base.OnKeyUp(e);
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && Enabled) { Focus(); mousePressed = true; hovered = true; Capture = true; Invalidate(); }
        base.OnMouseDown(e);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        bool activate = mousePressed && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location);
        if (e.Button == MouseButtons.Left) { mousePressed = false; Capture = false; Invalidate(); }
        base.OnMouseUp(e);
        if (activate) PerformClick();
    }
    protected override void OnMouseCaptureChanged(EventArgs e) { mousePressed = false; Invalidate(); base.OnMouseCaptureChanged(e); }
    protected override void OnLostFocus(EventArgs e) { spacePressed = false; Invalidate(); base.OnLostFocus(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnEnabledChanged(EventArgs e)
    {
        mousePressed = spacePressed = hovered = false; Invalidate(); base.OnEnabledChanged(e);
    }
    protected override AccessibleObject CreateAccessibilityInstance() => new ButtonAccessibleObject(this);
    private sealed class ButtonAccessibleObject(ActionButton owner) : ControlAccessibleObject(owner)
    {
        public override string DefaultAction => "Press";
        public override void DoDefaultAction() => owner.PerformClick();
    }
}
