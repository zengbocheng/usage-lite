using System.Drawing.Drawing2D;

namespace UsageLite;

internal static class RoundedShapes
{
    public static GraphicsPath Create(RectangleF bounds, float radius)
    {
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

public sealed class RoundedButton : Button
{
    private bool hovered, pressed;

    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Button's themed background may leave black pixels outside the rounded fill.
        e.Graphics.Clear(Parent?.BackColor ?? BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill = !Enabled ? Color.FromArgb(235, 239, 245) : pressed ? Color.FromArgb(205, 224, 249) :
            hovered ? Color.FromArgb(220, 234, 253) : Color.FromArgb(231, 240, 253);
        using var path = RoundedShapes.Create(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 10 * DeviceDpi / 96f);
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(Color.FromArgb(205, 222, 245));
        e.Graphics.FillPath(brush, path);
        e.Graphics.DrawPath(pen, path);
        var textBounds = Rectangle.Inflate(ClientRectangle, -6, -2);
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds,
            Enabled ? Color.FromArgb(31, 96, 182) : Color.FromArgb(128, 143, 163),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5));
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = e.Button == MouseButtons.Left; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); pressed = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); pressed = false; Invalidate(); }
}
