using System.Diagnostics;
using System.Text.RegularExpressions;
using SCSKiller.Core;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Planning;

/// <summary>The claim recording-free mode rests on: a PSO compiled from a synthesized neutral-state template fills the
/// driver cache for the game's real desc with the same (shaders + root signature). Takes FF7's recorded tuples, warms
/// them from a recording-free plan (A), then replays the real recorded descs under the same fake exe name (B) and under
/// a fresh one (control). B several times faster than the control = synthesized templates hit.
/// The GPU part runs only with SCSKILLER_GPU_TESTS=1 (it adds ~2k PSOs to the real driver cache under fake names).</summary>
[Trait("Needs", "Game")]
public class SynthesizedTemplateTests(ITestOutputHelper output)
{
    static readonly string Warm = Path.Combine(Ff7.ProxyBin, "scskiller_warm.exe");

    [Fact]
    public void SynthesizedTemplatesHitTheDriverCache()
    {
        if (!Ff7.HasIndex || !Ff7.HasInstall) return;
        var dir = Ff7.TempDir("synth");
        var (reader, engine, index) = Ff7.Installed(); // indexed once per run, shared with the planner tests
        var planner = new Planner();
        var full = planner.Build(Ff7.Game, engine, index, null, Ff7.Nvidia, Path.Combine(dir, "full"), null, CancellationToken.None);

        // the recorded tuples, and the part of the recording-free plan that covers them
        var recs = PsoDb.Read(Ff7.RecordingDb).ToList();
        var recorded = recs.Where(r => r.Tag != 'B').Select(r => (r, PsoDb.Parse(r).Tuple)).Where(x => PsoDb.Parse(x.r).Stages.Values.All(index.Shaders.ContainsKey)).ToList();
        var want = recorded.Select(x => x.Tuple).ToHashSet();
        var body = PlanFile.Read(full.FilePath).Records.ToList();
        var items = body.Where(r => r.Tag == 'P').Where(r => { var (_, rs, st) = PsoDb.ParseItem(r.Payload); return want.Contains(PsoDb.Tuple(rs, st)); }).ToList();
        var usedTemplates = items.Select(i => PsoDb.ParseItem(i.Payload).Template).ToHashSet();
        var templates = body.Where(r => r.Tag == 'S' && (usedTemplates.Contains(r.Key) || want.Contains(PsoDb.Parse(r).Tuple))).ToList();
        var covered = items.Select(i => PsoDb.ParseItem(i.Payload)).Select(i => PsoDb.Tuple(i.Rs, i.Stages)).Concat(templates.Select(t => PsoDb.Parse(t).Tuple)).ToHashSet();
        covered.IntersectWith(want);
        var usedRs = items.Select(i => PsoDb.ParseItem(i.Payload).Rs).Concat(templates.Select(t => PsoDb.Parse(t).Rs)).ToHashSet();
        var plan = full with { FilePath = Path.Combine(dir, "subset.bin") };
        PlanFile.Write(plan, body.Where(r => r.Tag == 'B' && usedRs.Contains(PsoDb.Hex(r.Payload.AsSpan(0, 20)))).Concat(templates).Concat(items));
        output.WriteLine($"recorded tuples {want.Count}, covered by the recording-free plan {covered.Count}; subset plan: {templates.Count} synthesized templates + {items.Count} items");

        // A: synthesized templates only (empty scskiller.db)
        var dirA = Path.Combine(dir, "a");
        planner.Materialize(plan, Ff7.Game, engine, reader, null, dirA, CancellationToken.None);
        Ff7.CheckWarmReady(dirA);
        // B and control: the real recorded descs of the covered tuples, one per tuple (a repeat would hit even when cold)
        var real = recs.Where(r => r.Tag == 'B').Concat(recorded.Where(x => covered.Contains(x.Tuple)).DistinctBy(x => x.Tuple).Select(x => x.r)).ToList();
        foreach (var d in new[] { "b", "c" })
        {
            Directory.CreateDirectory(Path.Combine(dir, d));
            using var f = File.Create(Path.Combine(dir, d, "scskiller.db"));
            foreach (var r in real) PsoDb.Write(f, r.Tag, r.Payload);
        }

        if (Environment.GetEnvironmentVariable("SCSKILLER_GPU_TESTS") != "1" || !File.Exists(Warm)) return;
        var id = Random.Shared.Next(100000, 999999);
        using var gpu = GpuLock();
        var a = Run(dirA, $"scsk_synth_{id}.exe");
        var b = Run(Path.Combine(dir, "b"), $"scsk_synth_{id}.exe");
        var c = Run(Path.Combine(dir, "c"), $"scsk_ctrl_{id}.exe");
        output.WriteLine($"A synthesized (cold): {a}\nB real descs, same name: {b}\ncontrol, fresh name: {c}");
        Assert.Equal(0, a.Failed);
        Assert.True(b.Rate > 3 * c.Rate, "real descs did not hit the cache filled from synthesized templates");
    }

