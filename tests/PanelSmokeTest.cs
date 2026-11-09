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
        var monday = new DateOnly(2026, 11, 2);
        form.UpdateTokens(new TokenUsage(monday.AddDays(3), new()
        {
            [monday.AddDays(-2)] = new() { ["D:/Earlier"] = 250_000 },
            [monday] = new() { ["D:/Project Alpha"] = 1_400_000 },
            [monday.AddDays(1)] = new() { ["D:/Project Beta"] = 2_300_000 },
            [monday.AddDays(2)] = new() { ["D:/Project Alpha"] = 1_700_000, [""] = 230_000 },
            [monday.AddDays(3)] = new() { ["D:/Project Alpha"] = 2_430_000, ["D:/Project Beta"] = 1_040_000, [""] = 70_000 }
        }));
        form.Show();
        Application.DoEvents();
        // Exercise the real WinForms DPI message path on this test window only.
        foreach (int dpi in new[] { 0, -1, 168, 216, 144, 96, 192, 216, 168 })
        {
            form.Hide();
            int S(int n) => (int)Math.Round(n * dpi / 96d);
            var bounds = new[] { form.Left, form.Top, form.Left + S(MainForm.PanelSize.Width), form.Top + S(MainForm.PanelSize.Height) };
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
            bool fits = cards.All(card => card.Height >= card.HeightForWidth(card.Width));
            var link = cards.SelectMany(card => card.Controls.OfType<LinkLabel>()).Single();
            // Measure against the simulated window DPI. PreferredSize uses the actual desktop DPI.
            using var measure = new Bitmap(1, 1);
            measure.SetResolution(form.DeviceDpi, form.DeviceDpi);
            using var graphics = Graphics.FromImage(measure);
            var lineSize = graphics.MeasureString(link.Text, link.Font);
            bool linkFits = link.Parent!.ClientRectangle.Contains(link.Bounds) &&
                link.Height >= lineSize.Height && link.Width >= lineSize.Width &&
                link.Text.StartsWith(((QuotaCard)link.Parent).SourceText) &&
                link.Text[link.LinkArea.Start..].StartsWith("今日已用");
            var footer = (TableLayoutPanel)layout.GetControlFromPosition(0, 3)!;
            var refresh = footer.GetControlFromPosition(0, 0)!;
            var hint = footer.GetControlFromPosition(1, 0)!;
            bool footerFits = footer.Bottom <= layout.ClientSize.Height - layout.Padding.Bottom &&
                hint.Left >= refresh.Right && footer.ClientRectangle.Contains(hint.Bounds);
            bool passed = fits && footerFits && linkFits;
            if (!passed) failures++;
            using var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
            image.Save(Path.Combine(Path.GetDirectoryName(output)!, $"panel-{dpi}dpi.png"));
            results.Add(new { dpi, actualDpi = form.DeviceDpi, passed, footerFits, linkFits, size = new { form.Width, form.Height },
                cards = cards.Select(c => new { c.Width, c.Height, logicalHeight = c.Height / (c.Width / 400d) }).ToArray() });
        }
        form.Hide();
        form.ClientSize = new Size(990, 1175);
        form.ShowPanel();
        Application.DoEvents();
        bool standardSize = form.ClientSize == form.LogicalToDeviceUnits(MainForm.PanelSize);
        if (!standardSize) failures++;
        results.Add(new { name = "Reopening restores the standard panel size", passed = standardSize });
        using (var image = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
            image.Save(Path.Combine(Path.GetDirectoryName(output)!, "panel-reopened.png"));
        }
        var dashboard = (TableLayoutPanel)form.Controls[0];
        var tokenLink = dashboard.Controls.OfType<QuotaCard>().SelectMany(card => card.Controls.OfType<LinkLabel>()).Single();
        tokenLink.AccessibilityObject.GetChild(0)!.DoDefaultAction();
        Application.DoEvents();
        var detail = form.Controls.OfType<TokenUsageView>().Single();
        var chart = (TableLayoutPanel)detail.GetControlFromPosition(0, 3)!;
        var projects = detail.GetControlFromPosition(0, 5)!.Controls.OfType<ListView>().Single();
        bool openedToday = detail.Visible && !dashboard.Visible && projects.Items.Count == 3 &&
            projects.Items[0].SubItems[1].Text == "243 万";
        using (var image = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
            image.Save(Path.Combine(Path.GetDirectoryName(output)!, "token-usage.png"));
        }
        ((Button)chart.GetControlFromPosition(0, 0)!).PerformClick();
        bool dayProjects = projects.Items.Count == 1 && projects.Items[0].SubItems[1].Text == "140 万";
        var weekTotal = (Button)detail.GetControlFromPosition(0, 2)!;
        weekTotal.PerformClick();
        bool weekProjects = weekTotal.Text.EndsWith("917 万 Token", StringComparison.Ordinal) &&
            projects.Items.Count == 3 && projects.Items[0].SubItems[1].Text == "553 万";
        var navigation = (TableLayoutPanel)detail.GetControlFromPosition(0, 1)!;
        ((Button)navigation.GetControlFromPosition(0, 0)!).PerformClick();
        bool previousWeek = projects.Items.Count == 1 && projects.Items[0].Text == "Earlier";
        bool tokenNavigation = openedToday && dayProjects && weekProjects && previousWeek;
        if (!tokenNavigation) failures++;
        results.Add(new { name = "Token details select today, switch day, and browse calendar weeks", passed = tokenNavigation,
            openedToday, dayProjects, weekProjects, previousWeek });
        form.Shutdown();
        File.WriteAllText(output, JsonSerializer.Serialize(new { failed = failures, results }, new JsonSerializerOptions { WriteIndented = true }));
        return failures == 0 ? 0 : 1;
    }
}
