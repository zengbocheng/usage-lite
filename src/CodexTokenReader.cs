using System.Buffers;
using System.Text.Json;

namespace UsageLite;

public sealed record TokenProject(string Directory, long Tokens)
{
    public string Name => Directory switch
    {
        "" => "未归类",
        "/" => "/",
        _ => Directory[(Directory.LastIndexOf('/') + 1)..]
    };
}

public sealed class TokenUsage
{
    private readonly Dictionary<DateOnly, Dictionary<string, long>> days;
    public DateOnly Today { get; }
    public bool Partial { get; }
    public DateOnly FirstDay => days.Count == 0 ? Today : days.Keys.Min();

    internal TokenUsage(DateOnly today, Dictionary<DateOnly, Dictionary<string, long>> days, bool partial = false)
    {
        Today = today;
        this.days = days;
        Partial = partial;
    }

    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-((int)day.DayOfWeek + 6) % 7);
    public long Total(DateOnly day) => days.TryGetValue(day, out var projects) ? projects.Values.Sum() : 0;

    public IReadOnlyList<TokenProject> Projects(DateOnly start, int length)
    {
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < length; i++)
        {
            if (!days.TryGetValue(start.AddDays(i), out var projects)) continue;
            foreach (var (directory, count) in projects)
                totals[directory] = totals.GetValueOrDefault(directory) + count;
        }
        return totals.Select(p => new TokenProject(p.Key, p.Value))
            .OrderByDescending(p => p.Tokens).ThenBy(p => p.Name).ToArray();
    }
}

public sealed class CodexTokenReader
{
    private const int MaxLine = 2 * 1024 * 1024;
    private readonly string home;
    private readonly Dictionary<string, Rollout> cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(DateTimeOffset Time, string Directory, long Count, string? ResponseId);
    private sealed class Rollout
    {
        public DateTime Created;
        public DateTime Modified;
        public long Length = -1;
        public long Offset;
        public string? Id;
        public DateTimeOffset? Started;
        public string Directory = "";
        public long? PreviousTotal;
        public bool HasResponseRecords;
        public readonly List<Entry> Entries = [];
    }

    public CodexTokenReader(string? root = null) => home = root ?? CodexReader.DefaultHome;

