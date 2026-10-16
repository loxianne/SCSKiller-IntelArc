using System.Runtime.InteropServices;
using System.Text;

namespace SCSKiller.Core.Vendors;

/// <summary>Intel's Direct3D 12 shader caches on Windows are stored within the system Direct3D shader cache
/// (<c>%LOCALAPPDATA%\D3DSCache</c>), plus any driver-specific cache under <c>%LOCALAPPDATA%\Intel\ShaderCache</c>.
/// In <c>D3DSCache</c>, each application has its own folder whose SQLite cache databases are tagged with
/// Intel's vendor ID: <c>&lt;GUID&gt;_VEN_8086&amp;DEV_...dxcache</c>.
/// Like NVIDIA and AMD, a key is attributed to a game by the files a process named like it holds open
/// (<see cref="KeysOpenBy"/>).</summary>
public sealed class IntelAppCache(string d3dsDir, string intelDir) : IAppCache
{
    public const string D3dsPrefix = "d3ds:";
    public const string IntelPrefix = "intel:";

    public string D3dsDir { get; } = d3dsDir;
    public string IntelDir { get; } = intelDir;

    public static string NormalizeKey(string key) =>
        key.StartsWith(D3dsPrefix, StringComparison.OrdinalIgnoreCase) ? key[D3dsPrefix.Length..]
        : key.StartsWith(IntelPrefix, StringComparison.OrdinalIgnoreCase) ? key[IntelPrefix.Length..]
        : key;

