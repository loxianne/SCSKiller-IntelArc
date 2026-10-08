using System.Runtime.InteropServices;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

/// <summary>Synthetic D3DSCache folders: dbs with the runtime's app_id table, made with Windows' own SQLite.</summary>
static class FakeD3DSCache
{
    /// <summary>A folder like the runtime's: a db naming <paramref name="exePath"/> plus its -shm and an empty -wal. broken: the
    /// db is not SQLite at all, and its -wal names the exe (a WAL scan alone must not attribute it).</summary>
    public static string Folder(string root, string hash, string exePath, bool broken = false)
    {
        var dir = Path.Combine(root, hash);
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "F4EB2D6C-ED2B-4BDD-AD9D-F913287E6768.dxcache");
        if (broken)
        {
            File.WriteAllBytes(db, new byte[4096]);
            File.WriteAllBytes(db + "-wal", Encoding.Latin1.GetBytes("\x01\x02" + exePath + "\x00"));
        }
        else
        {
            Db(db, exePath);
            File.WriteAllBytes(db + "-wal", []);
        }
        File.WriteAllBytes(db + "-shm", new byte[32768]);
        return dir;
    }

    public static void Db(string path, params string[] exePaths)
    {
        Assert.Equal(0, sqlite3_open(path, out var db));
        try
        {
            Exec(db, "create table app_id(id INTEGER PRIMARY KEY, exe_path TEXT, app_name TEXT, engine_name TEXT, app_version INTEGER, engine_version INTEGER, app_profile_version INTEGER)");
            foreach (var p in exePaths) Exec(db, $"insert into app_id(exe_path) values ('{p.Replace("'", "''")}')");
        }
        finally { sqlite3_close(db); }
    }

    static void Exec(nint db, string sql) => Assert.Equal(0, sqlite3_exec(db, sql, 0, 0, 0));

    [DllImport("winsqlite3.dll")] static extern int sqlite3_open([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out nint db);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_exec(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, nint callback, nint arg, nint error);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_close(nint db);
}

