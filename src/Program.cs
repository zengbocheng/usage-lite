using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace UsageLite;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        if (args.Contains("--self-test")) return SelfTests.Run(args);
        if (args.Contains("--menu-smoke-test"))
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            return MenuSmokeTest.Run(args);
        }
        if (args.Contains("--snapshot"))
        {
            var snapshot = new QuotaService().Read();
            string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            int index = Array.IndexOf(args, "--output");
            if (index < 0 || index + 1 >= args.Length) return 2;
            File.WriteAllText(args[index + 1], json);
            return 0;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write("fatal " + (e.ExceptionObject as Exception)?.GetType().Name);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        using var singleInstance = new Mutex(true, @"Local\UsageLite.Tray.v1", out bool first);
        if (!first)
        {
            MessageBox.Show("Usage Lite 已在运行，请点击托盘中的蓝色图标。", "Usage Lite", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Contains("--smoke-test"))
        {
            int index = Array.IndexOf(args, "--output");
            if (index < 0 || index + 1 >= args.Length) return 2;
            using var form = new MainForm();
            form.UpdateData(new QuotaService().Read());
            form.Show(); form.Activate();
            using var timer = new System.Windows.Forms.Timer { Interval = 700 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                using var image = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                image.Save(args[index + 1], System.Drawing.Imaging.ImageFormat.Png);
                form.Shutdown();
            };
            timer.Start(); Application.Run(form); return 0;
        }
        AppLog.Write("start version=0.1.0 pid=" + Environment.ProcessId);
        try
        {
            using var context = new TrayContext(args.Contains("--show"));
            Application.Run(context);
            AppLog.Write("exit normal");
            return 0;
        }
        finally { singleInstance.ReleaseMutex(); }
    }
}

public sealed class QuotaService
{
    private readonly CodexReader codex = new();
    private readonly ClaudeReader claude = new();
    public DashboardData Read()
    {
        var now = DateTimeOffset.UtcNow;
        QuotaSnapshot ReadSafe(Func<QuotaSnapshot> loader, string provider)
        {
            try { return loader(); }
            catch (Exception ex) { AppLog.Write("quota read failed provider=" + provider + " exception=" + ex.GetType().Name); return QuotaSnapshot.Empty(provider, "本地记录暂时不可读，稍后会重试。"); }
        }
        return new(ReadSafe(() => codex.Read(now), "Codex"), ReadSafe(() => claude.Read(now), "Claude"), now);
    }
}

