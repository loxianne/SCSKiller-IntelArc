using System.Net;
using System.Text.Json.Nodes;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

/// <summary>The first-run welcome text: the bundled default, validation of the server's untrusted copy, .com then .io.</summary>
public class WelcomeTests
{
    static readonly string Valid = new StreamReader(typeof(WelcomeContent).Assembly.GetManifestResourceStream("SCSKiller.Core.App.welcome.json")!).ReadToEnd();

    static string With(Action<JsonObject> edit)
    {
        var o = JsonNode.Parse(Valid)!.AsObject();
        edit(o);
        return o.ToJsonString();
    }

    static string LinkTo(string url) => With(o => o["link"] = new JsonObject { ["text"] = "x", ["url"] = url });

    [Fact]
    public void Bundled_default_is_valid()
    {
        Assert.Equal("Welcome to SCSKiller", WelcomeContent.Default.Title);
        Assert.Equal(3, WelcomeContent.Default.Paragraphs.Count);
        Assert.Equal("Sign in with Patreon", WelcomeContent.Default.SignIn);
        Assert.StartsWith("Help improve the community shader hash database", WelcomeContent.Default.Share);
    }

    [Theory]
    [InlineData("https://scskiller.com/support")]
    [InlineData("https://www.scskiller.io/a?b=c")]
    [InlineData("https://beta.scskiller.xyz/")]
    [InlineData("https://www.patreon.com/scskiller")]
    [InlineData("https://patreon.com/scskiller")]
    public void Accepts_https_links_to_our_domains_and_patreon(string url) => Assert.NotNull(WelcomeContent.Parse(LinkTo(url)));

    [Fact]
    public void Accepts_no_link() => Assert.Null(WelcomeContent.Parse(With(o => o["link"] = null))!.Link);

    [Theory]
    [InlineData("http://scskiller.com/support")]            // not https
    [InlineData("https://evil.com/")]
    [InlineData("https://scskiller.com.evil.com/")]
    [InlineData("https://evilscskiller.com/")]
    [InlineData("https://scskiller.com@evil.com/")]
    [InlineData("https://evil.com@scskiller.com/")]         // user info
    [InlineData("https://scskiller.com:8443/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/support")]
    [InlineData("")]
    public void Rejects_other_links(string url) => Assert.Null(WelcomeContent.Parse(LinkTo(url)));

    [Fact]
    public void Rejects_over_long_text_missing_fields_and_bad_json()
    {
        Assert.NotNull(WelcomeContent.Parse(Valid));
        Assert.Null(WelcomeContent.Parse(With(o => o["title"] = new string('x', 101))));
        Assert.NotNull(WelcomeContent.Parse(With(o => o["title"] = new string('x', 100))));
        Assert.Null(WelcomeContent.Parse(With(o => o["paragraphs"] = new JsonArray(Enumerable.Repeat("p", 9).Select(p => (JsonNode?)p).ToArray()))));
        Assert.Null(WelcomeContent.Parse(With(o => o["paragraphs"] = new JsonArray(new string('x', 1001)))));
        Assert.Null(WelcomeContent.Parse(With(o => o["paragraphs"] = new JsonArray())));
        Assert.Null(WelcomeContent.Parse(With(o => o["paragraphs"] = new JsonArray("ok", null))));
        Assert.Null(WelcomeContent.Parse(With(o => o["signIn"] = new string('x', 41))));
        Assert.Null(WelcomeContent.Parse(With(o => o["share"] = new string('x', 151))));
        Assert.NotNull(WelcomeContent.Parse(With(o => o["share"] = new string('x', 150))));
        Assert.Null(WelcomeContent.Parse(With(o => o["link"] = new JsonObject { ["text"] = new string('x', 101), ["url"] = "https://scskiller.com/" })));
        foreach (var field in new[] { "title", "paragraphs", "share", "signIn", "dismiss" })
            Assert.Null(WelcomeContent.Parse(With(o => o.Remove(field))));
        Assert.Null(WelcomeContent.Parse(With(o => o["title"] = " ")));
        Assert.Null(WelcomeContent.Parse(With(o => o["link"] = new JsonObject { ["text"] = "x" })));   // no url
        Assert.Null(WelcomeContent.Parse(With(o => o["title"] = 5)));
        foreach (var bad in new[] { "", "null", "[]", "{", "<html>blocked</html>", Valid[..^10] })
            Assert.Null(WelcomeContent.Parse(bad));
    }

