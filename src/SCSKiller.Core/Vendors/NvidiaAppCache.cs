namespace SCSKiller.Core.Vendors;

/// <summary>NVIDIA's DXCache is split per application: files are named <c>TTTTa91dKKKKKKKK.nvph</c>, where KKKKKKKK is the
/// application key, a 32-bit hash of the exe file name (case-insensitive, path-independent; not a plain
/// CRC/FNV/murmur/xxHash of it). TTTT is the file's type, and the types change with the driver (610.88: fc52 shader cache,
/// 0002 D3D12 pipelines, c54e ray tracing state objects; 617.14: 32e6, 0002, bc2f), so files match by key alone, whatever
/// their type. D3D12 and D3D11 share the key. A second process of the same name running at the same time gets key + 1. The
/// driver keeps the files open while the device lives, which is how a key is attributed to a game: a process named like
/// the game (its staged warm copy, or the game itself) has them open.</summary>
public sealed class NvidiaAppCache(string dir) : IAppCache
{
    public string Dir { get; } = dir;

    /// <summary>"0002a91d69d04596.nvph" -> "69d04596"; null for anything else.</summary>
    public static string? Key(string fileName) =>
        fileName.Length == 21 && fileName.EndsWith(".nvph", StringComparison.OrdinalIgnoreCase)
        && ulong.TryParse(fileName.AsSpan(0, 16), System.Globalization.NumberStyles.HexNumber, null, out _)
            ? fileName.Substring(8, 8).ToLowerInvariant() : null;

    public IReadOnlyList<FileInfo> FilesOf(IEnumerable<string> keys)
    {
        var set = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return set.Count == 0 || !Directory.Exists(Dir) ? []
            : new DirectoryInfo(Dir).EnumerateFiles("*.nvph").Where(f => Key(f.Name) is { } k && set.Contains(k)).ToList();
    }

    public long SizeOf(IEnumerable<string> keys) => FilesOf(keys).Sum(f => f.Length);

    /// <summary>The bytes the driver has filled in these keys' files (<see cref="UsedBytes"/>; a file that can't be read
    /// counts whole), at most <see cref="SizeOf"/>.</summary>
    public long UsedOf(IEnumerable<string> keys) => FilesOf(keys).Sum(f => UsedBytes(f) ?? f.Length);

    public IReadOnlySet<string> KeysOpenBy(string exeFileName) => !Directory.Exists(Dir) ? new HashSet<string>()
        : AppCacheFiles.KeysOpenBy(exeFileName, Directory.EnumerateFiles(Dir, "*.nvph")
            .Select(f => (Path: f, Key: Key(Path.GetFileName(f))!)).Where(f => f.Key != null));

    public int Delete(IEnumerable<string> keys) => AppCacheFiles.DeleteAll(FilesOf(keys));

    /// <summary>Measured: a game whose file of 4 GiB was 94-97% full lost hits on its own entries, and the file didn't grow.</summary>
    public const long FullFileBytes = 4L << 30;
    public const double FullShare = 0.9;

    /// <summary>A file of these keys of <see cref="FullFileBytes"/> or more whose data reaches <see cref="FullShare"/> of it.</summary>
    public bool NearlyFull(IEnumerable<string> keys) => FilesOf(keys).Any(f => f.Length >= FullFileBytes && Fill(f) >= FullShare);

    /// <summary>The share of the file the driver has filled (<see cref="UsedBytes"/>); 0 when it can't be read.</summary>
    internal static double Fill(FileInfo f, int block = 1 << 20) =>
        f.Length > 0 && UsedBytes(f, block) is { } used ? used / (double)f.Length : 0;

    static readonly Dictionary<string, (long Length, DateTime Written, DateTime At, long Used)> dataEnds = [];

    /// <summary>How much of the file the driver has filled. It pre-sizes files in powers of two and writes from the front:
    /// the u64 at offset 8 after the "nvph" magic, or where the zero tail starts when that isn't plausible (some types keep
    /// other data there). Null when the file can't be read. Read only, shared with the driver and a running game.</summary>
    public static long? UsedBytes(FileInfo f, int block = 1 << 20)
    {
        try
        {
            using var h = File.OpenHandle(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = RandomAccess.GetLength(h);
            Span<byte> header = stackalloc byte[16];
            var used = RandomAccess.Read(h, header, 0) == header.Length && header[..4].SequenceEqual("nvph"u8)
                ? System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(header[8..]) : -1;
            if (used >= header.Length && used <= length) return used;
            // the search reads one block per halving: cached for a minute while the file looks unchanged (a pre-sized
            // file keeps its length, and its write time may lag while the driver holds it open)
            lock (dataEnds)
                if (dataEnds.TryGetValue(f.FullName, out var c) && c.Length == f.Length && c.Written == f.LastWriteTimeUtc
                    && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(1)) return c.Used;
            var end = DataEnd(h, length, block);
            lock (dataEnds) dataEnds[f.FullName] = (f.Length, f.LastWriteTimeUtc, DateTime.UtcNow, end);
            return end;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Where the zero tail starts, to the block, by a binary search.</summary>
    static long DataEnd(Microsoft.Win32.SafeHandles.SafeFileHandle h, long length, int block)
    {
        long lo = 0, hi = (length + block - 1) / block;
        var buf = new byte[block];
        while (lo < hi)
        {
            long mid = (lo + hi) / 2;
            int n = RandomAccess.Read(h, buf, mid * block);
            if (buf.AsSpan(0, n).ContainsAnyExcept((byte)0)) lo = mid + 1; else hi = mid;
        }
        return Math.Min(lo * block, length);
    }
}
