namespace UsageLite;

internal sealed class ProjectUsageList : ListView
{
    private static readonly Color Ink = Color.FromArgb(30, 41, 59);
    private static readonly Color Muted = Color.FromArgb(99, 111, 130);
    private static readonly Color Blue = Color.FromArgb(31, 96, 182);
    private ImageList? rowSpacing;

    public ProjectUsageList()
    {
        Dock = DockStyle.Fill;
        View = View.Details;
        FullRowSelect = true;
        MultiSelect = false;
        ShowItemToolTips = true;
        BorderStyle = BorderStyle.None;
        HeaderStyle = ColumnHeaderStyle.Nonclickable;
        Margin = Padding.Empty;
        BackColor = Color.White;
        ForeColor = Ink;
        AccessibleName = "项目 Token 用量";
        OwnerDraw = true;
        DoubleBuffered = true;
        Columns.Add("项目");
        Columns.Add("Token").TextAlign = HorizontalAlignment.Right;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateRowSpacing();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateRowSpacing();
        ResizeColumns();
    }

    private void UpdateRowSpacing()
    {
        var height = Math.Clamp(LogicalToDeviceUnits(40), 1, 256);
        if (rowSpacing?.ImageSize.Height == height) return;
        var next = new ImageList { ImageSize = new Size(1, height) };
        var previous = rowSpacing;
        rowSpacing = next;
        SmallImageList = next;
        previous?.Dispose();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ResizeColumns();
    }

    internal void ResizeColumns()
    {
        if (Columns.Count != 2) return;
        int width = Math.Max(0, ClientSize.Width - 1);
        int countWidth = Math.Min(width, LogicalToDeviceUnits(128));
        Columns[0].Width = width - countWidth;
        Columns[1].Width = countWidth;
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        e.Graphics.FillRectangle(Brushes.White, e.Bounds);
        int inset = LogicalToDeviceUnits(12);
        var textBounds = Rectangle.Inflate(e.Bounds, -inset, 0);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        if (e.ColumnIndex == 1) flags |= TextFormatFlags.Right;
        TextRenderer.DrawText(e.Graphics, e.Header?.Text, Font, textBounds, Muted, flags);
        using var rule = new Pen(Color.FromArgb(225, 231, 239));
        e.Graphics.DrawLine(rule, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        if (e.Item is not { } item || e.Header is not { } header) return;
        var cell = new Rectangle(e.Bounds.Location, new Size(header.Width, e.Bounds.Height));
        bool selected = item.Selected && Focused;
        var background = selected ? Color.FromArgb(224, 237, 255) :
            e.ItemIndex % 2 == 0 ? Color.FromArgb(249, 251, 254) : Color.White;
        using var fill = new SolidBrush(background);
        e.Graphics.FillRectangle(fill, cell);
        using var rule = new Pen(Color.FromArgb(236, 241, 247));
        e.Graphics.DrawLine(rule, cell.Left, cell.Bottom - 1, cell.Right, cell.Bottom - 1);

        int inset = LogicalToDeviceUnits(12);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        if (e.ColumnIndex == 1)
        {
            using var bold = new Font(Font, FontStyle.Bold);
            TextRenderer.DrawText(e.Graphics, e.SubItem?.Text, bold, Rectangle.Inflate(cell, -inset, 0),
                Blue, flags | TextFormatFlags.Right);
            return;
        }

        int badgeSize = LogicalToDeviceUnits(21);
        var badge = new Rectangle(cell.Left + inset, cell.Top + (cell.Height - badgeSize) / 2, badgeSize, badgeSize);
        var smoothing = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var badgeFill = new SolidBrush(e.ItemIndex == 0 ? Color.FromArgb(224, 237, 255) : Color.FromArgb(235, 240, 247));
        e.Graphics.FillEllipse(badgeFill, badge);
        e.Graphics.SmoothingMode = smoothing;
        TextRenderer.DrawText(e.Graphics, (e.ItemIndex + 1).ToString(), Font, badge,
            e.ItemIndex == 0 ? Blue : Muted, flags | TextFormatFlags.HorizontalCenter);
        int left = badge.Right + LogicalToDeviceUnits(8);
        var nameBounds = Rectangle.FromLTRB(left, cell.Top, Math.Max(left, cell.Right - inset), cell.Bottom);
        TextRenderer.DrawText(e.Graphics, item.Text, Font, nameBounds, Ink, flags);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SmallImageList = null;
            rowSpacing?.Dispose();
        }
        base.Dispose(disposing);
    }
}
