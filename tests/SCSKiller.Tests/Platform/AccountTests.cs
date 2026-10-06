using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

// No real network: a fake handler stands in for both API routes; only the loopback listener is real (127.0.0.1).
public class AccountTests : IDisposable
{
    static readonly Uri Com = new("https://api.test.com/"), Io = new("https://api.test.io/");
    readonly string _dir = Path.Combine(Path.GetTempPath(), "scskiller-account-test-" + Guid.NewGuid().ToString("N")[..8]);
    readonly Clock _clock = new();

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override long GetTimestamp() => Now.UtcTicks;   // the monotonic clock moves with Now
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    sealed class Fake(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public readonly List<string> Log = [];   // "GET https://api.test.com/v1/x"
        public Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : this((r, _) => Task.FromResult(answer(r))) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            lock (Log) Log.Add($"{r.Method} {r.RequestUri}");
            return answer(r, ct);
        }
    }

    static HttpResponseMessage Ours(HttpStatusCode code = HttpStatusCode.OK, object? json = null)
    {
        var r = new HttpResponseMessage(code) { Content = json == null ? null : new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json") };
        r.Headers.Add("X-SCSK", "1");
        return r;
    }

    static HttpRequestException Down => new(HttpRequestError.ConnectionError, "unreachable");
    static bool IsCom(HttpRequestMessage r) => r.RequestUri!.Host == Com.Host;

    RouteFailover Routes(Fake fake, TimeSpan? headersTimeout = null) => new(fake, [Com, Io], _clock, headersTimeout);

    static async Task<HttpStatusCode> Get(RouteFailover routes, string path = "v1/x", CancellationToken ct = default)
    {
        using var http = new HttpClient(routes, false);
        using var r = await http.GetAsync(new Uri(Com, path), ct);
        return r.StatusCode;
    }

    [Fact]
    public async Task Failover_goes_to_the_fallback_stays_there_and_a_probe_decides_the_return()
    {
        var comDown = true;
        var fake = new Fake(r => IsCom(r) && comDown ? throw Down : Ours());
        var routes = Routes(fake);

        await Get(routes);
        Assert.Equal(["GET https://api.test.com/v1/x", "GET https://api.test.io/v1/x"], fake.Log);

        fake.Log.Clear();
        _clock.Now += TimeSpan.FromMinutes(29);
        await Get(routes);
        Assert.Equal(["GET https://api.test.io/v1/x"], fake.Log);   // sticky: no attempt on the primary

        // Expired while the primary is still down: the request stays on the fallback, the probe fails, another 30 min.
        fake.Log.Clear();
        _clock.Now += TimeSpan.FromMinutes(2);
        await Get(routes);
        await routes.Probe;
        Assert.Equal(["GET https://api.test.com/healthz", "GET https://api.test.io/v1/x"], fake.Log.Order());
        fake.Log.Clear();
        await Get(routes);
        Assert.Equal(["GET https://api.test.io/v1/x"], fake.Log);

        // Expired again with the primary back: the probe succeeds and the next request goes to the primary.
        comDown = false;
        _clock.Now += TimeSpan.FromMinutes(31);
        await Get(routes);
        await routes.Probe;
        fake.Log.Clear();
        await Get(routes);
        Assert.Equal(["GET https://api.test.com/v1/x"], fake.Log);
        Assert.Equal(Com, routes.Current);
    }

    /// <summary>A request that started on the fallback and answers after the probe put the primary back leaves the
    /// primary first (the probe's verdict), however the two interleave.</summary>
    [Fact]
    public async Task A_request_on_the_fallback_that_ends_after_a_good_probe_keeps_the_primary()
    {
        var comDown = true;
        var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = false;
        var fake = new Fake((r, _) => IsCom(r) ? comDown ? throw Down : Task.FromResult(Ours()) : hold ? held.Task : Task.FromResult(Ours()));
        var routes = Routes(fake);
        await Get(routes);   // failed over: the fallback is first
        Assert.Equal(Io, routes.Current);

        comDown = false;
        hold = true;
        _clock.Now += TimeSpan.FromMinutes(31);
        var onFallback = Get(routes);   // starts the probe and goes to the fallback, which answers only after the probe
        await routes.Probe;
        Assert.Equal(Com, routes.Current);
        held.SetResult(Ours());
        await onFallback;
        Assert.Equal(Com, routes.Current);
        fake.Log.Clear();
        hold = false;
        await Get(routes);
        Assert.Equal(["GET https://api.test.com/v1/x"], fake.Log);
    }

    [Fact]
    public async Task Failover_skips_a_route_error_page_but_not_our_own_answers()
    {
        var status = HttpStatusCode.ServiceUnavailable;
        var fake = new Fake(r => IsCom(r) ? (status == (HttpStatusCode)530 ? new HttpResponseMessage(status) : Ours(status)) : Ours());
        var routes = Routes(fake);

        foreach (var ours in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests })
        {
            status = ours;
            fake.Log.Clear();
            Assert.Equal(ours, await Get(routes));
            Assert.Equal(["GET https://api.test.com/v1/x"], fake.Log);
        }

        status = (HttpStatusCode)530;   // Cloudflare: the origin is unreachable (no X-SCSK)
        fake.Log.Clear();
        Assert.Equal(HttpStatusCode.OK, await Get(routes));
        Assert.Equal(["GET https://api.test.com/v1/x", "GET https://api.test.io/v1/x"], fake.Log);
    }

    [Fact]
    public async Task Failover_retries_a_post_only_when_it_cannot_have_been_sent()
    {
        var error = HttpRequestError.ConnectionError;
        var fake = new Fake(r => IsCom(r) ? throw new HttpRequestException(error, "x") : Ours());
        using var http = new HttpClient(Routes(fake), false);

        (await http.PostAsync(new Uri(Com, "v1/token"), null)).Dispose();
        Assert.Equal(["POST https://api.test.com/v1/token", "POST https://api.test.io/v1/token"], fake.Log);

        error = HttpRequestError.ResponseEnded;   // the connection broke after the request went out
        fake.Log.Clear();
        using var http2 = new HttpClient(Routes(fake), false);
        await Assert.ThrowsAsync<ApiUnreachableException>(() => http2.PostAsync(new Uri(Com, "v1/token"), null));
        Assert.Equal(["POST https://api.test.com/v1/token"], fake.Log);
    }

    [Fact]
    public async Task Failover_on_a_timeout_but_never_on_the_callers_cancel()
    {
        var fake = new Fake(async (r, ct) =>
        {
            if (IsCom(r)) await Task.Delay(Timeout.Infinite, ct);
            return Ours();
        });
        Assert.Equal(HttpStatusCode.OK, await Get(Routes(fake, TimeSpan.FromMilliseconds(100))));
        Assert.Equal(["GET https://api.test.com/v1/x", "GET https://api.test.io/v1/x"], fake.Log);

        fake.Log.Clear();
        using var cancel = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Get(Routes(fake), ct: cancel.Token));
        Assert.Equal(["GET https://api.test.com/v1/x"], fake.Log);
    }

    [Fact]
    public async Task Failover_on_a_refused_connection_with_real_sockets()
    {
        using var up = new LoopbackCallback();   // any local HTTP server: it answers 404
        _ = up.WaitAsync("-", CancellationToken.None);
        using var http = new HttpClient(new RouteFailover(routes: [new("http://127.0.0.1:1/"), new($"http://127.0.0.1:{up.Port}/")]));
        using var r = await http.PostAsync("http://127.0.0.1:1/v1/token", null);   // refused: never sent, so a POST may switch
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Failover_leaves_other_hosts_alone()
    {
        var fake = new Fake(_ => throw Down);
        using var http = new HttpClient(Routes(fake), false);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("https://dl.test.io/file"));
        Assert.Equal(["GET https://dl.test.io/file"], fake.Log);
    }

    [Fact]
    public void Pkce_challenge_is_S256_base64url()
    {
        // RFC 7636 appendix B
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var verifier = Pkce.Random(32);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", verifier);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", Pkce.Challenge(verifier));
    }

    [Fact]
    public void Dpapi_round_trips_and_detects_tampering()
    {
        var secret = "sd1_secret-device-token"u8.ToArray();
        var sealedBytes = Dpapi.Protect(secret);
        Assert.Equal(-1, sealedBytes.AsSpan().IndexOf(secret));
        Assert.Equal(secret, Dpapi.Unprotect(sealedBytes));
        sealedBytes[^5] ^= 0xFF;
        Assert.Throws<CryptographicException>(() => Dpapi.Unprotect(sealedBytes));
    }

    static async Task<HttpResponseMessage> Browse(string url)
    {
        using var http = new HttpClient();
        return await http.GetAsync(url);
    }

    [Fact]
    public async Task Loopback_answers_the_matching_state_and_404s_the_rest()
    {
        using var cb = new LoopbackCallback();
        var wait = cb.WaitAsync("s1", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, (await Browse($"http://127.0.0.1:{cb.Port}/favicon.ico")).StatusCode);
        var page = await Browse($"http://127.0.0.1:{cb.Port}/cb?code=one-time&state=s1");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("close this tab", await page.Content.ReadAsStringAsync());
        Assert.Equal("one-time", await wait);
    }

    [Theory]
    [InlineData("code=one-time&state=someone-else")]
    [InlineData("code=one-time")]
    [InlineData("error=access_denied&state=s1")]
    public async Task Loopback_rejects_a_wrong_state_or_an_error(string query)
    {
        using var cb = new LoopbackCallback();
        var wait = cb.WaitAsync("s1", CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, (await Browse($"http://127.0.0.1:{cb.Port}/cb?{query}")).StatusCode);
        await Assert.ThrowsAsync<AccountException>(() => wait);
    }

    void SignedInAlready(string token = "sd1_stored")
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "auth.dat"), Dpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(new { Member = token })));
    }

    [Fact]
    public async Task Sign_in_runs_the_loopback_flow_and_stores_the_device_token_under_dpapi()
    {
        string? challenge = null;
        var fake = new Fake(r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path == "/healthz") return IsCom(r) ? throw Down : Ours();   // the primary is blocked: sign-in uses the fallback
            if (path == "/v1/auth/exchange")
            {
                var body = JsonDocument.Parse(r.Content!.ReadAsStringAsync().Result).RootElement;
                Assert.Equal("O", body.GetProperty("code").GetString());
                Assert.StartsWith("Windows PC · ", body.GetProperty("label").GetString());
                if (Pkce.Challenge(body.GetProperty("verifier").GetString()!) != challenge) return Ours(HttpStatusCode.BadRequest, new { error = "invalid_grant" });
                return Ours(json: new { device_token = "sd1_new", device_id = "d1", access_token = "v4.public.a", exp = "2026-09-28T12:00:00+00:00", ent = new[] { "db", "beta" }, until = (string?)null });
            }
            return Ours(HttpStatusCode.NotFound);
        });
        Task? browser = null;
        var account = new Account(_dir, Routes(fake), url =>
        {
            Assert.Equal(Io.Host, url.Host);
            Assert.Equal("/v1/auth/start", url.AbsolutePath);
            var q = HttpUtility.ParseQueryString(url.Query);
            Assert.Equal("patreon", q["provider"]);
            challenge = q["challenge"];
            browser = Browse($"http://127.0.0.1:{q["port"]}/cb?code=O&state={q["state"]}");   // what the server's 302 makes the browser do
        }, _clock);

        Assert.True(await account.SignInAsync(), account.Problem);
        await browser!;
        Assert.True(account.SignedIn);
        Assert.Null(account.Problem);
        Assert.Equal(["db", "beta"], account.Status!.Ent);
        Assert.Null(account.Status.Until);
        var stored = File.ReadAllBytes(Path.Combine(_dir, "auth.dat"));
        Assert.Equal(-1, stored.AsSpan().IndexOf("sd1_new"u8));
        var restarted = new Account(_dir, Routes(fake));
        Assert.True(restarted.SignedIn);   // the next start reads it back
        Assert.False(restarted.OffersSharing(AppStore.DefaultSettings));   // ...but asks to share only right after a sign-in

        // The share prompt: after this sign-in, while sharing is off and not dismissed; never after a sign-out.
        Assert.True(account.OffersSharing(AppStore.DefaultSettings));
        Assert.False(account.OffersSharing(AppStore.DefaultSettings with { ShareRecordings = true }));
        Assert.False(account.OffersSharing(AppStore.DefaultSettings with { SharePromptDismissed = true }));
        await account.SignOutAsync();
        Assert.False(account.OffersSharing(AppStore.DefaultSettings));
    }

    [Fact]
    public async Task Sign_in_with_the_server_unreachable_says_so_and_opens_no_browser()
    {
        var account = new Account(_dir, Routes(new Fake(_ => throw Down)), _ => Assert.Fail("browser opened"), _clock);
        Assert.False(await account.SignInAsync());
        Assert.False(account.SignedIn);
        Assert.Contains("Can't reach the SCSKiller server", account.Problem);

        account = new Account(_dir, Routes(new Fake(_ => Ours(HttpStatusCode.ServiceUnavailable))), _ => Assert.Fail("browser opened"), _clock);
        Assert.False(await account.SignInAsync());
        Assert.Contains("error 503", account.Problem);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]    // this PC's clock 3 h ahead of the server's
    [InlineData(-3)]   // ...or behind
    public async Task Access_token_is_renewed_once_half_its_life_is_gone_whatever_the_pc_clock_says(int skewHours)
    {
        SignedInAlready();
        var calls = 0;
        var offline = false;
        var fake = new Fake(r =>
        {
            if (offline) throw Down;
            Assert.Equal("POST https://api.test.com/v1/token", $"{r.Method} {r.RequestUri}");
            Assert.Equal("Bearer sd1_stored", r.Headers.Authorization!.ToString());
            var server = _clock.Now - TimeSpan.FromHours(skewHours);
            var response = Ours(json: new { access_token = "v4.public." + ++calls, exp = (server + TimeSpan.FromHours(24)).ToString("O"), ent = new[] { "db" }, until = (string?)null });
            response.Headers.Date = server;
            return response;
        });
        var account = new Account(_dir, Routes(fake), clock: _clock);

        Assert.Equal("v4.public.1", await account.GetAccessTokenAsync());
        _clock.Now += TimeSpan.FromHours(11.9);
        Assert.Equal("v4.public.1", await account.GetAccessTokenAsync());   // more than 12 h left: no request
        _clock.Now += TimeSpan.FromHours(0.2);
        Assert.Equal("v4.public.2", await account.GetAccessTokenAsync());
        Assert.Equal(2, calls);

        offline = true;   // due for renewal but the server is down: the unexpired token still serves
        _clock.Now += TimeSpan.FromHours(13);
        Assert.Equal("v4.public.2", await account.GetAccessTokenAsync());
        _clock.Now += TimeSpan.FromHours(12);   // expired and still offline
        await Assert.ThrowsAsync<ApiUnreachableException>(() => account.GetAccessTokenAsync());
    }

    [Fact]
    public async Task The_community_database_gets_a_token_only_with_the_db_flag_and_a_fresh_one_on_request()
    {
        Assert.Null(await new Account(_dir, Routes(new Fake(_ => throw Down)), clock: _clock).GetDbTokenAsync());   // signed out: no request
        SignedInAlready();
        var (calls, ent) = (0, new[] { "beta" });
        var fake = new Fake(_ => Ours(json: new { access_token = "v4.public." + ++calls, exp = (_clock.Now + TimeSpan.FromHours(24)).ToString("O"), ent, until = (string?)null }));
        var account = new Account(_dir, Routes(fake), clock: _clock);
        Assert.Null(await account.GetDbTokenAsync());   // a supporter without the database
        ent = ["db", "beta"];
        Assert.Equal("v4.public.2", await account.GetDbTokenAsync(fresh: true));   // after a 401 at the edge
        Assert.Equal("v4.public.2", await account.GetDbTokenAsync());
        Assert.Equal(2, calls);
    }

    // Settings' "Download shared shader hashes" is enabled by HasDb (AccountVm.HasDb), re-read on every Changed.
    [Fact]
    public async Task Has_db_follows_refresh_lapse_and_sign_out_and_sign_out_is_raised()
    {
        Assert.False(new Account(_dir, Routes(new Fake(_ => throw Down)), clock: _clock).HasDb);   // signed out
        SignedInAlready();
        var ent = new[] { "db", "beta" };
        var fake = new Fake(r => r.RequestUri!.AbsolutePath == "/v1/token"
            ? Ours(json: new { access_token = "v4.public.a", exp = (_clock.Now + TimeSpan.FromHours(24)).ToString("O"), ent, until = (string?)null })
            : Ours(HttpStatusCode.NoContent));
        var account = new Account(_dir, Routes(fake), clock: _clock);
        var raised = 0;
        account.Changed += () => raised++;
        Assert.False(account.HasDb);   // signed in, status not read yet

        await account.RefreshAsync();
        Assert.True(account.HasDb);
        ent = ["beta"];   // the membership lapsed
        await account.RefreshAsync();
        Assert.False(account.HasDb);
        ent = ["db"];
        await account.RefreshAsync();
        Assert.True(account.HasDb);
        raised = 0;
        await account.SignOutAsync();
        Assert.False(account.HasDb);
        Assert.True(raised > 0);   // the page hears the sign-out
    }

    [Fact]
    public async Task A_late_answer_about_a_device_signed_out_meanwhile_leaves_the_new_sign_in_alone()
    {
        SignedInAlready();
        var late = new TaskCompletionSource<HttpResponseMessage>();
        var fake = new Fake((r, _) => r.RequestUri!.AbsolutePath switch
        {
            "/v1/token" when r.Headers.Authorization!.Parameter == "sd1_stored" => late.Task,   // the old device's renewal, answered later
            "/v1/auth/exchange" => Task.FromResult(Ours(json: new { device_token = "sd1_new", device_id = "d2", access_token = "v4.public.new",
                exp = (_clock.Now + TimeSpan.FromHours(24)).ToString("O"), ent = new[] { "db" }, until = (string?)null })),
            _ => Task.FromResult(Ours(HttpStatusCode.NoContent)),
        });
        Task? browser = null;
        var account = new Account(_dir, Routes(fake), url =>
        {
            var q = HttpUtility.ParseQueryString(url.Query);
            browser = Browse($"http://127.0.0.1:{q["port"]}/cb?code=O&state={q["state"]}");
        }, _clock);

        var renewal = account.GetAccessTokenAsync();
        await account.SignOutAsync();
        Assert.True(await account.SignInAsync(), account.Problem);
        await browser!;
        late.SetResult(Ours(HttpStatusCode.Unauthorized, new { error = "invalid_token" }));   // the old device was revoked
        Assert.Equal("v4.public.new", await renewal);

        Assert.True(account.SignedIn);
        Assert.True(account.HasDb);
        Assert.True(File.Exists(Path.Combine(_dir, "auth.dat")));
        Assert.True(new Account(_dir, Routes(fake)).SignedIn);
    }

    [Fact]
    public async Task Refresh_status_on_a_revoked_device_signs_this_pc_out()
    {
        SignedInAlready();
        var account = new Account(_dir, Routes(new Fake(_ => Ours(HttpStatusCode.Unauthorized, new { error = "invalid_token" }))), clock: _clock);
        await account.RefreshAsync();
        Assert.False(account.SignedIn);
        Assert.Contains("Sign in again", account.Problem);
        Assert.False(File.Exists(Path.Combine(_dir, "auth.dat")));
    }

    [Fact]
    public async Task Sign_out_revokes_at_the_server_and_forgets_locally()
    {
        SignedInAlready();
        var fake = new Fake(r =>
        {
            Assert.Equal("Bearer sd1_stored", r.Headers.Authorization!.ToString());
            return Ours(HttpStatusCode.NoContent);
        });
        var account = new Account(_dir, Routes(fake), clock: _clock);
        await account.SignOutAsync();
        Assert.Equal(["POST https://api.test.com/v1/auth/logout"], fake.Log);
        Assert.False(account.SignedIn);
        Assert.Null(account.Problem);
        Assert.False(File.Exists(Path.Combine(_dir, "auth.dat")));
    }

    [Fact]
    public async Task Sign_out_offline_still_forgets_locally()
    {
        SignedInAlready();
        var account = new Account(_dir, Routes(new Fake(_ => throw Down)), clock: _clock);
        await account.SignOutAsync();
        Assert.False(account.SignedIn);
        Assert.Contains("Signed out on this PC", account.Problem);
        Assert.False(File.Exists(Path.Combine(_dir, "auth.dat")));
        Assert.False(new Account(_dir, Routes(new Fake(_ => throw Down))).SignedIn);
    }

    [Fact]
    public void Benefits_NameTheShownFlags_InTokenOrder()
    {
        Assert.Equal("Supporter: community shader hash database, beta builds, alpha builds and priority requests.",
            new AccountStatus(["db", "beta", "alpha", "prio", "internal", "future"], null).Benefits);
        Assert.Equal("Supporter: community shader hash database and beta builds.", new AccountStatus(["db", "beta"], null).Benefits);
        Assert.Null(new AccountStatus(["internal"], null).Benefits);
    }
}
