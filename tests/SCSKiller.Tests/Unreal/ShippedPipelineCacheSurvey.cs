using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using SCSKiller.Tests.Planning;

namespace SCSKiller.Tests.Unreal;

/// <summary>What the installed Unreal games' shipped pipeline caches add to NVIDIA's plan (read-only). Manual:
/// SCSKILLER_PIPELINE_CACHE_SURVEY=&lt;dir&gt; (indexes, plans and survey.log go there), SCSKILLER_PIPELINE_CACHE_GAMES=a;b
/// limits it to names containing a or b. Per game: the shipped graphics PSOs, and how many of their stage units (shader +
/// the root signature the engine builds for the PSO) the plan has without and with them.</summary>
[Trait("Needs", "Game")]
public class ShippedPipelineCacheSurvey(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Survey()
    {
        var root = Environment.GetEnvironmentVariable("SCSKILLER_PIPELINE_CACHE_SURVEY");
        if (root == null) return;
        var only = Environment.GetEnvironmentVariable("SCSKILLER_PIPELINE_CACHE_GAMES")?.Split(';', StringSplitOptions.RemoveEmptyEntries);
        void Log(string s) { output.WriteLine(s); File.AppendAllText(Path.Combine(root, "survey.log"), s + Environment.NewLine); }
        var reader = new UnrealReader(Path.Combine(root, "data"));
        foreach (var g in new SteamSource().Discover().Concat(new EpicSource().Discover()).Concat(new XboxSource().Discover())
                     .Where(g => !g.InstallDir.Contains("FINAL FANTASY VII REBIRTH", StringComparison.OrdinalIgnoreCase))
                     .Where(g => only == null || only.Any(o => g.Name.Contains(o, StringComparison.OrdinalIgnoreCase))))
        {
            EngineInfo? e;
            try { e = reader.Detect(g); } catch (Exception) { continue; }
            if (e is not { Unsupported: null, Encrypted: false } || !e.GraphicsApi.Contains("D3D12") || RootSig.RuleFor(e) is not { } rule) continue;
            var index = reader.Index(g, e, new Progress(l => { if (l.Contains("upipelinecache")) Log($"  {l}"); }), CancellationToken.None);
            var shipped = index.Maps.Where(m => m.IsPipeline).ToList();
            if (shipped.Count == 0) continue;
            var maxSrvs = RootSig.MaxSrvsFor(rule, index.Shaders.Values);
            var samplers = RootSig.StaticSamplers(rule);
            HashSet<string> Units(ShaderIndex ix, string name)
            {
                var plan = new Planner().Build(g, e, ix, null, Ff7.Nvidia, Path.Combine(root, string.Concat(g.Id.Split(Path.GetInvalidFileNameChars())), name), null, CancellationToken.None);
                return [.. PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag is 'S' or 'P')
                    .Select(r => r.Tag == 'P' ? (PsoDb.ParseItem(r.Payload).Rs, PsoDb.ParseItem(r.Payload).Stages) : (PsoDb.Parse(r).Rs, PsoDb.Parse(r).Stages))
                    .SelectMany(p => p.Stages.Values.Select(h => $"{h}|{p.Rs}"))];
            }
            var without = Units(index with { Maps = [.. index.Maps.Where(m => !m.IsPipeline)] }, "without");
            var with = Units(index, "with");
            var need = shipped.SelectMany(m =>
            {
                var st = new SortedDictionary<Stage, ShaderInfo>(m.Shaders.Select(h => index.Shaders[h]).ToDictionary(s => s.Stage));
                var rs = Convert.ToHexStringLower(SHA1.HashData(RootSig.Serialize(RootSig.Build(rule, st, m.Platform.StartsWith("PCD3D_SM6"), maxSrvs), samplers)));
                return m.Shaders.Select(h => $"{h}|{rs}");
            }).ToHashSet();
            Log($"{g.Name} (Unreal {e.Version}): {shipped.Count} shipped graphics PSOs, {need.Count} stage units: {need.Count(without.Contains)} in the plan without them, "
                + $"{need.Count(with.Contains)} with them; plan units {without.Count} -> {with.Count}");
            Assert.Equal(need.Count, need.Count(with.Contains));
        }
    }

    sealed class Progress(Action<string> a) : IProgress<string> { public void Report(string v) => a(v); }
}
