using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace UsageLite;

public sealed class CodexReader
{
    private readonly string home;
    private readonly Dictionary<string, (long Size, DateTime Modified, QuotaSnapshot? Data)> cache = new();
    public CodexReader(string? root = null) => home = root ?? Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public QuotaSnapshot Read(DateTimeOffset now)
    {
        var snapshots = new List<QuotaSnapshot>();
        var sqlite = ReadSqlite(now);
        if (sqlite is not null) snapshots.Add(sqlite);
        var files = new List<FileInfo>();
        foreach (var root in new[] { "sessions", "archived_sessions" })
        {
            try
            {
                var directory = Path.Combine(home, root);
                if (Directory.Exists(directory))
                    files.AddRange(Directory.EnumerateFiles(directory, "*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Select(p => new FileInfo(p)));
            }
            catch (Exception ex) when (LocalFiles.IsReadError(ex)) { }
        }
        foreach (var file in files.OrderByDescending(f => f.LastWriteTimeUtc).Take(30))
        {
            try
            {
                if (!cache.TryGetValue(file.FullName, out var old) || old.Size != file.Length || old.Modified != file.LastWriteTimeUtc)
                {
                    QuotaSnapshot? latest = null;
                    foreach (string line in LocalFiles.TailLines(file.FullName))
                    {
                        if (!line.Contains("rate_limits", StringComparison.Ordinal)) continue;
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var root = doc.RootElement;
                            if (root.Get("type").Text() != "event_msg" || root.Get("payload").Get("type").Text() != "token_count") continue;
                            var parsed = Parse(root.Get("payload").Get("rate_limits"), root.Get("timestamp").Time(), "Codex 本地会话", now);
                            if (parsed is not null && (latest is null || parsed.ObservedAt >= latest.ObservedAt)) latest = parsed;
                        }
                        catch (JsonException) { }
                    }
                    old = (file.Length, file.LastWriteTimeUtc, latest);
                    cache[file.FullName] = old;
                }
                if (old.Data is not null) snapshots.Add(old.Data);
            }
            catch (Exception ex) when (LocalFiles.IsReadError(ex)) { }
        }
        if (cache.Count > 64)
        {
            var keep = files.OrderByDescending(f => f.LastWriteTimeUtc).Take(30).Select(f => f.FullName).ToHashSet();
            foreach (string key in cache.Keys.Where(k => !keep.Contains(k)).ToArray()) cache.Remove(key);
        }
        return snapshots.OrderByDescending(s => s.ObservedAt).FirstOrDefault() ?? QuotaSnapshot.Empty("Codex", "在 Codex 中使用一次后，等待本地额度记录更新。");
    }

    private QuotaSnapshot? ReadSqlite(DateTimeOffset now)
    {
        string path = Path.Combine(home, "logs_2.sqlite");
        if (!File.Exists(path)) return null;
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT ts, feedback_log_body FROM (SELECT ts,ts_nanos,id,feedback_log_body FROM logs ORDER BY id DESC LIMIT 12000) WHERE feedback_log_body LIKE '%codex.rate_limits%' OR feedback_log_body LIKE '%usage_limit_reached%' ORDER BY ts DESC,ts_nanos DESC,id DESC LIMIT 50";
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                string body = rows.IsDBNull(1) ? "" : rows.GetString(1);
                int marker = body.IndexOf("websocket event: ", StringComparison.Ordinal);
                if (marker < 0 || body.Length > 1024 * 1024) continue;
                try
                {
                    using var doc = JsonDocument.Parse(body[(marker + 17)..]);
                    DateTimeOffset? at = null;
                    try { at = DateTimeOffset.FromUnixTimeSeconds(rows.GetInt64(0)); } catch (ArgumentOutOfRangeException) { }
                    var evt = doc.RootElement;
                    QuotaSnapshot? result = null;
                    if (evt.Get("type").Text() == "codex.rate_limits") result = Parse(evt.Get("rate_limits"), at, "Codex 本地日志", now);
                    else if (evt.Get("type").Text() == "error") result = ParseError(evt, at, now);
                    if (result is not null) return result;
                }
                catch (JsonException) { }
            }
        }
        catch (Exception ex) when (ex is SqliteException || LocalFiles.IsReadError(ex)) { AppLog.Write("codex sqlite read unavailable: " + ex.GetType().Name); }
        return null;
    }

    public static QuotaSnapshot? Parse(JsonElement limits, DateTimeOffset? observed, string source, DateTimeOffset now)
    {
        if (limits.ValueKind != JsonValueKind.Object || observed is null || observed > now.AddMinutes(1)) return null;
        string? id = limits.Get("limit_id").Text();
        if (!string.IsNullOrEmpty(id) && id != "codex") return null;
        var primary = ParseWindow(limits.Get("primary"), 300);
        var secondary = ParseWindow(limits.Get("secondary"), 10080);
        QuotaWindow? session = null, weekly = null;
        foreach (var window in new[] { primary, secondary })
        {
            if (window is null) continue;
            if (window.Minutes <= 600) session ??= window;
            else weekly ??= window;
        }
        return session is null && weekly is null ? null : new("Codex", session, weekly, observed, source);
    }

    private static QuotaWindow? ParseWindow(JsonElement window, double fallbackMinutes)
    {
        var used = window.Get("used_percent").Percent();
        var reset = window.Get("resets_at").Time() ?? window.Get("reset_at").Time();
        double minutes = window.Get("window_minutes").Number() ?? fallbackMinutes;
        return used is null || minutes <= 0 ? null : new(used.Value, reset, minutes);
    }
    private static QuotaSnapshot? ParseError(JsonElement evt, DateTimeOffset? at, DateTimeOffset now)
    {
        var headers = evt.Get("headers");
        var windows = new List<QuotaWindow?>();
        foreach (var (prefix, minutes) in new[] { ("Primary", 300d), ("Secondary", 10080d) })
        {
            static double? Header(JsonElement h, string name)
            {
                var v = h.Get(name);
                return v.Number() ?? (double.TryParse(v.Text(), System.Globalization.CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null);
            }
            double? used = Header(headers, "X-Codex-" + prefix + "-Used-Percent");
            double? reset = Header(headers, "X-Codex-" + prefix + "-Reset-At");
            double? after = Header(headers, "X-Codex-" + prefix + "-Reset-After-Seconds");
            DateTimeOffset? end = null;
            try { end = reset is { } r ? DateTimeOffset.FromUnixTimeSeconds((long)r) : after is { } a && at is { } t ? t.AddSeconds(a) : null; } catch (ArgumentOutOfRangeException) { }
            windows.Add(used is >= 0 and <= 100 ? new(used.Value, end, minutes) : null);
        }
        return at is null || at > now.AddMinutes(1) || windows.All(w => w is null) ? null : new("Codex", windows[0], windows[1], at, "Codex 本地限额记录");
    }
}

