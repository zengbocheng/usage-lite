using System.Text.Json;

namespace UsageLite;

internal static class TaskbarLabelSmokeTest
{
    public static int Run(string[] args)
    {
        int index = Array.IndexOf(args, "--output");
        if (index < 0 || index + 1 >= args.Length) return 2;
        IntPtr bar = TaskbarNative.FindWindow("Shell_TrayWnd", null);
        IntPtr host = TaskbarNative.Children(bar).FirstOrDefault(w => TaskbarNative.ClassName(w) == "ReBarWindow32");
        var before = TaskbarNative.Bounds(host);
        using var menu = new ContextMenuStrip();
        menu.Font = new Font("Microsoft YaHei UI", 10);
        menu.Items.Add("打开额度面板"); menu.Items.Add("刷新本地记录");
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("托盘额度来源"); menu.Items.Add("托盘显示窗口"); menu.Items.Add("任务栏显示额度标签");
        menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("Usage Lite · 本地读取"); menu.Items.Add("退出");
        TaskbarMenu.Attach(menu);
        using var label = new TaskbarQuotaLabel(() => { }, menu);
        using var context = new ApplicationContext();
        var quota = new CodexReader().Read(DateTimeOffset.UtcNow);
        label.UpdateQuota(quota, false, DateTimeOffset.UtcNow, "Usage Lite label verification");
        using var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        bool shown = false, reserved = false, clear = false;
        Rectangle? labelBounds = null, during = null;
        int ticks = 0;
        bool restored = false;
        bool menuContained = false;
        timer.Tick += (_, _) =>
        {
            ticks++;
            if (ticks == 1 && label.Visible)
            {
                label.ShowContextMenu(new Point(label.Width / 2, label.Height / 2));
                menu.BeginInvoke(() => { menuContained = Screen.FromHandle(menu.Handle).WorkingArea.Contains(menu.Bounds); menu.Close(); });
            }
            if (ticks == 2)
            {
                shown = label.Visible;
                labelBounds = TaskbarNative.Bounds(label.Handle);
                during = TaskbarNative.Bounds(host);
                reserved = before is not null && during is not null && during.Value.Right < before.Value.Right;
                clear = labelBounds is { } b && during is { } d && !b.IntersectsWith(d);
                label.Dispose();
            }
            if (ticks == 3)
            {
                restored = before is not null && TaskbarNative.Bounds(host) == before;
                using var immediate = new TaskbarQuotaLabel(() => { }, menu);
                immediate.UpdateQuota(quota, false, DateTimeOffset.UtcNow, "Immediate disable verification");
                // Dispose before Explorer's asynchronous resize necessarily completes.
            }
            if (ticks < 4) return;
            timer.Stop();
            var after = TaskbarNative.Bounds(host);
            bool immediateDisableRestored = before is not null && after == before;
            var automationModules = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>().Where(m => m.ModuleName.StartsWith("UIAutomation", StringComparison.OrdinalIgnoreCase)).Select(m => m.ModuleName).ToArray();
            // WinForms may load the native UIA accessibility provider for its own controls.
            // The taskbar-reading UIA client used by the old app must not be introduced.
            var automationClients = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).Where(n => n is "UIAutomationClient" or "UIAutomationTypes").ToArray();
            int failures = (shown ? 0 : 1) + (reserved ? 0 : 1) + (clear ? 0 : 1) + (restored ? 0 : 1) + (immediateDisableRestored ? 0 : 1) + (menuContained ? 0 : 1) + automationClients.Length;
            File.WriteAllText(args[index + 1], JsonSerializer.Serialize(new { failures, shown, reserved, clear, restored, immediateDisableRestored, menuContained, automationModules, automationClients, before, during, labelBounds, after }, new JsonSerializerOptions { WriteIndented = true }));
            context.ExitThread();
        };
        timer.Start();
        Application.Run(context);
        using var result = JsonDocument.Parse(File.ReadAllText(args[index + 1]));
        return result.RootElement.GetProperty("failures").GetInt32() == 0 ? 0 : 1;
    }
}