    public TokenUsage Read(DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, timeZone).Date);
        bool partial = false;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in new[] { "sessions", "archived_sessions" })
        {
            try
            {
                var directory = Path.Combine(home, folder);
                if (!Directory.Exists(directory)) continue;
                foreach (var path in System.IO.Directory.EnumerateFiles(directory, "*.jsonl", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }))
                {
                    files.Add(path);
                    try { Update(path); }
                    catch (Exception ex) when (LocalFiles.IsReadError(ex)) { partial = true; }
                }
            }
            catch (Exception ex) when (LocalFiles.IsReadError(ex)) { partial = true; }
        }
        if (!partial)
            foreach (string path in cache.Keys.Where(path => !files.Contains(path)).ToArray()) cache.Remove(path);

        // A rollout can be copied to the archive. Count only its most complete copy.
        var rollouts = files.Where(cache.ContainsKey)
            .GroupBy(path => cache[path].Id ?? Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Select(path => cache[path]).MaxBy(rollout => rollout.Offset)!);
        var days = new Dictionary<DateOnly, Dictionary<string, long>>();
        var responses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in rollouts.SelectMany(rollout => rollout.Entries).OrderBy(entry => entry.Time))
        {
            if (entry.Time > now || (entry.ResponseId is { } id && !responses.Add(id))) continue;
            var day = Day(entry.Time);
            if (!days.TryGetValue(day, out var projects))
                days.Add(day, projects = new(StringComparer.Ordinal));
            projects[entry.Directory] = projects.GetValueOrDefault(entry.Directory) + entry.Count;
        }
        return new(Day(now), days, partial);
    }

    private void Update(string path)
    {
        var file = new FileInfo(path);
        long length = file.Length;
        DateTime modified = file.LastWriteTimeUtc;
        DateTime created = file.CreationTimeUtc;
        if (!cache.TryGetValue(path, out var state) || state.Created != created || state.Length > length ||
            (state.Length == length && state.Modified != modified && state.Offset == length))
            state = new Rollout { Created = created };
        if (state.Length == length && state.Modified == modified) return;

        using var stream = LocalFiles.Open(path);
        stream.Position = state.Offset;
        var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            var pending = new ArrayBufferWriter<byte>();
            bool tooLong = false;
            while (stream.Position < length)
            {
                long start = stream.Position;
                int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length - start));
                if (count == 0) break;
                int index = 0;
                while (index < count)
                {
                    var rest = buffer.AsSpan(index, count - index);
                    int end = rest.IndexOf((byte)'\n');
                    var part = end < 0 ? rest : rest[..end];
                    if (end >= 0 && pending.WrittenCount == 0 && !tooLong)
                    {
                        Consume(part, state);
                    }
                    else
                    {
                        tooLong |= pending.WrittenCount + part.Length > MaxLine;
                        if (!tooLong) pending.Write(part);
                        if (end >= 0 && !tooLong) Consume(pending.WrittenSpan, state);
                    }
                    if (end < 0) break;
                    index += end + 1;
                    state.Offset = start + index;
                    pending.Clear();
                    tooLong = false;
                }
            }
            // Keep the last incomplete line for the next refresh.
            state.Length = length;
            state.Modified = modified;
            cache[path] = state;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void Consume(ReadOnlySpan<byte> line, Rollout state)
    {
        var prefix = line[..Math.Min(line.Length, 256)];
        if (prefix.IndexOf("\"session_meta\""u8) < 0 && prefix.IndexOf("\"turn_context\""u8) < 0 &&
            prefix.IndexOf("\"token_usage_record\""u8) < 0 && prefix.IndexOf("\"token_count\""u8) < 0) return;
        try
        {
            var reader = new Utf8JsonReader(line.StartsWith("\uFEFF"u8) ? line[3..] : line);
            using var doc = JsonDocument.ParseValue(ref reader);
            var row = doc.RootElement;
            var payload = row.Get("payload");
            switch (row.Get("type").Text())
            {
                case "session_meta":
                    state.Id ??= payload.Get("id").Text() ?? payload.Get("session_id").Text();
                    state.Started ??= payload.Get("timestamp").Time();
                    SetDirectory(state, payload);
                    break;
                case "turn_context":
                    SetDirectory(state, payload);
                    break;
                case "token_usage_record":
                    if (row.Get("timestamp").Time() is not { } at || at < state.Started ||
                        payload.Get("response_id").Text() is not { Length: > 0 } id ||
                        (state.Id is { } thread && payload.Get("thread_id").Text() is { } owner && owner != thread) ||
                        Count(payload.Get("usage")) is not { } tokens) break;
                    state.HasResponseRecords = true;
                    if (tokens > 0) state.Entries.Add(new(at, state.Directory, tokens, id));
                    break;
                case "event_msg":
                    if (!state.HasResponseRecords && payload.Get("type").Text() == "token_count" &&
                        row.Get("timestamp").Time() is { } eventAt)
                        ConsumeLegacy(state, payload.Get("info"), eventAt);
                    break;
            }
        }
        catch (JsonException) { }
    }

    private static void ConsumeLegacy(Rollout state, JsonElement info, DateTimeOffset at)
    {
        if (Count(info.Get("total_token_usage")) is not { } total) return;
        long? previous = state.PreviousTotal;
        state.PreviousTotal = total;
        if (at < state.Started || total == previous) return;

        // Compaction can report a context estimate in last_token_usage. Require
        // actual input or output counters before attributing a fresh increment.
        var last = info.Get("last_token_usage");
        if (Count(last) is not { } reported || reported == 0 ||
            (ReadCount(last.Get("input_tokens")) ?? 0) + (ReadCount(last.Get("output_tokens")) ?? 0) == 0)
            return;
        long increment = previous switch
        {
            null => Math.Min(total, reported),
            { } baseline when total >= baseline => total - baseline,
            _ when total == reported => reported,
            _ => 0
        };
        if (increment > 0) state.Entries.Add(new(at, state.Directory, increment, null));
    }

    private static long? Count(JsonElement usage) => ReadCount(usage.Get("total_tokens"));
    private static long? ReadCount(JsonElement field) =>
        field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out long n) && n is >= 0 and <= 1_000_000_000_000 ? n : null;

    private static void SetDirectory(Rollout state, JsonElement payload)
    {
        if (payload.Get("cwd").Text() is not { Length: > 0 } cwd) return;
        if (cwd.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) cwd = @"\\" + cwd[8..];
        else if (cwd.StartsWith(@"\\?\", StringComparison.Ordinal)) cwd = cwd[4..];
        cwd = cwd.TrimEnd('/', '\\');
        if (cwd.Length == 0) cwd = "/";
        if (cwd.Contains('\\') || (cwd.Length >= 2 && cwd[1] == ':')) cwd = cwd.Replace('\\', '/');
        state.Directory = cwd;
    }
}
