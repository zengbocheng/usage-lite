using System.Globalization;
using System.Text;
using System.Text.Json;

namespace UsageLite;

public sealed record QuotaWindow(double Used, DateTimeOffset? ResetAt, double Minutes)
{
    public bool Expired(DateTimeOffset now) => ResetAt is { } end && end <= now;
    public double Remaining => Math.Clamp(100 - Used, 0, 100);
    public string Title => Minutes <= 600 ? "5 小时" : Minutes <= 11520 ? "每周" : Minutes <= 46080 ? "每月" : "额度";
}

public sealed record QuotaSnapshot(string Provider, QuotaWindow? Session, QuotaWindow? Weekly,
    DateTimeOffset? ObservedAt, string Source, string Note = "")
{
    public static QuotaSnapshot Empty(string provider, string note) => new(provider, null, null, null, "尚无本地记录", note);
    public bool Stale(DateTimeOffset now) => ObservedAt is { } at && now - at > TimeSpan.FromHours(2);
    public QuotaWindow? Preferred(bool weekly, DateTimeOffset now)
    {
        var first = weekly ? Weekly : Session;
        var second = weekly ? Session : Weekly;
        return first is not null && !first.Expired(now) ? first : second is not null && !second.Expired(now) ? second : null;
    }
}

public sealed record DashboardData(QuotaSnapshot Codex, QuotaSnapshot Claude, DateTimeOffset ReadAt);

public static class JsonValue
{
    public static JsonElement Get(this JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var child) ? child : default;
    public static string? Text(this JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    public static double? Number(this JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    public static double? Percent(this JsonElement value) => value.Number() is { } n && n >= 0 && n <= 100 ? n : null;
    public static DateTimeOffset? Time(this JsonElement value, bool milliseconds = false)
    {
        try
        {
            if (value.Number() is { } number && number >= 0 && number < 253402300799000)
                return milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds((long)number) : DateTimeOffset.FromUnixTimeSeconds((long)number);
            if (value.Text() is { } s && (s.EndsWith('Z') || System.Text.RegularExpressions.Regex.IsMatch(s, @"[+-]\d\d:\d\d$")) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) return at;
        }
        catch (ArgumentOutOfRangeException) { }
        return null;
    }
}

public static class LocalFiles
{
    public static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    public static byte[] Read(string path, int maximum)
    {
        using var stream = Open(path);
        if (stream.Length > maximum) throw new InvalidDataException("文件超过读取上限");
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = stream.Read(buffer, 0, Math.Min(buffer.Length, maximum + 1 - (int)result.Length))) > 0)
        {
            result.Write(buffer, 0, count);
            if (result.Length > maximum) throw new InvalidDataException("文件超过读取上限");
        }
        return result.ToArray();
    }
    public static byte[] Range(string path, long offset, int length)
    {
        if (offset < 0 || length < 0 || length > 16 * 1024 * 1024) throw new InvalidDataException();
        using var stream = Open(path);
        stream.Position = offset;
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        return bytes;
    }
    public static IEnumerable<string> TailLines(string path, int maximum = 8 * 1024 * 1024)
    {
        using var stream = Open(path);
        long start = Math.Max(0, stream.Length - maximum);
        stream.Position = start;
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 65536);
        if (start > 0) reader.ReadLine();
        string? line;
        while ((line = reader.ReadLine()) is not null)
            if (line.Length <= 2 * 1024 * 1024) yield return line;
    }
    public static bool IsReadError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException;
}

public sealed class AppSettings
{
    public string TrayProvider { get; set; } = "Codex";
    public bool Weekly { get; set; }
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageLite");
    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(LocalFiles.Read(Path.Combine(DataDirectory, "settings.json"), 65536)) ?? new(); }
        catch (Exception ex) when (LocalFiles.IsReadError(ex)) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        string file = Path.Combine(DataDirectory, "settings.json");
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(this)); File.Move(temporary, file, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public static class AppLog
{
    private static readonly object Gate = new();
    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.DataDirectory);
                string path = Path.Combine(AppSettings.DataDirectory, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Move(path, path + ".1", true);
                File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
