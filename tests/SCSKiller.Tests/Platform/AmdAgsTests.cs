using SCSKiller.Core;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Tests.Platform;

public class AmdAgsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "scskiller-ags-test-" + Guid.NewGuid().ToString("N")[..8]);
    static readonly EngineInfo Ue56 = new("Unreal", "5.6", null, "D3D12", false, null);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    Game Layout(string install, string exe, params string[] dirs)
    {
        var root = Path.Combine(_dir, install);
        foreach (var d in dirs) Directory.CreateDirectory(Path.Combine(root, d));
        var path = Path.Combine(root, exe);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x4D, 0x5A]);
        return new Game("test:ags", install, Store.Steam, root, path);
    }

    [Theory]
    [InlineData("Townfall", @"Townfall\Binaries\Win64\Townfall-Win64-Shipping.exe", "Townfall")]   // the Steam layout
    [InlineData("Hogwarts Legacy", @"Phoenix\Binaries\Win64\HogwartsLegacy.exe", "Phoenix")]      // exe named apart from its project
    [InlineData("Game", @"MyProj\Binaries\WinGDK\MyProj-WinGDK-Shipping.exe", "MyProj")]
    public void The_project_name_is_the_folder_holding_Binaries(string install, string exe, string expected) =>
        Assert.Equal(expected, AmdAgs.ProjectName(Layout(install, exe, @"Engine\Binaries\Win64")));

    [Fact]
    public void Without_a_Binaries_layout_the_only_project_with_paks_names_it()
    {
        Assert.Equal("Proj", AmdAgs.ProjectName(Layout("a", "Game.exe", @"Proj\Content\Paks", @"Engine\Content\Paks", "Other")));
        Assert.Null(AmdAgs.ProjectName(Layout("b", "Game.exe", @"One\Content\Paks", @"Two\Content\Paks")));   // which one is unknown
        Assert.Null(AmdAgs.ProjectName(Layout("c", "Game.exe", "Proj")));
    }

    [Fact]
    public void An_Unreal_game_that_uses_AGS_registers_its_project_and_engine()
    {
        var g = Layout("Townfall", @"Townfall\Binaries\Win64\Townfall-Win64-Shipping.exe");
        Assert.False(AmdAgs.UsesAgs(g.ExePath));   // neither the DLL next to it nor the export
        Assert.Null(AmdAgs.Of(g, Ue56));
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(g.ExePath)!, AmdAgs.DllName), []);
        Assert.Equal(new AgsRegistration("Townfall", "UnrealEngine5.6"), AmdAgs.Of(g, Ue56));
        Assert.Equal("UnrealEngine4.27", AmdAgs.Of(g, Ue56 with { Version = "4.27" })!.Engine);
        Assert.Equal("UnrealEngine4.25", AmdAgs.Of(g, Ue56 with { Version = "4.25" })!.Engine);
        Assert.Null(AmdAgs.Of(g, Ue56 with { Version = "4.24" }));   // 4.20-4.24 link AGS but create the D3D12 device without it
        Assert.Null(AmdAgs.Of(g, Ue56 with { Version = "4.20" }));
        Assert.Null(AmdAgs.Of(g, Ue56 with { Version = "GAME_Oak" }));
        Assert.Null(AmdAgs.Of(g, null));
        Assert.Null(AmdAgs.Of(g, Ue56 with { Family = "Carved" }));
    }

    [Fact]
    public void An_exe_that_cannot_be_read_may_use_AGS()
    {
        var g = Layout("9554CD53", @"NWD\Binaries\WinGDK\NWD-WinGDK-Shipping.exe");
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.ReadData,
            System.Security.AccessControl.AccessControlType.Deny);
        var exe = new FileInfo(g.ExePath);
        var acl = exe.GetAccessControl();
        acl.AddAccessRule(deny);
        exe.SetAccessControl(acl);
        try
        {
            Assert.Null(AmdAgs.UsesAgs(g.ExePath));
            Assert.Equal(new AgsRegistration("NWD", "UnrealEngine5.1", ExeUnread: true), AmdAgs.Of(g, Ue56 with { Version = "5.1" }));
            Assert.Null(AmdAgs.Of(g, Ue56 with { Version = "4.24" }));
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            exe.SetAccessControl(acl);
        }
    }

    [Fact]
    public void AGS_linked_into_the_exe_shows_in_its_exports()
    {
        var dll = Path.Combine(TestEnv.RepoRoot, "proxy", "build", "Release", AmdAgs.DllName);   // exports the AGS API; built with the proxy
        if (!File.Exists(dll)) return;
        var g = Layout("Linked", @"Proj\Binaries\Win64\Proj-Win64-Shipping.exe");
        File.Copy(dll, g.ExePath, true);
        Assert.True(AmdAgs.UsesAgs(g.ExePath));
        Assert.False(AmdAgs.UsesAgs(Environment.ProcessPath!));
        Assert.False(AmdAgs.UsesAgs(Path.Combine(_dir, "missing.exe")));
    }

    [Fact]
    public void The_games_own_AGS_6_DLL_is_used_else_the_bundled_one()
    {
        var g = Layout("Own", @"Proj\Binaries\Win64\Proj-Win64-Shipping.exe");
        Assert.Equal(@"C:\app\native\amd_ags_x64.dll", AmdAgs.DllFor(g, @"C:\app\native\amd_ags_x64.dll"));
        var own = Path.Combine(Path.GetDirectoryName(g.ExePath)!, AmdAgs.DllName);
        File.WriteAllBytes(own, []);   // no version resource: not AGS 6
        Assert.Equal(@"C:\app\native\amd_ags_x64.dll", AmdAgs.DllFor(g, @"C:\app\native\amd_ags_x64.dll"));
        var dll = Path.Combine(TestEnv.RepoRoot, "proxy", "build", "Release", AmdAgs.DllName);
        if (!File.Exists(dll)) return;
        File.Copy(dll, own, true);
        Assert.Equal(own, AmdAgs.DllFor(g, @"C:\app\native\amd_ags_x64.dll"));
    }

    /// <summary>The installed game, read-only.</summary>
    [Fact]
    public void Townfall_registers_Townfall()
    {
        var install = TestEnv.GameDir("Townfall");
        var exe = Path.Combine(install, "Townfall", "Binaries", "Win64", "Townfall-Win64-Shipping.exe");
        if (!File.Exists(exe)) return;
        var g = new Game("steam:1636440", "SILENT HILL: Townfall", Store.Steam, install, exe);
        Assert.Equal("Townfall", AmdAgs.ProjectName(g));
        Assert.True(AmdAgs.UsesAgs(exe));   // AGS is linked statically: no DLL in the install
        Assert.Equal("dxc:dc72f790", AmdAppCache.AgsKey(Path.GetFileName(exe), AmdAgs.Of(g, Ue56)!.App));
    }
}
