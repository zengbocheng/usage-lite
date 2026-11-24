using System.Runtime.InteropServices;
using System.Text.Json;
using UsageLite;

if (!OperatingSystem.IsMacOS()) return 1;
NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.SQLite3Provider_sqlite3).Assembly,
    (name, _, _) => name == "sqlite3" ? NativeLibrary.Load("/usr/lib/libsqlite3.dylib") : IntPtr.Zero);
SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());

string? fixture = args is ["--fixture-root", var root] ? root : null;
if (args.Length > 0 && fixture is null) return 2;
var codex = new CodexReader(fixture is null ? null : Path.Combine(fixture, ".codex"));
var claude = new ClaudeReader(local: fixture, home: fixture);
var now = DateTimeOffset.UtcNow;
Console.WriteLine(JsonSerializer.Serialize(new { codex = Snapshot(codex.Read(now)), claude = Snapshot(claude.Read(now)) }));
return 0;

static object Snapshot(QuotaSnapshot snapshot) => new
{
    session = Window(snapshot.Session),
    weekly = Window(snapshot.Weekly),
    observedAt = snapshot.ObservedAt?.ToUnixTimeMilliseconds(),
    note = snapshot.Note
};

static object? Window(QuotaWindow? window) => window is null ? null : new
{
    remaining = window.Remaining,
    resetAt = window.ResetAt?.ToUnixTimeMilliseconds(),
    title = window.Title
};
