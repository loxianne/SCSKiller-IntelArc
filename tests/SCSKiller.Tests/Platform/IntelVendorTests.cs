using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Vendors;
using Xunit;

namespace SCSKiller.Tests.Platform;

public class IntelVendorTests
{
    static GpuInfo IntelGpu(string driver = "32.0.101.9034", ulong vram = 16UL << 30) =>
        new(GpuVendor.Intel, "Intel(R) Arc(TM) B580 Graphics", driver, 0x1234, vram);

    [Fact]
    public void Intel_backend_initializes_with_correct_caps_and_vendor()
    {
        var gpu = IntelGpu();
        var backend = new IntelBackend(gpu);

        Assert.Equal(GpuVendor.Intel, backend.Vendor);
        Assert.Equal("intel-1", backend.Caps.Profile);
        Assert.True(backend.Caps.CacheKeyedByExeName);
        Assert.True(backend.Caps.StateIndependentCache);
        Assert.False(backend.Caps.CacheSizeConfigurable);
        Assert.False(backend.Caps.PerStageCache);
        Assert.Equal(RtCacheGranularity.WholeObject, backend.Caps.RtCacheGranularity);
        Assert.NotNull(backend.AppCache);
        Assert.Equal(new CacheLimit(null, false), backend.GetCacheLimit());
        Assert.Throws<NotSupportedException>(() => backend.SetCacheLimit(new CacheLimit(100, false)));
    }

    [Fact]
    public void Intel_driver_version_formatting()
    {
        // 4-part UMD version e.g. 32.0.101.9034
        Assert.Equal("101.9034 (32.0.101.9034)", IntelBackend.FormatVersion("32.0.101.9034"));
        Assert.Equal("101.6881 (32.0.101.6881)", IntelBackend.FormatVersion("32.0.101.6881"));

        // Fallbacks for unusual strings
        Assert.Equal("custom-driver", IntelBackend.FormatVersion("custom-driver"));
        Assert.Equal("", IntelBackend.FormatVersion(""));
    }

    [Fact]
    public void Intel_backend_refresh_updates_driver_version()
    {
        var backend = new IntelBackend(IntelGpu("32.0.101.6881"));
        Assert.Equal("101.6881 (32.0.101.6881)", backend.Gpu.DriverVersion);

        Assert.True(backend.Refresh(backend.Gpu with { DriverVersion = "32.0.101.9034" }));
        Assert.Equal("101.9034 (32.0.101.9034)", backend.Gpu.DriverVersion);
    }

