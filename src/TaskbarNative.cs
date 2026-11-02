using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace UsageLite;

internal static class TaskbarNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; public readonly Rectangle Bounds => Rectangle.FromLTRB(Left, Top, Right, Bottom); }
    [StructLayout(LayoutKind.Sequential)]
    private struct Blend { public byte Operation, Flags, Alpha, Format; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string? className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int size);
    [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ScreenToClient(IntPtr window, ref Point point);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr bitmap);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr bitmap);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref Point position, ref Size size, IntPtr source, ref Point origin, int key, ref Blend blend, uint flags);

    internal static Rectangle? Bounds(IntPtr window) => window != IntPtr.Zero && GetWindowRect(window, out var bounds) ? bounds.Bounds : null;
    internal static string ClassName(IntPtr window) { var name = new StringBuilder(128); GetClassName(window, name, name.Capacity); return name.ToString(); }
    internal static IEnumerable<IntPtr> Children(IntPtr parent)
    {
        if (parent == IntPtr.Zero) yield break;
        IntPtr child = IntPtr.Zero;
        for (int count = 0; count < 128; count++)
        {
            child = FindWindowEx(parent, child, null, null);
            if (child == IntPtr.Zero) yield break;
            yield return child;
        }
    }

    internal static bool MoveShellChild(IntPtr window, IntPtr parent, Rectangle screenBounds)
    {
        if (!IsWindow(window) || GetParent(window) != parent) return false;
        Point position = screenBounds.Location;
        if (!ScreenToClient(parent, ref position)) return false;
        // Explorer owns this window: do not synchronously wait for its UI thread.
        return SetWindowPos(window, IntPtr.Zero, position.X, position.Y, screenBounds.Width, screenBounds.Height, 0x4014);
    }

    internal static void ShowWithoutActivation(IntPtr window, Rectangle bounds) =>
        SetWindowPos(window, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0050);

    internal static void PaintLayered(IntPtr window, Bitmap bitmap, Point position)
    {
        IntPtr screen = GetDC(IntPtr.Zero), dc = IntPtr.Zero, handle = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            if (screen == IntPtr.Zero) throw new Win32Exception();
            dc = CreateCompatibleDC(screen);
            if (dc == IntPtr.Zero) throw new Win32Exception();
            handle = bitmap.GetHbitmap(Color.FromArgb(0));
            previous = SelectObject(dc, handle);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new Win32Exception();
            var origin = Point.Empty;
            var size = bitmap.Size;
            var blend = new Blend { Alpha = 255, Format = 1 };
            if (!UpdateLayeredWindow(window, screen, ref position, ref size, dc, ref origin, 0, ref blend, 2)) throw new Win32Exception();
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(dc, previous);
            if (handle != IntPtr.Zero) DeleteObject(handle);
            if (dc != IntPtr.Zero) DeleteDC(dc);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    internal static object Describe()
    {
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        IntPtr label = FindWindow(null, "Usage Lite · 任务栏额度");
        return new { taskbar = taskbar.ToInt64(), bounds = Bounds(taskbar), dpi = taskbar == IntPtr.Zero ? 0 : GetDpiForWindow(taskbar),
            label = new { window = label.ToInt64(), visible = label != IntPtr.Zero && IsWindowVisible(label), bounds = Bounds(label) },
            children = Children(taskbar).Select(w => new { window = w.ToInt64(), kind = ClassName(w), bounds = Bounds(w), visible = IsWindowVisible(w) }).ToArray() };
    }
}
