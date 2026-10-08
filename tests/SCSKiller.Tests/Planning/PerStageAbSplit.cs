using SCSKiller.Core;
using SCSKiller.Core.Planning;
using Xunit.Abstractions;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Tests.Planning;

/// <summary>The GPU A/B's input: FF7's recorded PSOs split, in file order, into a per-stage cover
/// (each PSO that brings a unit not seen yet, under <see cref="UnitPolicy.Nvidia"/> or <see cref="UnitPolicy.Amd"/>) and
/// the rest (every unit already covered). With a per-stage cache, warming the cover alone makes the rest cache hits.
/// Writes &lt;dir&gt;\cover.keys and rest.keys (record keys) when SCSKILLER_AB_OUT=&lt;dir&gt; [SCSKILLER_AB_POLICY=amd].</summary>
[Trait("Needs", "Game")]
public class PerStageAbSplit(ITestOutputHelper output)
{
    [Fact]
    public void Split()
    {
        if (Environment.GetEnvironmentVariable("SCSKILLER_AB_OUT") is not { } dir || !File.Exists(Ff7.RecordingDb)) return;
        var policy = Environment.GetEnvironmentVariable("SCSKILLER_AB_POLICY") == "amd" ? UnitPolicy.Amd : UnitPolicy.Nvidia;
        var x = ExactLayouts.FromDb(Ff7.RecordingDb, policy);
        var covered = new HashSet<Unit>();
        var (cover, rest) = (new List<string>(), new List<string>());
        foreach (var r in Read(Ff7.RecordingDb).Where(r => r.Tag is 'G' or 'S' or 'C'))
        {
            var units = r.Tag == 'C' ? [new Unit(Stage.Compute, Parse(r).Stages[(int)Stage.Compute], x.RsKey(Parse(r).Rs))]
                : UnitCover.UnitsOf(x, ParseState(r)!).ToList();
            (units.All(covered.Contains) ? rest : cover).Add(r.Key);
            covered.UnionWith(units);
        }
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "cover.keys"), cover);
        File.WriteAllLines(Path.Combine(dir, "rest.keys"), rest);
        output.WriteLine($"{policy.Name}: {cover.Count} cover + {rest.Count} rest, {covered.Count} units");
    }
}
