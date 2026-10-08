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
        foreach (var d in D3DSCache.Made(Root, dir, since))
            try { Directory.Delete(d, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // the runtime still holds it: left
    }
}