    sealed class Server(Func<string, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public readonly List<string> Asked = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Asked.Add(url);
            return answer(url, ct);
        }
    }

    static Task<HttpResponseMessage> Ok(string body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    const string Com = "https://api.scskiller.com/v1/content/welcome.json", Io = "https://api.scskiller.io/v1/content/welcome.json";
    static string Titled(string t) => With(o => o["title"] = t);
    static readonly TimeSpan Hung = TimeSpan.FromMinutes(1);   // a request that never comes: a failure, not a wait

    static RouteFailover Via(Server s, TimeProvider? clock = null) => new(s, RouteFailover.DefaultRoutes, clock);

    /// <summary>Time that moves only by <see cref="Advance"/>, which fires the timers due by then (one-shot, as the
    /// budgets' cancellation sources use them).</summary>
    internal sealed class ManualClock : TimeProvider
    {
        readonly List<Timer> timers = [];
        TimeSpan now;
        public override long GetTimestamp() { lock (timers) return now.Ticks; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var t = new Timer(this, callback, state);
            t.Change(dueTime, period);
            return t;
        }

        public void Advance(TimeSpan by)
        {
            lock (timers) now += by;
            for (Timer? due; (due = Due()) != null;) due.Fire();
        }

        Timer? Due() { lock (timers) return timers.Where(t => t.At <= now).MinBy(t => t.At); }

        sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public TimeSpan At;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock.timers)
                {
                    clock.timers.Remove(this);
                    if (dueTime == Timeout.InfiniteTimeSpan) return true;
                    At = clock.now + dueTime;
                    clock.timers.Add(this);
                }
                return true;
            }
            public void Fire()
            {
                Dispose();
                callback(state);
            }
            public void Dispose() { lock (clock.timers) clock.timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return default; }
        }
    }

    [Fact]
    public async Task Fetch_asks_com_and_stops_there_when_it_answers()
    {
        var s = new Server((_, _) => Ok(Titled("from com")));
        Assert.Equal("from com", (await WelcomeContent.FetchAsync(Via(s)))!.Title);
        Assert.Equal([Com], s.Asked);

        s = new Server((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));   // an answer, not a connect error
        Assert.Null(await WelcomeContent.FetchAsync(Via(s)));
        Assert.Equal([Com], s.Asked);

        s = new Server((_, _) => Ok(LinkTo("https://evil.com/")));   // invalid: rejected, no fallback
        Assert.Null(await WelcomeContent.FetchAsync(Via(s)));
        Assert.Equal([Com], s.Asked);
    }

    [Fact]
    public async Task Fetch_falls_back_to_io_on_a_connect_error_or_timeout()
    {
        var s = new Server((url, _) => url == Com ? throw new HttpRequestException("connection refused") : Ok(Titled("from io")));
        Assert.Equal("from io", (await WelcomeContent.FetchAsync(Via(s)))!.Title);
        Assert.Equal([Com, Io], s.Asked);

        s = new Server((url, _) => url == Com ? throw new TaskCanceledException("timed out", new TimeoutException()) : Ok(Titled("from io")));
        Assert.Equal("from io", (await WelcomeContent.FetchAsync(Via(s)))!.Title);
        Assert.Equal([Com, Io], s.Asked);

        s = new Server((_, _) => throw new HttpRequestException("no network"));
        Assert.Null(await WelcomeContent.FetchAsync(Via(s)));
        Assert.Equal([Com, Io], s.Asked);
    }

    [Fact]
    public async Task Fetch_gives_up_when_the_budget_runs_out()
    {
        var asked = new SemaphoreSlim(0);
        var s = new Server(async (_, ct) => { asked.Release(); await Task.Delay(Timeout.Infinite, ct); return null!; });   // neither route answers
        var clock = new ManualClock();
        var fetch = WelcomeContent.FetchAsync(Via(s, clock), TimeSpan.FromMilliseconds(200));
        Assert.True(await asked.WaitAsync(Hung));
        clock.Advance(TimeSpan.FromMilliseconds(100));   // .com's half of the budget
        Assert.True(await asked.WaitAsync(Hung));
        Assert.False(fetch.IsCompleted);   // .io has the rest
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Null(await fetch);
        Assert.Equal([Com, Io], s.Asked);
    }

    [Fact]
    public async Task Fetch_with_com_blackholed_still_gets_io_within_the_3_s_budget()
    {
        var asked = new SemaphoreSlim(0);
        var s = new Server(async (url, ct) =>
        {
            asked.Release();
            if (url == Com) await Task.Delay(Timeout.Infinite, ct);   // dropped SYNs: no error, just silence
            return await Ok(Titled("from io"));
        });
        var clock = new ManualClock();
        var fetch = WelcomeContent.FetchAsync(Via(s, clock));
        Assert.True(await asked.WaitAsync(Hung));
        clock.Advance(TimeSpan.FromSeconds(1.5));   // .com's half of the 3 s budget
        Assert.Equal("from io", (await fetch)!.Title);
        Assert.Equal([Com, Io], s.Asked);
    }
}