public sealed class ClaudeReader
{
    private readonly string roaming, local, home;
    public ClaudeReader(string? roaming = null, string? local = null, string? home = null)
    {
        this.roaming = roaming ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        this.local = local ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        this.home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
    public QuotaSnapshot Read(DateTimeOffset now)
    {
        var profiles = new List<string> { Path.Combine(roaming, "Claude") };
        try
        {
            string packages = Path.Combine(local, "Packages");
            if (Directory.Exists(packages)) profiles.AddRange(Directory.EnumerateDirectories(packages, "Claude_*").Select(p => Path.Combine(p, "LocalCache", "Roaming", "Claude")));
        }
        catch (Exception ex) when (LocalFiles.IsReadError(ex)) { }
        var observations = new List<QuotaSnapshot>();
        foreach (string profile in profiles)
        {
            try
            {
                using var history = JsonDocument.Parse(LocalFiles.Read(Path.Combine(profile, "plan-usage-history.json"), 4 * 1024 * 1024));
                var root = history.RootElement;
                if (root.Get("version").Number() is not (1 or 2) || root.Get("samples").ValueKind != JsonValueKind.Array) continue;
                JsonElement latest = default;
                DateTimeOffset? observed = null;
                foreach (var sample in root.Get("samples").EnumerateArray())
                {
                    var time = sample.Get("t").Time(true);
                    if (time is not null && (observed is null || time >= observed)) { latest = sample; observed = time; }
                }
                if (observed is null || observed > now.AddMinutes(1) || string.IsNullOrEmpty(latest.Get("org").Text())) continue;
                var u = latest.Get("u");
                double? five = u.Get("fh").Percent(), seven = u.Get("sd").Percent();
                DateTimeOffset? fiveReset = null, sevenReset = null;
                bool fresh = now - observed.Value <= TimeSpan.FromHours(2);
                var cached = ChromiumCache.Find(Path.Combine(profile, "Cache", "Cache_Data"), "https://claude.ai/api/organizations/" + latest.Get("org").Text() + "/usage", observed.Value.AddMinutes(-1), now);
                string source = "Claude Desktop 本地历史";
                if (cached is not null)
                {
                    var f = cached.Data.Get("five_hour"); var w = cached.Data.Get("seven_day");
                    var cf = f.Get("utilization").Percent(); var cw = w.Get("utilization").Percent();
                    if (cached.FetchedAt >= observed && (cf is not null || cw is not null))
                    {
                        five = cf; seven = cw; observed = cached.FetchedAt; fresh = true; source = "Claude Desktop 本地缓存";
                    }
                    if (fresh)
                    {
                        fiveReset = MatchingReset(f, five, observed.Value, now.AddHours(5).AddMinutes(1));
                        sevenReset = MatchingReset(w, seven, observed.Value, now.AddDays(7).AddMinutes(1));
                    }
                }
                if (fresh && (five is not null || seven is not null))
                    observations.Add(new("Claude", five is { } a ? new(a, fiveReset, 300) : null, seven is { } b ? new(b, sevenReset, 10080) : null, observed, source));
            }
            catch (Exception ex) when (LocalFiles.IsReadError(ex)) { }
        }
        // Read-only compatibility with an existing Claude Code quota hook. Never install or alter it.
        string statusFile = Path.Combine(home, ".claude", "usage-status.json");
        try
        {
            using var doc = JsonDocument.Parse(LocalFiles.Read(statusFile, 1024 * 1024));
            var root = doc.RootElement;
            var at = root.Get("_received_at").Time() ?? new DateTimeOffset(File.GetLastWriteTimeUtc(statusFile));
            if (at <= now.AddMinutes(1) && now - at <= TimeSpan.FromHours(2))
            {
                var rate = root.Get("rate_limits");
                var a = HookWindow(rate.Get("five_hour"), 300); var b = HookWindow(rate.Get("seven_day"), 10080);
                if (a is not null || b is not null) observations.Add(new("Claude", a, b, at, "Claude Code 本地额度记录"));
            }
        }
        catch (Exception ex) when (LocalFiles.IsReadError(ex)) { }
        return observations.OrderByDescending(s => s.ObservedAt).FirstOrDefault() ?? QuotaSnapshot.Empty("Claude", "请在 Claude 桌面端打开用量页面，让本地缓存更新。超过两小时的桌面缓存不作为当前额度。");
    }
    private static QuotaWindow? HookWindow(JsonElement value, double minutes) => value.Get("used_percentage").Percent() is { } used ? new(used, value.Get("resets_at").Time(), minutes) : null;
    public static DateTimeOffset? MatchingReset(JsonElement window, double? percent, DateTimeOffset observed, DateTimeOffset maximum)
    {
        var reset = window.Get("resets_at").Time();
        return percent is not null && window.Get("utilization").Percent() == percent && reset > observed && reset <= maximum ? reset : null;
    }
}
