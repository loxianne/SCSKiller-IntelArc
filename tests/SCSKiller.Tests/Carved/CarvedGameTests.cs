using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Carved;

/// <summary>The carver on this machine's installs (read-only; skipped when absent). Anti-cheat games (Gears, Sea of Thieves):
/// detection and indexing only. GPU part (SCSKILLER_GPU_TESTS=1): Starfield, ~1k plan PSOs warmed under a fake exe name,
/// then ~1k more under the D3D12 debug layer under another.</summary>
[Trait("Needs", "Game")]
public class CarvedGameTests(ITestOutputHelper output)
{
    static Game G(string id, string name, Store store, string dir, string exe) => new(id, name, store, dir, Path.Combine(dir, exe));

    public static readonly Dictionary<string, (Game Game, Readiness Expected)> Games = new()
    {
        ["Starfield"] = (G("xbox:Starfield", "Starfield", Store.Xbox, TestEnv.GameDir(@"Starfield\Content"), "Starfield.exe"), Readiness.Ready),
        ["Cyberpunk"] = (G("other:Cyberpunk 2077", "Cyberpunk 2077", Store.Other, TestEnv.GameDir("Cyberpunk 2077"), @"bin\x64\Cyberpunk2077.exe"), Readiness.NeedsRecording),
        ["Resonance"] = (G("xbox:Resonance", "Resonance: A Plague Tale Legacy", Store.Xbox, TestEnv.GameDir(@"Resonance- A Plague Tale Legacy\Content"), "Resonance.exe"), Readiness.NeedsRecording),
        ["Gears"] = (G("xbox:Gears", "Gears of War: Reloaded", Store.Xbox, TestEnv.GameDir(@"Gears of War- Reloaded\Content"), @"Binaries_x64\GOWDE-WinGDK.exe"), Readiness.NeedsRecording),
        ["SeaOfThieves"] = (G("xbox:SeaOfThieves", "Sea of Thieves", Store.Xbox, TestEnv.GameDir(@"Sea of Thieves\Content"), @"Athena\Binaries\WinGDK\SoTGame.exe"), Readiness.NeedsRecording),
    };

