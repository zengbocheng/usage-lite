using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace UsageLite;

public sealed class MainForm : Form
{
    internal static readonly Size PanelSize = new(440, 508);
    private const int TokenDetailHeight = 522;
    private readonly QuotaCard codex = new("Codex", Color.FromArgb(23, 107, 218));
    private readonly QuotaCard claude = new("Claude Code", Color.FromArgb(199, 108, 76));
    private readonly ClearTypeLinkLabel tokenSummary = new()
    {
        BackColor = Color.White, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(106, 119, 137),
        Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular),
        LinkBehavior = LinkBehavior.AlwaysUnderline, LinkColor = Color.FromArgb(31, 96, 182),
        ActiveLinkColor = Color.FromArgb(20, 66, 135), TabStop = true, UseMnemonic = false, AutoEllipsis = true
    };
    private readonly TokenUsageView tokenView = new() { Visible = false };
    private readonly TableLayoutPanel layout;
    private TokenUsage? tokens;
    private readonly RoundedButton refresh = new() { Text = "刷新本地记录", AutoSize = false, Dock = DockStyle.Fill, MinimumSize = new Size(116, 0), Padding = new Padding(8, 4, 8, 4), Margin = new Padding(0, 4, 0, 4) };
    private bool reallyClosing;
    public event Action? RefreshRequested;

    public MainForm(bool hideOnClose = true)
    {
        SuspendLayout();
        Text = "Usage Lite";
        Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
        ClientSize = PanelSize;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(245, 247, 251);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = !hideOnClose;
        StartPosition = FormStartPosition.Manual;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(20, 14, 20, 16), BackColor = Color.FromArgb(245, 247, 251) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, codex.HeightForWidth(400) + codex.Margin.Vertical));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, claude.HeightForWidth(400) + claude.Margin.Vertical));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32));
        var heading = new Label { Text = "Usage Lite", Dock = DockStyle.Fill, Margin = Padding.Empty, Font = new Font(Font.FontFamily, 18f, FontStyle.Bold), ForeColor = Color.FromArgb(30, 41, 59), TextAlign = ContentAlignment.MiddleLeft };
        var close = new Button { Text = "×", AccessibleName = "关闭面板", Dock = DockStyle.Fill, Margin = new Padding(0, 7, 0, 7), FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 13f), ForeColor = Color.FromArgb(99, 111, 130), Cursor = Cursors.Hand };
        close.FlatAppearance.BorderSize = 0;
        close.FlatAppearance.MouseOverBackColor = Color.FromArgb(229, 234, 242);
        close.FlatAppearance.MouseDownBackColor = Color.FromArgb(216, 224, 236);
        close.Click += (_, _) => Close();
        void DragHeader(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || sender is not Control source) return;
            Point point = source.PointToScreen(e.Location);
            // Include the top/side padding around the heading, but not the cards.
            if (PointToClient(point).Y >= PointToClient(codex.PointToScreen(Point.Empty)).Y) return;
            // Hand dragging to the standard Windows caption move loop.
            source.Capture = false;
            var message = Message.Create(Handle, 0x00A1, (IntPtr)2,
                (IntPtr)((point.X & 0xffff) | ((point.Y & 0xffff) << 16)));
            DefWndProc(ref message);
        }
        heading.MouseDown += DragHeader;
        header.MouseDown += DragHeader;
        layout.MouseDown += DragHeader;
        MouseDown += DragHeader;
        header.Controls.Add(heading, 0, 0);
        header.Controls.Add(close, 1, 0);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(codex, 0, 1);
        layout.Controls.Add(claude, 0, 2);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 2, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var hint = new Label
        {
            Text = "刷新只重新读取缓存", Dock = DockStyle.Fill, AutoEllipsis = true,
            ForeColor = Color.FromArgb(99, 111, 130), TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(12, 0, 0, 0)
        };
        footer.Controls.Add(refresh, 0, 0);
        footer.Controls.Add(hint, 1, 0);
        codex.SetSourceLine(tokenSummary);
        UpdateTokenSummary();
        layout.Controls.Add(footer, 0, 3);
        // Windows can resize a borderless window without changing its DPI. The
        // owner-drawn cards scale with width, so their rows must do the same.
        bool sizingCards = false;
        layout.Layout += (_, _) =>
        {
            if (sizingCards) return;
            int cardWidth = layout.ClientSize.Width - layout.Padding.Horizontal;
            if (cardWidth <= 0) return;
            sizingCards = true;
            try
            {
                int codexHeight = codex.HeightForWidth(cardWidth) + codex.Margin.Vertical;
                int claudeHeight = claude.HeightForWidth(cardWidth) + claude.Margin.Vertical;
                if (layout.RowStyles[1].Height != codexHeight) layout.RowStyles[1].Height = codexHeight;
                if (layout.RowStyles[2].Height != claudeHeight) layout.RowStyles[2].Height = claudeHeight;
            }
            finally { sizingCards = false; }
        };
        Controls.Add(layout);
        Controls.Add(tokenView);
        tokenSummary.LinkClicked += (_, _) => OpenTokens();
        tokenView.BackRequested += ShowQuotas;
        tokenView.CloseRequested += Close;
        ResumeLayout(true);
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Escape) return;
            if (tokenView.Visible) ShowQuotas(); else Close();
            e.Handled = true;
        };
        FormClosing += (_, e) =>
        {
            if (hideOnClose && !reallyClosing && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                ShowQuotas();
            }
        };
    }
    public void UpdateTokens(TokenUsage usage)
    {
        tokens = usage;
        UpdateTokenSummary();
        if (tokenView.Visible) tokenView.UpdateUsage(usage);
    }

    private void UpdateTokenSummary()
    {
        string prefix = codex.SourceText + "    ";
        string caption = "今日 Token · 读取中…";
        if (tokens is { } usage)
        {
            long today = usage.Total(usage.Today);
            caption = $"今日已用 {TokenUsageView.Compact(today)} Token{(usage.Partial ? " *" : "")}";
            tokenSummary.AccessibleDescription = $"今日 {today:N0} Token。打开每日和项目用量。";
        }
        tokenSummary.Text = prefix + caption;
        tokenSummary.LinkArea = new LinkArea(prefix.Length, caption.Length);
    }

    private void OpenTokens()
    {
        tokenView.Open(tokens, tokens?.Today ?? DateOnly.FromDateTime(DateTime.Today));
        layout.Hide();
        SetPanelHeight(TokenDetailHeight);
        tokenView.Show();
    }

    private void ShowQuotas()
    {
        tokenView.Hide();
        SetPanelHeight(PanelSize.Height);
        layout.Show();
    }

    private void SetPanelHeight(int logicalHeight)
    {
        int height = LogicalToDeviceUnits(logicalHeight);
        if (ClientSize.Height == height) return;
        // Keep the bottom edge in place when switching views above the taskbar.
        if (Visible) Top = Math.Max(Screen.FromControl(this).WorkingArea.Top, Bottom - height);
        ClientSize = new Size(ClientSize.Width, height);
    }

    public void UpdateData(DashboardData data)
    {
        codex.Snapshot = data.Codex;
        claude.Snapshot = data.Claude;
        UpdateTokenSummary();
        codex.Invalidate(); claude.Invalidate();
    }
    public void SetBusy(bool busy) { refresh.Enabled = !busy; refresh.Text = busy ? "正在读取…" : "刷新本地记录"; }
    public void ShowPanel()
    {
        // Recover the intended size after Windows snapping/display changes.
        WindowState = FormWindowState.Normal;
        ClientSize = LogicalToDeviceUnits(PanelSize);
        ShowQuotas();
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(Math.Max(area.Left, area.Right - Width - 12), Math.Max(area.Top, area.Bottom - Height - 12));
        Show(); Activate();
    }
    public void Shutdown() { reallyClosing = true; Close(); }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRoundedRegion();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UpdateRoundedRegion();
    }

    private void UpdateRoundedRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        using var path = RoundedShapes.Create(new RectangleF(0, 0, Width, Height), 16 * DeviceDpi / 96f);
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    private sealed class ClearTypeLinkLabel : LinkLabel
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            base.OnPaint(e);
        }
    }
}

