using System.Net;
using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

// No real network: every client is built over a RouteFailover whose inner handler is a fake.
public class UserAgentTests : IDisposable
{
    static readonly Uri Com = new("https://api.test.com/"), Io = new("https://api.test.io/");
    static readonly string Expected = RouteFailover.UserAgent(RouteFailover.Product, AppVersion.Current, Environment.OSVersion.Version.Build);
    readonly string _dir = Path.Combine(Path.GetTempPath(), "scskiller-ua-test-" + Guid.NewGuid().ToString("N")[..8]);
    readonly List<(string Client, string Request, string Agent)> _seen = [];
    string _client = "";

    public UserAgentTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    CommunityTests.Fake Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) => new(r =>
    {
        lock (_seen) _seen.Add((_client, $"{r.Method} {r.RequestUri}", r.Headers.UserAgent.ToString()));
        return answer(r);
    });

    [Theory]
    [InlineData("SCSKiller", "1.2.4", 26300, "SCSKiller/1.2.4 (stable; Windows 26300)")]
    [InlineData("SCSKiller", "1.3.0-beta.2", 22631, "SCSKiller/1.3.0-beta.2 (beta; Windows 22631)")]
    [InlineData("SCSKiller", "1.3.0-alpha.1+abc", 19045, "SCSKiller/1.3.0-alpha.1 (alpha; Windows 19045)")]
    [InlineData("SCSKiller", "0.0.0-internal.0", 26100, "SCSKiller/0.0.0-internal.0 (internal; Windows 26100)")]
    [InlineData("SCSKiller-CLI", "1.2.4", 26300, "SCSKiller-CLI/1.2.4 (stable; Windows 26300)")]
    public void The_User_Agent_is_the_product_the_version_the_channel_and_the_Windows_build(string product, string version, int build, string expected)
    {
        var ua = RouteFailover.UserAgent(product, AppVersion.Parse(version)!, build);
        Assert.Equal(expected, ua);
        using var request = new HttpRequestMessage();
        request.Headers.UserAgent.ParseAdd(ua);   // a valid header: one product token and one comment
        Assert.Equal(2, request.Headers.UserAgent.Count);
    }

    [Fact]
    public async Task Every_client_names_the_app_on_every_request()
    {
        var routes = new RouteFailover(Fake(r => r.RequestUri!.AbsolutePath == "/v1/active" ? CommunityTests.Ours(HttpStatusCode.NoContent)
            : CommunityTests.Ours(r.RequestUri.AbsolutePath == "/healthz" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NotFound)), [Com]);

        _client = "account";   // Patreon sign-in: its first request settles the route
        Assert.False(await new Account(Path.Combine(_dir, "a"), routes, _ => { }).SignInAsync());
        _client = "community";
        await new Community(Path.Combine(_dir, "c"), (_, _) => Task.FromResult<string?>("t"), routes).ManifestAsync();
        _client = "sharing";
        var game = Path.Combine(_dir, "games", "steam_480");
        Directory.CreateDirectory(game);
        File.WriteAllBytes(Path.Combine(game, "recording.db"), SharingTests.LocalRecording());
        await new Sharing(Path.Combine(_dir, "s"), () => true, routes).ShareAsync(game, new string('0', 40), () => new("steam:480@1", new string('0', 40)));
        _client = "active check";
        await new ActiveCheck(Path.Combine(_dir, "ac"), () => true, GpuVendor.Nvidia, AppVersion.Parse("1.2.4"), routes).SendAsync();
        _client = "content";   // known-stutter, engines, verdicts, welcome
        await routes.GetContentAsync("v1/content/x.json", TimeSpan.FromSeconds(5), 1024);
        _client = "updates";   // the feed and Velopack's package downloads: GitHub (stable) and the package host pass through
        var feeds = new FeedClient(routes, new FeedTrust(new AppStore(Path.Combine(_dir, "u")), FeedTrust.ReleaseKeys), () => Task.FromResult<string?>("t"));
        await Assert.ThrowsAsync<HttpRequestException>(() => feeds.PackageAsync(AppVersion.Parse("1.2.5")!, "a.nupkg", default));
        await Assert.ThrowsAsync<HttpRequestException>(() => feeds.PackageAsync(AppVersion.Parse("1.3.0-beta.1")!, "b.nupkg", default));

        Assert.Equal(["account", "community", "sharing", "active check", "content", "updates"], _seen.Select(s => s.Client).Distinct());
        Assert.Contains(_seen, s => s.Request.StartsWith("GET https://github.com/"));
        Assert.Contains(_seen, s => s.Request.StartsWith("GET https://dl.scskiller.io/"));
        Assert.All(_seen, s => Assert.Equal(Expected, s.Agent));
    }

    [Fact]
    public async Task A_failed_over_request_and_the_return_probe_carry_it_once()
    {
        var clock = new CommunityTests.Clock();
        var routes = new RouteFailover(Fake(r => r.RequestUri!.Host == Com.Host
            ? throw new HttpRequestException(HttpRequestError.ConnectionError, "unreachable") : CommunityTests.Ours(HttpStatusCode.OK)), [Com, Io], clock);
        using var http = new HttpClient(routes, false);

        (await http.GetAsync(new Uri(Com, "v1/x"))).Dispose();   // .com, then .io
        clock.Now += RouteFailover.Sticky;
        (await http.GetAsync(new Uri(Com, "v1/x"))).Dispose();   // starts the probe
        await routes.Probe;

        Assert.Equal(["GET https://api.test.com/healthz", "GET https://api.test.com/v1/x", "GET https://api.test.io/v1/x", "GET https://api.test.io/v1/x"],
            _seen.Select(s => s.Request).Order(StringComparer.Ordinal));   // the probe runs beside the second request
        Assert.All(_seen, s => Assert.Equal(Expected, s.Agent));
    }
}
