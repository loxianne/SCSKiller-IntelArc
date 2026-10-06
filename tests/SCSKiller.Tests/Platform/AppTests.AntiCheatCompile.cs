using System.Reflection;
using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;

namespace SCSKiller.Tests.Platform;

// An anti-cheat game is compiled from any plan source it has; only the recorder stays out.
public partial class AppTests
{
    ScsKiller AntiCheatGame(IGameSource[]? sources = null)
    {
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));
        return Killer(new FakeReader(Unreal), new UpdatedPlanner(recorded: true), sources: sources);
    }

    static void WriteRecording(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var shader = SCSKiller.Tests.Planning.MiddlewarePackTests.Container("DXIL", "a recorded shader");
        using var f = File.Create(path);
        PsoDb.WriteBlob(f, CommunityTests.Sha1(shader), shader);
        PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, CommunityTests.Sha1(shader)));
    }

    [Fact]
    public async Task An_anti_cheat_game_with_a_recording_here_compiles_and_keeps_it()
    {
        var k = AntiCheatGame();
        var recording = Path.Combine(k.Store.GameDir(_game.Id), "recording.db");
        WriteRecording(recording);
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal((AntiCheat.EasyAntiCheat, GameStatus.Ready, ScsKiller.SkipAntiCheat), (s.AntiCheat, s.Status, s.RecorderSkip));
        Assert.True(File.Exists(recording));   // the anti-cheat verdict never takes the recording
        await WarmOnce(k, _game.Id);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        await k.RecordingMigration.WaitAsync(TimeSpan.FromSeconds(10));   // a recording stored as a proxy db is converted
        Assert.True(File.Exists(recording));
    }

    [Fact]
    public async Task An_anti_cheat_game_with_a_community_recording_compiles()
    {
        var k = AntiCheatGame();
        var dir = k.Store.GameDir(_game.Id);
        WriteRecording(Path.Combine(dir, "community.db"));
        File.WriteAllBytes(Path.Combine(dir, "community.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new CommunityDownload(new string('d', 64), new string('c', 40), 1, DateTimeOffset.UtcNow)));
        Assert.Equal(GameStatus.Ready, (await k.ScanAsync(default)).Single().Status);
        await WarmOnce(k, _game.Id);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
    }

    [Fact]
    public async Task An_anti_cheat_game_with_no_plan_source_is_unsupported()
    {
        var s = (await AntiCheatGame().ScanAsync(default)).Single();
        Assert.Equal((GameStatus.Unsupported, "needs a recording, which EasyAntiCheat blocks"), (s.Status, s.StatusReason));
    }

    /// <summary>A game added by hand and recorded, then listed by a store (a launcher SCSKiller finds since) that flags its
    /// anti-cheat: the store's entry plans from that recording.</summary>
    [Fact]
    public async Task A_recording_made_on_a_game_added_by_hand_follows_it_to_the_store_entry()
    {
        var data = Path.Combine(_root, "data");
        var manual = new ManualSource(new AppStore(data));
        var added = manual.Add(new ManualEntry(_game.ExePath, _game.InstallDir, "Fake Game", Confirmed: true)).Game;
        var k = AntiCheatGame([new FakeSource([_game]), manual]);
        var from = Path.Combine(k.Store.GameDir(added.Id), "recording.db");
        WriteRecording(from);
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal((_game.Id, GameStatus.Ready), (s.Game.Id, s.Status));
        Assert.False(File.Exists(from));
        Assert.Single(PsoDb.Read(Path.Combine(k.Store.GameDir(_game.Id), "recording.db")), r => r.Tag == 'C');
        await WarmOnce(k, _game.Id);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
    }

    /// <summary>A game added by hand twice, by two exes in two folders of one install, each recorded: once a store entry
    /// with anti-cheat takes the game over, every file of ours leaves both folders, each copy's recorder by its own record,
    /// and both recordings plan the store entry.</summary>
    [Fact]
    public async Task Recorders_installed_on_games_added_by_hand_leave_every_folder_when_an_anti_cheat_store_entry_takes_them_over()
    {
        var manual = new ManualSource(new AppStore(Path.Combine(_root, "data")));
        var other = Path.Combine(_game.InstallDir, "Fake.exe");   // the launcher stub, in the install root
        var k = AntiCheatGame([new FakeSource([_game]), manual]);
        k.ManageRecorders = true;
        var shader = SCSKiller.Tests.Planning.MiddlewarePackTests.Container("DXIL", "a shader recorded in the second folder");
        foreach (var (exe, n) in new[] { (_game.ExePath, 0), (other, 1) })
        {
            var added = manual.Add(new ManualEntry(exe, _game.InstallDir, $"Fake Game {n}", Confirmed: true)).Game;
            var dir = Path.GetDirectoryName(exe)!;
            File.Copy(_proxy, Path.Combine(dir, "d3d12.dll"));
            File.WriteAllText(Path.Combine(dir, "scskiller.ini"), $"[scskiller] mode=record {n}");
            foreach (var f in new[] { Recordings.KeysFile, ScsKiller.ArmedFile, "scskiller_creates.csv", "scskiller.log" }) File.WriteAllText(Path.Combine(dir, f), "x");
            if (n == 0) WriteRecording(Path.Combine(dir, "scskiller.db"));   // not imported yet
            else
                using (var f = File.Create(Path.Combine(dir, "scskiller.db")))
                {
                    PsoDb.WriteBlob(f, CommunityTests.Sha1(shader), shader);
                    PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, CommunityTests.Sha1(shader)));
                }
            var was = k.Store.LoadGame(added.Id);
            foreach (var f in new[] { "d3d12.dll", "scskiller.ini" }) was.RecorderFiles[f] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, f))));
            (was.RecorderExe, was.RecorderInstallDir) = (exe, _game.InstallDir);
            k.Store.SaveGame(added.Id, was);
        }

        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal((AntiCheat.EasyAntiCheat, GameStatus.Ready), (s.AntiCheat, s.Status));   // from the recordings the folders held
        Assert.DoesNotContain(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Where(f => !f.StartsWith(Path.Combine(_root, "data")) && !f.StartsWith(Path.Combine(_root, "tools"))),
            f => Path.GetFileName(f).StartsWith("scskiller", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f) == "d3d12.dll");
        Assert.Equal(2, PsoDb.Read(Path.Combine(k.Store.GameDir(_game.Id), "recording.db")).Count(r => r.Tag == 'C'));
        foreach (var exe in new[] { _game.ExePath, other })
            Assert.Equal((0, (string?)null), (k.Store.LoadGame(ManualSource.IdOf(exe)).RecorderFiles.Count, k.Store.LoadGame(ManualSource.IdOf(exe)).RecorderExe));
        Assert.Empty(k.Store.LoadGame(_game.Id).RecorderFiles);   // never merged across folders
    }

    /// <summary>VALORANT added by hand from its exe, with no Vanguard file in its folders and a recorder already in: the
    /// scan finds it anti-cheat, takes every file of ours out and won't put the recorder back.</summary>
    [Fact]
    public async Task A_recorder_in_a_riot_game_added_by_hand_comes_out_and_stays_out()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Riot Games", "VALORANT", "live", "ShooterGame", "Binaries", "Win64")).FullName;
        var exe = Path.Combine(dir, "VALORANT-Win64-Shipping.exe");
        File.WriteAllBytes(exe, new byte[4096]);
        var manual = new ManualSource(new AppStore(Path.Combine(_root, "data")));
        var added = manual.Add(new ManualEntry(exe, dir, "VALORANT", Confirmed: true)).Game;
        var k = Killer(new FakeReader(Unreal), new UpdatedPlanner(recorded: true), sources: [manual]);
        k.ManageRecorders = true;
        File.Copy(_proxy, Path.Combine(dir, "d3d12.dll"));
        File.WriteAllText(Path.Combine(dir, "scskiller.ini"), "[scskiller] mode=record");
        foreach (var f in new[] { Recordings.KeysFile, ScsKiller.ArmedFile, "scskiller_creates.csv", "scskiller.log" }) File.WriteAllText(Path.Combine(dir, f), "x");
        WriteRecording(Path.Combine(dir, "scskiller.db"));
        var was = k.Store.LoadGame(added.Id);
        foreach (var f in new[] { "d3d12.dll", "scskiller.ini" }) was.RecorderFiles[f] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, f))));
        (was.RecorderExe, was.RecorderInstallDir) = (exe, dir);
        k.Store.SaveGame(added.Id, was);

        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal((AntiCheat.Other, ScsKiller.SkipAntiCheat), (s.AntiCheat, s.RecorderSkip));
        Assert.DoesNotContain(Directory.EnumerateFiles(dir), f => Path.GetFileName(f).StartsWith("scskiller", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f) == "d3d12.dll");
        Assert.Empty(k.Store.LoadGame(added.Id).RecorderFiles);
        Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(added.Id));
        Assert.False(File.Exists(Path.Combine(dir, "d3d12.dll")));
    }

    [Fact]
    public void The_scan_cache_is_keyed_on_the_exact_build()
    {
        // a release's informational version has no commit: two builds of one version still differ
        Assert.NotEqual(ScsKiller.BuildOf("1.2.1", "aaaa"), ScsKiller.BuildOf("1.2.1", "bbbb"));
        Assert.Equal("1.2.1+aaaa", ScsKiller.BuildOf("1.2.1", "aaaa"));
        Assert.Equal("0.0.0-internal.0+aaaa", ScsKiller.BuildOf("0.0.0-internal.0+aaaa", "aaaa"));
        var commit = typeof(ScsKiller).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(m => m.Key == "SourceRevisionId").Value;
        Assert.EndsWith("+" + commit, ScsKiller.CoreBuild);
    }
}
