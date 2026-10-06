using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

// The update system's pure parts (Core/App/Updates.cs). The keys here are generated per run: a test key never exists
// outside this file, and production pins only FeedTrust.ReleaseKeys.
public class UpdateTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "scskiller-update-test-" + Guid.NewGuid().ToString("N")[..8]);
    static readonly (string Seed, string Public) A = FeedTrust.NewKey(), B = FeedTrust.NewKey();
    static readonly byte[] Feed = """{"Assets":[{"PackageId":"SCSKiller.App","Version":"1.5.0-beta.1","Type":"Full","FileName":"SCSKiller.App-1.5.0-beta.1-full.nupkg","SHA256":"ab"}]}"""u8.ToArray();
    static readonly DateTimeOffset T = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    FeedTrust Trust() => new(new AppStore(_dir), new Dictionary<string, string> { ["rel-a"] = A.Public, ["rel-b"] = B.Public });
    static byte[] Sig(string seed, string kid, string channel, byte[] feed, DateTimeOffset at) =>
        Encoding.UTF8.GetBytes(FeedTrust.Sign(Convert.FromBase64String(seed), kid, channel, feed, at));

    [Fact]
    public void GoodSignature_WithEitherPinnedKey_IsAccepted()
    {
        var t = Trust();
        t.Accept("beta", Feed, Sig(A.Seed, "rel-a", "beta", Feed, T));
        t.Accept("beta", Feed, Sig(B.Seed, "rel-b", "beta", Feed, T.AddHours(1)));   // the backup key
        t.Accept("beta", Feed, Sig(A.Seed, "rel-a", "beta", Feed, T.AddHours(1)));   // the same feed fetched again
    }

    [Fact]
    public void BadSignatures_AreRefused()
    {
        var t = Trust();
        var other = FeedTrust.NewKey();
        Refused(() => t.Accept("beta", Feed, Sig(other.Seed, "rel-a", "beta", Feed, T)), "bad signature");        // not the pinned key
        Refused(() => t.Accept("beta", Feed, Sig(other.Seed, "rel-z", "beta", Feed, T)), "unknown key");          // an unpinned kid
        Refused(() => t.Accept("stable", Feed, Sig(A.Seed, "rel-a", "beta", Feed, T)), "another channel");        // a beta feed served as stable
        Refused(() => t.Accept("beta", Feed, "not json"u8.ToArray()), "malformed");
        Refused(() => new FeedTrust(new AppStore(_dir), FeedTrust.ReleaseKeys).Accept("beta", Feed, Sig(A.Seed, "rel-a", "beta", Feed, T)), "bad signature");   // a test key under a production kid
    }

    [Fact]
    public void TamperedFeedOrSignatureFile_IsRefused()
    {
        var t = Trust();
        var sig = Sig(A.Seed, "rel-a", "beta", Feed, T);
        var feed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Feed).Replace("\"ab\"", "\"cd\""));   // another package hash
        Refused(() => t.Accept("beta", feed, sig), "doesn't match");
        foreach (var (field, value) in new[] { ("signed_at", "2027-01-01T00:00:00Z"), ("feed_sha256", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(feed))) })
        {
            var j = JsonNode.Parse(sig)!;
            j[field] = value;   // signed fields changed after signing
            Refused(() => t.Accept("beta", field == "feed_sha256" ? feed : Feed, Encoding.UTF8.GetBytes(j.ToJsonString())), "bad signature");
        }
    }

    [Fact]
    public void OlderFeed_IsARefusedReplay_PerChannel()
    {
        var t = Trust();
        t.Accept("beta", Feed, Sig(A.Seed, "rel-a", "beta", Feed, T));
        Refused(() => t.Accept("beta", Feed, Sig(A.Seed, "rel-a", "beta", Feed, T.AddMinutes(-1))), "older");
        t.Accept("stable", Feed, Sig(A.Seed, "rel-a", "stable", Feed, T.AddDays(-30)));   // switching back to stable isn't a replay
        Refused(() => Trust().Accept("beta", Feed, Sig(A.Seed, "rel-a", "beta", Feed, T.AddMinutes(-1))), "older");   // remembered on disk
    }

    static void Refused(Action a, string why) => Assert.Contains(why, Assert.Throws<FeedRejectedException>(a).Message);

    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "", "stable")]
    [InlineData("v1.5.0-beta.2", 1, 5, 0, "beta.2", "beta")]
    [InlineData("1.5.0-alpha.1", 1, 5, 0, "alpha.1", "alpha")]
    [InlineData("1.5.0-internal.3+abc123", 1, 5, 0, "internal.3", "internal")]
    [InlineData("0.0.0-internal.0+4f2a9c1d", 0, 0, 0, "internal.0", "internal")]   // a dev build
    [InlineData("2.0.0-rc.1", 2, 0, 0, "rc.1", "internal")]                          // an unknown label goes nowhere public
    public void Versions_Parse(string s, int major, int minor, int patch, string pre, string channel)
    {
        var v = AppVersion.Parse(s)!;
        Assert.Equal((major, minor, patch, pre, channel), (v.Major, v.Minor, v.Patch, v.Pre, v.Channel));
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("")]
    [InlineData(null)]
    public void NotVersions(string? s) => Assert.Null(AppVersion.Parse(s));

    [Fact]
    public void Versions_OrderBySemVerPrecedence()
    {
        string[] ordered = ["1.4.9", "1.5.0-alpha.2", "1.5.0-alpha.10", "1.5.0-beta", "1.5.0-beta.1", "1.5.0-beta.2", "1.5.0-internal.1", "1.5.0", "1.5.1-alpha.1", "1.10.0"];
        var parsed = ordered.Select(s => AppVersion.Parse(s)!).ToList();
        Assert.Equal(ordered, parsed.OrderBy(v => v).Select(v => v.ToString()));
        for (var i = 1; i < parsed.Count; i++) Assert.True(parsed[i - 1].CompareTo(parsed[i]) < 0, $"{parsed[i - 1]} < {parsed[i]}");
        Assert.Equal(0, AppVersion.Parse("1.5.0+a")!.CompareTo(AppVersion.Parse("1.5.0+b")));   // build metadata doesn't order
        // internal sorts after beta: only the internal channel, which installs what its feed names, may go back
        Assert.True(AppVersion.Parse("1.5.0-internal.1")!.CompareTo(AppVersion.Parse("1.5.0-beta.3")) > 0);
        Assert.True(UpdateChannels.AllowsDowngrade("internal"));
        Assert.All(new[] { "stable", "beta", "alpha" }, c => Assert.False(UpdateChannels.AllowsDowngrade(c)));
    }

    [Fact]
    public void Channels_AreGatedByTheTokensFlags()
    {
        Assert.Equal(["stable"], UpdateChannels.Offered(null));                          // signed out: no picker
        Assert.Equal(["stable"], UpdateChannels.Offered(["db"]));
        Assert.Equal(["stable", "beta"], UpdateChannels.Offered(["db", "beta"]));
        Assert.Equal(["stable", "beta", "alpha"], UpdateChannels.Offered(["db", "beta", "alpha", "prio"]));
        Assert.Equal(["stable", "beta", "alpha", "internal"], UpdateChannels.Offered(["beta", "alpha", "internal"]));

        Assert.Equal("beta", UpdateChannels.Effective("beta", "stable", ["beta"]));
        Assert.Equal("alpha", UpdateChannels.Effective(null, "alpha", ["beta", "alpha"]));    // null: the build's own channel
        Assert.Equal("beta", UpdateChannels.Effective("alpha", "alpha", ["beta"]));           // alpha lapsed: beta, not stable
        Assert.Equal("stable", UpdateChannels.Effective("beta", "beta", ["db"]));             // beta lapsed
        Assert.Equal("stable", UpdateChannels.Effective("beta", "beta", null));               // signed out
        Assert.Equal("stable", UpdateChannels.Effective("stable", "beta", ["beta"]));         // chose stable: waits for stable to pass the beta
        Assert.Equal("stable", UpdateChannels.Effective(null, "stable", ["beta", "alpha"]));
        Assert.Equal("stable", UpdateChannels.Effective("nonsense", "beta", ["beta"]));
    }

    [Fact]
    public void A_staged_download_installs_only_while_its_channel_is_chosen()
    {
        Assert.True(UpdateChannels.Installs("internal", "internal", "internal", ["internal"]));
        Assert.False(UpdateChannels.Installs("internal", "internal", "internal", ["db"]));      // lapsed: the token says so
        Assert.True(UpdateChannels.Installs("stable", "beta", "stable", []));                   // signed out: stable
        Assert.False(UpdateChannels.Installs("beta", "beta", "stable", []));
        // signed in, no token read yet (a logon without network): any channel up to the chosen one
        Assert.True(UpdateChannels.Installs("internal", "internal", "internal", null));
        Assert.True(UpdateChannels.Installs("stable", "internal", "internal", null));
        Assert.True(UpdateChannels.Installs("stable", null, "beta", null));
        Assert.False(UpdateChannels.Installs("alpha", "beta", "beta", null));                   // switched to an earlier channel
        Assert.False(UpdateChannels.Installs("internal", "stable", "internal", null));
    }

    static readonly byte[] StableFeed = """{"Assets":[{"PackageId":"SCSKiller.App","Version":"1.2.4","Type":"Full","FileName":"SCSKiller.App-1.2.4-stable-full.nupkg","SHA256":"AB12","Size":124642678}]}"""u8.ToArray();
    static readonly byte[] BetaFeed = """{"Assets":[{"PackageId":"SCSKiller.App","Version":"1.3.0-beta.1","Type":"Full","FileName":"SCSKiller.App-1.3.0-beta.1-beta-full.nupkg","SHA256":"CD34","Size":124000000}]}"""u8.ToArray();
    static readonly StagedUpdate Staged = new("stable", "1.2.4", "SCSKiller.App-1.2.4-stable-full.nupkg", 124_642_678, "ab12",
        new Dictionary<string, SignedFeed> { ["stable"] = new(StableFeed, Sig(A.Seed, "rel-a", "stable", StableFeed, T)) });
    static readonly StagedUpdate StagedBeta = new("beta", "1.3.0-beta.1", "SCSKiller.App-1.3.0-beta.1-beta-full.nupkg", 124_000_000, "cd34",
        new Dictionary<string, SignedFeed> { ["beta"] = new(BetaFeed, Sig(A.Seed, "rel-a", "beta", BetaFeed, T)), ["stable"] = Staged.Feeds["stable"] });
    static readonly Dictionary<string, string> Keys = new() { ["rel-a"] = A.Public };

    [Fact]
    public void A_staged_download_survives_the_restart()
    {
        Assert.Null(StagedUpdate.Load(_dir));
        Staged.Save(_dir);
        var loaded = StagedUpdate.Load(_dir)!;
        Assert.Equal((Staged.Channel, Staged.Version, Staged.FileName, Staged.Size, Staged.Sha256), (loaded.Channel, loaded.Version, loaded.FileName, loaded.Size, loaded.Sha256));
        Assert.Null(loaded.Refused(Keys));   // the feed and its signature, byte for byte
        File.WriteAllText(Path.Combine(_dir, "update-staged.json"), "{\"Channel\":\"stable\"");   // torn write
        Assert.Null(StagedUpdate.Load(_dir));
        File.WriteAllText(Path.Combine(_dir, "update-staged.json"), "{}");
        Assert.Null(StagedUpdate.Load(_dir));
        Staged.Save(_dir);
        StagedUpdate.Forget(_dir);
        Assert.Null(StagedUpdate.Load(_dir));
        StagedUpdate.Forget(_dir);   // nothing to forget
    }

    [Fact]
    public void A_staged_record_its_signed_feeds_dont_back_is_refused()
    {
        Assert.Null(Staged.Refused(Keys));
        Assert.Null(StagedBeta.Refused(Keys));
        Assert.Null((StagedBeta with { Channel = "internal" }).Refused(Keys));   // internal's check reads beta's and stable's feeds
        // the record says stable but holds only a beta feed: a beta package passed off as stable
        Assert.NotNull((Staged with { Version = StagedBeta.Version, FileName = StagedBeta.FileName, Size = StagedBeta.Size, Sha256 = StagedBeta.Sha256,
            Feeds = new Dictionary<string, SignedFeed> { ["beta"] = StagedBeta.Feeds["beta"] } }).Refused(Keys));
        Assert.NotNull((StagedBeta with { Channel = "stable" }).Refused(Keys));
        Assert.NotNull((Staged with { Sha256 = "ef56" }).Refused(Keys));      // another package's hash
        Assert.NotNull((Staged with { Size = 1 }).Refused(Keys));
        Assert.NotNull((Staged with { Version = "1.9.0" }).Refused(Keys));
        Assert.NotNull((Staged with { FileName = "other.nupkg" }).Refused(Keys));
        Assert.NotNull((Staged with { Channel = "nonsense" }).Refused(Keys));
        Assert.NotNull((Staged with { Feeds = new Dictionary<string, SignedFeed>() }).Refused(Keys));
        var edited = StableFeed.ToArray();
        edited[^5] = (byte)'9';   // the size in the feed, after it was signed
        Assert.NotNull((Staged with { Feeds = new Dictionary<string, SignedFeed> { ["stable"] = Staged.Feeds["stable"] with { Feed = edited } } }).Refused(Keys));
        Assert.NotNull((Staged with { Feeds = new Dictionary<string, SignedFeed> { ["stable"] = new(BetaFeed, StagedBeta.Feeds["beta"].Sig) } }).Refused(Keys));   // beta's signature under stable
        Assert.NotNull(Staged.Refused(new Dictionary<string, string> { ["rel-b"] = B.Public }));   // a key that isn't pinned
    }

    [Fact]
    public void The_start_applies_a_staged_update()
    {
        Staged.Save(_dir);
        var staged = StagedUpdate.Load(_dir)!;
        Assert.Equal(StartStep.Apply, staged.AtStart(AppVersion.Parse("1.2.3")!, handedOver: false));
        Assert.Null(AutoInstall.HeldBack(AppStore.DefaultSettings, compiling: false, offline: false, playing: false));
        Assert.Equal(StartStep.Installed, staged.AtStart(AppVersion.Parse("1.2.4")!, handedOver: true));   // the hook didn't clear the marker
        Assert.Equal(StartStep.Failed, staged.AtStart(AppVersion.Parse("1.2.3")!, handedOver: true));      // never a restart loop
        Assert.Equal(StartStep.Older, staged.AtStart(AppVersion.Parse("1.3.0")!, handedOver: false));
        Assert.Equal(StartStep.Apply, (staged with { Channel = "internal", Version = "1.2.4-internal.1" }).AtStart(AppVersion.Parse("1.2.4")!, false));
    }

    [Fact]
    public void Automatic_install_is_on_by_default_also_for_an_older_settings_file()
    {
        Assert.True(AppStore.DefaultSettings.InstallUpdatesAutomatically);
        var store = new AppStore(_dir);
        store.SaveSettings(AppStore.DefaultSettings with { StartWithWindows = false });
        var file = Path.Combine(_dir, "settings.json");
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.True(json.Remove("InstallUpdatesAutomatically"));
        File.WriteAllText(file, json.ToJsonString());
        Assert.True(store.LoadSettings().InstallUpdatesAutomatically);
        Assert.False(store.LoadSettings().StartWithWindows);
    }

    [Fact]
    public void Nothing_installs_by_itself_with_the_setting_off()
    {
        var off = AppStore.DefaultSettings with { InstallUpdatesAutomatically = false };
        Assert.Equal(AutoInstall.Off, AutoInstall.HeldBack(off, false, false, false));
        Assert.DoesNotContain("starts or quits", AutoInstall.ReadyNote("1.2.4", off));
    }

    [Fact]
    public void A_compile_an_offline_session_or_a_game_holds_the_install_back()
    {
        var on = AppStore.DefaultSettings;
        Assert.Equal(AutoInstall.Compiling, AutoInstall.HeldBack(on, compiling: true, offline: false, playing: false));
        Assert.Equal(AutoInstall.Offline, AutoInstall.HeldBack(on, compiling: false, offline: true, playing: false));
        Assert.Equal(AutoInstall.Playing, AutoInstall.HeldBack(on, compiling: false, offline: false, playing: true));
    }

    [Fact]
    public void Restart_to_update_is_offered_for_a_staged_update_with_automatic_install_on_or_off()
    {
        foreach (var s in new[] { AppStore.DefaultSettings, AppStore.DefaultSettings with { InstallUpdatesAutomatically = false } })
        {
            Assert.True(AutoInstall.OffersRestart(Staged, s.UpdateChannel, "stable", []));
            Assert.Contains("Restart to update", AutoInstall.ReadyNote("1.2.4", s));
        }
        Assert.Contains("starts or quits", AutoInstall.ReadyNote("1.2.4", AppStore.DefaultSettings));
        Assert.False(AutoInstall.OffersRestart(null, null, "stable", []));
    }

    [Fact]
    public async Task BusyMutex_GuardsTheApply()
    {
        var name = @"Local\SCSKiller.Busy.test-" + Guid.NewGuid().ToString("N");   // not the real one: queue tests may hold it
        Assert.False(Busy.IsHeld(name));
        var h1 = Busy.Hold(name);
        var h2 = await Task.Run(() => Busy.Hold(name));   // another thread (or process) holding it too
        Assert.True(Busy.IsHeld(name));
        h1.Dispose();
        Assert.True(Busy.IsHeld(name));
        await Task.Run(h2.Dispose);   // released from a thread that didn't create it: no ownership needed
        Assert.False(Busy.IsHeld(name));
    }

    /// <summary>The worker's side of the handshake: it holds Busy before it reads the marker, and lets go while an update
    /// is handed over; the updater marks first, then reads Busy. Either order of the two sees the other.</summary>
    [Fact]
    public void A_compile_worker_holds_first_then_backs_off_while_an_update_is_handed_over()
    {
        var name = @"Local\SCSKiller.Busy.test-" + Guid.NewGuid().ToString("N");
        using (var hold = Busy.TryHold(_dir, T, name))
        {
            Assert.NotNull(hold);
            Busy.MarkApplying(_dir, T);    // the updater, after the worker's hold
            Assert.True(Busy.IsHeld(name));   // ...sees the compile, and doesn't apply
            Busy.ClearApplying(_dir);
        }
        Busy.MarkApplying(_dir, T);        // the updater first
        Assert.Null(Busy.TryHold(_dir, T, name));   // the worker doesn't start
        Assert.False(Busy.IsHeld(name));   // ...and holds nothing, so the apply goes ahead
        using (var late = Busy.TryHold(_dir, T.AddMinutes(3), name)) Assert.NotNull(late);   // a crashed apply's marker no longer stops it
        Busy.ClearApplying(_dir);
    }

    [Fact]
    public void ApplyingMarker_StopsTheCliForTwoMinutes()
    {
        Assert.False(Busy.Applying(_dir, T));
        Busy.MarkApplying(_dir, T);
        Assert.True(Busy.Applying(_dir, T.AddSeconds(90)));
        Assert.False(Busy.Applying(_dir, T.AddMinutes(3)));    // a crashed apply
        Assert.False(Busy.Applying(_dir, T.AddMinutes(-3)));   // the clock went back: not forever
        Busy.ClearApplying(_dir);
        Assert.False(Busy.Applying(_dir, T));
        Busy.ClearApplying(_dir);   // already gone
    }

    [Fact]
    public void Codecs_OnlyPinnedBytesAreKept()
    {
        string dir = Path.Combine(_dir, "codecs"), seed = Path.Combine(_dir, "seed");
        Directory.CreateDirectory(seed);
        File.WriteAllText(Path.Combine(seed, "zlib-ng2.dll"), "not it");   // a wrong copy next to the exe isn't used
        var downloads = 0;
        var e = Assert.Throws<InvalidDataException>(() => Codecs.Ensure("zlib-ng2.dll", p => { downloads++; File.WriteAllText(p, "tampered"); return true; }, dir, seed));
        Assert.Contains("SHA-256", e.Message);
        Assert.Equal(1, downloads);
        Assert.False(File.Exists(Path.Combine(dir, "zlib-ng2.dll")));   // never left behind to load
        Assert.Throws<InvalidDataException>(() => Codecs.Ensure("other.dll", _ => true, dir, seed));   // no pin, no load
    }

    [Fact]
    public void SourceCode_AndPackageUrls()
    {
        Assert.Equal($"https://github.com/{UpdateFeeds.GhRepo}/tree/v1.4.2", UpdateFeeds.Source(AppVersion.Parse("1.4.2")!)!.AbsoluteUri);
        Assert.Equal(new Uri(RouteFailover.Default.Primary, "v1/updates/beta/source-1.5.0-beta.2.zip"), UpdateFeeds.Source(AppVersion.Parse("1.5.0-beta.2+abc")!));
        Assert.Equal(new Uri(RouteFailover.Default.Primary, "v1/updates/alpha/source-1.5.0-alpha.1.zip"), UpdateFeeds.Source(AppVersion.Parse("1.5.0-alpha.1")!));
        Assert.Null(UpdateFeeds.Source(AppVersion.Parse("1.5.0-internal.1")!));
        Assert.Null(UpdateFeeds.Source(AppVersion.Parse("0.0.0-internal.0+abc")!));

        Assert.Equal($"https://github.com/{UpdateFeeds.GhRepo}/releases/latest/download/releases.stable.json", UpdateFeeds.Feed("stable", "releases.stable.json").AbsoluteUri);
        Assert.Equal(new Uri(RouteFailover.Default.Primary, "v1/updates/beta/releases.beta.json.sig"), UpdateFeeds.Feed("beta", "releases.beta.json.sig"));
        // a beta feed lists stable releases too: each package is fetched from its own version's channel
        Assert.Equal($"https://github.com/{UpdateFeeds.GhRepo}/releases/download/v1.4.2/SCSKiller.App-1.4.2-full.nupkg",
            UpdateFeeds.Package(AppVersion.Parse("1.4.2")!, "SCSKiller.App-1.4.2-full.nupkg").AbsoluteUri);
        if (Environment.GetEnvironmentVariable("SCSKILLER_API") is not { Length: > 0 })
            Assert.Equal("https://dl.scskiller.io/v1/updates/beta/SCSKiller.App-1.5.0-beta.2-delta.nupkg",
                UpdateFeeds.Package(AppVersion.Parse("1.5.0-beta.2")!, "SCSKiller.App-1.5.0-beta.2-delta.nupkg").AbsoluteUri);
    }

    [Fact]
    public void Checks_RunHourly() => Assert.Equal(TimeSpan.FromHours(1), UpdateFeeds.CheckEvery);

    /// <summary>The App project is WinUI and has no test seam: its source says that "Check for updates" and the Library's
    /// refresh download what they find.</summary>
    [Fact]
    public void ManualCheck_AndLibraryRefresh_Download()
    {
        var app = Path.Combine(TestEnv.RepoRoot, "src", "SCSKiller.App");
        var updater = File.ReadAllText(Path.Combine(app, "Updater.cs"));
        Assert.Matches(@"public static async Task CheckAsync\(bool backToStable = false\)", updater);
        var now = System.Text.RegularExpressions.Regex.Match(updater, @"public static Task CheckNowAsync\(\)\s*\{(.*?)\r?\n    \}",System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(now.Success);
        Assert.Contains("return CheckAsync();", now.Groups[1].Value);
        Assert.Contains("_ = Updater.CheckNowAsync();", File.ReadAllText(Path.Combine(app, "Pages", "AboutPage.xaml.cs")));
        Assert.Matches(@"real\.UserFetch = async \(\) => \{ await Account\.RefreshAsync\(\); await Updater\.CheckAsync\(\); \};", File.ReadAllText(Path.Combine(app, "App.xaml.cs")));
    }

    /// <summary>The App's source: the start and the tray's Quit both ask <see cref="AutoInstall.HeldBack"/> before applying,
    /// the start takes the staged download from the disk, and Windows' session end never applies.</summary>
    [Fact]
    public void Start_and_Quit_apply_only_through_the_automatic_install_rules()
    {
        var app = Path.Combine(TestEnv.RepoRoot, "src", "SCSKiller.App");
        var updater = File.ReadAllText(Path.Combine(app, "Updater.cs"));
        string Body(string signature) => System.Text.RegularExpressions.Regex.Match(updater, System.Text.RegularExpressions.Regex.Escape(signature) + @"\s*\{(.*?)\r?\n    \}",
            System.Text.RegularExpressions.RegexOptions.Singleline) is { Success: true } m ? m.Groups[1].Value : throw new Xunit.Sdk.XunitException(signature);
        var start = Body("public static async Task ApplyAtStartAsync(string[] args)");
        Assert.Contains("Staged()", start);
        Assert.Contains("case StartStep.Apply: ready = d;", start);
        Assert.Matches(@"HeldBack\(\) is null && BeginUpdate\(\)\s*&& !await ApplyAsync\(TimeSpan\.Zero, \(m, a\) => m\.ApplyUpdatesAndRestart\(a, args\)", start);
        var quit = Body("public static async Task ApplyOnExitAsync()");
        Assert.True(quit.IndexOf("HeldBack()") is >= 0 and var held && held < quit.IndexOf("ApplyAsync("));
        // a game started during Quit's wait for the check is seen right before the handover
        Assert.Contains("m.WaitExitThenApplyUpdates(r, silent: true, restart: false), () => !GameRunning())", quit);
        Assert.Contains("static bool GameRunning() => App.Core is ScsKiller k ? k.GameRunning()", updater);
        // the start scans whether or not a game is known to run: before its scan, GameRunning() can only say "maybe"
        Assert.Matches(@"Untouched\(\) && App\.Core\.Settings\.InstallUpdatesAutomatically\) await App\.Core\.ScanAsync\(", start);
        Assert.Contains("AutoInstall.HeldBack(App.Core.Settings, Busy.IsHeld() || App.Core.Compiling, OfflineBlocks(), GameRunning())", updater);
        // the start knows the games before it asks whether one runs, and asks again right before the handover
        Assert.True(start.IndexOf("await App.Core.ScanAsync(") is >= 0 and var scan && scan < start.IndexOf("BeginUpdate()"));
        Assert.Contains("() => Untouched() && !GameRunning()", start);
        Assert.Contains("s.Refused(FeedTrust.ReleaseKeys)", updater);
        Assert.Contains(".SetAutoApplyOnStartup(false)", updater);
        Assert.Contains("if (ready?.Channel == channel && !SignedFeedSource.Partial) Stage(null);", updater);   // a feed missing a channel drops nothing
        var appXaml = File.ReadAllText(Path.Combine(app, "App.xaml.cs"));
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(appXaml, @"Updater\.ApplyOnExitAsync\("));   // QuitAsync only, not SessionEnd
    }

    /// <summary>A package host can't fill the disk past the signed feed's size, nor hold the update check forever.</summary>
    [Fact]
    public async Task Download_StopsAtTheFeedsSize_AndOnAStall()
    {
        var to = new MemoryStream();
        await UpdateFeeds.Download(new MemoryStream(new byte[1000]), to, 1000, TimeSpan.FromSeconds(5), default);
        Assert.Equal(1000, to.Length);
        await Assert.ThrowsAsync<FeedRejectedException>(() => UpdateFeeds.Download(new MemoryStream(new byte[1001]), new MemoryStream(), 1000, TimeSpan.FromSeconds(5), default));
        await Assert.ThrowsAsync<TimeoutException>(() => UpdateFeeds.Download(new Stalling(), new MemoryStream(), 1000, TimeSpan.FromMilliseconds(200), default));
    }

    static readonly byte[] InternalFeed = """{"Assets":[{"PackageId":"SCSKiller.App","Version":"1.2.3-internal.5","Type":"Full","FileName":"SCSKiller.App-1.2.3-internal.5-internal-full.nupkg","SHA256":"EF56","Size":4}]}"""u8.ToArray();
    const string InternalPackage = "SCSKiller.App-1.2.3-internal.5-internal-full.nupkg";

    /// <summary>The servers by host and path: 404 for anything not given, 429 (Retry-After an hour) for a limited host.</summary>
    sealed class Servers(Dictionary<string, byte[]> files, params string[] limited) : HttpMessageHandler
    {
        public readonly List<Uri> Asked = [];
        public int To(string host) => Asked.Count(u => u.Host == host);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!;
            lock (Asked) Asked.Add(url);
            if (url.Host != "github.com") Assert.Equal("tok", request.Headers.Authorization?.Parameter);
            if (limited.Contains(url.Host))
            {
                var r = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new(TimeSpan.FromHours(1));
                return Task.FromResult(r);
            }
            var file = files.FirstOrDefault(f => url.AbsolutePath.EndsWith("/" + f.Key)).Value;
            return Task.FromResult(file is null ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) : new HttpResponseMessage { Content = new ByteArrayContent(file) });
        }
    }

    static Dictionary<string, byte[]> Signed(string channel, byte[] feed, string seed = "") => new()
    {
        [$"releases.{channel}.json"] = feed,
        [$"releases.{channel}.json.sig"] = Sig(seed.Length > 0 ? seed : A.Seed, "rel-a", channel, feed, T),
    };

    FeedClient Client(Servers s, List<string>? log = null) => new(s, Trust(), () => Task.FromResult<string?>("tok"), l => log?.Add(l));

    /// <summary>GitHub limiting the stable feed holds back neither the internal feed nor the internal package: the newer
    /// internal build is still read and downloaded, and GitHub isn't asked again before its Retry-After.</summary>
    [Fact]
    public async Task A_rate_limited_stable_feed_doesnt_hold_back_a_newer_internal_package()
    {
        var files = Signed("internal", InternalFeed);
        files[InternalPackage] = [1, 2, 3, 4];
        var s = new Servers(files, "github.com");
        var log = new List<string>();
        var c = Client(s, log);
        for (var i = 0; i < 2; i++)
        {
            var (feeds, partial) = await c.ReadAsync("internal");
            Assert.Equal(["internal"], feeds.Select(f => f.Channel));
            Assert.Equal(InternalFeed, feeds[0].Feed.Feed);
            Assert.True(partial);   // stable left out: a staged download it lists isn't dropped
        }
        Assert.Equal(1, s.To("github.com"));
        const string channel = "internal";
        Assert.Equal(4, s.Asked.Count(u => u.AbsolutePath.EndsWith($"releases.{channel}.json") || u.AbsolutePath.EndsWith($"releases.{channel}.json.sig")));
        Assert.Contains(log, l => l.Contains("github.com"));
        using var package = await c.PackageAsync(AppVersion.Parse("1.2.3-internal.5")!, InternalPackage, default);
        Assert.Equal([1, 2, 3, 4], await package.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_rate_limited_stable_feed_waits_on_the_stable_channel()
    {
        var s = new Servers([], "github.com");
        var c = Client(s);
        for (var i = 0; i < 2; i++)
            Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, (await Assert.ThrowsAsync<HttpRequestException>(() => c.ReadAsync("stable"))).StatusCode);
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => c.PackageAsync(AppVersion.Parse("1.2.4")!, "SCSKiller.App-1.2.4-stable-full.nupkg", default));
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, e.StatusCode);
        Assert.Equal(1, s.To("github.com"));   // the second read and the package waited without asking
    }

    [Fact]
    public async Task A_limited_package_host_doesnt_stop_the_feeds()
    {
        if (Environment.GetEnvironmentVariable("SCSKILLER_API") is { Length: > 0 }) return;   // packages then share the API host
        var s = new Servers(Signed("internal", InternalFeed), "dl.scskiller.io");
        var c = Client(s);
        for (var i = 0; i < 2; i++)
        {
            var e = await Assert.ThrowsAsync<HttpRequestException>(() => c.PackageAsync(AppVersion.Parse("1.2.3-internal.5")!, InternalPackage, default));
            Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, e.StatusCode);
        }
        Assert.Equal(1, s.To("dl.scskiller.io"));
        Assert.Single((await c.ReadAsync("internal")).Feeds);
    }

    [Fact]
    public async Task The_signature_is_still_required_beside_a_rate_limited_feed()
    {
        var other = FeedTrust.NewKey();
        await Assert.ThrowsAsync<FeedRejectedException>(() => Client(new Servers(Signed("internal", InternalFeed, other.Seed), "github.com")).ReadAsync("internal"));
        // a later channel's feed that fails its signature fails the read too
        var files = Signed("internal", InternalFeed);
        foreach (var (k, v) in Signed("stable", StableFeed, other.Seed)) files[k] = v;
        await Assert.ThrowsAsync<FeedRejectedException>(() => Client(new Servers(files)).ReadAsync("internal"));
    }

    /// <summary>beta and alpha unpublished (404) are channels without a feed, not feeds left out.</summary>
    [Fact]
    public async Task Unpublished_channels_are_no_feed_rather_than_a_gap()
    {
        var files = Signed("internal", InternalFeed);
        foreach (var (k, v) in Signed("stable", StableFeed)) files[k] = v;
        var (feeds, partial) = await Client(new Servers(files)).ReadAsync("internal");
        Assert.Equal(["internal", "stable"], feeds.Select(f => f.Channel));
        Assert.False(partial);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await Assert.ThrowsAsync<HttpRequestException>(() => Client(new Servers([])).ReadAsync("stable"))).StatusCode);
        Assert.Empty((await Client(new Servers([])).ReadAsync("beta")).Feeds);   // none yet on its own channel
    }

    /// <summary>Some bytes, then nothing until cancelled.</summary>
    sealed class Stalling : Stream
    {
        bool sent;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!sent) { sent = true; buffer.Span[0] = 1; return 1; }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