    public IReadOnlyList<FileInfo> FilesOf(IEnumerable<string> keys)
    {
        var files = new List<FileInfo>();
        var keySet = keys.Select(NormalizeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (keySet.Count == 0) return files;

        if (Directory.Exists(D3dsDir))
        {
            foreach (var subDir in Directory.EnumerateDirectories(D3dsDir))
            {
                var dirName = Path.GetFileName(subDir);
                if (!keySet.Contains(dirName)) continue;

                var d = new DirectoryInfo(subDir);
                foreach (var f in d.EnumerateFiles())
                {
                    if (f.Name.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase)
                        || f.Extension.Equals(".dxcache", StringComparison.OrdinalIgnoreCase)
                        || f.Name.EndsWith(".dxcache-wal", StringComparison.OrdinalIgnoreCase)
                        || f.Name.EndsWith(".dxcache-shm", StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(f);
                    }
                }
            }
        }

        if (Directory.Exists(IntelDir))
        {
            foreach (var f in new DirectoryInfo(IntelDir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (keySet.Contains(f.Name) || keySet.Contains(Path.GetFileNameWithoutExtension(f.Name)))
                    files.Add(f);
            }
        }

        return files;
    }

    public long SizeOf(IEnumerable<string> keys) => FilesOf(keys).Sum(f => f.Length);

    /// <summary>Returns the size in bytes of the largest shader cache container file in the IntelShaderCache directory.</summary>
    public long LargestContainerBytes()
    {
        if (!Directory.Exists(IntelDir)) return 0;
        try
        {
            return new DirectoryInfo(IntelDir).EnumerateFiles("*", SearchOption.AllDirectories)
                .Select(f => f.Length)
                .DefaultIfEmpty(0)
                .Max();
        }
        catch { return 0; }
    }

    public IReadOnlySet<string> KeysOpenBy(string exeFileName)
    {
        var candidates = new List<(string Path, string Key)>();

        if (Directory.Exists(D3dsDir))
        {
            foreach (var subDir in Directory.EnumerateDirectories(D3dsDir))
            {
                var dirName = Path.GetFileName(subDir);
                foreach (var file in Directory.EnumerateFiles(subDir))
                {
                    var fileName = Path.GetFileName(file);
                    if (fileName.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase)
                        || fileName.EndsWith(".dxcache", StringComparison.OrdinalIgnoreCase)
                        || fileName.EndsWith(".dxcache-wal", StringComparison.OrdinalIgnoreCase)
                        || fileName.EndsWith(".dxcache-shm", StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add((file, $"{D3dsPrefix}{dirName}"));
                    }
                }
            }
        }

        if (Directory.Exists(IntelDir))
        {
            foreach (var file in Directory.EnumerateFiles(IntelDir, "*", SearchOption.AllDirectories))
            {
                candidates.Add((file, $"{IntelPrefix}{Path.GetFileName(file)}"));
            }
        }

        return AppCacheFiles.KeysOpenBy(exeFileName, candidates);
    }

    public int Delete(IEnumerable<string> keys)
    {
        var files = FilesOf(keys);
        var deleted = AppCacheFiles.DeleteAll(files);

        if (Directory.Exists(D3dsDir))
        {
            var keySet = keys.Select(NormalizeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var dirName in keySet)
            {
                var dirPath = Path.Combine(D3dsDir, dirName);
                if (Directory.Exists(dirPath) && !Directory.EnumerateFileSystemEntries(dirPath).Any())
                {
                    try { Directory.Delete(dirPath); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
        }

        return deleted;
    }

    /// <summary>Calculates the deterministic DirectX D3DSCache subfolder hash for an executable path.</summary>
    public static string D3dsPathHash(string path)
    {
        ulong crc = 0x012345678abcdef0UL;
        var bytes = Encoding.Unicode.GetBytes(path);
        foreach (var b in bytes)
        {
            int idx = (int)((b ^ (byte)(crc & 0xff)) & 0xff);
            crc = (crc >> 8) ^ Crc64Table[idx];
        }
        return crc.ToString("x");
    }

    private static readonly ulong[] Crc64Table = InitCrc64Table();

    private static ulong[] InitCrc64Table()
    {
        const ulong poly = 0x92c64265d32139a4UL;
        var table = new ulong[256];
        for (int i = 0; i < 256; i++)
        {
            ulong crc = (ulong)i;
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 1) != 0)
                    crc = (crc >> 1) ^ poly;
                else
                    crc >>= 1;
            }
            table[i] = crc;
        }
        return table;
    }

    /// <summary>Promotes shader caches produced in temporary staging directories to the game's actual cache key,
    /// merging SQLite databases and updating the app_id path.</summary>
    public void PromoteWarm(string targetKey, IEnumerable<string> warmedKeys, string? gameExePath = null)
    {
        if (!Directory.Exists(D3dsDir)) return;
        var normalizedTarget = NormalizeKey(targetKey);
        var dstDir = Path.Combine(D3dsDir, normalizedTarget);
        var sources = warmedKeys.Select(NormalizeKey)
            .Where(k => !k.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sources.Count == 0) return;
        Directory.CreateDirectory(dstDir);

        foreach (var srcKey in sources)
        {
            var srcDir = Path.Combine(D3dsDir, srcKey);
            if (!Directory.Exists(srcDir)) continue;

            var d = new DirectoryInfo(srcDir);
            var dxcacheFiles = d.EnumerateFiles("*.dxcache").ToList();

            foreach (var srcFile in dxcacheFiles)
            {
                var dstFile = Path.Combine(dstDir, srcFile.Name);
                if (!File.Exists(dstFile))
                {
                    Checkpoint(srcFile.FullName);
                    try
                    {
                        File.Copy(srcFile.FullName, dstFile, true);
                        var srcWal = srcFile.FullName + "-wal";
                        var srcShm = srcFile.FullName + "-shm";
                        if (File.Exists(srcWal)) File.Copy(srcWal, dstFile + "-wal", true);
                        if (File.Exists(srcShm)) File.Copy(srcShm, dstFile + "-shm", true);

                        if (!string.IsNullOrEmpty(gameExePath))
                        {
                            SetAppIdExePath(dstFile, gameExePath);
                        }
                    }
                    catch { }
                }
                else
                {
                    MergeDxcache(srcFile.FullName, dstFile, gameExePath);
                }
            }

            // Cleanup the temporary staging folder
            try
            {
                foreach (var f in d.EnumerateFiles())
                {
                    try { f.Delete(); } catch { }
                }
                if (!Directory.EnumerateFileSystemEntries(srcDir).Any())
                {
                    Directory.Delete(srcDir);
                }
            }
            catch { }
        }
    }

    private static void SetAppIdExePath(string dbPath, string gameExePath)
    {
        if (sqlite3_open_v2(dbPath, out var db, 0x06 /* READWRITE | CREATE */, 0) == 0)
        {
            try
            {
                var escaped = gameExePath.Replace("'", "''");
                ExecSql(db, $"UPDATE app_id SET exe_path = '{escaped}' WHERE exe_path IS NOT NULL;");
                ExecSql(db, "PRAGMA wal_checkpoint(FULL);");
            }
            finally { sqlite3_close_v2(db); }
        }
    }

    private static void Checkpoint(string dbPath)
    {
        if (!File.Exists(dbPath)) return;
        if (sqlite3_open_v2(dbPath, out var db, 0x06 /* READWRITE | CREATE */, 0) == 0)
        {
            try { ExecSql(db, "PRAGMA wal_checkpoint(FULL);"); }
            finally { sqlite3_close_v2(db); }
        }
    }

    private static void MergeDxcache(string srcDbPath, string dstDbPath, string? gameExePath)
    {
        Checkpoint(srcDbPath);
        Checkpoint(dstDbPath);
        if (sqlite3_open_v2(dstDbPath, out var db, 0x06 /* READWRITE | CREATE */, 0) != 0) return;
        try
        {
            var escapedSrc = srcDbPath.Replace("'", "''");
            if (!ExecSql(db, $"ATTACH DATABASE '{escapedSrc}' AS src;")) return;
            try
            {
                ExecSql(db, "BEGIN TRANSACTION;");
                ExecSql(db, """
                    INSERT INTO keys (key, last_accessed)
                    SELECT s.key, s.last_accessed
                    FROM src.keys s
                    WHERE s.key NOT IN (SELECT d.key FROM main.keys d);
                    """);
                ExecSql(db, """
                    INSERT INTO vals (keyid, type, value)
                    SELECT m.keyid, sv.type, sv.value
                    FROM src.vals sv
                    JOIN src.keys sk ON sv.keyid = sk.keyid
                    JOIN main.keys m ON m.key = sk.key
                    WHERE m.keyid NOT IN (SELECT v.keyid FROM main.vals v);
                    """);
                if (TableExists(db, "src", "groupkeys") && TableExists(db, "main", "groupkeys"))
                {
                    ExecSql(db, """
                        INSERT INTO groupkeys (key, version)
                        SELECT s.key, s.version
                        FROM src.groupkeys s
                        WHERE s.key NOT IN (SELECT d.key FROM main.groupkeys d);
                        """);
                    if (TableExists(db, "src", "groups") && TableExists(db, "main", "groups"))
                    {
                        ExecSql(db, """
                            INSERT INTO groups (groupkeyid, valuekeyid)
                            SELECT mg.keyid, mv.keyid
                            FROM src.groups sg
                            JOIN src.groupkeys sgk ON sg.groupkeyid = sgk.keyid
                            JOIN main.groupkeys mg ON mg.key = sgk.key
                            JOIN src.keys sk ON sg.valuekeyid = sk.keyid
                            JOIN main.keys mv ON mv.key = sk.key
                            WHERE NOT EXISTS (
                                SELECT 1 FROM main.groups g 
                                WHERE g.groupkeyid = mg.keyid AND g.valuekeyid = mv.keyid
                            );
                            """);
                    }
                }
                if (!string.IsNullOrEmpty(gameExePath))
                {
                    var escapedExe = gameExePath.Replace("'", "''");
                    ExecSql(db, $"UPDATE app_id SET exe_path = '{escapedExe}' WHERE exe_path IS NOT NULL;");
                }
                ExecSql(db, "COMMIT;");
            }
            catch
            {
                ExecSql(db, "ROLLBACK;");
            }
            finally
            {
                ExecSql(db, "DETACH DATABASE src;");
            }
            ExecSql(db, "PRAGMA wal_checkpoint(FULL);");
        }
        catch { }
        finally { sqlite3_close_v2(db); }
    }

    private static bool TableExists(nint db, string schema, string table)
    {
        bool exists = false;
        Rows(db, $"SELECT 1 FROM {schema}.sqlite_master WHERE type='table' AND name='{table}' LIMIT 1;", _ => exists = true);
        return exists;
    }

    private static bool Rows(nint db, string sql, Action<nint> row)
    {
        if (sqlite3_prepare_v2(db, sql, -1, out var st, 0) != 0) return false;
        try
        {
            int rc;
            while ((rc = sqlite3_step(st)) == 100 /* ROW */) row(st);
            return rc is 100 or 101 /* DONE */;
        }
        finally { sqlite3_finalize(st); }
    }

    private static bool ExecSql(nint db, string sql) =>
        sqlite3_exec(db, sql, 0, 0, out _) == 0;

    [DllImport("winsqlite3.dll", ExactSpelling = true)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out nint db, int flags, nint vfs);

    [DllImport("winsqlite3.dll", ExactSpelling = true)]
    private static extern int sqlite3_close_v2(nint db);

    [DllImport("winsqlite3.dll", ExactSpelling = true)]
    private static extern int sqlite3_prepare_v2(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out nint stmt, nint tail);

    [DllImport("winsqlite3.dll", ExactSpelling = true)]
    private static extern int sqlite3_step(nint stmt);

    [DllImport("winsqlite3.dll", ExactSpelling = true)]
    private static extern int sqlite3_finalize(nint stmt);

    [DllImport("winsqlite3.dll", ExactSpelling = true)]
    private static extern int sqlite3_exec(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, nint callback, nint arg, out nint errMsg);
}
