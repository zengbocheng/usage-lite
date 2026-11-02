namespace UsageLite;

public sealed record TaskbarLabelPlacement(Rectangle Label, Rectangle TaskList);

public static class TaskbarLabelLayout
{
    // Reserve real task-list space rather than reading the shell accessibility tree.
    public static TaskbarLabelPlacement? Place(Rectangle bar, Rectangle taskList, Rectangle tray,
        IEnumerable<Rectangle> widgets, Size label, int gap)
    {
        if (bar.Width <= bar.Height || label.Width <= 0 || label.Height <= 0 || label.Height > bar.Height ||
            taskList.Width <= 0 || taskList.Height <= 0 || !bar.IntersectsWith(taskList) || !bar.IntersectsWith(tray)) return null;
        int right = Math.Min(bar.Right, tray.Left) - gap;
        foreach (var widget in widgets)
            if (bar.IntersectsWith(widget) && widget.Right > taskList.Left) right = Math.Min(right, widget.Left - gap);
        var bounds = new Rectangle(right - label.Width, bar.Top + (bar.Height - label.Height) / 2, label.Width, label.Height);
        int taskRight = Math.Min(taskList.Right, bounds.Left - gap);
        if (!bar.Contains(bounds) || taskRight - taskList.Left < 160) return null;
        return new(bounds, Rectangle.FromLTRB(taskList.Left, taskList.Top, taskRight, taskList.Bottom));
    }

    public static bool FullyVisible(Rectangle bar, Rectangle monitor) =>
        bar.Width > 0 && bar.Height > 0 && Rectangle.Intersect(bar, monitor).Width >= bar.Width - 2 && Rectangle.Intersect(bar, monitor).Height >= bar.Height - 2;

    public static bool CoversMonitor(Rectangle window, Rectangle monitor) =>
        window.Left <= monitor.Left + 1 && window.Top <= monitor.Top + 1 && window.Right >= monitor.Right - 1 && window.Bottom >= monitor.Bottom - 1;

    public static string Text(QuotaSnapshot snapshot, bool weekly, DateTimeOffset now) =>
        snapshot.Provider + ": " + (snapshot.Preferred(weekly, now) is { } w ? w.Remaining.ToString("0") + "%" : "—");
}
