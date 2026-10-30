using System.Reflection;
using System.Text.Json;

namespace UsageLite;

public static class MenuSmokeTest
{
    // Calls the same presenter as NotifyIcon, without moving the mouse.
    public static int Run(string[] args)
    {
        int index = Array.IndexOf(args, "--output");
        if (index < 0 || index + 1 >= args.Length) return 2;
        var screen = Screen.PrimaryScreen!;
        var area = screen.WorkingArea;
        var full = screen.Bounds;
        Point point = new(area.Right - 20, area.Bottom + (full.Bottom - area.Bottom) / 2);
        if (area.Top > full.Top) point = new(area.Right - 20, full.Top + (area.Top - full.Top) / 2);
        else if (area.Left > full.Left) point = new(full.Left + (area.Left - full.Left) / 2, area.Bottom - 20);
        else if (area.Right < full.Right) point = new(area.Right + (full.Right - area.Right) / 2, area.Bottom - 20);
        var results = new List<object>();
        int failures = 0;
        using var context = new ApplicationContext();
        var presenter = typeof(ContextMenuStrip).GetMethod("ShowInTaskbar", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException("WinForms tray presenter unavailable");
        void RunCase(bool fix)
        {
            var menu = new ContextMenuStrip { Font = new Font("Microsoft YaHei UI", 10, FontStyle.Regular) };
            menu.Items.Add("打开额度面板"); menu.Items.Add("刷新本地记录");
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("托盘额度来源", null, new ToolStripMenuItem("Codex"), new ToolStripMenuItem("Claude")));
            menu.Items.Add(new ToolStripMenuItem("托盘显示窗口", null, new ToolStripMenuItem("优先显示 5 小时"), new ToolStripMenuItem("优先显示每周")));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Usage Lite 0.1.0 · 本地读取") { Enabled = false }); menu.Items.Add("退出");
            if (fix) TaskbarMenu.Attach(menu);
            presenter.Invoke(menu, [point.X, point.Y]);
            menu.BeginInvoke(() =>
            {
                bool contained = area.Contains(menu.Bounds);
                results.Add(new { fix, bounds = new { menu.Left, menu.Top, menu.Width, menu.Height, menu.Right, menu.Bottom }, contained, dpi = menu.DeviceDpi });
                if (fix && !contained) failures++;
                menu.Close(); menu.Dispose();
                if (fix) context.ExitThread(); else RunCase(true);
            });
        }
        RunCase(false);
        Application.Run(context);
        File.WriteAllText(args[index + 1], JsonSerializer.Serialize(new { failures, workingArea = new { area.Left, area.Top, area.Right, area.Bottom }, cases = results }, new JsonSerializerOptions { WriteIndented = true }));
        return failures == 0 ? 0 : 1;
    }
}