    [Fact]
    public void Intel_app_cache_keys_and_files()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "intel-cache-test-" + Guid.NewGuid().ToString("N")[..8]);
        var d3ds = Path.Combine(tempDir, "D3DSCache");
        var intel = Path.Combine(tempDir, "Intel", "ShaderCache");

        try
        {
            var appFolder = Path.Combine(d3ds, "bff55fa8eb08f7dc");
            Directory.CreateDirectory(appFolder);
            var dbFile = Path.Combine(appFolder, "6F75932F-7DFC-4FB0-B4B8-12DE1AC415DA_VEN_8086&DEV_E20B&SUBSYS_6020&REV_0.dxcache");
            File.WriteAllBytes(dbFile, new byte[1024]);
            var walFile = Path.Combine(appFolder, "6F75932F-7DFC-4FB0-B4B8-12DE1AC415DA_VEN_8086&DEV_E20B&SUBSYS_6020&REV_0.dxcache-wal");
            File.WriteAllBytes(walFile, new byte[256]);

            var cache = new IntelAppCache(d3ds, intel);

            // FilesOf by prefixed key or directory name
            var filesPrefixed = cache.FilesOf(["d3ds:bff55fa8eb08f7dc"]);
            Assert.Equal(2, filesPrefixed.Count);
            Assert.Equal(1280, cache.SizeOf(["d3ds:bff55fa8eb08f7dc"]));

            var filesPlain = cache.FilesOf(["bff55fa8eb08f7dc"]);
            Assert.Equal(2, filesPlain.Count);

            // Deletion
            var deleted = cache.Delete(["d3ds:bff55fa8eb08f7dc"]);
            Assert.Equal(2, deleted);
            Assert.False(File.Exists(dbFile));
            Assert.False(File.Exists(walFile));
            Assert.False(Directory.Exists(appFolder));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GpuBackends_detects_Intel_as_IntelBackend()
    {
        var dxgi = new DxgiAdapter(IntelGpu(), 0xE20B, 0x60201849);
        var primary = GpuBackends.Primary([dxgi]);
        Assert.NotNull(primary);
        Assert.Equal(GpuVendor.Intel, primary.Vendor);
    }

    [Trait("Needs", "Gpu")]
    [Fact]
    public void Intel_real_hardware_detect()
    {
        var backend = GpuBackends.Detect();
        if (backend.Vendor != GpuVendor.Intel) return;
        Assert.IsType<IntelBackend>(backend);
        Assert.Equal(GpuVendor.Intel, backend.Vendor);
        var usage = backend.GetCacheUsage();
        Assert.NotNull(usage.Path);
    }

    [Fact]
    public void Intel_D3dsPathHash_matches_Windows_d3dscache_hash()
    {
        Assert.Equal("f8a99be89df52393", IntelAppCache.D3dsPathHash(@"E:\SteamLibrary\steamapps\common\FINAL FANTASY VII REBIRTH\End\Binaries\Win64\ff7rebirth_.exe"));
        Assert.Equal("52bd78272f3463a3", IntelAppCache.D3dsPathHash(@"E:\SteamLibrary\steamapps\common\FINAL FANTASY VII REMAKE\End\Binaries\Win64\ff7remake_.exe"));
        Assert.Equal("3f0a791a7528c36c", IntelAppCache.D3dsPathHash(@"D:\Programmu\ludusavi.exe"));
        Assert.Equal("83fe5639c8404bde", IntelAppCache.D3dsPathHash(@"C:\WINDOWS\Explorer.EXE"));
    }

    [Fact]
    public void Intel_PromoteWarm_merges_and_promotes_caches()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "intel-promote-test-" + Guid.NewGuid().ToString("N")[..8]);
        var d3ds = Path.Combine(tempDir, "D3DSCache");
        var intelDir = Path.Combine(tempDir, "Intel");

        try
        {
            var stagedDir = Path.Combine(d3ds, "staged_hash_123");
            var targetDir = Path.Combine(d3ds, "target_hash_456");
            Directory.CreateDirectory(stagedDir);
            Directory.CreateDirectory(targetDir);

            var dbName = "6F75932F-7DFC-4FB0-B4B8-12DE1AC415DA_VEN_8086&DEV_E20B&SUBSYS_6020&REV_0.dxcache";
            var stagedDb = Path.Combine(stagedDir, dbName);
            var targetDb = Path.Combine(targetDir, dbName);

            // Create target db with 1 key
            CreateTestDb(targetDb, @"E:\Games\MyGame\game.exe", [(1, "01020304")]);

            // Create staged db with 1 overlapping key and 1 new key
            CreateTestDb(stagedDb, @"C:\Temp\stage-1\game.exe", [(1, "01020304"), (2, "05060708")]);

            var cache = new IntelAppCache(d3ds, intelDir);
            cache.PromoteWarm("d3ds:target_hash_456", ["d3ds:staged_hash_123"], @"E:\Games\MyGame\game.exe");

            // Staged directory should be deleted
            Assert.False(Directory.Exists(stagedDir));

            // Target db should now contain both keys (2 total)
            Assert.Equal(2, CountKeys(targetDb));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    static void CreateTestDb(string path, string exePath, (int id, string hex)[] keys)
    {
        Assert.Equal(0, sqlite3_open(path, out var db));
        try
        {
            Assert.Equal(0, sqlite3_exec(db, "CREATE TABLE keys(keyid INTEGER PRIMARY KEY, key BLOB, last_accessed REAL);", null, 0, 0));
            Assert.Equal(0, sqlite3_exec(db, "CREATE TABLE vals(keyid INTEGER REFERENCES keys(keyid) ON DELETE CASCADE, type INTEGER, value BLOB);", null, 0, 0));
            Assert.Equal(0, sqlite3_exec(db, "CREATE TABLE app_id(id INTEGER PRIMARY KEY, exe_path TEXT, app_name TEXT, engine_name TEXT, app_version INTEGER, engine_version INTEGER, app_profile_version INTEGER);", null, 0, 0));
            Assert.Equal(0, sqlite3_exec(db, $"INSERT INTO app_id(id, exe_path) VALUES (0, '{exePath.Replace("'", "''")}');", null, 0, 0));
            foreach (var (id, hex) in keys)
            {
                Assert.Equal(0, sqlite3_exec(db, $"INSERT INTO keys VALUES ({id}, x'{hex}', 100.0);", null, 0, 0));
                Assert.Equal(0, sqlite3_exec(db, $"INSERT INTO vals VALUES ({id}, 1, x'{hex}aabb');", null, 0, 0));
            }
        }
        finally { sqlite3_close(db); }
    }

    static int CountKeys(string path)
    {
        Assert.Equal(0, sqlite3_open(path, out var db));
        try
        {
            int count = 0;
            Assert.Equal(0, sqlite3_exec(db, "SELECT count(*) FROM keys;", (_, _, values, _) =>
            {
                var s = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(System.Runtime.InteropServices.Marshal.ReadIntPtr(values));
                count = int.Parse(s!);
                return 0;
            }, 0, 0));
            return count;
        }
        finally { sqlite3_close(db); }
    }

    [System.Runtime.InteropServices.DllImport("winsqlite3.dll")] static extern int sqlite3_open([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string file, out nint db);
    [System.Runtime.InteropServices.DllImport("winsqlite3.dll")] static extern int sqlite3_exec(nint db, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string sql, ExecCallback? callback, nint arg, nint error);
    [System.Runtime.InteropServices.DllImport("winsqlite3.dll")] static extern int sqlite3_close(nint db);
    delegate int ExecCallback(nint arg, int columns, nint values, nint names);

    [Fact]
    public void Intel_warm_misses_game_is_false_when_warmed_key_matches_target_key()
    {
        var rec = new GameRecord { GameKeys = ["d3ds:f8a99be89df52393"], WarmedKeys = ["d3ds:f8a99be89df52393"] };
        Assert.False(ScsKiller.WarmMissesGame(rec));

        var recMiss = new GameRecord { GameKeys = ["d3ds:f8a99be89df52393"], WarmedKeys = ["d3ds:3c92b6e6a1bc511e"] };
        Assert.True(ScsKiller.WarmMissesGame(recMiss));
    }

    [Fact]
    public void AppCacheFiles_DeleteAll_handles_duplicate_files_without_sharing_violation()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "scsk_test_" + Guid.NewGuid().ToString("N") + ".dxcache");
        try
        {
            File.WriteAllBytes(tempFile, [1, 2, 3, 4]);
            var fi = new FileInfo(tempFile);
            // Passing the same file multiple times (as happened between DriverPart and WindowsPart on Intel)
            var deleted = AppCacheFiles.DeleteAll([fi, new FileInfo(tempFile), fi]);
            Assert.Equal(1, deleted);
            Assert.False(File.Exists(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
