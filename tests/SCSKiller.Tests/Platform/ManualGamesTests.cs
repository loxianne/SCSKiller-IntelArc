using System.Buffers.Binary;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;

namespace SCSKiller.Tests.Platform;

public sealed class ManualGamesTests : IDisposable
{
    readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "scskiller-manual-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose() => Directory.Delete(_root, true);

    /// <summary>A minimal x64 exe; <paramref name="imports"/>: the one DLL its import table names (a graphics API makes it the game).</summary>
    internal static byte[] Exe(string? imports = null, int padding = 0)
    {
        var pe = Planning.MiddlewarePackTests.Pe(null);
        if (imports != null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x58 + 120), 0x1100);          // import directory RVA
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x200 + 0x100 + 12), 0x1180);  // its one descriptor's Name RVA
            System.Text.Encoding.ASCII.GetBytes(imports).CopyTo(pe, 0x200 + 0x180);
        }
        return [.. pe, .. new byte[padding]];
    }

    /// <summary>&lt;root&gt;\Game.exe (Unreal's launcher stub), Game\Binaries\Win64\Game-Win64-Shipping.exe, Engine\.</summary>
    internal static (string Root, string Stub, string Shipping) UnrealLayout(string root)
    {
        var bin = Directory.CreateDirectory(Path.Combine(root, "Game", "Binaries", "Win64")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "Engine", "Binaries", "Win64"));
        File.WriteAllBytes(Path.Combine(root, "Engine", "Binaries", "Win64", "CrashReportClient.exe"), Exe(padding: 20_000));
        var shipping = Path.Combine(bin, "Game-Win64-Shipping.exe");
        File.WriteAllBytes(shipping, Exe(padding: 8192));
        var stub = Path.Combine(root, "Game.exe");
        File.WriteAllBytes(stub, Exe());
        return (root, stub, shipping);
    }

    [Fact]
    public void An_unreal_launcher_stub_resolves_to_its_shipping_exe_and_install_root()
    {
        var (root, stub, shipping) = UnrealLayout(Path.Combine(_root, "Some Game"));
        Assert.Equal(new ManualEntry(shipping, root, "Some Game"), ManualSource.Resolve(stub));
        Assert.Equal(new ManualEntry(shipping, root, "Some Game"), ManualSource.Resolve(shipping));   // the root from <Project>\Binaries\Win64
        Assert.Equal(ManualSource.IdOf(shipping), ManualSource.IdOf(shipping.ToUpperInvariant()));
    }

    [Fact]
    public void A_launcher_resolves_to_the_one_exe_that_loads_d3d_and_several_are_ambiguous()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Plain")).FullName;
        var launcher = Path.Combine(dir, "Launcher.exe");
        File.WriteAllBytes(launcher, Exe(padding: 50_000));   // the largest, importing nothing graphic
        File.WriteAllBytes(Path.Combine(dir, "game_dx12.exe"), Exe("d3d12.dll"));
        Assert.Equal(Path.Combine(dir, "game_dx12.exe"), ManualSource.Resolve(launcher).Exe);
        Assert.Equal(Path.Combine(dir, "game_dx12.exe"), ManualSource.Resolve(Path.Combine(dir, "game_dx12.exe")).Exe);   // the game itself: kept

        File.WriteAllBytes(Path.Combine(dir, "game_dx11.exe"), Exe("d3d11.dll"));
        var e = Assert.Throws<ArgumentException>(() => ManualSource.Resolve(launcher));
        Assert.Contains("can't tell which", e.Message);
        Assert.Contains("game_dx11.exe", e.Message);
    }

    /// <summary>Returnal: a modular Unreal build whose Shipping exe imports its RHI module, not a graphics API, beside a
    /// 32-bit Launcher.exe that does. The pick is the game.</summary>
    [Fact]
    public void A_modular_unreal_shipping_exe_is_kept_over_a_32_bit_launcher_beside_it()
    {
        var (root, stub, shipping) = UnrealLayout(Path.Combine(_root, "Modular"));
        File.WriteAllBytes(shipping, Exe("Game-RHI-Win64-Shipping.dll", padding: 8192));
        var launcher = Exe("d3d11.dll");
        BinaryPrimitives.WriteUInt16LittleEndian(launcher.AsSpan(BinaryPrimitives.ReadInt32LittleEndian(launcher.AsSpan(0x3C)) + 4), 0x14C);   // i386
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(shipping)!, "Launcher.exe"), launcher);
        Assert.Equal(new ManualEntry(shipping, root, "Modular"), ManualSource.Resolve(shipping));
        Assert.Equal(shipping, ManualSource.Resolve(stub).Exe);
        File.WriteAllBytes(shipping, Exe(padding: 8192));   // one whose imports name nothing graphic (a packed exe): still not the launcher
        Assert.Equal(shipping, ManualSource.Resolve(shipping).Exe);
    }

    /// <summary>Returnal's Launcher.exe is 32-bit and larger than its Shipping exe: discovery and the launcher stub still find
    /// the game, also with anti-cheat in the install, where no exe is read and the Shipping name decides.</summary>
    [Fact]
    public void A_larger_32_bit_launcher_beside_the_shipping_exe_is_never_the_game()
    {
        var (root, stub, shipping) = UnrealLayout(Path.Combine(_root, "Returnal"));
        var launcher = Exe("d3d11.dll", padding: 50_000);
        BinaryPrimitives.WriteUInt16LittleEndian(launcher.AsSpan(BinaryPrimitives.ReadInt32LittleEndian(launcher.AsSpan(0x3C)) + 4), 0x14C);   // i386
        var launcherPath = Path.Combine(Path.GetDirectoryName(shipping)!, "Launcher.exe");
        File.WriteAllBytes(launcherPath, launcher);
        Assert.Equal(shipping, GameFiles.FindExe(root));
        Assert.Equal(new ManualEntry(shipping, root, "Returnal"), ManualSource.Resolve(stub));
        Directory.CreateDirectory(Path.Combine(root, "EasyAntiCheat"));
        Assert.Equal(shipping, GameFiles.FindExe(root));
    }

    /// <summary>Returnal from Steam: the 64-bit Epic Online Services installer is the largest exe in Binaries\Win64.</summary>
    [Fact]
    public void Installers_and_engine_helpers_beside_an_unreal_exe_are_never_the_game()
    {
        var (root, stub, shipping) = UnrealLayout(Path.Combine(_root, "Returnal"));
        var bin = Path.GetDirectoryName(shipping)!;
        foreach (var helper in new[] { "EpicOnlineServicesInstaller.exe", "CrashReportClient.exe", "UnrealCEFSubProcess.exe",
                     "EasyAntiCheat_EOS_Setup.exe", "vc_redist.x64.exe", "dxsetup.exe", "UE4PrereqSetup_x64.exe" })
            File.WriteAllBytes(Path.Combine(bin, helper), Exe("d3d11.dll", padding: 50_000));
        Assert.Equal(shipping, GameFiles.FindExe(root));
        Assert.Equal(shipping, ManualSource.Resolve(stub).Exe);

        var game = Path.Combine(bin, "Game.exe");   // a game exe not named for Shipping: the largest that is no helper
        File.Move(shipping, game);
        Assert.Equal(game, GameFiles.FindExe(root));
    }

    [Fact]
    public void A_suggested_folder_sees_anti_cheat_in_the_folders_above_it_and_a_confirmed_folder_is_checked_whole()
    {
        var game = Directory.CreateDirectory(Path.Combine(_root, "Shooter")).FullName;
        Directory.CreateDirectory(Path.Combine(game, "EasyAntiCheat"));
        var retail = Directory.CreateDirectory(Path.Combine(game, "bin64", "retail")).FullName;
        var exe = Path.Combine(retail, "shooter.exe");
        File.WriteAllBytes(exe, Exe("d3d12.dll"));
        var e = ManualSource.Resolve(exe);
        Assert.Equal((exe, retail), (e.Exe, e.InstallDir));   // a suggestion: the user confirms or changes it
        Assert.Equal(AntiCheat.EasyAntiCheat, GameFiles.DetectAntiCheat(ManualSource.ToGame(e)));   // in the folder above
        Assert.Equal(AntiCheat.EasyAntiCheat, GameFiles.DetectAntiCheat(ManualSource.ToGame(e with { InstallDir = game })));
        Assert.Null(ManualSource.RootProblem(game, exe, []));
    }

    [Fact]
    public void A_folder_of_many_games_or_a_drive_is_never_a_game_folder()
    {
        string Dir(params string[] parts) => Directory.CreateDirectory(Path.Combine([_root, .. parts])).FullName;
        var exe = Path.Combine(_root, "x.exe");
        Assert.Contains("whole drive", ManualSource.RootProblem(Path.GetPathRoot(_root)!, exe, []));
        Assert.Contains("doesn't exist", ManualSource.RootProblem(Path.Combine(_root, "gone"), exe, []));
        Assert.Contains("many programs", ManualSource.RootProblem(Dir("SteamLibrary", "steamapps", "common"), exe, []));
        Assert.Contains("many programs", ManualSource.RootProblem(Dir("XboxGames"), exe, []));
        Assert.Contains("many programs", ManualSource.RootProblem(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), exe, []));
        var library = Dir("Library");
        foreach (var g in new[] { "One", "Two" }) File.WriteAllBytes(Path.Combine(Dir("Library", g), g + ".exe"), Exe());
        Assert.Contains("several games", ManualSource.RootProblem(library, exe, []));
        var listed = new Game("steam:1", "Listed Game", Store.Steam, Dir("Shelf", "Listed"), Path.Combine(_root, "Shelf", "Listed", "l.exe"));
        Assert.Contains("Listed Game", ManualSource.RootProblem(Path.Combine(_root, "Shelf"), exe, [listed]));

        var (unreal, _, shipping) = UnrealLayout(Path.Combine(_root, "Unreal Game"));
        File.WriteAllBytes(Path.Combine(Dir("Unreal Game", "Engine", "Binaries", "ThirdParty"), "helper.exe"), Exe());
        Assert.Null(ManualSource.RootProblem(unreal, shipping, [listed]));
        var red = Dir("Red Game");
        File.WriteAllBytes(Path.Combine(Dir("Red Game", "bin", "x64"), "game.exe"), Exe("d3d12.dll"));
        File.WriteAllBytes(Path.Combine(Dir("Red Game", "_CommonRedist"), "vcredist.exe"), Exe());
        Dir("Red Game", "content");
        Assert.Null(ManualSource.RootProblem(red, Path.Combine(red, "bin", "x64", "game.exe"), []));
        Assert.Equal(red, ManualSource.Resolve(Path.Combine(red, "bin", "x64", "game.exe")).InstallDir);
    }

    [Fact]
    public void Picks_that_are_no_64_bit_program_are_refused()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Bad")).FullName;
        string Put(string name, byte[] bytes) { var p = Path.Combine(dir, name); File.WriteAllBytes(p, bytes); return p; }
        Assert.Contains("isn't a program", Assert.Throws<ArgumentException>(() => ManualSource.Resolve(Put("game.lnk", Exe()))).Message);
        Assert.Contains("isn't a Windows program", Assert.Throws<ArgumentException>(() => ManualSource.Resolve(Put("text.exe", "hello"u8.ToArray()))).Message);
        Assert.Contains("doesn't exist", Assert.Throws<ArgumentException>(() => ManualSource.Resolve(Path.Combine(dir, "gone.exe"))).Message);
        var wow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "cmd.exe");
        Assert.Contains("32-bit", Assert.Throws<ArgumentException>(() => ManualSource.Resolve(Put("old.exe", File.ReadAllBytes(wow)))).Message);
    }

    [Fact]
    public void The_exe_a_pick_resolves_to_is_checked_too()
    {
        var (_, stub, shipping) = UnrealLayout(Path.Combine(_root, "Broken"));
        File.WriteAllBytes(shipping, new byte[50_000]);   // still the largest exe under Binaries\Win64
        Assert.Contains("Game-Win64-Shipping.exe isn't a Windows program", Assert.Throws<ArgumentException>(() => ManualSource.Resolve(stub)).Message);
    }

    [Fact]
    public void Malformed_entries_are_left_out_and_the_rest_kept()
    {
        var (root, _, shipping) = UnrealLayout(Path.Combine(_root, "Some Game"));
        var data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        var good = System.Text.Json.JsonSerializer.Serialize(new ManualEntry(shipping, root, "Some Game"));
        File.WriteAllText(Path.Combine(data, "manual-games.json"),
            """[null, 7, "x", {}, {"Exe": "relative.exe", "InstallDir": "dir"}, {"Exe": "C:/a\u0000b.exe", "InstallDir": "C:/a"}, """ + good + "]");
        var source = new ManualSource(new AppStore(data));
        Assert.Equal(shipping, Assert.Single(source.Discover()).ExePath);

        var other = UnrealLayout(Path.Combine(_root, "Other Game"));
        Assert.False(source.Add(new ManualEntry(other.Shipping, other.Root, "Other Game")).Existed);
        Assert.Equal(2, new ManualSource(new AppStore(data)).Discover().Count);
    }

    [Fact]
    public void Adds_from_two_sources_at_once_lose_nothing()
    {
        var data = Path.Combine(_root, "data");
        ManualSource a = new(new AppStore(data)), b = new(new AppStore(data));
        Parallel.For(0, 40, i => (i % 2 == 0 ? a : b).Add(new ManualEntry(Path.Combine(_root, $"g{i}", "g.exe"), Path.Combine(_root, $"g{i}"), $"g{i}")));
        Assert.Equal(40, a.Entries().Count);
        Parallel.For(0, 40, i => Assert.True((i % 2 == 0 ? b : a).Remove(ManualSource.IdOf(Path.Combine(_root, $"g{i}", "g.exe")))));
        Assert.Empty(b.Entries());
    }

    [Fact]
    public void Added_games_persist_in_the_data_folder_and_are_removed_by_id()
    {
        var (root, _, shipping) = UnrealLayout(Path.Combine(_root, "Some Game"));
        var data = Path.Combine(_root, "data");
        var entry = new ManualEntry(shipping, root, "Some Game");
        var (added, existed) = new ManualSource(new AppStore(data)).Add(entry);
        Assert.False(existed);
        Assert.True(File.Exists(Path.Combine(data, "manual-games.json")));

        var source = new ManualSource(new AppStore(data));   // as the next start reads it
        var g = Assert.Single(source.Discover());
        Assert.Equal(new Game(added.Id, "Some Game", Store.Manual, root, shipping), g);
        Assert.StartsWith("manual:", g.Id);
        Assert.True(source.Add(entry).Existed);

        File.Move(shipping, shipping + ".away");   // a drive unplugged: not listed, still kept
        Assert.Empty(source.Discover());
        File.Move(shipping + ".away", shipping);
        Assert.Single(source.Discover());

        Assert.True(source.Remove(g.Id));
        Assert.Empty(new ManualSource(new AppStore(data)).Discover());
        Assert.False(source.Remove(g.Id));
        Assert.True(File.Exists(shipping));
    }
}
