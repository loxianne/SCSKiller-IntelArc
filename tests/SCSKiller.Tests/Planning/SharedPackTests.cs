using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Platform;
using Xunit.Abstractions;
using static SCSKiller.Tests.Planning.MiddlewarePackTests;

namespace SCSKiller.Tests.Planning;

/// <summary>Shared middleware packs: the manifest's 'P' records, the free download
/// matched to this install's DLL copy, and seeding by GPU vendor (fake DLLs, a fake handler, no network).</summary>
public class SharedPackTests(ITestOutputHelper output)
{
    static readonly VendorCaps Nvidia = new("nvidia-1", true, true, true), Amd = new("amd-1", true, false, true);

    /// <summary>A container with one part of these bytes.</summary>
    static byte[] Part(string fourcc, byte[] data)
    {
        var b = new byte[44 + data.Length];
        "DXBC"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), (uint)b.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), 36);
        Encoding.ASCII.GetBytes(fourcc).CopyTo(b, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40), (uint)data.Length);
        data.CopyTo(b, 44);
        return b;
    }

    /// <summary>A shader with [WaveSize(64)] in its PSV0 runtime info (what FidelityFX picks on AMD).</summary>
    internal static byte[] Wave64(string seed)
    {
        var psv = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(psv, 24);
        Encoding.ASCII.GetBytes(seed.PadRight(16, '.')[..16]).CopyTo(psv, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(psv.AsSpan(20), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(psv.AsSpan(24), 64);
        return Part("PSV0", psv);
    }

    sealed record Setup(string Root, byte[] Dll, byte[] A, byte[] B, byte[] W64, byte[] Rs, string Shared, string Local);

    static Setup Make(string name)
    {
        var root = Ff7.TempDir(name);
        var (a, b, w64) = (Container("DXIL", "ffx a"), Container("DXIL", "ffx b"), Wave64("ffx wave64"));
        return new(root, Pe("amd_fidelityfx_dx12.dll", a, b, w64), a, b, w64, Container("RTS0", "ffx root signature"),
            Path.Combine(root, "community", "packs", "nvidia"), Path.Combine(root, "packs"));
    }

    static string Install(Setup x, string name, byte[] dll)
    {
        var dir = Path.Combine(x.Root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "game.exe"), Pe(null));
        File.WriteAllBytes(Path.Combine(dir, "amd_fidelityfx_dx12.dll"), dll);
        return dir;
    }

    /// <summary>What the server serves for a pack: canonical records, Brotli.</summary>
    static List<PsoDb.Rec> Records(Setup x, params byte[][] shaders) => HashOnly.Canonical(
        [new PsoDb.Rec('B', [.. SHA1.HashData(x.Rs), .. x.Rs]), .. shaders.Select(s => new PsoDb.Rec('C', PsoDb.Compute(Sha(x.Rs), Sha(s))))], local: false, out _);

    internal static byte[] Pack(byte[] hash20, byte[] obj, int psos) => CommunityTests.Record('P', r =>
    {
        BinaryPrimitives.WriteUInt16LittleEndian(r[2..], 1);
        hash20.CopyTo(r[4..]);
        SHA256.HashData(obj).CopyTo(r[24..]);
        BinaryPrimitives.WriteUInt32LittleEndian(r[56..], (uint)obj.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(r[60..], (uint)psos);
    });

    static string Key(Setup x, byte[] dll, string gpu = "nvidia") => HashOnly.PackKey(gpu, "amd", "amd_fidelityfx_dx12.dll", Sha(dll));

    [Fact]
    public void The_manifest_lists_packs_by_their_key_hash_apart_from_entries()
    {
        var x = Make("sp-manifest");
        var key = Key(x, x.Dll);
        Assert.Equal($"nvidia:amd:amd_fidelityfx_dx12.dll:{Sha(x.Dll)}", key);
        Assert.True(HashOnly.IsPackKey(key));
        Assert.False(HashOnly.IsPackKey("steam:480@1"));
        var hash = Convert.FromHexString(HashOnly.PackHash(key));
        var obj = "pack"u8.ToArray();
        var m = CommunityManifest.Parse(CommunityTests.Manifest(Pack(hash, obj, 2)));
        Assert.True(m.HasPacks);
        Assert.Equal((PsoDb.Hex(SHA256.HashData(obj)), 2), (m.FindPack(key)!.Object, m.FindPack(key)!.Psos));
        Assert.Null(m.FindPack(Key(x, x.Dll, "amd")));   // the other GPU vendor's is another pack
        Assert.Null(m.Find(new Game("g", "g", Store.Other, "", ""), PsoDb.Hex(hash)));   // never an entry
        Assert.Null(CommunityManifest.Parse(CommunityTests.Manifest(Pack(hash, obj, 2), CommunityTests.Tombstone(PsoDb.Hex(hash)))).FindPack(key));
    }

    [Fact]
    public async Task A_pack_downloads_without_a_token_and_keeps_only_what_this_dll_copy_holds()
    {
        var x = Make("sp-download");
        var dir = Install(x, "game", x.Dll);
        var dll = Middleware.Detect(dir).Single();
        var image = Middleware.Scan(dll.Path);
        var stranger = Container("DXIL", "a shader this DLL doesn't have");
        var body = HashOnly.Compress(Records(x, x.A, x.B, x.W64, stranger));
        var entry = new CommunityEntry(HashOnly.PackHash(Key(x, x.Dll)), PsoDb.Hex(SHA256.HashData(body)), body.Length, 4, 1);
        var serve = body;
        var fake = new CommunityTests.Fake(r => CommunityTests.Ours(HttpStatusCode.OK, serve));
        var community = new Community(x.Root, (_, _) => throw new InvalidOperationException("no token is ever asked for"),
            new RouteFailover(fake, [new("https://api.test.com/")]));
        var shared = new MiddlewarePacks(x.Shared);

        Assert.Equal(2, await community.DownloadPackAsync(entry, dll, image, shared, amd: false));   // not the stranger, nor the wave64 one on NVIDIA
        Assert.Equal([$"GET https://api.test.com/v1/p/{entry.Object}"], fake.Log);   // no Authorization
        var pack = shared.Load(dll, image)!;
        Assert.Equal((entry.Object, 2, 1), (pack.Header.Object, pack.Entries.Count, pack.RootSignatures.Count));
        Assert.Equal(["community"], pack.Header.Sources);

        serve = [.. body, 0];   // not what the manifest names
        Assert.Null(await community.DownloadPackAsync(entry, dll, image, shared, amd: false));
        Assert.Contains("checksum", community.Problem);
        // a pack carrying anything but PSOs and their root signatures is refused
        var records = Records(x, x.A);
        serve = HashOnly.Compress([.. records, new PsoDb.Rec('L', Convert.FromHexString(records[^1].Key))]);
        Assert.Null(await community.DownloadPackAsync(entry with { Object = PsoDb.Hex(SHA256.HashData(serve)), Size = serve.Length }, dll, image, shared, amd: false));
        Assert.Equal(entry.Object, shared.Load(dll, image)!.Header.Object);   // the good one stays
        serve = body;
        Assert.Equal(3, await new Community(Path.Combine(x.Root, "amd"), (_, _) => Task.FromResult<string?>(null), new RouteFailover(fake, [new("https://api.test.com/")]))
            .DownloadPackAsync(entry, dll, image, new MiddlewarePacks(Path.Combine(x.Root, "amd-packs")), amd: true));   // AMD runs wave64
    }

    [Fact]
    public void A_shared_pack_seeds_like_a_local_one_without_the_wave_sizes_this_gpu_doesnt_run()
    {
        var x = Make("sp-seed");
        var game = GameIn(Install(x, "game", x.Dll), "test:game");
        var dll = Middleware.Detect(game).Single();
        var image = Middleware.Scan(dll.Path);
        var planner = new Planner(x.Local, x.Shared);
        Assert.Equal("", planner.PackFingerprint(game));
        var stored = MiddlewarePacks.FromShared(Records(x, x.A, x.B, x.W64), dll, image, amd: true, "obj", out _);   // with a wave64 PSO, as an AMD install keeps it
        stored.Write(planner.SharedPacks!.PathOf(dll.Vendor, dll.Name, image.ContentHash));
        Assert.StartsWith("|shared:amd_fidelityfx_dx12.dll:", planner.PackFingerprint(game));   // a download re-plans
        Assert.Equal(3, planner.PackPipelines(dll));

        var log = new CommunityLines();
        var plan = planner.Build(game, Engine, Index(), null, Nvidia, Path.Combine(x.Root, "plan-nv"), log, default);
        output.WriteLine(string.Join("\n", log.All));
        Assert.Equal((2, 2), (plan.Stats.MiddlewareItems, plan.Stats.MiddlewareSharedItems));   // the wave64 one: AMD only
        Assert.Contains(log.All, l => l.Contains("2 shared pack PSOs") && l.Contains("1 with a wave size this GPU doesn't run left out"));
        Assert.Equal(3, planner.Build(game, Engine, Index(), null, Amd, Path.Combine(x.Root, "plan-amd"), null, default).Stats.MiddlewareItems);

        // this PC's own pack seeds first: what both hold isn't counted as the shared pack's
        var local = new MiddlewarePack(dll.Vendor, dll.Name, image.ContentHash, image.Size);
        local.RootSignatures[Sha(x.Rs)] = x.Rs;
        local.Add(new PsoDb.Rec('C', PsoDb.Compute(Sha(x.Rs), Sha(x.A))), "test:game");
        local.Write(planner.Packs!.PathOf(dll.Vendor, dll.Name, image.ContentHash));
        plan = planner.Build(game, Engine, Index(), null, Nvidia, Path.Combine(x.Root, "plan-both"), null, default);
        Assert.Equal((2, 1), (plan.Stats.MiddlewareItems, plan.Stats.MiddlewareSharedItems));
        Assert.Equal(3, planner.PackPipelines(dll));

        // the shared pack is never promoted into: a recording's PSOs go to this PC's pack
        Assert.Single(MiddlewarePack.Read(planner.Packs.PathOf(dll.Vendor, dll.Name, image.ContentHash)).Entries);
    }

    [Fact]
    public void A_recording_merged_with_another_vendors_promotes_only_what_this_gpu_runs()
    {
        var x = Make("sp-promote");
        var game = GameIn(Install(x, "game", x.Dll), "test:game");
        var db = Path.Combine(x.Root, "recording.db");
        using (var f = File.Create(db))
        {
            foreach (var blob in new[] { x.Rs, x.A, x.W64 }) PsoDb.WriteBlob(f, Sha(blob), blob);
            PsoDb.Write(f, 'C', PsoDb.Compute(Sha(x.Rs), Sha(x.A)));
            PsoDb.Write(f, 'C', PsoDb.Compute(Sha(x.Rs), Sha(x.W64)));   // an AMD PC's, from a community recording
        }
        var nv = new Planner(Path.Combine(x.Root, "packs-nv"));
        nv.Build(game, Engine, Index(), new Recording(db), Nvidia, Path.Combine(x.Root, "plan-nv"), null, default);
        Assert.Equal(1, nv.PackPipelines(Middleware.Detect(game).Single()));
        var amd = new Planner(Path.Combine(x.Root, "packs-amd"));
        amd.Build(game, Engine, Index(), new Recording(db), Amd, Path.Combine(x.Root, "plan-amd"), null, default);
        Assert.Equal(2, amd.PackPipelines(Middleware.Detect(game).Single()));
    }

    [Fact]
    public async Task A_pack_body_that_stalls_after_its_headers_is_given_up()
    {
        var x = Make("sp-stall");
        var dll = Middleware.Detect(Install(x, "game", x.Dll)).Single();
        var image = Middleware.Scan(dll.Path);
        var (clock, body) = (new WelcomeTests.ManualClock(), new CommunityTests.Stalled());
        var fake = new CommunityTests.Fake(_ => CommunityTests.StalledBody(body));
        var community = new Community(x.Root, (_, _) => Task.FromResult<string?>(null), new RouteFailover(fake, [new("https://api.test.com/")], clock), clock);
        var entry = new CommunityEntry(HashOnly.PackHash(Key(x, x.Dll)), new string('e', 64), 1000, 1, 1);
        var download = community.DownloadPackAsync(entry, dll, image, new MiddlewarePacks(x.Shared), amd: false);
        await body.Reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(community.BodyIdle);   // the body's idle time, on the community's clock
        Assert.Null(await download.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("didn't answer in time", community.Problem);
        Assert.Null(await community.DownloadPackAsync(entry, dll, image, new MiddlewarePacks(x.Shared), amd: false));   // backing off
        Assert.Single(fake.Log);
    }

    [Fact]
    public void A_shared_pack_replaced_by_one_of_the_same_length_still_re_plans()
    {
        var x = Make("sp-fingerprint");
        var game = GameIn(Install(x, "game", x.Dll), "test:game");
        var dll = Middleware.Detect(game).Single();
        var image = Middleware.Scan(dll.Path);
        var planner = new Planner(x.Local, x.Shared);
        var path = planner.SharedPacks!.PathOf(dll.Vendor, dll.Name, image.ContentHash);
        MiddlewarePacks.FromShared(Records(x, x.A), dll, image, amd: false, new string('1', 64), out _).Write(path);
        var (first, length) = (planner.PackFingerprint(game), new FileInfo(path).Length);
        MiddlewarePacks.FromShared(Records(x, x.B), dll, image, amd: false, new string('2', 64), out _).Write(path);   // A withdrawn, B published
        Assert.Equal(length, new FileInfo(path).Length);
        Assert.NotEqual(first, planner.PackFingerprint(game));
    }

    [Fact]
    public void A_gpu_change_starts_this_pcs_pack_over_and_a_pack_from_before_is_adopted_without_what_this_gpu_doesnt_run()
    {
        var x = Make("sp-gpu");
        var game = GameIn(Install(x, "game", x.Dll), "test:game");
        var dll = Middleware.Detect(game).Single();
        var image = Middleware.Scan(dll.Path);
        string Db(params byte[][] shaders)
        {
            var db = Path.Combine(x.Root, $"rec-{shaders.Length}-{Sha(shaders[0])[..6]}.db");
            using var f = File.Create(db);
            foreach (var blob in shaders.Prepend(x.Rs)) PsoDb.WriteBlob(f, Sha(blob), blob);
            foreach (var s in shaders) PsoDb.Write(f, 'C', PsoDb.Compute(Sha(x.Rs), Sha(s)));
            return db;
        }
        var planner = new Planner(x.Local);
        var path = planner.Packs!.PathOf(dll.Vendor, dll.Name, image.ContentHash);
        MiddlewarePack Pack() => MiddlewarePack.Read(path);

        planner.Build(game, Engine, Index(), new Recording(Db(x.A, x.W64)), Amd, Path.Combine(x.Root, "plan-1"), null, default);
        Assert.Equal(("amd", 2), (Pack().Header.Gpu, Pack().Entries.Count));
        // the same PC with an NVIDIA GPU: the AMD pipelines are of no use here and never shared as NVIDIA's
        planner.Build(game, Engine, Index(), new Recording(Db(x.B)), Nvidia, Path.Combine(x.Root, "plan-2"), null, default);
        Assert.Equal(("nvidia", new PsoDb.Rec('C', PsoDb.Compute(Sha(x.Rs), Sha(x.B))).Key), (Pack().Header.Gpu, Pack().Entries.Single().Key));

        // a pack from before packs kept their GPU: the next promotion here adopts it, without its wave64 PSO
        var legacy = new MiddlewarePack(dll.Vendor, dll.Name, image.ContentHash, image.Size);
        legacy.RootSignatures[Sha(x.Rs)] = x.Rs;
        foreach (var s in new[] { x.A, x.W64 }) legacy.Add(new PsoDb.Rec('C', PsoDb.Compute(Sha(x.Rs), Sha(s))), "test:old");
        legacy.Write(path);
        Assert.Null(Pack().Header.Gpu);
        planner.Build(game, Engine, Index(), new Recording(Db(x.B)), Nvidia, Path.Combine(x.Root, "plan-3"), null, default);
        Assert.Equal("nvidia", Pack().Header.Gpu);
        Assert.Equal(new[] { x.A, x.B }.Select(s => new PsoDb.Rec('C', PsoDb.Compute(Sha(x.Rs), Sha(s))).Key).Order(), Pack().Entries.Select(e => e.Key).Order());
    }

    [Fact]
    public void A_key_file_replaced_with_the_same_size_and_time_is_read_again()
    {
        var x = Make("sp-keyfiles");
        var path = Path.Combine(x.Root, "rec.db");
        void Write(byte[] shader)
        {
            using (var f = File.Create(path)) PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, Sha(shader)));
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }
        static HashSet<string> Read(string p) => [.. PsoDb.Read(p).Select(r => r.Key)];
        Write(x.A);
        var (first, stamp) = (KeyFiles.Keys(path, Read).Single(), (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)));
        Write(x.B);   // another program's write: same size, same time
        Assert.Equal(stamp, (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)));
        Assert.NotEqual(first, KeyFiles.Keys(path, Read).Single());
    }

    [Fact]
    public void The_key_cache_keeps_at_most_its_bound_dropping_the_least_recently_used_and_never_a_set_past_it()
    {
        var x = Make("sp-keybound");
        var bound = KeyFiles.MaxKeys;
        KeyFiles.MaxKeys = 3000;
        try
        {
            var third = Enumerable.Range(0, 1001).Select(i => i.ToString("x40")).ToHashSet();   // three are past the bound
            string File(string name) { var p = Path.Combine(x.Root, name); System.IO.File.WriteAllText(p, name); return p; }
            var (one, two, three, huge) = (File("one"), File("two"), File("three"), File("huge"));
            var reads = 0;
            HashSet<string> Read(string _) { reads++; return third; }
            KeyFiles.Keys(one, Read);
            KeyFiles.Keys(two, Read);
            KeyFiles.Keys(one, Read);     // used again: two is now the least recent
            KeyFiles.Keys(three, Read);   // past the bound: two goes
            reads = 0;
            KeyFiles.Keys(one, Read);
            KeyFiles.Keys(three, Read);
            Assert.Equal(0, reads);
            KeyFiles.Keys(two, Read);
            Assert.Equal(1, reads);

            var past = Enumerable.Range(0, 3001).Select(i => i.ToString("x40")).ToHashSet();
            var hugeReads = 0;
            Assert.Same(past, KeyFiles.Keys(huge, _ => { hugeReads++; return past; }));
            Assert.Same(past, KeyFiles.Keys(huge, _ => { hugeReads++; return past; }));
            Assert.Equal(2, hugeReads);   // read each time, never cached

            // a bound lowered below two cached sets that each fit: a hit evicts the other
            KeyFiles.MaxKeys = 3000;
            var (p, q) = (File("p"), File("q"));
            var thousand = Enumerable.Range(0, 1000).Select(i => i.ToString("x40")).ToHashSet();
            KeyFiles.Keys(p, _ => thousand);
            KeyFiles.Keys(q, _ => thousand);
            KeyFiles.MaxKeys = 1500;
            KeyFiles.Keys(q, _ => thousand);
            Assert.True(KeyFiles.CachedKeys <= 1500);

        }
        finally { KeyFiles.MaxKeys = bound; }
    }

    /// <summary>A library's key files (66 games, 1.99M keys, 251 MB as strings) stay cached at about 30 MB.</summary>
    [Fact]
    public void The_key_cache_holds_a_quarter_million_keys_at_most()
    {
        var x = Make("sp-keycap");
        Assert.Equal(250_000, KeyFiles.MaxKeys);
        var set = Enumerable.Range(0, 60_000).Select(i => i.ToString("x40")).ToHashSet();
        for (var i = 0; i < 10; i++)
        {
            var path = Path.Combine(x.Root, $"keys{i}");
            File.WriteAllText(path, $"keys{i}");
            KeyFiles.Keys(path, _ => set);
            Assert.InRange(KeyFiles.CachedKeys, 0, 250_000);
        }
    }

    [Fact]
    public void The_key_cache_drops_every_set_once_none_was_asked_for_in_its_idle_time()
    {
        var x = Make("sp-keyidle");
        var path = Path.Combine(x.Root, "keys");
        File.WriteAllText(path, "keys");
        var reads = 0;
        HashSet<string> Read(string _) { reads++; return ["a"]; }
        KeyFiles.Keys(path, Read);
        Assert.False(KeyFiles.DropIdle(TimeSpan.FromHours(1)));   // just asked for
        KeyFiles.Keys(path, Read);
        Assert.Equal(1, reads);
        Assert.True(KeyFiles.DropIdle(TimeSpan.Zero));
        Assert.Equal(0, KeyFiles.CachedKeys);
        Assert.False(KeyFiles.DropIdle(TimeSpan.Zero));   // nothing left
        KeyFiles.Keys(path, Read);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void The_app_drops_the_key_sets_a_minute_after_the_last_was_asked_for()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), ScsKiller.KeyCacheIdle);
        const long asked = 5_000_000;   // TickCount64 at the last lookup
        Assert.False(KeyFiles.Idle(asked, asked + 59_999, ScsKiller.KeyCacheIdle));
        Assert.True(KeyFiles.Idle(asked, asked + 60_000, ScsKiller.KeyCacheIdle));
        Assert.True(KeyFiles.Idle(asked, asked + 5 * 60_000, ScsKiller.KeyCacheIdle));
    }

    [Fact]
    public void A_damaged_key_file_reads_as_unknown_and_is_written_again()
    {
        var x = Make("sp-damaged");
        var keys = new[] { new string('1', 40), new string('2', 40) };
        var path = Path.Combine(x.Root, KeyFiles.Write(x.Root, "warm", keys));
        Assert.Equal(keys.Order(), KeyFiles.Set(path)!.Order());
        File.WriteAllBytes(path, File.ReadAllBytes(path)[..20]);   // a key lost: not what its name says
        Assert.Null(KeyFiles.Set(path));
        Assert.Equal(path, Path.Combine(x.Root, KeyFiles.Write(x.Root, "warm", keys)));
        Assert.Equal(keys.Order(), KeyFiles.Set(path)!.Order());
    }

    [Fact]
    public void A_dll_replaced_with_the_same_size_and_time_is_scanned_again()
    {
        var x = Make("sp-rescan");
        var path = Path.Combine(x.Root, "amd_fidelityfx_dx12.dll");
        void Write(byte[] shader)
        {
            File.WriteAllBytes(path, Pe("amd_fidelityfx_dx12.dll", shader));
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }
        Write(x.A);
        Assert.True(Middleware.Scan(path).Containers.ContainsKey(Sha(x.A)));
        Write(x.B);   // the same size and time, another shader
        Assert.True(Middleware.Scan(path).Containers.ContainsKey(Sha(x.B)));
    }

    sealed class CommunityLines : IProgress<string>
    {
        public readonly List<string> All = [];
        public void Report(string value) { lock (All) All.Add(value); }
    }
}
