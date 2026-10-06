using SCSKiller.Core.App;

namespace SCSKiller.Tests;

/// <summary>The D3D12 runtime keeps a <c>%LOCALAPPDATA%\D3DSCache</c> folder per exe path, and tests run selftest.exe and
/// staged warm exes from a new temp folder each time: thousands of folders a week on a CI runner. A test that ran exes
/// deletes the folders they left.</summary>
static class TestD3DSCache
{
    static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3DSCache");

    /// <summary>Deletes the folders made since <paramref name="since"/> whose every exe path is under <paramref name="dir"/>.</summary>
    public static void Clean(string dir, DateTime since)
    {
        foreach (var d in Made(Root, dir, since))
            try { Directory.Delete(d, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // the runtime still holds it: left
    }

    /// <summary>Folders in <paramref name="root"/> created since <paramref name="since"/> whose dbs name only exes under
    /// <paramref name="dir"/>; one in doubt (an unreadable db, no path) is never one.</summary>
    public static IReadOnlyList<string> Made(string root, string dir, DateTime since)
    {
        var under = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)) + '\\';
        var found = new List<string>();
        if (!Directory.Exists(root)) return found;
        foreach (var d in new DirectoryInfo(root).EnumerateDirectories().Where(d => d.CreationTimeUtc >= since.ToUniversalTime().AddSeconds(-2)))
            try
            {
                if (D3DSCache.ExePaths(d.FullName) is { Count: > 0 } paths && paths.All(p => p.StartsWith(under, StringComparison.OrdinalIgnoreCase)))
                    found.Add(d.FullName);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return found;
    }
}
