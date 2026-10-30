using System.Drawing.Drawing2D;

namespace UsageLite;

public sealed class MainForm : Form
{
    private readonly QuotaCard codex = new("Codex", Color.FromArgb(23, 107, 218));
    private readonly QuotaCard claude = new("Claude", Color.FromArgb(199, 108, 76));
    private readonly Label timestamp = new() { Dock = DockStyle.Fill, AutoSize = false, ForeColor = Color.FromArgb(99, 111, 130), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button refresh = new() { Text = "刷新本地记录", AutoSize = false, Dock = DockStyle.Fill, MinimumSize = new Size(174, 0), FlatStyle = FlatStyle.Flat, Padding = new Padding(8, 4, 8, 4), Margin = new Padding(5, 4, 0, 4) };
    private readonly Label hint = new() { AutoSize = true, MaximumSize = new Size(402, 0), ForeColor = Color.FromArgb(99, 111, 130), Text = "额度随 Claude / Codex 的本地记录更新。刷新只重新读取缓存。", Margin = new Padding(0, 8, 0, 0) };
    private bool reallyClosing;
    public event Action? RefreshRequested;

    public MainForm()
    {
        SuspendLayout();
        Text = "Usage Lite";
        Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
        ClientSize = new Size(440, 522);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(245, 247, 251);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(20, 14, 20, 16), BackColor = BackColor };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 180));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 180));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var heading = new Label { Text = "Usage Lite", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 18f, FontStyle.Bold), ForeColor = Color.FromArgb(30, 41, 59), TextAlign = ContentAlignment.TopLeft };
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(codex, 0, 1);
        layout.Controls.Add(claude, 0, 2);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 2, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(timestamp, 0, 0);
        footer.Controls.Add(refresh, 1, 0);
        layout.Controls.Add(footer, 0, 3);
        layout.Controls.Add(hint, 0, 4);
        Controls.Add(layout);
        ResumeLayout(true);
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; } };
        FormClosing += (_, e) => { if (!reallyClosing && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }
    public void UpdateData(DashboardData data)
    {
        codex.Snapshot = data.Codex;
        claude.Snapshot = data.Claude;
        timestamp.Text = "已读取 " + data.ReadAt.ToLocalTime().ToString("HH:mm:ss");
        codex.Invalidate(); claude.Invalidate();
    }
    public void SetBusy(bool busy) { refresh.Enabled = !busy; refresh.Text = busy ? "正在读取…" : "刷新本地记录"; }
    public void ShowPanel()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(Math.Max(area.Left, area.Right - Width - 12), Math.Max(area.Top, area.Bottom - Height - 12));
        Show(); Activate();
    }
    public void Shutdown() { reallyClosing = true; Close(); }
}

public sealed class QuotaCard : Control
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public QuotaSnapshot Snapshot { get; set; }
    private readonly string provider;
    private readonly Color accent;
    public QuotaCard(string provider, Color accent)
    {
        this.provider = provider; this.accent = accent;
        Snapshot = QuotaSnapshot.Empty(provider, "正在读取本地额度…");
        Dock = DockStyle.Fill;
        Margin = new Padding(0, 0, 0, 12);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AccessibleName = provider + " 额度";
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float scale = Width / 400f;
        var g = e.Graphics;
        g.ScaleTransform(scale, scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float height = Height / scale;
        using var border = new Pen(Color.FromArgb(225, 231, 239));
        using var path = RoundRect(new RectangleF(0.5f, 0.5f, 399, height - 1), 12);
        g.FillPath(Brushes.White, path); g.DrawPath(border, path);
        using var titleFont = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textFont = new Font("Microsoft YaHei UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
        using var smallFont = new Font("Microsoft YaHei UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
        using var valueFont = new Font("Segoe UI", 17, FontStyle.Regular, GraphicsUnit.Pixel);
        using var colorBrush = new SolidBrush(accent);
        using var ink = new SolidBrush(Color.FromArgb(30, 41, 59));
        using var muted = new SolidBrush(Color.FromArgb(106, 119, 137));
        g.FillEllipse(colorBrush, 16, 19, 8, 8);
        g.DrawString(provider, titleFont, ink, 31, 13);
        bool stale = Snapshot.Stale(DateTimeOffset.UtcNow);
        if (stale) g.DrawString("较早的记录", smallFont, muted, 310, 16);
        if (Snapshot.Session is null && Snapshot.Weekly is null)
        {
            g.DrawString("暂未读取到额度", valueFont, muted, 16, 49);
            g.DrawString(Snapshot.Note, textFont, muted, new RectangleF(16, 82, 368, 52));
        }
        else
        {
            DrawRow(g, Snapshot.Session, "5 小时", 42, textFont, smallFont, valueFont, accent, stale);
            DrawRow(g, Snapshot.Weekly, Snapshot.Weekly?.Title ?? "每周", 91, textFont, smallFont, valueFont, accent, stale);
        }
        string source = Snapshot.Source;
        if (Snapshot.ObservedAt is { } at) source += " · " + at.ToLocalTime().ToString("MM-dd HH:mm");
        using var ellipsis = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(source, smallFont, muted, new RectangleF(16, height - 24, 368, 17), ellipsis);
    }
    private static void DrawRow(Graphics g, QuotaWindow? window, string title, float y, Font text, Font small, Font valueFont, Color accent, bool stale)
    {
        using var ink = new SolidBrush(Color.FromArgb(61, 73, 92));
        using var muted = new SolidBrush(Color.FromArgb(118, 130, 147));
        var now = DateTimeOffset.UtcNow;
        bool expired = window?.Expired(now) == true;
        string value = window is null || expired ? "—" : window.Remaining.ToString("0.#") + "%";
        g.DrawString(title, text, ink, 16, y + 2);
        string quota = value == "—" ? value : "剩余 " + value;
        var width = g.MeasureString(quota, valueFont).Width;
        g.DrawString(quota, valueFont, ink, 385 - width, y - 1);
        using var track = new SolidBrush(Color.FromArgb(235, 239, 245));
        using var fill = new SolidBrush(stale ? Color.FromArgb(157, 170, 187) : accent);
        g.FillRectangle(track, 16, y + 27, 368, 4);
        if (window is not null && !expired)
        {
            float remainingWidth = 368 * (float)window.Remaining / 100;
            g.FillRectangle(fill, 384 - remainingWidth, y + 27, remainingWidth, 4);
        }
        string reset = window is null ? "尚无该窗口记录" : expired ? "已到重置时间，等待新记录" : window.ResetAt is { } end ? "重置 " + end.ToLocalTime().ToString("MM-dd HH:mm") : "重置时间未知";
        g.DrawString(reset, small, muted, 16, y + 32);
    }
    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float diameter = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, diameter, diameter, 180, 90);
        p.AddArc(r.Right - diameter, r.Y, diameter, diameter, 270, 90);
        p.AddArc(r.Right - diameter, r.Bottom - diameter, diameter, diameter, 0, 90);
        p.AddArc(r.X, r.Bottom - diameter, diameter, diameter, 90, 90);
        p.CloseFigure(); return p;
    }
}