    static readonly string Selftest = Path.Combine(Ff7.ProxyBin, "selftest.exe");

    /// <summary>GS PSOs replayed under the D3D12 debug layer (selftest debugwarm). Before (GS input unknown: every chain
    /// on each recorded GS template, all triangle in FF7) the point-GS chains are rejected; after (recording-free, the
    /// GS's input topology) none is. Same sample of chains both times: every point-GS chain plus every 8th other one.
    /// GPU part: SCSKILLER_GPU_TESTS=1, ~1.3k PSOs under two fake exe names.</summary>
    [Fact]
    public void GsPsosPassTheDebugLayer()
    {
        if (!Ff7.HasIndex) return;
        var dir = Ff7.TempDir("gsdebug");
        var planner = new Planner();
        var ue = new EngineInfo("Unreal", "4.26", "GAME_FinalFantasy7Rebirth", "D3D12", false, null);
        var bc = Ff7.GsIndex().Shaders;
        var plans = new[]
        {
            ("before", planner.Build(Ff7.Game, ue, Ff7.Index(), new Recording(Ff7.RecordingDb), new VendorCaps("test", true, false, false), Path.Combine(dir, "before"), null, CancellationToken.None)),
            ("after", planner.Build(Ff7.Game, ue, Ff7.GsIndex(), null, Ff7.Nvidia, Path.Combine(dir, "after"), null, CancellationToken.None)),
        };
        var keepSet = PlanFile.Read(plans[1].Item2.FilePath).Records.Where(r => r.Tag is 'P' or 'S').Select(Stages)
            .Where(st => st.ContainsKey((int)Stage.Geometry)).Select(st => (st, key: PsoDb.Tuple("", st))).DistinctBy(x => x.key).OrderBy(x => x.key, StringComparer.Ordinal)
            .Where((x, i) => bc[x.st[(int)Stage.Geometry]].GsInputPrimitive == 1 || i % 8 == 0).Select(x => x.key).ToHashSet();
        var runs = plans.Select(p => (Name: p.Item1, Work: Sample(p.Item2, keepSet, Path.Combine(dir, p.Item1)))).ToList();

        if (Environment.GetEnvironmentVariable("SCSKILLER_GPU_TESTS") != "1" || !File.Exists(Selftest)) return;
        var id = Random.Shared.Next(100000, 999999);
        using var gpu = GpuLock();
        var results = runs.ToDictionary(r => r.Name, r => DebugWarm(r.Work, $"scsk_gs{r.Name}_{id}.exe"));
        foreach (var (name, (ok, failed, log)) in results) output.WriteLine($"{name}: {ok} ok, {failed} rejected\n{log}");
        Assert.True(results["before"].Failed > 0);
        Assert.Equal(0, results["after"].Failed);
    }

    /// <summary>VS and MS PSOs without a pixel shader whose shader writes attributes (depth passes) pass the debug layer
    /// on synthesized templates. Every 4th of them, recording-free.
    /// GPU part: SCSKILLER_GPU_TESTS=1, ~1.4k PSOs under a fake exe name.</summary>
    [Fact]
    public void SourceOnlyPsosPassTheDebugLayer()
    {
        if (!Ff7.HasIndex) return;
        var dir = Ff7.TempDir("srcdebug");
        var ue = new EngineInfo("Unreal", "4.26", "GAME_FinalFantasy7Rebirth", "D3D12", false, null);
        var bc = Ff7.GsIndex().Shaders;
        var full = new Planner().Build(Ff7.Game, ue, Ff7.GsIndex(), null, Ff7.Nvidia, dir, null, CancellationToken.None);
        var keepSet = PlanFile.Read(full.FilePath).Records.Where(r => r.Tag is 'P' or 'S').Select(Stages)
            .Where(st => st.Count == 1 && st.Keys.Single() is (int)Stage.Vertex or (int)Stage.Mesh && bc[st.Values.Single()].Outputs.Any(o => o.SysValue == 0))
            .Select(st => PsoDb.Tuple("", st)).Distinct().Order(StringComparer.Ordinal).Where((_, i) => i % 4 == 0).ToHashSet();
        var work = Sample(full, keepSet, Path.Combine(dir, "sample"));
        if (Environment.GetEnvironmentVariable("SCSKILLER_GPU_TESTS") != "1" || !File.Exists(Selftest)) return;
        using var gpu = GpuLock();
        var (ok, failed, log) = DebugWarm(work, $"scsk_src_{Random.Shared.Next(100000, 999999)}.exe");
        output.WriteLine($"{ok} ok, {failed} rejected\n{log}");
        Assert.Equal(0, failed);
    }

