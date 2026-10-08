using System.Diagnostics;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Tests.Planning;

/// <summary>Which shader kinds the installed games have and what the planner makes of them (read-only; FF7 Rebirth's
/// install is never read: its numbers come from the other FF7 tests' index). Manual: SCSKILLER_COVER_SURVEY=&lt;dir&gt;
/// (plans and survey.log go there; SCSKILLER_COVER_SURVEY_GAMES=a;b limits it to names containing a or b). Per game: its
/// shaders by stage, then NVIDIA's per-stage plan (with the game's recording from %LOCALAPPDATA%\SCSKiller\games when there
/// is one): PSOs by stage set, depth-only VS+GS, AS stage sets, ray tracing collections ('Y'), uncovered stage sets.</summary>
[Trait("Needs", "Game")]
public class CoverAllSurvey(Xunit.Abstractions.ITestOutputHelper output)
{
    static readonly VendorCaps Nvidia = new("nvidia-1", true, true, true, PerStageCache: true, RtCacheGranularity: RtCacheGranularity.Collection); // NvidiaBackend's

    sealed class L(Action<string> a) : IProgress<string> { public void Report(string v) => a(v); }

    [Fact]
    public void Survey()
    {
        var root = Environment.GetEnvironmentVariable("SCSKILLER_COVER_SURVEY");
        if (root == null) return;
        var only = Environment.GetEnvironmentVariable("SCSKILLER_COVER_SURVEY_GAMES")?.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var logFile = Path.Combine(root, "survey.log");
        void Log(string s) { output.WriteLine(s); File.AppendAllText(logFile, s + Environment.NewLine); }
        var reader = new EngineReaders(("Unreal", new UnrealReader(Path.Combine(root, "data"))), (CarvedReader.Family, new CarvedReader()));
        var games = new IGameSource[] { new SteamSource(), new EpicSource(), new XboxSource(), new GogSource(), new UbisoftSource(), new BattleNetSource(), new EaSource() }
            .SelectMany(s => { try { return s.Discover(); } catch (Exception) { return []; } })
            .Where(g => !g.InstallDir.Contains("FINAL FANTASY VII REBIRTH", StringComparison.OrdinalIgnoreCase))
            .Where(g => only == null || only.Any(o => g.Name.Contains(o, StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var g in games)
        {
            EngineInfo? e;
            try { e = reader.Detect(g); } catch (Exception) { continue; }
            if (e == null || e.Unsupported != null || e.Encrypted || !e.GraphicsApi.Contains("D3D12")) continue;
            ShaderIndex index;
            try { index = reader.Index(g, e, null, CancellationToken.None); }
            catch (Exception ex) { Log($"{g.Name}: index failed: {ex.Message}"); continue; }
            var db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCSKiller", "games", g.Id.Replace(':', '_'), "recording.db");
            SurveyOne(g, e, index, File.Exists(db) ? new Recording(db) : null, Path.Combine(root, string.Concat(g.Id.Split(Path.GetInvalidFileNameChars()))), Log);
            GC.Collect();
        }
        if (Ff7.HasIndex) SurveyOne(Ff7.Game, new("Unreal", "4.26", "GAME_FinalFantasy7Rebirth", "D3D12", false, null), Ff7.Index(), new Recording(Ff7.RecordingDb), Path.Combine(root, "ff7"), Log);
    }

    static void SurveyOne(Game g, EngineInfo e, ShaderIndex index, Recording? rec, string dir, Action<string> log)
    {
        var sw = Stopwatch.StartNew();
        var byStage = index.Shaders.Values.GroupBy(s => s.Stage).ToDictionary(x => x.Key, x => x.Count());
        var planLog = new List<string>();
        var plan = new Planner().Build(g, e, index, rec, Nvidia, dir, new L(planLog.Add), CancellationToken.None);
        var body = PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag is 'S' or 'P' or 'Y').ToList();
        var shapes = body.Where(r => r.Tag != 'Y').Select(r => r.Tag == 'P' ? PsoDb.ParseItem(r.Payload).Stages : PsoDb.Parse(r).Stages)
            .GroupBy(s => string.Join('+', s.Keys.Select(k => ((Stage)k).ToString()[..2]))).OrderByDescending(x => x.Count()).Select(x => $"{x.Key} {x.Count()}");
        int N(Stage s) => byStage.GetValueOrDefault(s);
        log($"{g.Name} ({e.Family} {e.Version}{(rec != null ? ", recording" : "")}): {index.Shaders.Count} shaders, AS {N(Stage.Amplification)}, MS {N(Stage.Mesh)}, GS {N(Stage.Geometry)}, "
            + $"HS {N(Stage.Hull)}, DS {N(Stage.Domain)}, DXIL libraries {N(Stage.Library)} -> plan {plan.Stats.Generated} ({string.Join(", ", shapes)}; ray tracing collections {body.Count(r => r.Tag == 'Y')}), "
            + $"uncovered {plan.Stats.Uncovered} ({sw.Elapsed.TotalSeconds:F0}s)");
        foreach (var l in planLog.Where(l => l.StartsWith("ray tracing") || l.StartsWith("plan:"))) log("  " + l);
    }
}