public sealed class QuotaCard : Control
{
    private const int BaseHeight = 182;
    private const float DesignWidth = 400;
    private LinkLabel? sourceLine;
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

    internal string SourceText => Snapshot.ObservedAt is { } at
        ? "本地记录 · " + at.ToLocalTime().ToString("MM-dd HH:mm")
        : "尚无本地记录";

    internal void SetSourceLine(LinkLabel link)
    {
        sourceLine = link;
        Controls.Add(link);
    }

    internal int HeightForWidth(int width) => (int)Math.Ceiling(width * (double)BaseHeight / DesignWidth);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (sourceLine is null || Width <= 0) return;
        float scale = Width / DesignWidth;
        int inset = (int)Math.Round(16 * scale);
        int height = (int)Math.Round(20 * scale);
        sourceLine.SetBounds(inset, Math.Max(0, Height - (int)Math.Round(28 * scale)),
            Math.Max(0, Width - 2 * inset), height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width <= 0 || Height <= 0) return;
        float scale = Width / DesignWidth;
        var g = e.Graphics;
        g.ScaleTransform(scale, scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float height = Height / scale;
        using var border = new Pen(Color.FromArgb(225, 231, 239));
        using var path = RoundRect(new RectangleF(0.5f, 0.5f, 399, height - 1), 12);
        g.FillPath(Brushes.White, path); g.DrawPath(border, path);
        using var titleFont = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
        // The card scales its 96-DPI design pixels. Twelve pixels here is 9 pt.
        using var textFont = new Font("Microsoft YaHei UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
        using var valueFont = new Font("Segoe UI", 17, FontStyle.Regular, GraphicsUnit.Pixel);
        using var colorBrush = new SolidBrush(accent);
        using var ink = new SolidBrush(Color.FromArgb(30, 41, 59));
        using var muted = new SolidBrush(Color.FromArgb(106, 119, 137));
        g.FillEllipse(colorBrush, 16, 19, 8, 8);
        g.DrawString(provider, titleFont, ink, 31, 13);
        bool stale = Snapshot.Stale(DateTimeOffset.UtcNow);
        if (stale) g.DrawString("较早的记录", textFont, muted, 310, 16);
        if (Snapshot.Session is null && Snapshot.Weekly is null)
        {
            g.DrawString("暂未读取到额度", valueFont, muted, 16, 49);
            g.DrawString(Snapshot.Note, textFont, muted, new RectangleF(16, 82, 368, 52));
        }
        else
        {
            DrawRow(g, Snapshot.Session, "5 小时", 42, textFont, valueFont, accent, stale);
            DrawRow(g, Snapshot.Weekly, Snapshot.Weekly?.Title ?? "每周", 97, textFont, valueFont, accent, stale);
        }
        if (sourceLine is null)
        {
            using var ellipsis = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(SourceText, textFont, muted, new RectangleF(16, height - 24, 368, 17), ellipsis);
        }
    }
    private static void DrawRow(Graphics g, QuotaWindow? window, string title, float y, Font text, Font valueFont, Color accent, bool stale)
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
        g.DrawString(reset, text, muted, 16, y + 32);
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
