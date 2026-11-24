using System.Buffers;
using System.Text.Json;

namespace UsageLite;

public sealed class ClaudeTokenReader
{
    private const int MaxLine = 16 * 1024 * 1024;
    private readonly string[] directories;
    private readonly Dictionary<string, Transcript> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> projects = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Counts(long Input, long Output, long Write, long Read)
    {
        public long Total => Input + Output + Write + Read;
        public Counts Merge(Counts other) => new(Math.Max(Input, other.Input), Math.Max(Output, other.Output),
            Math.Max(Write, other.Write), Math.Max(Read, other.Read));
    }
    private sealed record Entry(DateTimeOffset Time, string Directory, Counts Tokens);
    private sealed class Transcript
    {
        public DateTime Created, Modified;
        public long Length = -1, Offset;
        public bool Partial;
        public readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    }

    public ClaudeTokenReader(IEnumerable<string>? projectDirectories = null)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        directories = (projectDirectories ?? (Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Path.Combine(p == "~" ? home : p.StartsWith("~/") || p.StartsWith("~\\") ? Path.Combine(home, p[2..]) : p, "projects"))
            .Concat([Path.Combine(home, ".claude", "projects"), Path.Combine(home, ".config", "claude", "projects")]))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public TokenUsage Read(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).Date);
        bool partial = false;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in directories)
        {
            try
            {
                if (!System.IO.Directory.Exists(directory)) continue;
                foreach (string path in System.IO.Directory.EnumerateFiles(directory, "*.jsonl", new EnumerationOptions
                {
                    RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint
                }))
                {
                    if (!files.Add(path)) continue;
                    try { Update(path); }
                    catch (Exception ex) when (LocalFiles.IsReadError(ex)) { partial = true; }
                }
            }
            catch (Exception ex) when (LocalFiles.IsReadError(ex)) { partial = true; }
        }
        if (!partial)
            foreach (string path in cache.Keys.Where(p => !files.Contains(p)).ToArray()) cache.Remove(path);

        // The same assistant response appears in streamed blocks, subagent logs,
        // and copied transcripts. Keep the final counters once across all files.
        var unique = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (string path in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!cache.TryGetValue(path, out var transcript)) continue;
            partial |= transcript.Partial;
            foreach (var (id, entry) in transcript.Entries)
            {
                if (entry.Time > now) continue;
                if (unique.TryGetValue(id, out var old)) unique[id] = Merge(old, entry);
                else unique.Add(id, entry);
            }
        }
        var days = new Dictionary<DateOnly, Dictionary<string, long>>();
        foreach (var entry in unique.Values)
        {
            DateOnly day = Day(entry.Time);
            if (!days.TryGetValue(day, out var values)) days.Add(day, values = new(StringComparer.Ordinal));
            values[entry.Directory] = values.GetValueOrDefault(entry.Directory) + entry.Tokens.Total;
        }
        return new(Day(now), days, partial);
    }

    private static Entry Merge(Entry old, Entry next) => old with
    {
        Tokens = old.Tokens.Merge(next.Tokens),
        Directory = old.Directory.Length > 0 ? old.Directory : next.Directory
    };

    private void Update(string path)
    {
        var file = new FileInfo(path);
        long length = file.Length;
        if (!cache.TryGetValue(path, out var state) || state.Created != file.CreationTimeUtc || state.Length > length ||
            (state.Length == length && state.Modified != file.LastWriteTimeUtc && state.Offset == length))
            state = new Transcript { Created = file.CreationTimeUtc };
        if (state.Length == length && state.Modified == file.LastWriteTimeUtc) return;
        using var stream = LocalFiles.Open(path);
        stream.Position = state.Offset;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
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
                    if (end >= 0 && pending.WrittenCount == 0 && !tooLong) Consume(part, state, path);
                    else
                    {
                        tooLong |= pending.WrittenCount + part.Length > MaxLine;
                        if (!tooLong) pending.Write(part);
                        if (end >= 0 && !tooLong) Consume(pending.WrittenSpan, state, path);
                    }
                    if (end < 0) break;
                    state.Partial |= tooLong;
                    index += end + 1;
                    state.Offset = start + index;
                    pending.Clear();
                    tooLong = false;
                }
            }
            state.Length = length;
            state.Modified = file.LastWriteTimeUtc;
            cache[path] = state;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private void Consume(ReadOnlySpan<byte> line, Transcript state, string path)
    {
        if (line.Trim((byte)'\r').IsEmpty) return;
        try
        {
            var reader = new Utf8JsonReader(line.StartsWith("\uFEFF"u8) ? line[3..] : line);
            using var doc = JsonDocument.ParseValue(ref reader);
            var row = doc.RootElement;
            if (row.Get("type").Text() != "assistant" || row.Get("timestamp").Time() is not { } at) return;
            var message = row.Get("message");
            var usage = message.Get("usage");
            if (usage.ValueKind != JsonValueKind.Object) return;
            string directory = Project(row.Get("cwd").Text());
            string? messageId = message.Get("id").Text(), requestId = row.Get("requestId").Text();
            string key = !string.IsNullOrEmpty(messageId) || !string.IsNullOrEmpty(requestId)
                ? $"response:{messageId}:{requestId}"
                : row.Get("uuid").Text() is { Length: > 0 } uuid ? "uuid:" + uuid : $"line:{path}:{state.Offset}";
            Add(key, usage);
            if (usage.Get("iterations") is { ValueKind: JsonValueKind.Array } iterations)
            {
                int index = 0;
                foreach (var iteration in iterations.EnumerateArray())
                {
                    if (iteration.Get("type").Text() == "advisor_message") Add(key + "#advisor" + index, iteration);
                    index++;
                }
            }
            void Add(string id, JsonElement counters)
            {
                if (ParseCounts(counters) is not { Total: > 0 } count) return;
                var entry = new Entry(at, directory, count);
                state.Entries[id] = state.Entries.TryGetValue(id, out var old) ? Merge(old, entry) : entry;
            }
        }
        catch (JsonException) { state.Partial = true; }
    }

    private static Counts? ParseCounts(JsonElement usage)
    {
        long? Count(string key)
        {
            var value = usage.Get(key);
            if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return 0;
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long n) && n is >= 0 and <= 1_000_000_000_000 ? n : null;
        }
        return (Count("input_tokens"), Count("output_tokens"), Count("cache_creation_input_tokens"), Count("cache_read_input_tokens"))
            is ({ } input, { } output, { } write, { } read) ? new(input, output, write, read) : null;
    }

    private string Project(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return "";
        if (projects.TryGetValue(cwd, out var known)) return known;
        string path = cwd;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        // Only walk local absolute Windows directories. Never turn a Linux or
        // remote path from a transcript into an unrelated local project.
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
        {
            try
            {
                var current = new DirectoryInfo(path);
                while (current is not null && current.Exists)
                {
                    string git = Path.Combine(current.FullName, ".git");
                    if (System.IO.Directory.Exists(git) || File.Exists(git)) { path = current.FullName; break; }
                    current = current.Parent;
                }
            }
            catch (Exception ex) when (LocalFiles.IsReadError(ex)) { }
        }
        path = path.Replace('\\', '/').TrimEnd('/');
        if (path.Length == 0) path = "/";
        if (path.Length >= 2 && path[1] == ':') path = char.ToUpperInvariant(path[0]) + path[1..];
        projects[cwd] = path;
        return path;
    }
}
