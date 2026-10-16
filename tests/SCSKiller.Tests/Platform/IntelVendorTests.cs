using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;
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
        Assert.Equal(new CacheLimit(IntelBackend.ContainerCeilingBytes, false), backend.GetCacheLimit());
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

    [Fact]
    public void Intel_LargestContainerBytes_finds_max_container_file()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "intel-container-test-" + Guid.NewGuid().ToString("N")[..8]);
        var d3ds = Path.Combine(tempDir, "D3DSCache");
        var intel = Path.Combine(tempDir, "Intel", "ShaderCache");
        Directory.CreateDirectory(intel);

        try
        {
            File.WriteAllBytes(Path.Combine(intel, "container_small"), new byte[100]);
            File.WriteAllBytes(Path.Combine(intel, "container_large"), new byte[5000]);

            var cache = new IntelAppCache(d3ds, intel);
            Assert.Equal(5000, cache.LargestContainerBytes());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Intel_plan_builder_cap_constant_is_safe_threshold()
    {
        Assert.Equal(35_000, Core.Planning.PlanBuilder.IntelMaxSpeculativePsos);
    }

    [Fact]
    public void Hook_conflict_detects_ShaderInjector_dll()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "hook-conflict-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            Assert.Null(ScsKiller.DetectHookConflict(tempDir));

            var injectorDll = Path.Combine(tempDir, "ShaderInjector.dll");
            File.WriteAllBytes(injectorDll, [1, 2, 3]);

            var conflict = ScsKiller.DetectHookConflict(tempDir);
            Assert.NotNull(conflict);
            Assert.Contains("ShaderInjector.dll detected", conflict);
            Assert.Contains("EXCEPTION_STACK_OVERFLOW", conflict);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ChainBlocker_detects_ShaderInjector_and_MinHook()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "chain-blocker-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var fakeMod = Path.Combine(tempDir, "d3d12.dll");
            // Write simulated DLL containing MinHook pattern
            var content = System.Text.Encoding.ASCII.GetBytes("SomeHeader_MH_Initialize_SomeTrailer");
            File.WriteAllBytes(fakeMod, content);

            var blocker = ScsKiller.ChainBlocker(fakeMod);
            Assert.NotNull(blocker);
            Assert.Contains("ShaderInjector", blocker);
            Assert.Contains("MinHook", blocker);

            Assert.Contains("ShaderInjector", ScsKiller.NotChainableReason("ShaderInjector"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Intel_cache_warning_thresholds()
    {
        long limit = IntelBackend.ContainerCeilingBytes; // 512 MB
        long nearCapacity = (long)(0.92 * limit);
        long safeCapacity = (long)(0.50 * limit);

        // Near 90% threshold produces Intel-specific warning
        var warnNear = ScsKiller.QueueCacheWarning([], nearCapacity, 0, limit, GpuVendor.Intel);
        Assert.NotNull(warnNear);
        Assert.Contains("Intel shader cache container is near its", warnNear);
        Assert.Contains(">90%", warnNear);

        // Safe capacity has no warning
        var warnSafe = ScsKiller.QueueCacheWarning([], safeCapacity, 0, limit, GpuVendor.Intel);
        Assert.Null(warnSafe);
    }

    [Fact]
    public void Planner_RecordedOnly_skips_speculative_permutations()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rec-only-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var a = new ShaderInfo(new string('a', 40), Stage.Compute, "cs_6_0", 0, new(0, 0, 0, 0), [], [], []);
            var unseen = new ShaderInfo(new string('e', 40), Stage.Compute, "cs_6_0", 0, new(0, 1, 0, 0), [new("srv", 0, 0, 1)], [], []);
            var index = new ShaderIndex("synthetic", ["PCD3D_SM6"], new[] { a, unseen }.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM6", [a.Sha1, unseen.Sha1])]);

            var rsBlob = RootSig.Serialize(RootSig.Build(RootSig.Rule.Ff7, new Dictionary<Stage, ShaderInfo> { [Stage.Compute] = a }, true), RootSig.StaticSamplers(RootSig.Rule.Ff7).ToArray());
            var rsSha = PsoDb.Hex(SHA1.HashData(rsBlob));
            var dbPath = Path.Combine(dir, "rec.db");
            using (var f = File.Create(dbPath))
            {
                PsoDb.WriteBlob(f, rsSha, rsBlob);
                PsoDb.Write(f, 'C', PsoDb.Compute(rsSha, a.Sha1));
            }

            var game = new Game("steam:2909400", "FF7 Rebirth", Store.Steam, @"C:\Fake", @"C:\Fake\ff7.exe");
            var ue426 = new EngineInfo("Unreal", "4.26", "GAME_FinalFantasy7Rebirth", "D3D12", false, null);
            var rec = new Recording(dbPath);
            var planner = new Planner();

            var caps = new IntelBackend(IntelGpu()).Caps;
            var defaultPlan = planner.Build(game, ue426, index, rec, caps, Path.Combine(dir, "default"), null, CancellationToken.None);
            var maxPlan = planner.Build(game, ue426, index, rec, caps, Path.Combine(dir, "max"), null, CancellationToken.None, maximum: true);

            // Default on Intel with a recording: automatically recorded-only (0 generated permutations)
            Assert.Equal(0, defaultPlan.Stats.Generated);
            Assert.True(defaultPlan.Stats.Recorded > 0);

            // Maximum mode explicitly requested: allows speculative generation
            Assert.True(maxPlan.Stats.Generated > 0);
            Assert.Equal(defaultPlan.Stats.Recorded, maxPlan.Stats.Recorded);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
