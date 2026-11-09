using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace UsageLite;

public static class SelfTests
{
    public static int Run(string[] args)
    {
        int outputAt = Array.IndexOf(args, "--output"), fixtureAt = Array.IndexOf(args, "--fixtures");
        if (outputAt < 0 || outputAt + 1 >= args.Length || fixtureAt < 0 || fixtureAt + 1 >= args.Length) return 2;
        string artifacts = Path.GetDirectoryName(Path.GetFullPath(args[outputAt + 1]))!;
        string root = Path.Combine(artifacts, "cases-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string fixtureRoot = Path.GetFullPath(args[fixtureAt + 1]);
        var results = new List<object>(); int failures = 0;
        var now = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);
        JsonElement J(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }
        void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        void Test(string name, Action body)
        {
            try { body(); results.Add(new { name, passed = true }); }
            catch (Exception ex) { failures++; results.Add(new { name, passed = false, error = ex.Message, exception = ex.GetType().Name }); }
        }
        QuotaSnapshot? Parse(string json, DateTimeOffset? time = null) => CodexReader.Parse(J(json), time ?? now, "test", now);
        string Case(string name) { string p = Path.Combine(root, name); Directory.CreateDirectory(p); return p; }
        string CreateProfile(string folder, string samples, int version = 2)
        {
            string path = Path.Combine(folder, "Claude"); Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "plan-usage-history.json"), "{\"version\":" + version + ",\"samples\":" + samples + "}"); return path;
        }
        string Sample(long seconds, string org, string usage) => "{\"t\":" + seconds * 1000 + ",\"org\":\"" + org + "\",\"u\":" + usage + "}";
        Test("Codex weekly-only primary is not a 5-hour quota", () => { var q = Parse("{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":26,\"window_minutes\":10080,\"resets_at\":2000010000}}"); Check(q?.Session is null && q?.Weekly?.Used == 26, "window misclassified"); });
        Test("Codex two distinct windows and reset_at spelling", () => { var q = Parse("{\"primary\":{\"used_percent\":8,\"window_minutes\":300,\"reset_at\":2000000100},\"secondary\":{\"used_percent\":51,\"window_minutes\":10080}}"); Check(q?.Session?.Remaining == 92 && q.Weekly?.Remaining == 49 && q.Session.ResetAt == now.AddSeconds(100), "percent or timestamp wrong"); });
        Test("Codex rejects Spark-specific quotas", () => Check(Parse("{\"limit_id\":\"codex_spark\",\"primary\":{\"used_percent\":1}}") is null, "wrong quota scope"));
        Test("Invalid percentages never become 100% remaining", () => { foreach (string invalid in new[] { "true", "-1", "101", "null", "\"26\"" }) Check(Parse("{\"primary\":{\"used_percent\":" + invalid + "}}") is null, invalid); });
        Test("Future quota observation rejected", () => Check(Parse("{\"primary\":{\"used_percent\":25}}", now.AddMinutes(2)) is null, "future accepted"));
        Test("Expired quota becomes unknown without inventing availability", () => { var q = Parse("{\"primary\":{\"used_percent\":100,\"resets_at\":1999999999}}"); Check(q?.Preferred(false, now) is null, "expired quota shown as current"); });
        Test("Codex response records deduplicate and roll up by local day and project", () =>
        {
            string root = Case("token-responses");
            string active = Path.Combine(root, "sessions");
            string archive = Path.Combine(root, "archived_sessions");
            Directory.CreateDirectory(active);
            Directory.CreateDirectory(archive);
            string Row(string at, string type, object payload) => JsonSerializer.Serialize(new { timestamp = at, type, payload }) + "\n";
            string Response(string at, string id, int count) => Row(at, "token_usage_record",
                new { response_id = id, usage = new { total_tokens = count, cached_input_tokens = 2 } });
            string log = Row("2026-11-01T01:00:00Z", "session_meta", new { id = "a", cwd = @"\\?\D:\Alpha" }) +
                Response("2026-11-02T06:59:59Z", "one", 10) +
                Response("2026-11-02T07:00:00Z", "two", 20) +
                Row("2026-11-02T07:00:01Z", "token_usage_record",
                    new { thread_id = "parent", response_id = "inherited", usage = new { total_tokens = 999 } }) +
                Row("2026-11-02T07:01:00Z", "turn_context", new { cwd = @"D:\Beta" }) +
                Response("2026-11-02T07:02:00Z", "two", 20) +
                Response("2026-11-02T07:03:00Z", "three", 7);
            string activeFile = Path.Combine(active, "current.jsonl");
            File.WriteAllText(activeFile, log);
            File.WriteAllText(Path.Combine(archive, "copy.jsonl"), log);
            File.WriteAllText(Path.Combine(active, "no-project.jsonl"), Response("2026-11-02T07:05:00Z", "four", 3));
            var reader = new CodexTokenReader(root);
            var zone = TimeZoneInfo.CreateCustomTimeZone("Test -07", TimeSpan.FromHours(-7), "Test", "Test");
            var readAt = DateTimeOffset.Parse("2026-11-02T09:00:00Z");
            var monday = new DateOnly(2026, 11, 2);
            var usage = reader.Read(readAt, zone);
            Check(usage.Today == monday && TokenUsage.WeekStart(usage.Today) == monday &&
                TokenUsage.WeekStart(monday.AddDays(-1)) == monday.AddDays(-7), "natural week boundary");
            Check(usage.Total(monday.AddDays(-1)) == 10 && usage.Total(monday) == 30, "day grouping or duplicate response");
            Check(usage.Projects(monday, 7).Select(p => (p.Name, p.Tokens)).SequenceEqual(new[] { ("Alpha", 20L), ("Beta", 7L), ("未归类", 3L) }), "project attribution or weekly reconciliation");

            string appended = Response("2026-11-02T07:06:00Z", "five", 9);
            File.AppendAllText(activeFile, appended[..^1]);
            Check(reader.Read(readAt, zone).Total(monday) == 30, "incomplete line counted");
            File.AppendAllText(activeFile, "\n");
            Check(reader.Read(readAt, zone).Total(monday) == 39 &&
                reader.Read(readAt, zone).Total(monday) == 39, "incremental refresh missed or repeated response");
        });
        Test("Codex legacy snapshots count fresh increments and skip replay and context estimates", () =>
        {
            string root = Case("token-legacy");
            string sessions = Path.Combine(root, "sessions");
            Directory.CreateDirectory(sessions);
            string Snapshot(int second, int total, int last, int input) => JsonSerializer.Serialize(new
            {
                timestamp = $"2026-11-02T07:00:{second:00}Z", type = "event_msg",
                payload = new
                {
                    type = "token_count", info = new
                    {
                        total_token_usage = new { total_tokens = total },
                        last_token_usage = new { total_tokens = last, input_tokens = input }
                    }
                }
            });
            File.WriteAllLines(Path.Combine(sessions, "legacy.jsonl"), new[]
            {
                Snapshot(0, 1000, 10, 8), Snapshot(1, 1000, 10, 8), Snapshot(2, 1020, 20, 18),
                Snapshot(3, 1020, 950, 0), Snapshot(4, 6, 6, 4), Snapshot(5, 13, 7, 4)
            });
            var result = new CodexTokenReader(root).Read(DateTimeOffset.Parse("2026-11-02T08:00:00Z"), TimeZoneInfo.Utc);
            Check(result.Total(result.Today) == 43, "legacy replay, estimate or reset was miscounted");
        });
        Test("Missing local directories return empty snapshots", () => { var p = Case("missing"); Check(new CodexReader(p).Read(now).Session is null, "Codex missing"); Check(new ClaudeReader(p, p, p).Read(now).Weekly is null, "Claude missing"); });
        Test("JSONL incomplete tail retains preceding valid quota", () =>
        {
            string p = Case("jsonl"); Directory.CreateDirectory(Path.Combine(p, "sessions"));
            string file = Path.Combine(p, "sessions", "session.jsonl");
            string line = "{\"timestamp\":\"2033-05-18T03:33:20Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":13,\"window_minutes\":300}}}}";
            File.WriteAllText(file, line + "\n{\"rate_limits\":");
            var q = new CodexReader(p).Read(now);
            Check(q.Session?.Used == 13, "valid quota lost");
            Check(File.ReadAllText(file) == line + "\n{\"rate_limits\":", "input modified");
        });
        Test("SQLite websocket quotas read without modifying database", () =>
        {
            string p = Case("sqlite"); string dbPath = Path.Combine(p, "logs_2.sqlite");
            using (var db = new SqliteConnection("Data Source=" + dbPath + ";Pooling=False"))
            {
                db.Open(); using var command = db.CreateCommand();
                command.CommandText = "CREATE TABLE logs(id INTEGER PRIMARY KEY,ts INTEGER,ts_nanos INTEGER,feedback_log_body TEXT); INSERT INTO logs VALUES(1,2000000000,0,$body)";
                command.Parameters.AddWithValue("$body", "websocket event: {\"type\":\"codex.rate_limits\",\"rate_limits\":{\"primary\":{\"used_percent\":12,\"window_minutes\":300,\"reset_at\":2000001000}}}"); command.ExecuteNonQuery();
            }
            byte[] before = SHA256.HashData(File.ReadAllBytes(dbPath));
            Check(new CodexReader(p).Read(now).Session?.Used == 12, "SQLite not read");
            Check(before.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(dbPath))), "SQLite modified");
        });
        Test("Claude latest organization cannot borrow previous weekly quota", () =>
        {
            string p = Case("org-switch");
            CreateProfile(p, "[" + Sample(1999999990, "org-a", "{\"sd\":99}") + "," + Sample(2000000000, "org-b", "{\"fh\":12}") + "]");
            var q = new ClaudeReader(p, p, p).Read(now);
            Check(q.Session?.Used == 12 && q.Weekly is null && q.Session.ResetAt is null, "cross-org merge or invented reset");
        });
        Test("Claude stale desktop history is not current quota", () => { string p = Case("stale"); CreateProfile(p, "[" + Sample(1999992799, "org-a", "{\"fh\":15}") + "]"); Check(new ClaudeReader(p, p, p).Read(now).Session is null, "stale accepted"); });
        Test("Claude Store profile discovered", () =>
        {
            string p = Case("store"); string roaming = Path.Combine(p, "roaming");
            string profileParent = Path.Combine(p, "Packages", "Claude_test", "LocalCache", "Roaming");
            CreateProfile(profileParent, "[" + Sample(2000000000, "org-a", "{\"fh\":8,\"sd\":0}") + "]");
            Check(new ClaudeReader(roaming, p, p).Read(now).Session?.Remaining == 92, "Store path not found");
        });
        Test("Claude relocated desktop data is discovered and supersedes older profiles", () =>
        {
            string root = Case("relocated-desktop");
            string roaming = Path.Combine(root, "roaming");
            string local = Path.Combine(root, "local");
            CreateProfile(roaming, "[" + Sample(1999999940, "org-a", "{\"fh\":70,\"sd\":80}") + "]");
            string profile = Path.Combine(local, "Claude-Data", "claude_test");
            Directory.CreateDirectory(profile);
            string history = Path.Combine(profile, "plan-usage-history.json");
            string contents = "{\"version\":2,\"samples\":[" + Sample(2000000000, "org-a", "{\"fh\":12,\"sd\":34}") + "]}";
            File.WriteAllText(history, contents);
            var reader = new ClaudeReader(roaming, local, root);
            var quota = reader.Read(now);
            Check(quota.Session?.Remaining == 88 && quota.Weekly?.Remaining == 66, "relocated profile missed");
            File.WriteAllText(history, "{\"version\":2,\"samples\":[" + Sample(2000000001, "org-a", "{\"fh\":13,\"sd\":35}") + "]}");
            Check(reader.Read(now.AddSeconds(1)).Session?.Remaining == 87, "refresh did not re-read the relocated profile");
        });
        Test("Claude malformed history is read-only", () =>
        {
            string p = Case("malformed"); string profile = CreateProfile(p, "[]"); string f = Path.Combine(profile, "plan-usage-history.json");
            File.WriteAllText(f, "{\"version\":");
            Check(new ClaudeReader(p, p, p).Read(now).Session is null && File.ReadAllText(f) == "{\"version\":", "malformed input changed");
        });
        Test("Only matching percent and time may supply a Claude reset", () =>
        {
            var w = J("{\"utilization\":14,\"resets_at\":\"2033-05-18T03:35:00Z\"}");
            Check(ClaudeReader.MatchingReset(w, 14, now, now.AddHours(5)) == now.AddSeconds(100), "matching reset lost");
            Check(ClaudeReader.MatchingReset(w, 13, now, now.AddHours(5)) is null, "mismatched reset reused");
        });
        foreach (string encoding in new[] { "plain", "gzip", "deflate", "zstd" })
        {
            Test("Chromium block cache: " + encoding + " (read-only)", () =>
            {
                string path = Path.Combine(fixtureRoot, encoding);
                var before = Directory.GetFiles(path).ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
                var cached = ChromiumCache.Find(path, "https://claude.ai/api/organizations/org-a/usage", now.AddMinutes(-1), now);
                Check(cached?.Data.Get("five_hour").Get("utilization").Number() == 14 && cached.FetchedAt == now, "cache decode failed");
                Check(before.All(pair => pair.Value == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Key)))) , "cache modified");
            });
        }
        foreach (string scenario in new[] { "wrong-org", "dirty", "inactive", "stale", "bad-status", "truncated" })
            Test("Chromium refuses " + scenario, () => Check(ChromiumCache.Find(Path.Combine(fixtureRoot, scenario), "https://claude.ai/api/organizations/org-a/usage", now.AddMinutes(-1), now) is null, "unsafe cache accepted"));
        Test("Fresh HTTP cache supersedes expired history", () =>
        {
            string p = Case("fresh-http"); string profile = CreateProfile(p, "[" + Sample(1999990000, "org-a", "{\"fh\":90,\"sd\":80}") + "]");
            string target = Path.Combine(profile, "Cache", "Cache_Data"); Directory.CreateDirectory(target);
            foreach (string f in Directory.GetFiles(Path.Combine(fixtureRoot, "zstd"))) File.Copy(f, Path.Combine(target, Path.GetFileName(f)));
            var q = new ClaudeReader(p, p, p).Read(now);
            Check(q.Session?.Used == 14 && q.Weekly?.Used == 1 && q.Session.ResetAt == now.AddSeconds(100), "new cached quota not used");
        });
        Test("Concurrent cache delete-sharing supported", () =>
        {
            string f = Path.Combine(Case("sharing"), "input"); File.WriteAllText(f, "data");
            using var owner = new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            Check(Encoding.UTF8.GetString(LocalFiles.Read(f, 16)) == "data", "sharing not supported");
        });
        Test("Cache allocation bounds enforced", () =>
        {
            string path = Path.Combine(Case("bounded"), "large"); File.WriteAllBytes(path, new byte[65]);
            bool rejected = false; try { LocalFiles.Read(path, 64); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "oversized file accepted");
        });
        Test("Tray popup fits work area for every taskbar edge and scale", () =>
        {
            foreach (var area in new[] { new Rectangle(0, 0, 1920, 1040), new Rectangle(0, 40, 1920, 1040), new Rectangle(60, 0, 1860, 1080), new Rectangle(0, 0, 1860, 1080), new Rectangle(-1920, 0, 1920, 1040) })
            foreach (int gap in new[] { 4, 6, 7, 8 })
            foreach (Point cursor in new[] { new Point(area.Right - 10, area.Bottom + 25), new Point(area.Left - 20, area.Top - 15) })
            {
                var size = new Size(240 * gap / 4, 260 * gap / 4);
                var moved = new Rectangle(TaskbarMenu.Constrain(new Rectangle(cursor, size), area, gap), size);
                Check(area.Contains(moved), "menu overlaps excluded taskbar area");
            }
        });
        Test("Tray popup preserves an already safe location", () =>
        {
            var bounds = new Rectangle(200, 200, 250, 300);
            Check(TaskbarMenu.Constrain(bounds, new Rectangle(0, 0, 1920, 1040), 7) == bounds.Location, "safe popup moved");
        });
        Test("Taskbar label reserves space beside visible weather at multiple DPI scales", () =>
        {
            foreach (double scale in new[] { 1d, 1.5, 1.75, 2 })
            {
                int S(int n) => (int)(n * scale);
                var bar = new Rectangle(-S(1920), S(1040), S(1920), S(40));
                var host = new Rectangle(bar.Left + S(150), bar.Top, S(1470), bar.Height);
                var tray = new Rectangle(bar.Right - S(300), bar.Top, S(300), bar.Height);
                var weather = new Rectangle(tray.Left - S(160), bar.Top, S(160), bar.Height);
                var result = TaskbarLabelLayout.Place(bar, host, tray, [weather], new Size(S(100), S(24)), S(4));
                Check(result is not null && bar.Contains(result.Label) && !result.Label.IntersectsWith(tray) && !result.Label.IntersectsWith(weather) && !result.Label.IntersectsWith(result.TaskList), "label overlaps shell content");
                Check(result!.TaskList.Left == host.Left && result.TaskList.Width >= 160, "task list displaced or removed");
            }
        });
        Test("Taskbar label fails closed on a crowded or vertical taskbar", () =>
        {
            Check(TaskbarLabelLayout.Place(new(0, 1040, 400, 40), new(150, 1040, 100, 40), new(250, 1040, 150, 40), [], new(120, 24), 4) is null, "crowded taskbar covered");
            Check(TaskbarLabelLayout.Place(new(0, 0, 60, 1080), new(0, 150, 60, 600), new(0, 750, 60, 330), [], new(100, 24), 4) is null, "vertical taskbar covered");
        });
        Test("Taskbar visibility distinguishes auto-hide and fullscreen from maximized windows", () =>
        {
            var monitor = new Rectangle(0, 0, 1920, 1080);
            Check(TaskbarLabelLayout.FullyVisible(new(0, 1040, 1920, 40), monitor), "visible taskbar hidden");
            Check(!TaskbarLabelLayout.FullyVisible(new(0, 1078, 1920, 40), monitor), "auto-hidden taskbar shown");
            Check(TaskbarLabelLayout.CoversMonitor(monitor, monitor) && !TaskbarLabelLayout.CoversMonitor(new(-8, -8, 1936, 1056), monitor), "maximized window mistaken for fullscreen");
        });
        Test("Taskbar label follows provider and preferred quota with expired fallback", () =>
        {
            var q = new QuotaSnapshot("Codex", new(8, now.AddHours(1), 300), new(29, now.AddDays(1), 10080), now, "test");
            Check(TaskbarLabelLayout.Text(q, false, now) == "Codex: 92%" && TaskbarLabelLayout.Text(q, true, now) == "Codex: 71%", "wrong quota label");
            Check(TaskbarLabelLayout.Text(q, false, now.AddHours(2)) == "Codex: 71%", "expired session not skipped");
            Check(TaskbarLabelLayout.Text(QuotaSnapshot.Empty("Claude", "test"), false, now) == "Claude: —", "missing quota invented");
        });
        Test("Taskbar label defaults on without overriding an explicit disabled preference", () =>
        {
            var settings = JsonSerializer.Deserialize<AppSettings>("{\"TrayProvider\":\"Claude\",\"Weekly\":true}")!;
            Check(new AppSettings().TaskbarLabelEnabled && settings.TaskbarLabelEnabled && settings.TrayProvider == "Claude" && settings.Weekly, "default or existing provider preferences changed");
            Check(!JsonSerializer.Deserialize<AppSettings>("{\"TaskbarLabelEnabled\":false}")!.TaskbarLabelEnabled, "explicit disabled preference overwritten");
        });
        Test("Transparent taskbar label has clickable alpha and visible regular text", () =>
        {
            using var font = new Font("Segoe UI", 10, FontStyle.Regular);
            using var image = TaskbarQuotaLabel.Render("Codex: 71%", font, Color.White, new Size(160, 42));
            Check(image.GetPixel(0, 0).A == 1, "background is opaque or not clickable");
            bool text = false;
            for (int y = 0; y < image.Height && !text; y++)
            for (int x = 0; x < image.Width && !text; x++) text = image.GetPixel(x, y).A > 128;
            Check(text, "quota text not rendered");
        });
        File.WriteAllText(args[outputAt + 1], JsonSerializer.Serialize(new { passed = results.Count - failures, failed = failures, results }, new JsonSerializerOptions { WriteIndented = true }));
        return failures == 0 ? 0 : 1;
    }
}
