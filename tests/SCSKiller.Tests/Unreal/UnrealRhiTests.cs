using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Unreal;

public class UnrealRhiTests(ITestOutputHelper output)
{
    static readonly string[] Sm5 = ["PCD3D_SM5"], Sm6 = ["PCD3D_SM6"], Both = ["PCD3D_SM5", "PCD3D_SM6"];

    static Dictionary<string, string> Config(string defaultEngine, string? windowsEngine = null)
    {
        var d = new Dictionary<string, string> { ["Game/Config/DefaultEngine.ini"] = defaultEngine };
        if (windowsEngine != null) d["Game/Config/Windows/WindowsEngine.ini"] = windowsEngine;
        return d;
    }

    static string Rhi(string value) => $"[/Script/WindowsTargetPlatform.WindowsTargetSettings]\nDefaultGraphicsRHI={value}\n";

    /// <summary>A user Saved folder with Config\Windows\GameUserSettings.ini (and optionally a log).</summary>
    static string UserDir(string gameUserSettings, string? log = null)
    {
        var d = Ff7.TempDir("rhi-user-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(d, "Config", "Windows"));
        File.WriteAllText(Path.Combine(d, "Config", "Windows", "GameUserSettings.ini"), gameUserSettings);
        if (log != null)
        {
            Directory.CreateDirectory(Path.Combine(d, "Logs"));
            File.WriteAllText(Path.Combine(d, "Logs", "Game.log"), log);
        }
        return d;
    }

    static string Api(int ue, string[] platforms, Dictionary<string, string> config, string? user = null, string launch = "") =>
        UnrealRhi.Resolve(ue, platforms, config, "Game", user, launch).Api;

    [Fact]
    public void ProjectDefault()
    {
        Assert.Equal("D3D12", Api(4, Sm5, Config(Rhi("DefaultGraphicsRHI_DX12"))));
        Assert.Equal("D3D11", Api(4, Sm5, Config(Rhi("DefaultGraphicsRHI_Default")))); // UE4's default is DX11
        Assert.Equal("D3D11", Api(4, Sm5, Config("[/Script/Engine.RendererSettings]\n")));
        Assert.Equal("D3D12", Api(5, Both, Config(Rhi("DefaultGraphicsRHI_Default")))); // UE5's is DX12
        Assert.Equal("Vulkan", Api(5, Both, Config(Rhi("DefaultGraphicsRHI_Vulkan"))));
        Assert.Equal("D3D11", Api(4, Sm5, Config(Rhi("DefaultGraphicsRHI_DX12"), Rhi("DefaultGraphicsRHI_DX11")))); // the platform ini overrides
        Assert.Equal("D3D12", Api(4, Sm6, Config(Rhi("DefaultGraphicsRHI_DX11")))); // SM6 runs on DX12 only
        Assert.Equal(UnrealRhi.Ambiguous, Api(5, Both, Config(Rhi("DefaultGraphicsRHI_DX11")))); // Palworld: DX12 shaders ship too
    }

    /// <summary>Dead Island 2: its engine's BaseEngine.ini sets DX12 and the project doesn't override it.</summary>
    [Fact]
    public void EngineBaseDefault()
    {
        var config = Config("[/Script/Engine.RendererSettings]\n");
        config["Engine/Config/BaseEngine.ini"] = Rhi("DefaultGraphicsRHI_DX12");
        Assert.Equal("D3D12", Api(4, Sm5, config));
        Assert.Equal("D3D12", Api(4, Sm5, config, UserDir("[D3DRHIPreference]\nPreferredRHI=dx11\n"))); // explicit: UE4 ignores the user
        config["Game/Config/DefaultEngine.ini"] = Rhi("DefaultGraphicsRHI_Default");
        Assert.Equal("D3D11", Api(4, Sm5, config)); // the project overrides the engine
        Assert.Equal(UnrealRhi.Ambiguous, Api(4, Sm5, new() { ["Engine/Config/BaseEngine.ini"] = Rhi("DefaultGraphicsRHI_DX12") })); // project config encrypted
    }

    [Fact]
    public void UserSettingsAndLaunchOptions()
    {
        var dx12Pref = UserDir("[D3DRHIPreference]\nbUseD3D12InGame=True\n");
        Assert.Equal("D3D12 (user setting)", Api(4, Sm5, Config(Rhi("DefaultGraphicsRHI_Default")), dx12Pref));
        Assert.Equal("D3D11", Api(4, Sm5, Config(Rhi("DefaultGraphicsRHI_DX11")), dx12Pref)); // UE4: an explicit project default wins
        Assert.Equal("D3D11 (user setting)", Api(5, Both, Config(Rhi("DefaultGraphicsRHI_DX12")), UserDir("[D3DRHIPreference]\nPreferredRHI=dx11\n")));
        Assert.Equal("D3D12 (user setting)", Api(4, Sm5, Config(""), UserDir("[/Script/OakGame.OakGameUserSettings]\nPreferredGraphicsAPI=DX12\n"))); // Gearbox
        Assert.Equal("D3D11 (launch option)", Api(5, Both, Config(Rhi("DefaultGraphicsRHI_DX12")), dx12Pref, "-dx11 -skipintro"));
        Assert.Equal("D3D12", Api(5, Both, Config(Rhi("DefaultGraphicsRHI_DX12")), launch: "-nodx11warning")); // not the flag
    }

    /// <summary>FF7 Remake's 4.18 fork starts DX12 unless -dx11/-d3d11 is given, whatever its config says; it ships SM5 only.</summary>
    [Fact]
    public void ForkStartsDx12()
    {
        const string ff7r = "GAME_FinalFantasy7Remake";
        var config = Config("[/Script/Engine.RendererSettings]\n");
        Assert.Equal("D3D12", UnrealRhi.Resolve(4, Sm5, config, "Game", null, "", fork: ff7r).Api);
        Assert.Equal("D3D12", UnrealRhi.Resolve(4, Sm5, new Dictionary<string, string>(), "Game", null, "", fork: ff7r).Api);   // encrypted, no log
        Assert.Equal("D3D11 (launch option)", UnrealRhi.Resolve(4, Sm5, config, "Game", null, "-d3d11", fork: ff7r).Api);
        Assert.Equal(UnrealRhi.Ambiguous, UnrealRhi.Resolve(4, Sm5, config, "Game", null, "", [" Play", "-dx11 Play (DirectX 11)"], fork: ff7r).Api);
        Assert.Equal("D3D11", UnrealRhi.Resolve(4, Sm5, config, "Game", null, "", fork: "GAME_FinalFantasy7Rebirth").Api);
    }

    [Fact]
    public void UnreadableConfig()
    {
        Assert.Equal(UnrealRhi.Ambiguous, Api(5, Both, new())); // encrypted paks, no log
        Assert.Equal("D3D12 (last run)", Api(5, Both, new(), UserDir("", "[2026.03.04-18.43.38:621][  0]LogRHI: Using Default RHI: D3D12\n")));
        Assert.Equal("D3D11 (last run)", Api(4, Sm5, new(), UserDir("", "LogD3D11RHI: Chosen D3D11 Adapter:\n")));
    }

    /// <summary>UE4 DX12 may run the SM5 libraries, so a DX11-default project that ships ray tracing (DX12-only) offers DX12 (Ghostrunner).</summary>
    [Fact]
    public void RayTracingOffersDx12()
    {
        const string rt = "[/Script/Engine.RendererSettings]\nr.RayTracing=True\n";
        Assert.Equal(UnrealRhi.Ambiguous, Api(4, Both, Config(Rhi("DefaultGraphicsRHI_DX11"))));   // SM5 + SM6 libraries
        Assert.Equal(UnrealRhi.Ambiguous, Api(4, Sm5, Config(rt)));
        Assert.Equal("D3D11", Api(4, Sm5, Config(Rhi("DefaultGraphicsRHI_DX11") + "[/Script/Engine.RendererSettings]\nr.RayTracing=False\n")));
        Assert.Equal("D3D11", Api(4, Sm5, Config(Rhi("DefaultGraphicsRHI_DX11")), UserDir("")));   // DX11 only stays DX11
        Assert.Equal("D3D12", Api(4, Sm5, Config(Rhi("DefaultGraphicsRHI_DX12") + rt)));
        // encrypted: unreadable before the key, the shipped config decides after it
        Assert.Equal(UnrealRhi.Ambiguous, Api(4, Sm5, new()));
        Assert.Equal(UnrealRhi.Ambiguous, Api(4, Sm5, Config(Rhi("DefaultGraphicsRHI_DX11") + rt)));
        var user = UserDir("");
        File.WriteAllText(Path.Combine(user, "Config", "Windows", "Engine.ini"), rt);
        Assert.Equal("D3D11", Api(4, Sm5, Config(Rhi("DefaultGraphicsRHI_DX11")), user));   // the player's own ini doesn't ship ray tracing
    }

    static readonly Game Drg = new("steam:548430", "Deep Rock Galactic", Store.Steam, @"C:\Steam\steamapps\common\Deep Rock Galactic",
        @"C:\Steam\steamapps\common\Deep Rock Galactic\FSD\Binaries\Win64\FSD-Win64-Shipping.exe");

    static Dictionary<string, object> Entry(string? args, string desc, Dictionary<string, object>? config = null, string exe = "FSD.exe")
    {
        var e = new Dictionary<string, object> { ["executable"] = exe, ["description"] = desc };
        if (args != null) e["arguments"] = args;
        if (config != null) e["config"] = config;
        return e;
    }

    static Dictionary<string, object> App(params Dictionary<string, object>[] entries) => new()
    {
        ["config"] = new Dictionary<string, object> { ["launch"] = entries.Select((e, i) => (e, i)).ToDictionary(x => x.i.ToString(), x => (object)x.e) },
    };

    /// <summary>An editor entry passing -dx12 is not a way to play: the playable entry runs the project's DX11.</summary>
    [Fact]
    public void LaunchMenuKeepsOnlyEntriesThatStartTheGame()
    {
        var (menu, _) = UnrealRhi.LaunchMenu(App(Entry(null, "Play Deep Rock Galactic"), Entry("-dx12", "Editor", exe: @"Engine\Binaries\Win64\UE4Editor.exe"),
            Entry("-dx12", "VR", exe: @"..\Other Game\FSD.exe")), Drg);
        Assert.Equal([" Play Deep Rock Galactic"], menu);
        Assert.Equal("D3D11", UnrealRhi.Resolve(4, Sm5, Config("[/Script/Engine.RendererSettings]\n"), "Game", null, "", menu).Api);
    }

    /// <summary>Deep Rock Galactic: UE4, SM5 only, no DefaultGraphicsRHI (UE4 default DX11), and a Steam launch menu
    /// "Play ... (DirectX 12)" -dx12 / "Play ... (DirectX 11)" -dx11.</summary>
    [Fact]
    public void SteamLaunchMenu()
    {
        var win = new Dictionary<string, object> { ["oslist"] = "windows" };
        var app = App(
            Entry("-dx12", "Play Deep Rock Galactic (DirectX 12)", win),
            Entry("-dx11", "Play Deep Rock Galactic (DirectX 11)", win, @"FSD\Binaries\Win64\FSD-Win64-Shipping.exe"),
            Entry("-vulkan", "Linux", new() { ["oslist"] = "linux" }),
            Entry("-dx11 -test", "Beta", new() { ["oslist"] = "windows", ["BetaKey"] = "experimental" }),
            Entry("-dx11", "Benchmark (DirectX 11)", win, "Benchmark.exe"));
        var (menu, def) = UnrealRhi.LaunchMenu(app, Drg);
        Assert.Equal(2, menu.Count);
        Assert.Null(def);
        var drg = Config("[/Script/Engine.RendererSettings]\n");

        Assert.Equal(UnrealRhi.Ambiguous, UnrealRhi.Resolve(4, Sm5, drg, "Game", null, "", menu).Api); // the recorder may go in
        Assert.Equal("D3D12 (last run)", UnrealRhi.Resolve(4, Sm5, drg, "Game", UserDir("", "LogRHI: Using Default RHI: D3D12\n"), "", menu).Api);
        Assert.Equal("D3D11 (last run)", UnrealRhi.Resolve(4, Sm5, drg, "Game", UserDir("", "LogD3D11RHI: Chosen D3D11 Adapter:\n"), "", menu).Api);
        Assert.Equal("D3D12 (launch option)", UnrealRhi.Resolve(4, Sm5, drg, "Game", null, "-dx12", menu).Api); // the user's own launch option
        Assert.Equal("D3D12 (launch option)", UnrealRhi.Resolve(4, Sm5, drg, "Game", null, "", [menu[0]]).Api);
        Assert.Equal("D3D12 (launch option)", UnrealRhi.Resolve(4, Sm5, drg, "Game", null, "", [" Play (DX12)"]).Api); // description only
        Assert.Equal(UnrealRhi.Ambiguous, UnrealRhi.Resolve(4, Sm5, drg, "Game", null, "", ["-dx12 DX12", " Play"]).Api); // the other entry runs the project's DX11
        Assert.Equal("D3D11", UnrealRhi.Resolve(4, Sm5, drg, "Game", null, "", [" Play", "-windowed Windowed"]).Api); // no API in the menu
        ((Dictionary<string, object>)((Dictionary<string, object>)((Dictionary<string, object>)app["config"])["launch"])["0"])["type"] = "default";
        (menu, def) = UnrealRhi.LaunchMenu(app, Drg);
        Assert.Equal(menu[0], def);
        Assert.Equal("D3D12 (launch option)", UnrealRhi.Resolve(4, Sm5, drg, "Game", null, "", menu, def).Api); // Steam's default entry runs DX12
        Assert.Equal("D3D11 (last run)", UnrealRhi.Resolve(4, Sm5, drg, "Game", UserDir("", "LogD3D11RHI: Chosen D3D11 Adapter:\n"), "", menu, def).Api);
        Assert.Equal(UnrealRhi.Ambiguous, UnrealRhi.Resolve(4, Sm5, drg, "Game", null, "", menu, menu[1]).Api); // a DX11 default doesn't decide
        // SAND LAND: the default entry has no flag, the other forces DX11
        string[] sandLand = ["-DefaultOnlineSubsystem=Steam ", "-DefaultOnlineSubsystem=Steam -dx11 force use DirectX11"];
        Assert.Equal("D3D12", UnrealRhi.Resolve(4, Sm5, Config(Rhi("DefaultGraphicsRHI_DX12")), "Game", null, "", sandLand, sandLand[0]).Api);
        Assert.Equal(UnrealRhi.Ambiguous, UnrealRhi.Resolve(4, Sm5, Config(Rhi("DefaultGraphicsRHI_DX12")), "Game", null, "", sandLand).Api);
        Assert.Equal(UnrealRhi.Ambiguous, UnrealRhi.Resolve(4, Sm5, drg, "Game", UserDir("[D3DRHIPreference]\nbUseD3D12InGame=True\n"), "", sandLand).Api); // the flagless entry runs the user setting
        Assert.Equal("D3D11", UnrealRhi.Resolve(4, Sm5, drg, "Game", null, "", sandLand).Api); // both entries run DX11

        var state = new GameState(Ff7.Game, new EngineInfo("Unreal", "4.27", null, UnrealRhi.Ambiguous, false, null), AntiCheat.None, GameStatus.Ready, "", null, null, null, null, null, null, null, false, null);
        Assert.Null(ScsKiller.RecorderSkip(state, null));
        Assert.Equal(ScsKiller.SkipNotDx12, ScsKiller.RecorderSkip(state with { Engine = state.Engine! with { GraphicsApi = "D3D11" } }, null));
    }

    /// <summary>An install whose paks aren't there yet (mid-update) gets a stamp once they are: a miss isn't kept. A key added changes it.</summary>
    [Fact]
    public void DetectStampFindsTheProjectOnceThePaksAppear()
    {
        var install = Ff7.TempDir("rhi-stamp-" + Guid.NewGuid().ToString("N")[..8]);
        var game = new Game("epic:stamp", "Stamp", Store.Epic, install, Path.Combine(install, "Proj", "Binaries", "Win64", "Proj-Win64-Shipping.exe"));
        var data = Ff7.TempDir("rhi-stamp-data");
        var reader = new UnrealReader(data);
        var engine = new EngineInfo("Unreal", "4.27", null, "D3D11", false, null);
        Assert.Equal("", reader.DetectStamp(game, engine));
        var paks = Directory.CreateDirectory(Path.Combine(install, "Proj", "Content", "Paks")).FullName;
        File.WriteAllBytes(Path.Combine(paks, "Proj-Windows.pak"), new byte[16]);
        var before = reader.DetectStamp(game, engine);
        Assert.NotEqual("", before);
        Directory.CreateDirectory(new AppStore(data).GameDir(game.Id));
        File.WriteAllText(Path.Combine(new AppStore(data).GameDir(game.Id), "aes.key"), "0x" + new string('7', 64));
        Assert.NotEqual(before, reader.DetectStamp(game, engine));   // a key added re-runs detection
    }

    [Fact]
    public void CheckRefusesDirectX11()
    {
        var p = new Planner();
        var ue = new EngineInfo("Unreal", "4.26", null, "D3D11", false, null);
        Assert.Equal(new PlanCheck(Readiness.Unsupported, "runs on DirectX 11"), p.Check(Ff7.Game, ue, null, Ff7.Nvidia with { Profile = "unmeasured" }));
        Assert.Equal(new PlanCheck(Readiness.Unsupported, "runs on DirectX 11 (user setting)"), p.Check(Ff7.Game, ue with { GraphicsApi = "D3D11 (user setting)" }, null, Ff7.Nvidia with { Profile = "unmeasured" }));
        Assert.Equal(new PlanCheck(Readiness.Unsupported, "runs on Vulkan"), p.Check(Ff7.Game, ue with { GraphicsApi = "Vulkan" }, null, Ff7.Nvidia));
        Assert.Equal(Readiness.Ready, p.Check(Ff7.Game, ue with { GraphicsApi = "D3D12 (user setting)" }, null, Ff7.Nvidia).Readiness);
        var both = p.Check(Ff7.Game, ue with { GraphicsApi = UnrealRhi.Ambiguous }, null, Ff7.Nvidia);
        Assert.Equal(Readiness.Ready, both.Readiness);
        Assert.Contains("DirectX 11", both.Reason);
    }

    /// <summary>The installed UE games (dev machine; read-only: paks, the user's saved ini, logs, Steam launch options).
    /// Expectations only where the project's own config or shader formats settle it.</summary>
    [Fact]
    public void InstalledGames()
    {
        Ff7.Codecs();
        var expect = new Dictionary<string, string>
        {
            ["FINAL FANTASY VII REBIRTH"] = "D3D12", ["Orcs Must Die! 3"] = "D3D11", ["Stellar Blade™ Demo"] = "D3D12", ["Palworld"] = UnrealRhi.Ambiguous,
            ["Life is Strange: Reunion"] = "D3D12", ["Darwin's Paradox"] = "D3D12", ["Lost Records: Bloom & Rage"] = "D3D12",
            ["Dead Island 2"] = "D3D12", ["Deep Rock Galactic"] = "D3D11", ["High on Life"] = "D3D12",
        };
        var reader = new UnrealReader(Ff7.TempDir("rhi-data"));
        IEnumerable<Game> games;
        try { games = new SteamSource().Discover().Concat(new EpicSource().Discover()).Concat(new XboxSource().Discover()).ToList(); }
        catch (Exception) { return; } // no stores on this machine
        foreach (var g in games)
        {
            if (reader.Detect(g, out var why) is not { } e) continue;
            output.WriteLine($"{g.Name,-45} UE {e.Version,-4} {e.GraphicsApi,-22} {why}");
            if (expect.TryGetValue(g.Name, out var api)) Assert.Equal(api, e.GraphicsApi);
        }
    }
}
