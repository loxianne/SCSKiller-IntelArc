using System.Text.Json;
using SCSKiller.Core;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Tests.Planning;

/// <summary>Real-game test data on the dev machines (gitignored, read-only). The old tool's dump (out\ff7rebirth: jsonl
/// index, bytecode, recording.db) is on the NVIDIA machine only; elsewhere the index comes from indexing the local install
/// with <see cref="UnrealReader"/> (~18 s, ~1.9 GB peak, once per test run), shader bytes from the install and the recording
/// from the rehydrated copy (<see cref="RehydratedDb"/>, the same 994 PSOs). Tests return early when neither is there.</summary>
static class Ff7
{
    /// <summary>The main checkout (<see cref="TestEnv.MainCheckout"/>), where the gitignored out\ dump lives.</summary>
    public static readonly string Main = TestEnv.MainCheckout;
    public static readonly string Out = Path.Combine(Main, @"out\ff7rebirth");
    /// <summary>This checkout's native build (proxy\build\Release under the folder with SCSKiller.slnx above the test
    /// binaries), not <see cref="Main"/>'s: from a worktree that is another checkout's build.</summary>
    public static readonly string ProxyBin = Path.Combine(TestEnv.RepoRoot, "proxy", "build", "Release");

    static readonly string DumpRecordingDb = Path.Combine(Out, "recording.db");
    const string Folder = "FINAL FANTASY VII REBIRTH";
    static string ExeOf(string install) => Path.Combine(install, @"End\Binaries\Win64\ff7rebirth_.exe");
    /// <summary>The first install found. A played install: read only, never launched.</summary>
    public static readonly string Install = TestEnv.GameRoots.Select(r => Path.Combine(r, Folder)).FirstOrDefault(d => File.Exists(ExeOf(d))) ?? TestEnv.GameDir(Folder);
    public static readonly Game Game = new("steam:2909400", "FINAL FANTASY VII REBIRTH", Store.Steam, Install, ExeOf(Install));
    public static readonly VendorCaps Nvidia = new("nvidia-1", true, true, true, RtCacheGranularity: RtCacheGranularity.Collection);
    /// <summary>AMD's caps (AMD probes, <c>AmdBackend</c>).</summary>
    public static readonly VendorCaps Amd = new("amd-1", true, false, false, PerStageCache: true);

    /// <summary>A rehydrated recording (994 PSO records + every shader and root signature as 'B' blobs): enough for
    /// state/layout tests without an index or an install. Tests return early when it is absent.</summary>
    public static readonly string RehydratedDb = Path.Combine(TestEnv.DevDir, "ff7", "ff7rebirth-recording.db");

    /// <summary>A 5.5-minute FF7 Rebirth session recorded on AMD (OptiScaler + FSR4), with blobs.</summary>
    public static readonly string AmdSessionDb = Path.Combine(Path.GetDirectoryName(RehydratedDb)!, "amd-session1.db");

    /// <summary>The old tool's dump (the Python reference's own input) is on this machine.</summary>
    public static bool HasDump => File.Exists(Path.Combine(Out, "bytecode.jsonl")) && File.Exists(DumpRecordingDb);
    public static bool HasInstall => File.Exists(Game.ExePath);
    /// <summary>An FF7 index and the NVIDIA machine's recording: the dump's, or the install's + the rehydrated recording.</summary>
    public static bool HasIndex => HasDump || (HasInstall && File.Exists(RehydratedDb));
    /// <summary>The NVIDIA machine's FF7 recording: the dump's, else the rehydrated copy.</summary>
    public static string RecordingDb => HasDump ? DumpRecordingDb : RehydratedDb;

    static ShaderIndex? index, gsIndex;
    static readonly Lock gate = new();
    static (UnrealReader Reader, EngineInfo Engine, ShaderIndex Index)? installed;
    static string? jsonlDir;

    /// <summary>The install indexed with <see cref="UnrealReader"/>, once per test run, with its detected engine.</summary>
    public static (UnrealReader Reader, EngineInfo Engine, ShaderIndex Index) Installed()
    {
        lock (gate)
        {
            if (installed is { } i) return i;
            var reader = new UnrealReader(TempDir("ff7-install-data"));
            var engine = reader.Detect(Game) ?? throw new InvalidOperationException($"{Install}: not detected");
            installed = (reader, engine, reader.Index(Game, engine, null, CancellationToken.None));
            return installed.Value;
        }
    }

