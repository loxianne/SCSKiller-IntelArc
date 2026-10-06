namespace SCSKiller.Core.App;

/// <summary>Client failover between the API's two routes (hosting.md §9): requests are written against
/// <see cref="Primary"/> (api.scskiller.com, through Cloudflare) and go to api.scskiller.io (the VPS) when the primary
/// can't be reached: DNS, connect, TLS or timeout, or a 5xx that isn't ours (no X-SCSK header: Cloudflare's 52x/530).
/// 4xx and our own 5xx are answers and never fail over. A POST fails over only when it can't have been sent.
/// After a failover the fallback stays first for <see cref="Sticky"/>, then a probe decides. Requests to other hosts pass through.
/// SCSKILLER_API overrides the routes (comma-separated, e.g. http://localhost:5080) for development.</summary>
public sealed class RouteFailover : DelegatingHandler
{
    public static readonly Uri[] DefaultRoutes = [new("https://api.scskiller.com/"), new("https://api.scskiller.io/")];
    public static readonly TimeSpan Sticky = TimeSpan.FromMinutes(30);
    /// <summary>Process-wide, so every client shares which route works (sign-in, community DB, beta updates).</summary>
    public static readonly RouteFailover Default = new();

    public IReadOnlyList<Uri> Routes { get; }
    public Uri Primary => Routes[0];
    readonly TimeProvider clock;
    readonly TimeSpan headersTimeout;
    readonly Lock gate = new();
    int current;               // index of the route that answered last
    DateTimeOffset stickyUntil;

    public RouteFailover(HttpMessageHandler? inner = null, IReadOnlyList<Uri>? routes = null, TimeProvider? clock = null, TimeSpan? headersTimeout = null)
        : base(inner ?? new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3), PooledConnectionLifetime = TimeSpan.FromMinutes(5) })   // lifetime: DNS changes reach a tray process
    {
        Routes = routes ?? FromEnvironment() ?? DefaultRoutes;
        this.clock = clock ?? TimeProvider.System;
        this.headersTimeout = headersTimeout ?? TimeSpan.FromSeconds(15);
    }

    static Uri[]? FromEnvironment() => Environment.GetEnvironmentVariable("SCSKILLER_API") is { Length: > 0 } v
        ? v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(h => new Uri(h.TrimEnd('/') + "/")).ToArray()
        : null;

    /// <summary>The route requests try first right now (the browser's sign-in page opens on it too).</summary>
    public Uri Current => Routes[First()];

    /// <summary>Caps each route's attempt of one request (default: 15 s to the headers), for a caller with a short total
    /// budget: a blackholed primary then still leaves the fallback time.</summary>
    public static readonly HttpRequestOptionsKey<TimeSpan> AttemptTimeout = new("SCSKiller.AttemptTimeout");

    /// <summary>The running return probe, if any (tests await it).</summary>
    public Task Probe { get; private set; } = Task.CompletedTask;

    // Once the stickiness expires, the first request after it starts a background GET <primary>/healthz (3 s, must carry
    // X-SCSK) and still goes to the fallback: the probe's success makes the primary first again, its failure keeps the
    // fallback for another Sticky. So a blocked primary never costs a request its connect timeout.
    int First()
    {
        lock (gate)
        {
            if (current != 0 && clock.GetUtcNow() >= stickyUntil && Probe.IsCompleted)
            {
                stickyUntil = DateTimeOffset.MaxValue;   // one probe at a time
                Probe = Task.Run(ProbeAsync);
            }
            return current;
        }
    }

    async Task ProbeAsync()
    {
        bool ok;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Primary, "healthz"));
            using var response = await base.SendAsync(request, timeout.Token).ConfigureAwait(false);
            ok = response.Headers.Contains("X-SCSK");
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { ok = false; }
        lock (gate) (current, stickyUntil) = ok ? (0, default) : (current, clock.GetUtcNow() + Sticky);
    }

    /// <summary>GET of a small public file (a content file) within <paramref name="budget"/> in total; each route gets half,
    /// so a blackholed Cloudflare still leaves .io time. The body of a success, else null (an error status is an answer: no
    /// fallback); never throws.</summary>
    public async Task<string?> GetContentAsync(string path, TimeSpan budget, int maxBytes)
    {
        using var http = new HttpClient(this, disposeHandler: false) { MaxResponseContentBufferSize = maxBytes };
        using var cts = new CancellationTokenSource(budget);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Primary, path));
        request.Options.Set(AttemptTimeout, budget / 2);
        try
        {
            using var r = await http.SendAsync(request, cts.Token);
            return r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync(cts.Token) : null;
        }
        catch (Exception) { return null; }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        if (Uri.Compare(uri, Primary, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
            return await base.SendAsync(request, ct).ConfigureAwait(false);
        var path = uri.PathAndQuery.TrimStart('/');
        var idempotent = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;
        var first = First();
        Exception? last = null;
        for (var i = 0; i < Routes.Count; i++)
        {
            var at = (first + i) % Routes.Count;
            request.RequestUri = new Uri(Routes[at], path);
            bool sent;   // whether the failure can have happened after the request went out
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(request.Options.TryGetValue(AttemptTimeout, out var cap) ? cap : headersTimeout);
            try
            {
                var response = await base.SendAsync(request, attempt.Token).ConfigureAwait(false);
                if ((int)response.StatusCode >= 500 && !response.Headers.Contains("X-SCSK"))   // the route failed, not our server
                {
                    if (!idempotent) return response;
                    last = new HttpRequestException($"{Routes[at].Host}: {(int)response.StatusCode} from the route", null, response.StatusCode);
                    response.Dispose();
                    continue;
                }
                // only a request that failed over moves the routes, and only from where it started: one that began on the
                // fallback before a probe put the primary back must not undo the probe's verdict
                lock (gate) if (at != first && current == first) (current, stickyUntil) = (at, clock.GetUtcNow() + Sticky);
                return response;
            }
            catch (HttpRequestException e) when (!ct.IsCancellationRequested)
            {
                last = e;
                sent = e.HttpRequestError is not (HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
                    or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError);
            }
            catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
            {
                last = e;
                sent = attempt.IsCancellationRequested;   // our headers timeout; otherwise the handler's ConnectTimeout
            }
            if (sent && !idempotent) break;
        }
        throw new ApiUnreachableException(last);
    }
}

public sealed class ApiUnreachableException(Exception? inner) : HttpRequestException("Can't reach the SCSKiller server.", inner);
