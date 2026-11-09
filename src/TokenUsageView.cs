namespace UsageLite;

internal sealed class TokenUsageView : TableLayoutPanel
{
    private static readonly Color Ink = Color.FromArgb(30, 41, 59);
    private static readonly Color Muted = Color.FromArgb(99, 111, 130);
    private static readonly Color Blue = Color.FromArgb(23, 107, 218);
    private readonly RoundedButton previous = new() { Text = "‹", AccessibleName = "上一周", Dock = DockStyle.Fill };
    private readonly RoundedButton next = new() { Text = "›", AccessibleName = "下一周", Dock = DockStyle.Fill };
    private readonly Label range = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Ink };
    private readonly RoundedButton total = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
    private readonly Label projectTitle = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ink };
    private readonly Label note = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted };
    private readonly ProjectUsageList projects = new();
    private readonly Label empty = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        BackColor = Color.White, ForeColor = Muted, Text = "这段时间没有本地 Token 记录"
    };
    private readonly DayButton[] bars = new DayButton[7];
    private readonly ToolTip tooltips = new();
    private TokenUsage? usage;
    private DateOnly week;
    private DateOnly? selectedDay;
    private const string Weekdays = "一二三四五六日";
    public event Action? BackRequested;
    public event Action? CloseRequested;

    public TokenUsageView()
    {
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        Padding = new Padding(20, 14, 20, 16);
        BackColor = Color.FromArgb(245, 247, 251);
        ColumnCount = 1;
        RowCount = 7;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        RowStyles.Add(new RowStyle(SizeType.Absolute, 158));
        RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12));
        var back = new RoundedButton { Text = "‹ 返回", AccessibleName = "返回额度面板", Dock = DockStyle.Fill };
        back.Click += (_, _) => BackRequested?.Invoke();
        var heading = new Label { Text = "Codex Token 用量", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Ink };
        var close = new RoundedButton { Text = "×", AccessibleName = "关闭面板", Dock = DockStyle.Fill };
        close.Click += (_, _) => CloseRequested?.Invoke();
        header.Controls.Add(back, 0, 0);
        header.Controls.Add(heading, 1, 0);
        header.Controls.Add(close, 2, 0);
        Controls.Add(header, 0, 0);

        var navigation = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12));
        navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 76));
        navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12));
        navigation.Controls.Add(previous, 0, 0);
        navigation.Controls.Add(range, 1, 0);
        navigation.Controls.Add(next, 2, 0);
        Controls.Add(navigation, 0, 1);
        Controls.Add(total, 0, 2);

        var chart = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1, Margin = Padding.Empty };
        chart.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < bars.Length; i++)
        {
            int index = i;
            chart.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 7));
            var bar = new DayButton { Dock = DockStyle.Fill, Margin = new Padding(2) };
            bar.Click += (_, _) => SelectDay(week.AddDays(index));
            bars[i] = bar;
            chart.Controls.Add(bar, i, 0);
        }
        Controls.Add(chart, 0, 3);
        Controls.Add(projectTitle, 0, 4);

        var results = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Color.White };
        results.Controls.Add(projects);
        results.Controls.Add(empty);
        Controls.Add(results, 0, 5);
        Controls.Add(note, 0, 6);

        previous.Click += (_, _) => MoveWeek(-7);
        next.Click += (_, _) => MoveWeek(7);
        total.Click += (_, _) => SelectDay(null);
    }

    public void Open(TokenUsage? snapshot, DateOnly today)
    {
        usage = snapshot;
        week = TokenUsage.WeekStart(today);
        selectedDay = today;
        Render();
    }

    public void UpdateUsage(TokenUsage? snapshot)
    {
        var oldToday = usage?.Today;
        usage = snapshot;
        if (snapshot is not null && oldToday is { } old && selectedDay == old)
        {
            selectedDay = snapshot.Today;
            week = TokenUsage.WeekStart(snapshot.Today);
        }
        Render();
    }

    private void MoveWeek(int days)
    {
        week = week.AddDays(days);
        selectedDay = usage is not null && week == TokenUsage.WeekStart(usage.Today) ? usage.Today : null;
        Render();
    }

    private void SelectDay(DateOnly? day)
    {
        selectedDay = day;
        Render();
    }

    private void Render()
    {
        var today = usage?.Today ?? DateOnly.FromDateTime(DateTime.Today);
        var thisWeek = TokenUsage.WeekStart(today);
        var end = week.AddDays(6);
        range.Text = $"{week:yyyy-MM-dd} 至 {(end.Year == week.Year ? end.ToString("MM-dd") : end.ToString("yyyy-MM-dd"))}";
        previous.Enabled = usage is not null && week > TokenUsage.WeekStart(usage.FirstDay);
        next.Enabled = week < thisWeek;
        var totals = Enumerable.Range(0, 7).Select(i => usage?.Total(week.AddDays(i)) ?? 0).ToArray();
        total.Text = $"{(week == thisWeek ? "本周合计" : "整周合计")}    {(usage is null ? "…" : Compact(totals.Sum()))} Token";
        total.AccessibleDescription = selectedDay is null ? "已选中整周" : "查看整周项目用量";
        total.Highlighted = selectedDay is null;
        total.Invalidate();
        long maximum = totals.Max();
        for (int i = 0; i < bars.Length; i++)
        {
            var day = week.AddDays(i);
            var bar = bars[i];
            bar.Amount = day > today || usage is null ? "—" : Compact(totals[i], chart: true);
            bar.Caption = $"周{Weekdays[i]}\n{day:MM/dd}";
            bar.Ratio = maximum == 0 ? 0 : totals[i] / (double)maximum;
            bar.Selected = day == selectedDay;
            bar.Enabled = usage is not null && day <= today;
            bar.AccessibleName = $"{day:MM-dd}，{bar.Amount} Token{(bar.Selected ? "，已选中" : "")}";
            tooltips.SetToolTip(bar, $"{day:yyyy-MM-dd} · {totals[i]:N0} Token");
            bar.Invalidate();
        }
        projectTitle.Text = selectedDay is { } chosen ? $"{chosen:MM-dd} 项目用量" : "整周项目用量";
        var rows = usage?.Projects(selectedDay ?? week, selectedDay is null ? 7 : 1) ?? [];
        projects.BeginUpdate();
        try
        {
            projects.Items.Clear();
            foreach (var row in rows)
            {
                var item = new ListViewItem(row.Name) { ToolTipText = row.Directory };
                item.SubItems.Add(Compact(row.Tokens));
                projects.Items.Add(item);
            }
        }
        finally { projects.EndUpdate(); }
        projects.Visible = rows.Count > 0;
        projects.ResizeColumns();
        empty.Visible = rows.Count == 0;
        empty.Text = usage is null ? "正在读取本地 Codex 会话…" : "这段时间没有本地 Token 记录";
        note.Text = usage?.Partial == true ? "部分会话暂时无法读取，稍后会重试。" : "本地 Codex 记录 · 按工作目录归类";
    }

    internal static string Compact(long value, bool chart = false)
    {
        string format = chart ? "0.#" : "0.##";
        if (value >= 100_000_000) return (value / 100_000_000d).ToString(format) + " 亿";
        if (value >= 10_000) return (value / 10_000d).ToString(chart && value >= 1_000_000 ? "0" : format) + " 万";
        return value.ToString("N0");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) tooltips.Dispose();
        base.Dispose(disposing);
    }

    private sealed class DayButton : Button
    {
        public string Amount = "";
        public string Caption = "";
        public double Ratio;
        public bool Selected;
        public DayButton()
        {
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? BackColor);
            int gap = LogicalToDeviceUnits(5);
            int amountHeight = LogicalToDeviceUnits(23);
            int captionHeight = LogicalToDeviceUnits(40);
            int plotBottom = Height - captionHeight - gap;
            if (Width <= gap * 2 || plotBottom <= amountHeight) return;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = RoundedShapes.Create(new RectangleF(.5f, .5f, Width - 1, Height - 1), LogicalToDeviceUnits(8));
            using var fill = new SolidBrush(Selected ? Color.FromArgb(224, 237, 255) : Color.White);
            using var outline = new Pen(Selected ? Blue : Color.FromArgb(222, 229, 239));
            g.FillPath(fill, path);
            g.DrawPath(outline, path);
            int barHeight = Ratio > 0 ? Math.Max(2, (int)((plotBottom - amountHeight) * Ratio)) : 0;
            int barWidth = LogicalToDeviceUnits(18);
            using var paint = new SolidBrush(Selected ? Blue : Color.FromArgb(110, 162, 232));
            if (barHeight > 0) g.FillRectangle(paint, (Width - barWidth) / 2, plotBottom - barHeight, barWidth, barHeight);
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, Amount, Font, new Rectangle(0, gap, Width, amountHeight - gap), Enabled ? Ink : Muted, flags);
            TextRenderer.DrawText(g, Caption, Font, new Rectangle(0, plotBottom + gap, Width, captionHeight), Enabled ? Ink : Muted, flags);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -3, -3));
        }
    }
}
