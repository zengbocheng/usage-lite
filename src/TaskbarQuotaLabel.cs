using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace UsageLite;

// All methods, timer callbacks and native window ownership stay on the WinForms UI thread.
internal sealed class TaskbarQuotaLabel : Form
{
    private readonly System.Windows.Forms.Timer layoutTimer = new() { Interval = 500 };
    private readonly ToolTip tooltip = new();
    private readonly Action openPanel;
    private readonly ContextMenuStrip menu;
    private IntPtr reservedHost, reservedBar;
    private Rectangle originalHost, appliedHost;
    private readonly HashSet<Rectangle> ownedHostBounds = new();
    private string labelText = "Codex: —", lastPaint = "";
    private string? lastFailure;
    private bool stale;

    public TaskbarQuotaLabel(Action openPanel, ContextMenuStrip menu)
    {
        this.openPanel = openPanel; this.menu = menu;
        Text = "Usage Lite · 任务栏额度";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        layoutTimer.Tick += (_, _) => UpdateLayout();
        layoutTimer.Start();
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var value = base.CreateParams; value.ExStyle |= 0x08080080; return value; } // NOACTIVATE | LAYERED | TOOLWINDOW
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; } // MA_NOACTIVATE
        base.WndProc(ref m);
    }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) openPanel();
        else if (e.Button == MouseButtons.Right) ShowContextMenu(e.Location);
    }

    internal void ShowContextMenu(Point location)
    {
        TaskbarNative.SetForegroundWindow(Handle);
        menu.Show(this, location);
    }

    public void UpdateQuota(QuotaSnapshot snapshot, bool weekly, DateTimeOffset now, string details)
    {
        labelText = TaskbarLabelLayout.Text(snapshot, weekly, now);
        stale = snapshot.Stale(now);
        tooltip.SetToolTip(this, details);
        UpdateLayout();
    }

    private void UpdateLayout()
    {
        if (IsDisposed || Disposing) return;
        try { RefreshPlacement(); lastFailure = null; }
        catch (Exception ex) when (ex is Win32Exception or ExternalException or InvalidOperationException)
        {
            Hide(); RestoreTaskList();
            if (lastFailure != ex.GetType().Name) { lastFailure = ex.GetType().Name; AppLog.Write("taskbar label unavailable: " + lastFailure); }
        }
    }

    private void RefreshPlacement()
    {
        IntPtr bar = TaskbarNative.FindWindow("Shell_TrayWnd", null);
        if (bar == IntPtr.Zero || TaskbarNative.Bounds(bar) is not { } bounds)
        { Hide(); RestoreTaskList(); return; }
        if (reservedBar != IntPtr.Zero && reservedBar != bar) RestoreTaskList();
        var screen = Screen.FromHandle(bar);
        if (!TaskbarNative.IsWindowVisible(bar) || !TaskbarLabelLayout.FullyVisible(bounds, screen.Bounds) || IsFullscreen(bar, screen.Bounds))
        { Hide(); return; }
        IntPtr host = IntPtr.Zero;
        Rectangle? tray = null;
        var widgets = new List<Rectangle>();
        foreach (IntPtr child in TaskbarNative.Children(bar))
        {
            if (!TaskbarNative.IsWindowVisible(child) || TaskbarNative.Bounds(child) is not { } childBounds) continue;
            string kind = TaskbarNative.ClassName(child);
            if (kind == "TrayNotifyWnd") tray = childBounds;
            else if (kind == "ReBarWindow32") host = child;
            else if (kind.StartsWith("DynamicContent", StringComparison.Ordinal) && childBounds.Width > 0 && childBounds.Height > 0) widgets.Add(childBounds);
        }
        // Only use a known native task-list host. Unsupported layouts remain icon-only.
        if (host == IntPtr.Zero || tray is null || TaskbarNative.Bounds(host) is not { } actualHost)
        { Hide(); RestoreTaskList(); return; }
        if (reservedHost != IntPtr.Zero && reservedHost != host) RestoreTaskList();
        Rectangle baseline = reservedHost == host && (actualHost == originalHost || ownedHostBounds.Contains(actualHost)) ? originalHost : actualHost;
        int dpi = (int)TaskbarNative.GetDpiForWindow(bar);
        if (dpi <= 0) dpi = 96;
        using var font = new Font("Segoe UI", 10 * dpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
        Size measured = TextRenderer.MeasureText(labelText, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        int padding = Math.Max(4, 6 * dpi / 96), gap = Math.Max(4, 4 * dpi / 96);
        var size = new Size(measured.Width + padding * 2, Math.Min(bounds.Height, Math.Max(measured.Height + padding, 24 * dpi / 96)));
        var placement = TaskbarLabelLayout.Place(bounds, baseline, tray.Value, widgets, size, gap);
        if (placement is null) { Hide(); RestoreTaskList(); return; }
        if (placement.TaskList != actualHost)
        {
            if (reservedHost != host || baseline != originalHost) ownedHostBounds.Clear();
            reservedHost = host; reservedBar = bar; originalHost = baseline; appliedHost = placement.TaskList;
            ownedHostBounds.Add(appliedHost);
            if (!TaskbarNative.MoveShellChild(host, bar, appliedHost)) { Hide(); RestoreTaskList(); return; }
            // Explorer applies the asynchronous resize on its own thread. Wait until it has done so.
            if (TaskbarNative.Bounds(host) != appliedHost) { Hide(); return; }
        }
        bool light = IsLightTaskbar();
        Color color = stale ? (light ? Color.FromArgb(92, 106, 126) : Color.FromArgb(180, 190, 204)) : light ? Color.FromArgb(32, 33, 36) : Color.FromArgb(242, 242, 242);
        string signature = labelText + ":" + dpi + ":" + color.ToArgb() + ":" + size;
        if (!Visible) { Bounds = placement.Label; Show(); lastPaint = ""; }
        if (signature != lastPaint)
        {
            using var image = Render(labelText, font, color, size);
            TaskbarNative.PaintLayered(Handle, image, placement.Label.Location);
            lastPaint = signature;
        }
        TaskbarNative.ShowWithoutActivation(Handle, placement.Label);
    }

    private static bool IsLightTaskbar()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int value && value != 0; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    private static bool IsFullscreen(IntPtr bar, Rectangle monitor)
    {
        IntPtr window = TaskbarNative.GetForegroundWindow();
        if (window == IntPtr.Zero || window == bar || !TaskbarNative.IsWindowVisible(window)) return false;
        if (TaskbarNative.ClassName(window) is "Progman" or "WorkerW" or "Shell_SecondaryTrayWnd") return false;
        return TaskbarNative.Bounds(window) is { } bounds && TaskbarLabelLayout.CoversMonitor(bounds, monitor);
    }

    internal static Bitmap Render(string text, Font font, Color color, Size size)
    {
        var image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(image);
        // Alpha 1 keeps the whole text rectangle clickable without a visible background.
        g.Clear(Color.FromArgb(1, 0, 0, 0));
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(text, font, brush, new RectangleF(0, 0, size.Width, size.Height), format);
        return image;
    }

    private void RestoreTaskList()
    {
        // Restore only our own unchanged resize; never undo a new Explorer layout.
        // Queue restoration even if Explorer has not applied our asynchronous resize yet.
        // Its UI thread processes this after any pending resize requests from this label.
        if (reservedHost != IntPtr.Zero && TaskbarNative.Bounds(reservedHost) is { } actual &&
            (actual == originalHost || ownedHostBounds.Contains(actual)))
            TaskbarNative.MoveShellChild(reservedHost, reservedBar, originalHost);
        reservedHost = reservedBar = IntPtr.Zero;
        originalHost = appliedHost = Rectangle.Empty;
        ownedHostBounds.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { layoutTimer.Stop(); layoutTimer.Dispose(); Hide(); RestoreTaskList(); tooltip.Dispose(); }
        base.Dispose(disposing);
    }
}
