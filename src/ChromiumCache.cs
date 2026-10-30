// Chromium block-file cache parsing is adapted from aqua5230/usage (AGPL-3.0-only).
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ZstdSharp;

namespace UsageLite;

public sealed record CachedQuota(JsonElement Data, DateTimeOffset FetchedAt);

public sealed class ChromiumCache
{
    private const int Header = 8192, IndexHeader = 368, MaxBody = 1024 * 1024;
    private readonly string path;
    private readonly byte[] index;
    private readonly int tableLength;
    private static readonly int[] BlockSizes = [0, 36, 256, 1024, 4096, 8, 256, 256];
    private static int I32(byte[] b, int offset) => offset >= 0 && offset <= b.Length - 4 ? BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(offset, 4)) : throw new InvalidDataException();
    private static uint U32(byte[] b, int offset) => unchecked((uint)I32(b, offset));
    private static ushort U16(byte[] b, int offset) => offset >= 0 && offset <= b.Length - 2 ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(offset, 2)) : throw new InvalidDataException();
    private ChromiumCache(string path)
    {
        this.path = path;
        index = LocalFiles.Read(Path.Combine(path, "index"), 4 * 1024 * 1024);
        uint version = U32(index, 4);
        if (U32(index, 0) != 0xC103CAC3 || version is not (0x20000 or 0x20001 or 0x30000)) throw new InvalidDataException();
        tableLength = I32(index, 28);
        if (tableLength == 0) tableLength = 65536;
        if (tableLength <= 0 || tableLength > 1024 * 1024 || (tableLength & (tableLength - 1)) != 0 || index.Length != IndexHeader + 4 * tableLength) throw new InvalidDataException();
    }
    public static CachedQuota? Find(string path, string url, DateTimeOffset earliest, DateTimeOffset now)
    {
        try { return new ChromiumCache(path).Responses(url, earliest, now).OrderByDescending(r => r.FetchedAt).FirstOrDefault(); }
        catch (Exception ex) when (LocalFiles.IsReadError(ex) || ex is OverflowException) { return null; }
    }
    private byte[] Stream(uint address, int length)
    {
        if ((address & 0x80000000) == 0 || length <= 0 || length > MaxBody) throw new InvalidDataException();
        int kind = (int)((address >> 28) & 7);
        if (kind == 0) return LocalFiles.Range(Path.Combine(path, "f_" + (address & 0x0FFFFFFF).ToString("x6")), 0, length);
        int size = BlockSizes[kind];
        if (length > size * (((address >> 24) & 3) + 1)) throw new InvalidDataException();
        return LocalFiles.Range(Path.Combine(path, "data_" + ((address >> 16) & 255)), Header + (address & 65535) * (long)size, length);
    }
    private bool Active(byte[] entry, int file, int block)
    {
        int bucket = (int)(U32(entry, 0) & (tableLength - 1));
        uint address = U32(index, IndexHeader + bucket * 4);
        var visited = new HashSet<uint>();
        for (int i = 0; i < 128; i++)
        {
            int kind = (int)((address >> 28) & 7);
            if (address == 0 || !visited.Add(address) || kind is not (2 or 6)) return false;
            if (((address >> 16) & 255) == file && (address & 65535) == block) return true;
            address = U32(Stream(address, 256), 4);
        }
        return false;
    }
    private List<CachedQuota> Responses(string url, DateTimeOffset earliest, DateTimeOffset now)
    {
        var files = new HashSet<int>();
        for (int offset = IndexHeader; offset < index.Length; offset += 4)
        {
            uint address = U32(index, offset);
            if (((address >> 28) & 7) is 2 or 6) files.Add((int)((address >> 16) & 255));
        }
        var pending = new Queue<(int File, int Depth)>(files.Select(f => (f, 0)));
        var visited = new HashSet<int>();
        var results = new List<CachedQuota>();
        while (pending.Count > 0 && visited.Count < 32)
        {
            var (file, depth) = pending.Dequeue();
            if (!visited.Add(file)) continue;
            byte[] raw;
            try { raw = LocalFiles.Read(Path.Combine(path, "data_" + file), 16 * 1024 * 1024); }
            catch (Exception ex) when (LocalFiles.IsReadError(ex)) { continue; }
            if (raw.Length < Header || U32(raw, 0) != 0xC104CAC3 || U32(raw, 4) != 0x20000 || U16(raw, 8) != file || I32(raw, 12) != 256) continue;
            ushort next = U16(raw, 10);
            if (next != 0 && depth < 15) pending.Enqueue((next, depth + 1));
            for (int offset = Header; offset <= raw.Length - 256; offset += 256)
            {
                try
                {
                    var result = Response(raw.AsSpan(offset, 256).ToArray(), raw, offset, file, url, earliest, now);
                    if (result is not null) results.Add(result);
                }
                catch (Exception ex) when (LocalFiles.IsReadError(ex) || ex is OverflowException) { }
            }
        }
        return results;
    }
    private CachedQuota? Response(byte[] entry, byte[] raw, int offset, int file, string url, DateTimeOffset earliest, DateTimeOffset now)
    {
        if (I32(entry, 20) != 0) return null;
        int keyLength = I32(entry, 32); uint longKey = U32(entry, 36);
        if (keyLength <= 0 || keyLength > 4096) return null;
        if (longKey == 0 && offset + 96 + keyLength > raw.Length) return null;
        var keyBytes = longKey != 0 ? Stream(longKey, keyLength) : raw.AsSpan(offset + 96, keyLength).ToArray();
        string key = Encoding.UTF8.GetString(keyBytes);
        int start = key.LastIndexOf("https://", StringComparison.Ordinal);
        if (start < 0 || key[start..].Split('?')[0] != url || !Active(entry, file, (offset - Header) / 256)) return null;
        if (I32(Stream(U32(entry, 8), 36), 28) != 0) return null;
        var metadata = Stream(U32(entry, 56), I32(entry, 40));
        var (fetched, headers) = ResponseInfo(metadata);
        var lower = earliest > now.AddHours(-2) ? earliest : now.AddHours(-2);
        if (fetched < lower || fetched > now.AddMinutes(1)) return null;
        var body = Stream(U32(entry, 60), I32(entry, 44));
        if (!LocalFiles.Range(Path.Combine(path, "data_" + file), offset, 96).AsSpan().SequenceEqual(entry.AsSpan(0, 96))) return null;
        if (!Stream(U32(entry, 56), I32(entry, 40)).AsSpan().SequenceEqual(metadata)) return null;
        using var doc = JsonDocument.Parse(Decode(body, headers));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        return new(doc.RootElement.Clone(), fetched);
    }
    private static (DateTimeOffset, string) ResponseInfo(byte[] raw)
    {
        if (raw.Length < 28 || U32(raw, 0) != raw.Length - 4) throw new InvalidDataException();
        uint flags = U32(raw, 4);
        if ((flags & 255) != 3 || (flags & (1 << 12)) != 0) throw new InvalidDataException();
        uint extra = (flags & 0x80000000) != 0 ? U32(raw, 8) : 0;
        int offset = (flags & 0x80000000) != 0 ? 12 : 8;
        long microseconds = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(offset + 8, 8));
        var fetched = DateTimeOffset.FromUnixTimeMilliseconds(microseconds / 1000 - 11644473600000);
        offset += 16 + ((extra & 4) != 0 ? 8 : 0);
        int length = I32(raw, offset);
        if (length < 0 || length > raw.Length - offset - 4) throw new InvalidDataException();
        string headers = Encoding.Latin1.GetString(raw, offset + 4, length).ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(headers, @"^http/\S+ 200(?: |\x00)") || !headers.Contains("application/json")) throw new InvalidDataException();
        return (fetched, headers);
    }
    public static byte[] Decode(byte[] body, string headers)
    {
        if (body.Length > MaxBody) throw new InvalidDataException();
        if (body.Length >= 4 && U32(body, 0) == 0xFD2FB528)
        {
            try
            {
                using var decoder = new Decompressor();
                decoder.SetParameter(ZstdSharp.Unsafe.ZSTD_dParameter.ZSTD_d_windowLogMax, 23);
                return decoder.Unwrap(body, MaxBody).ToArray();
            }
            catch (Exception ex) when (ex is ZstdException or ArgumentException) { throw new InvalidDataException("不支持的压缩缓存", ex); }
        }
        if (body.Length >= 2 && body[0] == 0x1f && body[1] == 0x8b || headers.Contains("content-encoding: deflate", StringComparison.OrdinalIgnoreCase))
        {
            using var input = new MemoryStream(body);
            using System.IO.Stream decoder = body.Length >= 2 && body[0] == 0x1f && body[1] == 0x8b ? new GZipStream(input, CompressionMode.Decompress) : new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = decoder.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + count > MaxBody) throw new InvalidDataException();
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        return body;
    }
}