    /// <summary>The FF7 index as the old index dumper wrote it (what the Python reference was run on): the dump's, else
    /// the install's without what the old tool didn't dump (geometry shaders' input primitive; see <see cref="GsIndex"/>).</summary>
    public static ShaderIndex Index()
    {
        if (!HasDump)
        {
            var full = Installed().Index;
            lock (gate)
                return index ??= full with
                {
                    Shaders = full.Shaders.ToDictionary(s => s.Key, s => s.Value.Stage != Stage.Geometry ? s.Value : s.Value with { GsInputPrimitive = 0 }),
                };
        }
        lock (gate) return index ??= LoadJsonl(Out);
    }

    /// <summary><see cref="Index"/> with geometry shaders' input primitive: the old tool didn't dump it (parsed from the
    /// bytecode dump like the reader does); the install's index has it.</summary>
    public static ShaderIndex GsIndex()
    {
        if (!HasDump) return Installed().Index;
        var idx = Index();
        lock (gate)
            return gsIndex ??= idx with
            {
                Shaders = idx.Shaders.ToDictionary(s => s.Key, s => s.Value.Stage != Stage.Geometry ? s.Value : s.Value with
                {
                    GsInputPrimitive = ShaderContainer.Parse(File.ReadAllBytes(Path.Combine(Out, "bytecode", s.Key + ".bin")), s.Key, s.Value.Counts)!.GsInputPrimitive,
                }),
            };
    }

    /// <summary>Shader bytes by SHA-1: the bytecode dump, else the install.</summary>
    public static IEngineReader Shaders() => HasDump ? new BytecodeDir(Path.Combine(Out, "bytecode")) : new InstallShaders();

    /// <summary>A folder with bytecode.jsonl + shaders.jsonl for the Python reference: the dump, else <see cref="Index"/> written in
    /// the old tool's format (no bytecode\: the reference is run without writing blobs).</summary>
    public static string JsonlDir()
    {
        if (HasDump) return Out;
        var idx = Installed().Index;
        lock (gate)
        {
            if (jsonlDir != null) return jsonlDir;
            var dir = TempDir("ff7-jsonl");
            static object Sigs(IReadOnlyList<SigElement> l) => l.Select(e => new { semantic = e.Semantic, index = e.Index, register = e.Register, mask = e.Mask, sys_value = e.SysValue, comp_type = e.CompType });
            using (var w = new StreamWriter(Path.Combine(dir, "bytecode.jsonl")))
                foreach (var s in idx.Shaders.Values)
                    w.WriteLine(JsonSerializer.Serialize(new
                    {
                        sha1 = s.Sha1, frequency = Frequency(s.Stage), shader_model = s.ShaderModel, size = s.Size,
                        ue_counts = new { cb = s.Counts.Cb, srv = s.Counts.Srv, uav = s.Counts.Uav, sampler = s.Counts.Sampler },
                        bindings = s.Bindings.Select(b => new { @class = b.Class, space = b.Space, lower = b.Lower, count = b.Count }),
                        inputs = Sigs(s.Inputs), outputs = Sigs(s.Outputs),
                    }));
            using (var w = new StreamWriter(Path.Combine(dir, "shaders.jsonl")))
                foreach (var m in idx.Maps)
                    foreach (var h in m.Shaders)
                        w.WriteLine(JsonSerializer.Serialize(new { shadermap_hash = m.Hash, library = m.Library, platform = m.Platform, sha1 = h }));
            return jsonlDir = dir;
        }
    }

    static string Frequency(Stage s) => s switch
    {
        Stage.Vertex => "vertex", Stage.Pixel => "pixel", Stage.Domain => "domain", Stage.Hull => "hull", Stage.Geometry => "geometry",
        Stage.Compute => "compute", Stage.Amplification => "amplification", Stage.Mesh => "mesh", _ => "library",
    };

