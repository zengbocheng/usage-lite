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
        File.WriteAllText(args[outputAt + 1], JsonSerializer.Serialize(new { passed = results.Count - failures, failed = failures, results }, new JsonSerializerOptions { WriteIndented = true }));
        return failures == 0 ? 0 : 1;
    }
}