    [Theory]
    [InlineData("Starfield")]
    [InlineData("Cyberpunk")]
    [InlineData("Resonance")]
    [InlineData("Gears")]
    [InlineData("SeaOfThieves")]
    public void DetectsAndIndexesInstalledGame(string key)
    {
        var (game, expected) = Games[key];
        if (!Directory.Exists(game.InstallDir)) return;
        var reader = new CarvedReader();
        var sw = Stopwatch.StartNew();
        var engine = reader.Detect(game, out var notes)!;
        var check = new Planner().Check(game, engine, null, Ff7.Nvidia);
        output.WriteLine($"{game.Name}: detect {sw.Elapsed.TotalSeconds:F1}s: {engine}\n  {notes}\n  anti-cheat {GameFiles.DetectAntiCheat(game)}; check: {check}");
        Assert.Null(engine.Unsupported);
        Assert.Equal(expected, check.Readiness);
        Assert.Contains("D3D12", engine.GraphicsApi);

        var index = reader.Index(game, engine, new Progress<string>(output.WriteLine), CancellationToken.None);
        var rts = index.Shaders.Values.Count(s => s.RootSignature != null);
        output.WriteLine($"  index: {index.Shaders.Count} shaders, {rts} with a root signature, {index.Maps.Count(m => m.IsPipeline)} distinct shipped pipelines "
            + $"({string.Join(", ", index.Maps.Where(m => m.IsPipeline).GroupBy(m => string.Join('+', m.Shaders.Where(index.Shaders.ContainsKey).Select(h => index.Shaders[h].Stage)))
                .OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))}), pools: "
            + string.Join("; ", index.Maps.Where(m => !m.IsPipeline).GroupBy(m => Path.GetExtension(m.Library)).Select(g => $"{g.Count()} x {g.Key} ({g.Sum(m => m.Shaders.Count)} shaders)")));
        Assert.True(index.Shaders.Count >= CarvedReader.MinGraphics);
        if (key == "Starfield") Assert.Contains("D3D12 wave64", index.Platforms); // AMD-only compute twins: rejected on NVIDIA, not planned
        if (expected == Readiness.Ready)
        {
            Assert.True(rts >= 0.9 * index.Shaders.Count);
            var dir = Ff7.TempDir($"carved-{key}");
            sw.Restart();
            var plan = new Planner().Build(game, engine, index, null, Ff7.Nvidia, dir, new Progress<string>(output.WriteLine), CancellationToken.None);
            output.WriteLine($"  plan: {plan.Stats} ({sw.Elapsed.TotalSeconds:F1}s, {new FileInfo(plan.FilePath).Length / 1024} KiB)");
            var planned = PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag is 'P' or 'S')
                .SelectMany(r => r.Tag == 'P' ? PsoDb.ParseItem(r.Payload).Stages.Values : PsoDb.Parse(r).Stages.Values).ToHashSet();
            output.WriteLine("  shaders in no planned PSO: " + string.Join(", ", index.Shaders.Values.Where(s => !planned.Contains(s.Sha1))
                .GroupBy(s => s.Stage).Select(g => $"{g.Count()} {g.Key}")));
            Assert.True(plan.Stats.Generated + plan.Stats.D3D11Shaders > 0);
        }
    }

    /// <summary>Detect on every Steam install (the bounded generic scan on games nobody wrote a hint for): what it finds, what
    /// it costs. Manual: SCSKILLER_CARVER_SWEEP=1. FF7 Rebirth is never touched (a played install).</summary>
    [Fact]
    public void SweepSteamLibraries()
    {
        if (Environment.GetEnvironmentVariable("SCSKILLER_CARVER_SWEEP") != "1") return;
        string[] skip = ["FINAL FANTASY VII REBIRTH", "Steamworks Shared", "SteamVR", "Lossless Scaling", "3DMark", "Steam Controller Configs", "Valheim dedicated server"];
        var dirs = TestEnv.SteamCommon
            .SelectMany(Directory.EnumerateDirectories).Where(d => !skip.Contains(Path.GetFileName(d))).Order();
        foreach (var dir in dirs)
        {
            var game = new Game("sweep:" + Path.GetFileName(dir), Path.GetFileName(dir), Store.Steam, dir, GameFiles.FindExe(dir) ?? Path.Combine(dir, "game.exe"));
            var sw = Stopwatch.StartNew();
            var e = new CarvedReader().Detect(game, out var notes)!;
            output.WriteLine($"{game.Name} ({sw.Elapsed.TotalSeconds:F1}s): {e.Version} {e.GraphicsApi} -> {new Planner().Check(game, e, null, Ff7.Nvidia).Reason} | {notes}");
        }
    }

    static readonly string Warm = Path.Combine(Ff7.ProxyBin, "scskiller_warm.exe");
    static readonly string Selftest = Path.Combine(Ff7.ProxyBin, "selftest.exe");

    /// <summary>Recording-free Starfield: a sample of its plan (every k-th item, ~1k PSOs, synthesized templates, the game's
    /// own root signatures) materialized from the install, warmed under a fake exe name (0 rejected), and another sample
    /// replayed under the D3D12 debug layer.</summary>
    [Fact]
    public void StarfieldSampleWarmsClean()
    {
        var (game, _) = Games["Starfield"];
        if (!Directory.Exists(game.InstallDir)) return;
        var reader = new CarvedReader();
        var engine = reader.Detect(game)!;
        var index = reader.Index(game, engine, null, CancellationToken.None);
        var dir = Ff7.TempDir("carved-starfield-gpu");
        var planner = new Planner();
        var full = planner.Build(game, engine, index, null, Ff7.Nvidia, dir, null, CancellationToken.None);
        var works = new[] { 0, 1 }.Select(part => Sample(planner, full, game, engine, reader, Path.Combine(dir, $"sample{part}"), part)).ToList();

        if (Environment.GetEnvironmentVariable("SCSKILLER_GPU_TESTS") != "1" || !File.Exists(Warm) || !File.Exists(Selftest)) return;
        var id = Random.Shared.Next(100000, 999999);
        using var gpu = GpuLock();
        var (done, failed, seconds) = Run(works[0], $"scsk_carved_{id}.exe");
        output.WriteLine($"warm: {done} done, {failed} rejected in {seconds:F1}s");
        var (ok, rejected, log) = DebugWarm(works[1], $"scsk_carveddbg_{id}.exe");
        output.WriteLine($"debug layer: {ok} ok, {rejected} rejected\n{log}");
        Assert.Equal(0, failed);
        Assert.Equal(0, rejected);
    }

    /// <summary>Every 2k-th item + <paramref name="part"/> of the plan (disjoint samples), the templates they use, materialized.</summary>
    string Sample(Planner planner, Plan full, Game game, EngineInfo engine, IEngineReader reader, string dir, int part)
    {
        var body = PlanFile.Read(full.FilePath).Records.ToList();
        var all = body.Where(r => r.Tag == 'P').ToList();
        var step = Math.Max(2, all.Count / 1000);
        var items = all.Where((_, i) => i % step == part).ToList();
        var used = items.Select(i => PsoDb.ParseItem(i.Payload).Template).ToHashSet();
        var templates = body.Where(r => r.Tag is 'S' or 'G' or 'C' && used.Contains(r.Key)).ToList();
        var plan = full with { FilePath = Path.Combine(dir, "sample.bin") };
        PlanFile.Write(plan, templates.Concat(items));
        var work = Path.Combine(dir, "work");
        planner.Materialize(plan, game, engine, reader, null, work, CancellationToken.None);
        Ff7.CheckWarmReady(work);
        output.WriteLine($"sample {part}: {templates.Count} templates + {items.Count} items");
        return work;
    }

    // ARCHITECTURE.md protocol: scskiller_warm <workdir> <exe name> --threads N; stdout ends with the "done" event
    static (long Done, long Failed, double Seconds) Run(string folder, string exe)
    {
        var psi = new ProcessStartInfo(Warm, [folder, exe, "--threads", "4"]) { StandardOutputEncoding = System.Text.Encoding.UTF8, RedirectStandardOutput = true, UseShellExecute = false };
        string o;
        using (var p = Process.Start(psi)!) { o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
        var done = o.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('{')).Select(l => JsonDocument.Parse(l).RootElement)
            .LastOrDefault(e => e.GetProperty("event").GetString() == "done");
        Assert.True(done.ValueKind == JsonValueKind.Object, o);
        return (done.GetProperty("done").GetInt64(), done.GetProperty("failed").GetInt64(), done.GetProperty("seconds").GetDouble());
    }

    /// <summary>selftest.exe renamed to <paramref name="exe"/> next to the proxy in <paramref name="work"/>: replays it with
    /// the D3D12 debug layer on and prints the runtime's errors.</summary>
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