    public static ShaderIndex LoadJsonl(string dir)
    {
        static List<SigElement> Sigs(JsonElement e, string k) => e.TryGetProperty(k, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(s => new SigElement(s.GetProperty("semantic").GetString()!, s.GetProperty("index").GetInt32(), s.GetProperty("register").GetInt32(),
                s.GetProperty("mask").GetByte(), s.GetProperty("sys_value").GetInt32(), s.GetProperty("comp_type").GetInt32())).ToList() : [];
        var shaders = new Dictionary<string, ShaderInfo>();
        foreach (var line in File.ReadLines(Path.Combine(dir, "bytecode.jsonl")))
        {
            var e = JsonDocument.Parse(line).RootElement;
            Stage? stage = e.GetProperty("frequency").GetString() switch
            {
                "vertex" => Stage.Vertex, "pixel" => Stage.Pixel, "domain" => Stage.Domain, "hull" => Stage.Hull, "geometry" => Stage.Geometry,
                "compute" => Stage.Compute, "amplification" => Stage.Amplification, "mesh" => Stage.Mesh, "library" => Stage.Library, _ => null,
            };
            if (stage == null) continue;
            var c = e.TryGetProperty("ue_counts", out var uc) && uc.ValueKind == JsonValueKind.Object
                ? new ResourceCounts(uc.GetProperty("cb").GetInt32(), uc.GetProperty("srv").GetInt32(), uc.GetProperty("uav").GetInt32(), uc.GetProperty("sampler").GetInt32())
                : new ResourceCounts(0, 0, 0, 0);
            var bindings = e.TryGetProperty("bindings", out var b) && b.ValueKind == JsonValueKind.Array
                ? b.EnumerateArray().Select(x => new Binding(x.GetProperty("class").GetString()!, x.GetProperty("space").GetInt32(), x.GetProperty("lower").GetInt32(), x.GetProperty("count").GetInt32())).ToList()
                : [];
            var sha = e.GetProperty("sha1").GetString()!;
            shaders[sha] = new ShaderInfo(sha, stage.Value, e.GetProperty("shader_model").GetString()!, e.GetProperty("size").GetInt32(), c, bindings, Sigs(e, "inputs"), Sigs(e, "outputs"));
        }
        var maps = new List<ShaderMap>();
        var platforms = new List<string>();
        foreach (var g in File.ReadLines(Path.Combine(dir, "shaders.jsonl")).Select(l => JsonDocument.Parse(l).RootElement)
                     .GroupBy(e => (Hash: e.GetProperty("shadermap_hash").GetString()!, Lib: e.GetProperty("library").GetString()!, Plat: e.GetProperty("platform").GetString()!)))
        {
            maps.Add(new ShaderMap(g.Key.Hash, g.Key.Lib, g.Key.Plat, g.Select(e => e.GetProperty("sha1").GetString()!).ToList()));
            if (!platforms.Contains(g.Key.Plat)) platforms.Add(g.Key.Plat);
        }
        return new ShaderIndex("jsonl", platforms, shaders, maps);
    }

    /// <summary>(root sig, stage -> sha1) tuples of a plan's generated PSOs: 'P' items and templates that aren't recorded ones.</summary>
    public static HashSet<string> PlanTuples(string planPath)
    {
        var recorded = PsoDb.Read(RecordingDb).Where(r => r.Tag != 'B').Select(r => r.Key).ToHashSet();
        var set = new HashSet<string>();
        foreach (var r in PlanFile.Read(planPath).Records)
            if (r.Tag == 'P') { var (_, rs, st) = PsoDb.ParseItem(r.Payload); set.Add(PsoDb.Tuple(rs, st)); }
            else if (r.Tag is 'G' or 'C' or 'S' && !recorded.Contains(r.Key)) set.Add(PsoDb.Parse(r).Tuple);
        return set;
    }

    /// <summary>A copy of the recording cut after its first <paramref name="n"/> PSO records (blobs precede their records).</summary>
    public static string Truncated(int n, string path)
    {
        using var dst = File.Create(path);
        foreach (var r in PsoDb.Read(RecordingDb))
        {
            if (r.Tag != 'B' && n-- == 0) break;
            PsoDb.Write(dst, r.Tag, r.Payload);
        }
        return path;
    }

    /// <summary>Every item's template, shaders and root signature are in the work folder; templates parse canonically.</summary>
    public static void CheckWarmReady(string workDir)
    {
        var recs = PsoDb.Read(Path.Combine(workDir, "scskiller.db")).Concat(PsoDb.Read(Path.Combine(workDir, "scskiller_gen.db"))).ToList();
        var blobs = recs.Where(r => r.Tag == 'B').Select(r => PsoDb.Hex(r.Payload.AsSpan(0, 20))).ToHashSet();
        var templates = recs.Where(r => r.Tag is 'G' or 'C' or 'S').ToDictionary(r => r.Key, PsoDb.Parse);
        foreach (var t in templates.Values) Assert.True(blobs.Contains(t.Rs) && t.Stages.Values.All(blobs.Contains));
        foreach (var (tmpl, rs, st) in recs.Where(r => r.Tag == 'P').Select(r => PsoDb.ParseItem(r.Payload)))
            Assert.True(templates.ContainsKey(tmpl) && blobs.Contains(rs) && st.Values.All(blobs.Contains));
        var gen = PsoDb.Read(Path.Combine(workDir, "scskiller_gen.db")).ToList(); // D3D11 items: the blob is in gen.db itself
        var genBlobs = gen.Where(r => r.Tag == 'B').Select(r => PsoDb.Hex(r.Payload.AsSpan(0, 20))).ToHashSet();
        foreach (var r in gen.Where(r => r.Tag == '1')) Assert.True(r.Payload.Length == 24 && genBlobs.Contains(PsoDb.Hex(r.Payload.AsSpan(4, 20))));
        foreach (var r in gen.Where(r => r.Tag == '2')) Assert.True(r.Payload.Length == 40 && Rehydrate.References([r]).IsSubsetOf(genBlobs)); // HS + DS
    }

    /// <summary>SCSKiller's recording of <paramref name="game"/> on this machine as the app plans from it: the shaders the
    /// install ships read back into a copy in a temp folder. Null without one. The app's data folder is
    /// SCSKILLER_TEST_APP_DATA's when set (a copy, so a test never reads a live install's), else the app's own.</summary>
    public static string? Recording(Game game, EngineInfo engine, IEngineReader reader, string name)
    {
        var data = Environment.GetEnvironmentVariable("SCSKILLER_TEST_APP_DATA") is { Length: > 0 } d ? d : SCSKiller.Core.App.AppStore.DefaultDir;
        var store = Path.Combine(new SCSKiller.Core.App.AppStore(data).GameDir(game.Id), "recording.db");
        if (!File.Exists(store)) return null;
        var db = Path.Combine(TempDir(name), "recording.db");
        Rehydrate.Run(store, db, game, engine, reader, moreBlobs: h => Middleware.Blobs(game, h));
        return db;
    }

    /// <summary>An empty folder of this test process: two runs on one PC at once don't take each other's, and the folders
    /// of runs that ended go.</summary>
    public static string TempDir(string name)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "scskiller-tests")).FullName;
        foreach (var d in Directory.GetDirectories(root, name + ".*"))
            if (Path.GetExtension(d) is { Length: > 1 } ext && int.TryParse(ext.AsSpan(1), out var pid) && (pid == Environment.ProcessId || !Running(pid)))
                try { Directory.Delete(d, true); }
                catch (Exception e) when (pid != Environment.ProcessId && e is IOException or UnauthorizedAccessException) { }   // another run is deleting it too
        return Directory.CreateDirectory(Path.Combine(root, $"{name}.{Environment.ProcessId}")).FullName;

        static bool Running(int pid)
        {
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
            catch (ArgumentException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return true; }   // a pid reused by a process this user can't open: leave the folder
        }
    }
}

/// <summary>Serves shaders from the local install through the shared <see cref="Ff7.Installed"/> reader, whatever engine the
/// caller passes (tests plan with a hand-made 4.26 EngineInfo).</summary>
sealed class InstallShaders : IEngineReader
{
    public EngineInfo? Detect(Game game) => Ff7.Installed().Engine;
    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => Ff7.Installed().Index;
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        var (reader, e, _) = Ff7.Installed();
        reader.ReadShaders(Ff7.Game, e, sha1s, sink, ct);
    }
}

/// <summary>Serves shaders from the old tool's bytecode dump instead of the game install.</summary>
sealed class BytecodeDir(string dir) : IEngineReader
{
    public EngineInfo? Detect(Game game) => null;
    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => throw new NotSupportedException();
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        foreach (var h in sha1s.Where(h => File.Exists(Path.Combine(dir, h + ".bin")))) sink(h, File.ReadAllBytes(Path.Combine(dir, h + ".bin")));
    }
}