    static SortedDictionary<int, string> Stages(PsoDb.Rec r) => r.Tag == 'P' ? PsoDb.ParseItem(r.Payload).Stages : PsoDb.Parse(r).Stages;

    /// <summary>A warm-ready work folder (dir\work) with the plan's items and templates whose stage set is in <paramref name="keep"/>
    /// (plus the templates those items use), shaders from the FF7 bytecode dump.</summary>
    string Sample(Plan full, HashSet<string> keep, string dir)
    {
        var body = PlanFile.Read(full.FilePath).Records.ToList();
        var items = body.Where(r => r.Tag == 'P' && keep.Contains(PsoDb.Tuple("", PsoDb.ParseItem(r.Payload).Stages))).ToList();
        var used = items.Select(i => PsoDb.ParseItem(i.Payload).Template).ToHashSet();
        var templates = body.Where(r => r.Tag is 'G' or 'S' && (used.Contains(r.Key) || keep.Contains(PsoDb.Tuple("", PsoDb.Parse(r).Stages)))).ToList();
        var rs = items.Select(i => PsoDb.ParseItem(i.Payload).Rs).Concat(templates.Select(t => PsoDb.Parse(t).Rs)).ToHashSet();
        var plan = full with { FilePath = Path.Combine(dir, "sample.bin") };
        PlanFile.Write(plan, body.Where(r => r.Tag == 'B' && rs.Contains(PsoDb.Hex(r.Payload.AsSpan(0, 20)))).Concat(templates).Concat(items));
        var work = Path.Combine(dir, "work");
        new Planner().Materialize(plan, Ff7.Game, new EngineInfo("Unreal", "4.26", "GAME_FinalFantasy7Rebirth", "D3D12", false, null),
            Ff7.Shaders(), null, work, CancellationToken.None);
        Ff7.CheckWarmReady(work);
        output.WriteLine($"{Path.GetFileName(dir)}: {templates.Count} templates + {items.Count} items for {keep.Count} sampled stage sets");
        return work;
    }

    /// <summary>selftest.exe renamed to <paramref name="exe"/> in <paramref name="work"/> next to the proxy: replays the work
    /// folder with the D3D12 debug layer on and prints the runtime's errors.</summary>
    static (long Ok, long Failed, string Log) DebugWarm(string work, string exe)
    {
        File.Copy(Selftest, Path.Combine(work, exe), true);
        File.Copy(Path.Combine(Path.GetDirectoryName(Selftest)!, "d3d12.dll"), Path.Combine(work, "d3d12.dll"), true);
        var psi = new ProcessStartInfo(Path.Combine(work, exe), ["debugwarm", "1"]) { RedirectStandardOutput = true, UseShellExecute = false };
        psi.Environment["SCSKILLER_THREADS"] = "4";
        psi.Environment["SCSKILLER_SELFTEST_UNARMED"] = "1";   // a warm host: the proxy admits it unarmed
        string o;
        using (var p = Process.Start(psi)!) { o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
        var m = Regex.Match(o, @"replayed ok=(\d+) fail=(\d+)");
        Assert.True(m.Success, o);
        return (long.Parse(m.Groups[1].Value), long.Parse(m.Groups[2].Value), o);
    }

    sealed record WarmRun(long Ok, long Failed, double Seconds)
    {
        public double Rate => (Ok + Failed) / Math.Max(Seconds, 0.05); // the log has 0.1 s resolution
        public override string ToString() => $"{Ok} ok, {Failed} failed in {Seconds:F1} s = {Rate:F0} PSO/s";
    }

    // ARCHITECTURE.md protocol: scskiller_warm <workdir> <exe name> --threads N; stdout ends with the "done" event
    static WarmRun Run(string folder, string exe)
    {
        var psi = new ProcessStartInfo(Warm, [folder, exe, "--threads", "4"]) { StandardOutputEncoding = System.Text.Encoding.UTF8, RedirectStandardOutput = true, UseShellExecute = false }; // light: the PC may be in use
        string o;
        using (var p = Process.Start(psi)!) { o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
        var done = o.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('{')).Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
            .LastOrDefault(e => e.GetProperty("event").GetString() == "done");
        Assert.True(done.ValueKind == System.Text.Json.JsonValueKind.Object, o);
        var failed = done.GetProperty("failed").GetInt64();
        return new WarmRun(done.GetProperty("done").GetInt64() - failed, failed, done.GetProperty("seconds").GetDouble());
    }

    /// <summary>One heavy GPU run at a time on this machine: <see cref="TestEnv.GpuLockPath"/>.</summary>
    static IDisposable GpuLock()
    {
        var path = TestEnv.GpuLockPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (var deadline = DateTime.UtcNow.AddMinutes(30); ; Thread.Sleep(5000))
        {
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromMinutes(30)) File.Delete(path);
            try { return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (DateTime.UtcNow < deadline) { }
        }
    }
}
