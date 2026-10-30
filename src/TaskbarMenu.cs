namespace UsageLite;

public static class TaskbarMenu
{
    public static void Attach(ContextMenuStrip menu)
    {
        menu.Opened += (_, _) =>
        {
            KeepInsideWorkingArea(menu);
            // Recheck after WinForms recreates a dropdown handle for monitor DPI.
            menu.BeginInvoke(() => KeepInsideWorkingArea(menu));
        };
        menu.SizeChanged += (_, _) => KeepInsideWorkingArea(menu);
    }

    private static void KeepInsideWorkingArea(ContextMenuStrip menu)
    {
        if (menu.IsDisposed || !menu.Visible || !menu.IsHandleCreated) return;
        Rectangle area = Screen.FromHandle(menu.Handle).WorkingArea;
        int gap = Math.Max(4, (int)Math.Ceiling(menu.DeviceDpi / 96d * 4));
        Point location = Constrain(menu.Bounds, area, gap);
        if (menu.Location != location) menu.Location = location;
    }

    public static Point Constrain(Rectangle menu, Rectangle area, int gap)
    {
        int left = area.Left + gap, top = area.Top + gap;
        return new Point(
            Math.Clamp(menu.Left, left, Math.Max(left, area.Right - gap - menu.Width)),
            Math.Clamp(menu.Top, top, Math.Max(top, area.Bottom - gap - menu.Height)));
    }
}
