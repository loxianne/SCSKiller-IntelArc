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
}