// Everything here is under %TEMP%; the real D3DSCache and Saved folders are never read.
public class GameCachesTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "scskiller-caches-test-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    static Game Steam(string install) =>
        new("steam:1", "Fake", Store.Steam, install, Path.Combine(install, "Fake", "Binaries", "Win64", "Fake-Win64-Shipping.exe"));

    [Fact]
    public void A_D3DSCache_folder_is_the_games_only_when_every_db_reads_and_every_path_it_names_is_the_games_exe()
    {
        var g = Steam(@"C:\Games\Steam\steamapps\common\Fake Game");
        var d3ds = Path.Combine(_root, "D3DSCache");
        var ours = FakeD3DSCache.Folder(d3ds, "01", g.ExePath.ToUpperInvariant());   // the path's case doesn't matter
        FakeD3DSCache.Folder(d3ds, "02", @"C:\Other\Fake-Win64-Shipping.exe");       // same exe name, another path
        FakeD3DSCache.Folder(d3ds, "03", g.ExePath, broken: true);                   // unparsable db: nothing, whatever the WAL says

        var walOnly = Path.Combine(d3ds, "04");   // a db without the table yet: its rows are only in the WAL
        Directory.CreateDirectory(walOnly);
        File.WriteAllBytes(Path.Combine(walOnly, "a.dxcache"), []);
        File.WriteAllBytes(Path.Combine(walOnly, "a.dxcache-wal"), Encoding.Latin1.GetBytes("junk" + g.ExePath + "\0more"));

        var mixed = FakeD3DSCache.Folder(d3ds, "05", g.ExePath);   // a second db names another exe: not positively the game's
        FakeD3DSCache.Db(Path.Combine(mixed, "other.dxcache"), @"C:\Other\other.exe");

        var alsoBroken = FakeD3DSCache.Folder(d3ds, "06", g.ExePath);   // a readable db beside an unreadable one
        File.WriteAllText(Path.Combine(alsoBroken, "b.dxcache"), "not a database, just text long enough to have a header....................................................................................");

        Directory.CreateDirectory(Path.Combine(d3ds, "07"));   // empty

        Assert.Equal([ours, walOnly], D3DSCache.FoldersOf(d3ds, g).Order());
        Assert.Null(D3DSCache.ExePaths(Path.Combine(d3ds, "03")));
        Assert.Empty(D3DSCache.FoldersOf(Path.Combine(_root, "missing"), g));
    }

    /// <summary>A folder whose db names another exe is decided without its WAL (one held exclusively would make it a doubt);
    /// the db's paths are kept until the db changes.</summary>
    [Fact]
    public void A_folder_another_exe_owns_is_decided_from_its_db_without_reading_the_WAL()
    {
        var g = Steam(@"C:\Games\Steam\steamapps\common\Fake Game");
        var other = FakeD3DSCache.Folder(Path.Combine(_root, "D3DSCache"), "01", @"C:\Other\other.exe");
        var db = Path.Combine(other, "F4EB2D6C-ED2B-4BDD-AD9D-F913287E6768.dxcache");
        using (new FileStream(db + "-wal", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(D3DSCache.IsGames(other, g));
            using (new FileStream(db, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.False(D3DSCache.IsGames(other, g));   // its paths kept: the db isn't read again
        }

        File.Delete(db);   // the db rewritten for the game: read again
        FakeD3DSCache.Db(db, g.ExePath);
        File.SetLastWriteTimeUtc(db, DateTime.UtcNow.AddMinutes(1));
        Assert.True(D3DSCache.IsGames(other, g));
        using (new FileStream(db + "-wal", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Null(D3DSCache.IsGames(other, g));   // the game's: its WAL is read, and can't be: a doubt
    }

    /// <summary>A compile or a test deletes the folders its own exes left since it started, nothing else.</summary>
    [Fact]
    public void A_test_deletes_only_the_D3DSCache_folders_of_its_own_exes()
    {
        var (d3ds, mine) = (Path.Combine(_root, "D3DSCache"), Path.Combine(_root, "scskiller-app-test-1"));
        var started = DateTime.UtcNow;
        var selftest = FakeD3DSCache.Folder(d3ds, "01", Path.Combine(mine, "admit", "selftest.exe"));
        var staged = FakeD3DSCache.Folder(d3ds, "02", Path.Combine(mine, "data", "work", "stage-1-1", "scsk-cp-1.exe").ToUpperInvariant());
        FakeD3DSCache.Folder(d3ds, "03", Path.Combine(_root, "scskiller-app-test-10", "selftest.exe"));   // another test's
        FakeD3DSCache.Folder(d3ds, "04", @"C:\Games\Fake\Fake.exe");                                     // a game
        FakeD3DSCache.Folder(d3ds, "05", Path.Combine(mine, "b.exe"), broken: true);                     // in doubt
        var mixed = FakeD3DSCache.Folder(d3ds, "06", Path.Combine(mine, "c.exe"));
        FakeD3DSCache.Db(Path.Combine(mixed, "other.dxcache"), @"C:\Games\Fake\Fake.exe");
        var older = FakeD3DSCache.Folder(d3ds, "07", Path.Combine(mine, "d.exe"));
        Directory.SetCreationTimeUtc(older, started.AddMinutes(-5));                                     // before this test

        Assert.Equal([selftest, staged], D3DSCache.Made(d3ds, mine, started).Order());
        Assert.Empty(D3DSCache.Made(Path.Combine(_root, "missing"), mine, started));
    }

    [Fact]
    public void A_path_only_in_the_WAL_is_found_with_non_ascii_characters()
    {
        var g = Steam(@"C:\Users\José\Games\Fake Game");
        var walOnly = Path.Combine(_root, "D3DSCache", "01");
        Directory.CreateDirectory(walOnly);
        File.WriteAllBytes(Path.Combine(walOnly, "a.dxcache"), []);
        File.WriteAllBytes(Path.Combine(walOnly, "a.dxcache-wal"), [.. "junk"u8, .. Encoding.UTF8.GetBytes(g.ExePath), 0, .. "more"u8]);   // SQLite's text: UTF-8
        Assert.Equal([walOnly], D3DSCache.FoldersOf(Path.Combine(_root, "D3DSCache"), g));

        // a fragment of a path before a byte that isn't UTF-8 text: not joined to the path after it
        File.WriteAllBytes(Path.Combine(walOnly, "a.dxcache-wal"), [.. @"C:\partial"u8, 0xFF, .. Encoding.UTF8.GetBytes(g.ExePath), 0]);
        Assert.Equal([g.ExePath], D3DSCache.ExePaths(walOnly)!);
    }

    [Fact]
    public void An_Xbox_game_matches_its_WindowsApps_package_path_by_family_and_path_in_Content()
    {
        var content = @"D:\XboxGames\Fake Game\Content";
        var g = new Game("xbox:Pub.FakeGame_8wekyb3d8bbwe", "Fake", Store.Xbox, content, content + @"\Fake\Binaries\WinGDK\Fake-WinGDK-Shipping.exe");
        Assert.True(D3DSCache.IsGameExe(@"C:\Program Files\WindowsApps\Pub.FakeGame_1.0.12.0_x64__8wekyb3d8bbwe\Fake\Binaries\WinGDK\Fake-WinGDK-Shipping.exe", g));
        Assert.True(D3DSCache.IsGameExe(@"C:\Program Files\WindowsApps\PUB.FAKEGAME_1.0.9.0_x64__8wekyb3d8bbwe\fake\binaries\wingdk\fake-wingdk-shipping.exe", g));   // an older version's
        Assert.True(D3DSCache.IsGameExe(g.ExePath, g));
        Assert.False(D3DSCache.IsGameExe(@"C:\Program Files\WindowsApps\Pub.Other_1.0.12.0_x64__8wekyb3d8bbwe\Fake\Binaries\WinGDK\Fake-WinGDK-Shipping.exe", g));
        Assert.False(D3DSCache.IsGameExe(@"C:\Program Files\WindowsApps\Pub.FakeGame_1.0.12.0_x64__otherpublisher\Fake\Binaries\WinGDK\Fake-WinGDK-Shipping.exe", g));
        Assert.False(D3DSCache.IsGameExe(@"C:\Program Files\WindowsApps\Pub.FakeGame_1.0.12.0_x64__8wekyb3d8bbwe\Fake-WinGDK-Shipping.exe", g));   // another path in the package
        Assert.False(D3DSCache.IsGameExe(@"C:\Program Files\WindowsApps\Pub.FakeGame_1.0.12.0_x64__8wekyb3d8bbwe\Fake\Binaries\WinGDK\Launcher.exe", g));
        Assert.False(D3DSCache.IsGameExe(@"C:\Users\me\AppData\Local\SCSKiller\games\x\work\stage\Fake-WinGDK-Shipping.exe", g));   // a staged warm copy
        Assert.False(D3DSCache.IsGameExe(@"C:\Program Files\WindowsApps\Pub.FakeGame_1.0.12.0_x64__8wekyb3d8bbwe\Fake\Binaries\WinGDK\Fake-WinGDK-Shipping.exe",
            g with { Store = Store.Steam, Id = "steam:1" }));   // only for Xbox games
    }

    [Fact]
    public void Unreals_user_pipeline_cache_is_found_by_its_project_name_in_Saved_folders_and_never_in_the_install()
    {
        var local = Path.Combine(_root, "Local");
        var myGames = Path.Combine(_root, "My Games");
        var g = Steam(Path.Combine(local, "Programs", "Fake Game"));   // installed under %LOCALAPPDATA%\Programs
        Assert.Equal("Fake", UnrealUserCache.Project(g.ExePath));
        Assert.Null(UnrealUserCache.Project(@"C:\Games\Other\other.exe"));

        string F(params string[] parts)
        {
            var path = Path.Combine([_root, .. parts]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[10]);
            return path;
        }
        string[] ours =
        [
            F("Local", "Fake", "Saved", "Fake_PCD3D_SM6.upipelinecache"),
            F("Local", "Iris", "Saved_Steam_7656", "fake_PCD3D_SM5.upipelinecache"),
            F("My Games", "Fake Game", "Saved", "Fake_SF_VULKAN_SM6.stable.upipelinecache"),
        ];
        F("Local", "Fake", "Saved", "FakeOther_PCD3D_SM6.upipelinecache");    // another project
        F("Local", "Fake", "Saved", "Fake_Two_PCD3D_SM6.upipelinecache");     // project "Fake_Two"
        F("Local", "Fake", "Saved", "Config", "Fake_PCD3D_SM6.upipelinecache");   // not directly in Saved
        F("Local", "Fake", "SavedGames", "Fake_PCD3D_SM6.upipelinecache");
        F("Local", "Programs", "Fake Game", "Fake", "Content", "PipelineCaches", "Windows", "Fake_PCD3D_SM6.stable.upipelinecache");   // shipped

        Assert.Equal(ours.Order(), UnrealUserCache.FilesOf(g, [local, myGames, Path.Combine(_root, "missing")]).Order());

        var inInstall = Steam(Path.Combine(_root, "Local2", "FakeInstall"));   // an install one level under a root
        F("Local2", "FakeInstall", "Saved", "Fake_PCD3D_SM6.upipelinecache");   // looks like the game's Saved, but inside the install
        var outside = F("Local2", "Fake", "Saved", "Fake_PCD3D_SM6.upipelinecache");
        Assert.Equal([outside], UnrealUserCache.FilesOf(inInstall, [Path.Combine(_root, "Local2")]));
    }
}
