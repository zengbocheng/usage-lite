using System.Runtime.InteropServices;
using System.Text.Json;

namespace UsageLite;

internal static class PanelSmokeTest
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    public static int Run(string[] args)
    {
        int outputAt = Array.IndexOf(args, "--output");
        if (outputAt < 0 || outputAt + 1 >= args.Length) return 2;
        string output = Path.GetFullPath(args[outputAt + 1]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var results = new List<object>();
        int failures = 0;
        using var form = new MainForm();
        var now = DateTimeOffset.UtcNow;
        form.UpdateData(new(
            new("Codex", null, new(9, now.AddDays(2), 10080), now, "Codex 本地会话"),
            new("Claude", new(29, now.AddHours(2), 300), new(24, now.AddDays(2), 10080), now, "Claude Desktop 本地缓存"), now));
        form.Show();
        Application.DoEvents();
        // Exercise the real WinForms DPI message path on this test window only.
        foreach (int dpi in new[] { 0, -1, 168, 216, 144, 96, 192, 216, 168 })
        {
            form.Hide();
            int S(int n) => (int)Math.Round(n * dpi / 96d);
            var bounds = new[] { form.Left, form.Top, form.Left + S(440), form.Top + S(522) };
            IntPtr rect = Marshal.AllocHGlobal(sizeof(int) * 4);
            try
            {
                Marshal.Copy(bounds, 0, rect, bounds.Length);
                if (dpi > 0) SendMessage(form.Handle, 0x02E0, (IntPtr)(dpi | (dpi << 16)), rect);
                else if (dpi == -1) form.ClientSize = new Size(990, 1175);
            }
            finally { Marshal.FreeHGlobal(rect); }
            form.Show();
            Application.DoEvents();
            var layout = (TableLayoutPanel)form.Controls[0];
            var cards = layout.Controls.OfType<QuotaCard>().ToArray();
            bool fits = cards.All(card => card.Height / (card.Width / 400d) >= 168);
            bool footerFits = layout.GetControlFromPosition(0, 4)!.Bottom <= layout.ClientSize.Height - layout.Padding.Bottom;
            bool passed = fits && footerFits;
            if (!passed) failures++;
            using var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
            image.Save(Path.Combine(Path.GetDirectoryName(output)!, $"panel-{dpi}dpi.png"));
            results.Add(new { dpi, actualDpi = form.DeviceDpi, passed, footerFits, size = new { form.Width, form.Height },
                cards = cards.Select(c => new { c.Width, c.Height, logicalHeight = c.Height / (c.Width / 400d) }).ToArray() });
        }
        form.Hide();
        form.ClientSize = new Size(990, 1175);
        form.ShowPanel();
        Application.DoEvents();
        bool standardSize = form.ClientSize == form.LogicalToDeviceUnits(new Size(440, 522));
        if (!standardSize) failures++;
        results.Add(new { name = "Reopening restores the standard panel size", passed = standardSize });
        using (var image = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
            image.Save(Path.Combine(Path.GetDirectoryName(output)!, "panel-reopened.png"));
        }
        form.Shutdown();
        File.WriteAllText(output, JsonSerializer.Serialize(new { failed = failures, results }, new JsonSerializerOptions { WriteIndented = true }));
        return failures == 0 ? 0 : 1;
    }
}