public sealed class TrayContext : ApplicationContext
{
    private readonly MainForm form = new();
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip menu = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 60000 };
    private readonly QuotaService service = new();
    private readonly AppSettings settings = AppSettings.Load();
    private DashboardData? data;
    private bool busy, closing;
    private string? iconKey;
    private readonly ToolStripMenuItem codexItem = new("Codex");
    private readonly ToolStripMenuItem claudeItem = new("Claude");
    private readonly ToolStripMenuItem sessionItem = new("优先显示 5 小时");
    private readonly ToolStripMenuItem weeklyItem = new("优先显示每周");
    private readonly ToolStripMenuItem refreshItem;
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    public TrayContext(bool show)
    {
        // Force a UI-thread handle before scheduling work; all icon/form changes stay on this thread.
        _ = form.Handle;
        tray = new NotifyIcon { Text = "Usage Lite · 正在读取本地额度", Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!), Visible = true, ContextMenuStrip = menu };
        menu.Font = new Font("Microsoft YaHei UI", 10, FontStyle.Regular);
        TaskbarMenu.Attach(menu);
        menu.Items.Add("打开额度面板", null, (_, _) => form.ShowPanel());
        refreshItem = new("刷新本地记录", null, (_, _) => _ = RefreshAsync());
        menu.Items.Add(refreshItem);
        menu.Items.Add(new ToolStripSeparator());
        var providers = new ToolStripMenuItem("托盘额度来源");
        providers.DropDownItems.AddRange([codexItem, claudeItem]);
        menu.Items.Add(providers);
        var windows = new ToolStripMenuItem("托盘显示窗口");
        windows.DropDownItems.AddRange([sessionItem, weeklyItem]);
        menu.Items.Add(windows);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Usage Lite 0.1.0 · 本地读取") { Enabled = false });
        menu.Items.Add("退出", null, (_, _) => ExitThread());
        codexItem.Click += (_, _) => Change(() => settings.TrayProvider = "Codex");
        claudeItem.Click += (_, _) => Change(() => settings.TrayProvider = "Claude");
        sessionItem.Click += (_, _) => Change(() => settings.Weekly = false);
        weeklyItem.Click += (_, _) => Change(() => settings.Weekly = true);
        menu.Opening += (_, _) => UpdateChecks();
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) { if (form.Visible) form.Hide(); else form.ShowPanel(); } };
        form.RefreshRequested += () => _ = RefreshAsync();
        timer.Tick += (_, _) => _ = RefreshAsync();
        timer.Start();
        form.BeginInvoke(() => { if (show) form.ShowPanel(); _ = RefreshAsync(); });
        UpdateChecks();
    }
    private void Change(Action mutation)
    {
        mutation();
        try { settings.Save(); }
        catch (Exception ex) when (LocalFiles.IsReadError(ex)) { AppLog.Write("settings save failed " + ex.GetType().Name); }
        UpdateChecks(); UpdateTray();
    }
    private void UpdateChecks()
    {
        codexItem.Checked = settings.TrayProvider != "Claude";
        claudeItem.Checked = !codexItem.Checked;
        weeklyItem.Checked = settings.Weekly;
        sessionItem.Checked = !settings.Weekly;
    }
    private async Task RefreshAsync()
    {
        if (busy || closing) return;
        busy = true; form.SetBusy(true); refreshItem.Enabled = false;
        try
        {
            var next = await Task.Run(service.Read);
            if (closing) return;
            data = next; form.UpdateData(next); UpdateTray();
        }
        catch (Exception ex) { AppLog.Write("refresh failed " + ex.GetType().Name); }
        finally
        {
            busy = false;
            if (!closing) { form.SetBusy(false); refreshItem.Enabled = true; }
        }
    }
    private void UpdateTray()
    {
        if (data is null || closing) return;
        var selected = settings.TrayProvider == "Claude" ? data.Claude : data.Codex;
        var now = DateTimeOffset.UtcNow;
        var window = selected.Preferred(settings.Weekly, now);
        string text = window is null ? "—" : window.Remaining.ToString("0");
        bool stale = selected.Stale(now);
        string nextKey = selected.Provider + ":" + text + ":" + stale;
        if (nextKey != iconKey)
        {
            using var bitmap = new Bitmap(32, 32);
            using var g = Graphics.FromImage(bitmap);
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var color = stale || window is null ? Color.FromArgb(100, 116, 139) : selected.Provider == "Claude" ? Color.FromArgb(185, 92, 61) : Color.FromArgb(23, 107, 218);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 0, 0, 31, 31);
            using var font = new Font("Segoe UI", text.Length > 2 ? 14 : 17, FontStyle.Regular, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, -1, 32, 32), format);
            IntPtr handle = bitmap.GetHicon();
            Icon next;
            try { using var wrapper = Icon.FromHandle(handle); next = (Icon)wrapper.Clone(); }
            finally { DestroyIcon(handle); }
            var previous = tray.Icon; tray.Icon = next; previous?.Dispose(); iconKey = nextKey;
        }
        string Row(QuotaSnapshot s)
        {
            var w = s.Preferred(settings.Weekly, now);
            return s.Provider + ": " + (w is null ? "暂无额度记录" : w.Remaining.ToString("0.#") + "% 剩余 · " + w.Title) + (s.Stale(now) ? "（较早记录）" : "");
        }
        string tooltip = Row(data.Codex) + "\n" + Row(data.Claude);
        tray.Text = tooltip.Length > 120 ? tooltip[..120] : tooltip;
    }
    protected override void ExitThreadCore()
    {
        if (closing) return;
        closing = true; timer.Stop(); tray.Visible = false;
        AppLog.Write("quit requested");
        form.Shutdown(); base.ExitThreadCore();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closing = true; timer.Dispose(); tray.Visible = false;
            var image = tray.Icon; tray.Dispose(); image?.Dispose(); menu.Dispose(); form.Dispose();
        }
        base.Dispose(disposing);
    }
}
