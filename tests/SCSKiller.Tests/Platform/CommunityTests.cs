using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;

namespace SCSKiller.Tests.Platform;

// The community database's read side against a fake handler: no network, synthetic recordings only.
public class CommunityTests : IDisposable
{
    static readonly Uri Com = new("https://api.test.com/"), Io = new("https://api.test.io/");
    readonly string _dir = Path.Combine(Path.GetTempPath(), "scskiller-community-test-" + Guid.NewGuid().ToString("N")[..8]);
    readonly Clock _clock = new();

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override long GetTimestamp() => Now.UtcTicks;   // the monotonic clock moves with Now
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    internal sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<string> Log = [];   // "GET https://api.test.com/v1/x [bytes=80-] [Bearer t]"
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            lock (Log) Log.Add($"{r.Method} {r.RequestUri}{(r.Headers.Range is { } range ? " " + range : "")}{(r.Headers.Authorization is { } a ? " " + a : "")}");
            return Task.FromResult(answer(r));
        }
    }

    internal static HttpResponseMessage Ours(HttpStatusCode code, byte[]? body = null)
    {
        var r = new HttpResponseMessage(code) { Content = new ByteArrayContent(body ?? []) };
        r.Headers.Add("X-SCSK", "1");
        return r;
    }

    internal static byte[] Record(char kind, Action<Span<byte>> fill)
    {
        var r = new byte[CommunityManifest.RecordSize];
        r[0] = (byte)kind;
        fill(r);
        return r;
    }
    internal static byte[] Header(uint epoch = 1) => Record('M', r => { r[1] = 1; BinaryPrimitives.WriteUInt32LittleEndian(r[4..], epoch); });
    internal static byte[] Entry(string contentHash, byte[] obj, int psos, ushort uploaders = 2) => Record('E', r =>
    {
        BinaryPrimitives.WriteUInt16LittleEndian(r[2..], uploaders);
        Convert.FromHexString(contentHash).CopyTo(r[4..]);
        SHA256.HashData(obj).CopyTo(r[24..]);
        BinaryPrimitives.WriteUInt32LittleEndian(r[56..], (uint)obj.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(r[60..], (uint)psos);
    });
    internal static byte[] Alias(string storeBuildKey, string contentHash) => Record('A', r =>
    {
        SHA1.HashData(Encoding.UTF8.GetBytes(storeBuildKey)).CopyTo(r[4..]);
        Convert.FromHexString(contentHash).CopyTo(r[24..]);
    });
    internal static byte[] Tombstone(string contentHash) => Record('T', r => Convert.FromHexString(contentHash).CopyTo(r[4..]));
    internal static byte[] Manifest(params byte[][] records) => [.. Header(), .. records.SelectMany(r => r)];

    internal static string Sha1(byte[] b) => PsoDb.Hex(SHA1.HashData(b));

    /// <summary>A root signature as the database carries it: a DXBC container of one RTS0 part (made-up bytes), padded to
    /// <paramref name="size"/>.</summary>
    internal static byte[] RootSignature(int size = 68)
    {
        var c = new byte[size];
        "DXBC"u8.CopyTo(c);
        BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(24), (uint)c.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(32), 36);
        "RTS0"u8.CopyTo(c.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(40), (uint)size - 44);
        // an empty 1.0 root signature: no parameters, no static samplers
        foreach (var (at, v) in new[] { (44, 1u), (52, 24u), (60, 24u) }) BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(at), v);
        return c;
    }

    /// <summary>A hash-only recording: the root signature's 'B' and one pipeline stream naming <paramref name="vsSha"/>.</summary>
    internal static byte[] HashOnly(string vsSha)
    {
        var rs = RootSignature();
        using var m = new MemoryStream();
        PsoDb.WriteBlob(m, Sha1(rs), rs);
        PsoDb.Write(m, 'S', PsoDb.Stream(Sha1(rs), new Dictionary<int, string> { [(int)Stage.Vertex] = vsSha }, [], 3, [PsoDb.R16G16B16A16Float], 0));
        return m.ToArray();
    }

    internal static byte[] Brotli(byte[] raw)
    {
        using var m = new MemoryStream();
        using (var b = new BrotliStream(m, CompressionLevel.Optimal)) b.Write(raw);
        return m.ToArray();
    }

    const string Content = "0123456789abcdef0123456789abcdef01234567";
    static readonly Game Game = new("steam:480", "Spacewar", Store.Steam, @"C:\nowhere", @"C:\nowhere\x.exe", "1");

    Community Make(Fake fake, string? token = "t", Func<bool, string?>? tokens = null) =>
        new(_dir, (fresh, _) => Task.FromResult(tokens != null ? tokens(fresh) : token), new RouteFailover(fake, [Com, Io], _clock), _clock);

    [Fact]
    public void The_manifest_replays_entries_aliases_and_tombstones_in_order()
    {
        var (a, b, c) = ("a"u8.ToArray(), "b"u8.ToArray(), "c"u8.ToArray());
        const string other = "fedcba9876543210fedcba9876543210fedcba98";
        var m = CommunityManifest.Parse(Manifest(Entry(Content, a, 10), Alias("steam:480@1", Content), Entry(Content, b, 20, 3),
            Entry(other, c, 5), Tombstone(other), Record('Z', _ => { })));   // an unknown kind is skipped
        Assert.Equal(1u, m.Epoch);
        var e = m.Find(Game, null)!;   // by the store build
        Assert.Equal((Content, PsoDb.Hex(SHA256.HashData(b)), 1L, 20, 3), (e.ContentHash, e.Object, e.Size, e.Psos, e.Uploaders));   // the last 'E' wins
        Assert.Same(e, m.Find(Game with { Version = "2" }, Content));   // by content hash (a fresh index)
        Assert.Null(m.Find(Game with { Version = "2" }, null));
        Assert.Null(m.Find(Game with { Version = null }, null));
        Assert.Null(m.Find(Game, other));   // withdrawn
        Assert.Equal(Sha1(Encoding.UTF8.GetBytes("steam:480@1")), CommunityManifest.AliasKey(Game));
        Assert.Throws<InvalidDataException>(() => CommunityManifest.Parse(new byte[81]));
        Assert.Throws<InvalidDataException>(() => CommunityManifest.Parse(Entry(Content, a, 1)));   // no header
    }

    [Fact]
    public async Task The_manifest_is_cached_for_hours_then_brought_up_to_date_with_a_range_request()
    {
        var obj = "object"u8.ToArray();
        var file = Manifest(Alias("steam:480@1", Content));
        var fake = new Fake(r =>
        {
            if (r.Headers.Range?.Ranges.Single().From is not { } from) return Ours(HttpStatusCode.OK, file);
            if (from == file.Length) return Ours(HttpStatusCode.RequestedRangeNotSatisfiable);
            var part = Ours(HttpStatusCode.PartialContent, file[(int)from..]);
            part.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, file.Length - 1, file.Length);
            return part;
        });
        var community = Make(fake);

        Assert.Null((await community.ManifestAsync())!.Find(Game, null));   // an alias with no entry points at nothing
        Assert.Equal(["GET https://api.test.com/v1/manifest/0"], fake.Log);   // no copy: epoch 0, whole

        fake.Log.Clear();
        _clock.Now += TimeSpan.FromHours(3);
        await community.ManifestAsync();
        await Make(fake).ManifestAsync();   // a restart reads the copy
        Assert.Empty(fake.Log);

        file = [.. file, .. Entry(Content, obj, 7)];   // published meanwhile
        _clock.Now += TimeSpan.FromHours(1);
        Assert.Equal(7, (await community.ManifestAsync())!.Find(Game, null)!.Psos);
        Assert.Equal(["GET https://api.test.com/v1/manifest/1 bytes=160-"], fake.Log);   // only the records after the copy's end

        fake.Log.Clear();
        _clock.Now += TimeSpan.FromHours(5);
        Assert.Equal(7, (await Make(fake).ManifestAsync())!.Find(Game, null)!.Psos);   // 416: nothing new
        Assert.Equal(["GET https://api.test.com/v1/manifest/1 bytes=240-"], fake.Log);
        Assert.Equal(file, File.ReadAllBytes(Path.Combine(_dir, "community", "manifest.bin")));
    }

    [Fact]
    public async Task A_refused_or_failed_manifest_check_waits_before_the_next()
    {
        var answer = HttpStatusCode.TooManyRequests;
        var fake = new Fake(r =>
        {
            if (answer == 0) throw new HttpRequestException(HttpRequestError.ConnectionError, "unreachable");
            var a = Ours(answer, answer == HttpStatusCode.OK ? Manifest() : null);
            if (answer == HttpStatusCode.TooManyRequests) a.Headers.RetryAfter = new(TimeSpan.FromMinutes(20));
            if (answer == HttpStatusCode.ServiceUnavailable) a.Headers.RetryAfter = new(TimeSpan.FromDays(30));
            return a;
        });
        var community = Make(fake);
        int Checks() => fake.Log.Count(l => l.Contains("/v1/manifest/"));

        Assert.Null(await community.ManifestAsync());
        community.Expire();   // a user's refresh doesn't skip the wait either
        Assert.Null(await community.ManifestAsync());
        Assert.Equal(1, Checks());
        _clock.Now += TimeSpan.FromMinutes(21);
        answer = HttpStatusCode.ServiceUnavailable;
        await community.ManifestAsync();
        Assert.Equal(2, Checks());
        _clock.Now += TimeSpan.FromHours(23);
        await community.ManifestAsync();
        Assert.Equal(2, Checks());   // 30 days asked: a day at most
        _clock.Now += TimeSpan.FromHours(1);
        answer = 0;
        await community.ManifestAsync();
        _clock.Now += TimeSpan.FromMinutes(4);
        await community.ManifestAsync();
        Assert.Equal(4, Checks());   // offline (each route once): 5 minutes
        _clock.Now += TimeSpan.FromMinutes(1);
        answer = HttpStatusCode.OK;
        Assert.NotNull(await community.ManifestAsync());
        Assert.Equal(5, Checks());
    }

    [Fact]
    public async Task Offline_or_down_keeps_the_copy_and_says_so_quietly()
    {
        var up = true;
        var fake = new Fake(r => up ? Ours(HttpStatusCode.OK, Manifest(Alias("steam:480@1", Content), Entry(Content, "o"u8.ToArray(), 3)))
            : throw new HttpRequestException(HttpRequestError.ConnectionError, "unreachable"));
        var community = Make(fake);
        await community.ManifestAsync();
        up = false;
        _clock.Now += TimeSpan.FromHours(5);
        Assert.NotNull((await community.ManifestAsync())!.Find(Game, null));
        Assert.Contains("Can't reach", community.Problem);
        var restarted = Make(new Fake(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "unreachable")));
        Assert.NotNull((await restarted.ManifestAsync())!.Find(Game, null));   // the copy on disk still answers after a restart

        var entry = (await community.ManifestAsync())!.Find(Game, null)!;
        fake.Log.Clear();
        Assert.Null(await community.DownloadAsync(entry, _dir));
        Assert.Null(await community.DownloadAsync(entry, _dir));   // offline: the next game doesn't wait for the timeouts again
        Assert.Equal(2, fake.Log.Count(l => l.Contains("/v1/o/")));   // each route once
    }

    [Fact]
    public async Task A_refused_manifest_check_waits_for_its_retry_after_instead_of_asking_once_per_game()
    {
        var refuse = true;
        var fake = new Fake(_ =>
        {
            if (!refuse) return Ours(HttpStatusCode.OK, Manifest(Alias("steam:480@1", Content)));
            var r = Ours(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new(TimeSpan.FromMinutes(30));
            return r;
        });
        var community = Make(fake);
        for (var game = 0; game < 3; game++) Assert.Null(await community.ManifestAsync());
        Assert.Single(fake.Log);
        Assert.NotNull(community.Problem);

        refuse = false;
        _clock.Now += TimeSpan.FromMinutes(31);
        Assert.NotNull(await community.ManifestAsync());
        Assert.Equal(2, fake.Log.Count);

        // Retry-After as a date
        var dated = new Fake(_ =>
        {
            var r = Ours(HttpStatusCode.ServiceUnavailable);
            r.Headers.RetryAfter = new(_clock.Now + TimeSpan.FromMinutes(10));
            return r;
        });
        var other = new Community(Path.Combine(_dir, "dated"), (_, _) => Task.FromResult<string?>("t"), new RouteFailover(dated, [Com, Io], _clock), _clock);
        await other.ManifestAsync();
        _clock.Now += TimeSpan.FromMinutes(9);
        await other.ManifestAsync();
        Assert.Single(dated.Log);
        _clock.Now += TimeSpan.FromMinutes(2);
        await other.ManifestAsync();
        Assert.Equal(2, dated.Log.Count);

        // unreachable: each route once, then not again for a while
        var down = new Fake(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "unreachable"));
        var offline = new Community(Path.Combine(_dir, "other"), (_, _) => Task.FromResult<string?>("t"), new RouteFailover(down, [Com, Io], _clock), _clock);
        for (var game = 0; game < 3; game++) Assert.Null(await offline.ManifestAsync());
        Assert.Equal(2, down.Log.Count);
        Assert.Contains("Can't reach", offline.Problem);
    }

    /// <summary>A body that sends nothing after the headers, until cancelled. <see cref="Reading"/>: its first read began.</summary>
    internal sealed class Stalled : Stream
    {
        public readonly TaskCompletionSource Reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal static HttpResponseMessage StalledBody(Stalled? body = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body ?? new Stalled()) };
        r.Headers.Add("X-SCSK", "1");
        return r;
    }

    [Fact]
    public async Task A_body_that_stalls_after_its_headers_is_given_up_and_backs_off()
    {
        var obj = Brotli(HashOnly(new string('1', 40)));
        var entry = new CommunityEntry(Content, PsoDb.Hex(SHA256.HashData(obj)), obj.Length, 1, 2);
        var (clock, body) = (new WelcomeTests.ManualClock(), new Stalled());
        var fake = new Fake(_ => StalledBody(body));
        var community = new Community(_dir, (_, _) => Task.FromResult<string?>("t"), new RouteFailover(fake, [Com, Io], clock), clock);
        var game = Path.Combine(_dir, "games", "steam_480");

        var download = community.DownloadAsync(entry, game);   // no CancellationToken: a background pass's
        await body.Reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(community.BodyIdle);   // the body's idle time, on the community's clock
        Assert.Null(await download.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("didn't answer in time", community.Problem);
        Assert.Null(await community.DownloadAsync(entry, game));   // backing off: not asked again yet
        Assert.Single(fake.Log);
        Assert.False(File.Exists(Path.Combine(game, "community.db")));
    }

    /// <summary>A body sent a few bytes at a time, <paramref name="gap"/> apart. Each read waits on the reading thread and
    /// completes synchronously: a Task.Delay's continuation waits for a thread-pool thread, which a loaded test run starves
    /// for seconds, so the gaps the reader saw outgrew the idle limit though the stub kept to its 250 ms.</summary>
    sealed class Dribble(byte[] data, int chunks, TimeSpan gap) : Stream
    {
        int at;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (at >= data.Length) return ValueTask.FromResult(0);
            ct.WaitHandle.WaitOne(gap);
            ct.ThrowIfCancellationRequested();   // given up while it waited
            var n = Math.Min(Math.Min(buffer.Length, (data.Length + chunks - 1) / chunks), data.Length - at);
            data.AsMemory(at, n).CopyTo(buffer);
            at += n;
            return ValueTask.FromResult(n);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => at; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_slow_body_that_keeps_coming_is_not_given_up()
    {
        var obj = Brotli(HashOnly(new string('1', 40)));
        var entry = new CommunityEntry(Content, PsoDb.Hex(SHA256.HashData(obj)), obj.Length, 1, 2);
        var fake = new Fake(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Dribble(obj, 12, TimeSpan.FromMilliseconds(250))) };
            r.Headers.Add("X-SCSK", "1");
            return r;
        });
        var community = new Community(_dir, (_, _) => Task.FromResult<string?>("t"), new RouteFailover(fake, [Com, Io], _clock), _clock)
            { BodyIdle = TimeSpan.FromSeconds(2) };
        var took = System.Diagnostics.Stopwatch.StartNew();
        Assert.NotNull(await community.DownloadAsync(entry, Path.Combine(_dir, "games", "steam_480")));   // 12 x 250 ms: 1.5 times the idle limit in all, each gap an eighth of it
        Assert.True(took.Elapsed > community.BodyIdle);
        Assert.Null(community.Problem);
    }

    [Fact]
    public async Task A_download_is_checked_by_sha256_and_kept_with_its_pso_count()
    {
        var obj = Brotli(HashOnly(new string('1', 40)));
        var entry = new CommunityEntry(Content, PsoDb.Hex(SHA256.HashData(obj)), obj.Length, 1, 2);
        var game = Path.Combine(_dir, "games", "steam_480");
        var body = obj.ToArray();
        body[^1] ^= 1;   // corrupted on the way
        var fake = new Fake(_ => Ours(HttpStatusCode.OK, body));
        var community = Make(fake);

        Assert.Null(await community.DownloadAsync(entry, game));
        Assert.Contains("checksum", community.Problem);
        Assert.False(File.Exists(Path.Combine(game, "community.db")));
        Assert.Equal([$"GET https://api.test.com/v1/o/{entry.Object} Bearer t"], fake.Log);

        body = obj;
        var got = await community.DownloadAsync(entry, game);
        Assert.Equal((entry.Object, 1, _clock.Now), (got!.Object, got.Psos, got.DownloadedAt));
        Assert.Equal(got, Community.Downloaded(game));
        Assert.Equal(HashOnly(new string('1', 40)), File.ReadAllBytes(Path.Combine(game, "community.db")));
        Assert.Null(community.Problem);
    }

    [Fact]
    public async Task Not_entitled_downloads_nothing()
    {
        var fake = new Fake(_ => Ours(HttpStatusCode.OK));
        Assert.Null(await Make(fake, token: null).DownloadAsync(new CommunityEntry(Content, new string('a', 64), 10, 1, 1), _dir));
        Assert.Empty(fake.Log);
    }

    [Fact]
    public async Task A_401_retries_once_with_a_fresh_token_and_a_429_backs_off()
    {
        var obj = Brotli(HashOnly(new string('1', 40)));
        var entry = new CommunityEntry(Content, PsoDb.Hex(SHA256.HashData(obj)), obj.Length, 1, 2);
        var quota = false;
        var fake = new Fake(r => quota ? WithRetryAfter(Ours(HttpStatusCode.TooManyRequests))
            : r.Headers.Authorization!.Parameter == "old" ? Ours(HttpStatusCode.Unauthorized) : Ours(HttpStatusCode.OK, obj));
        var community = Make(fake, tokens: fresh => fresh ? "new" : "old");

        Assert.NotNull(await community.DownloadAsync(entry, Path.Combine(_dir, "g1")));
        Assert.Equal([$"GET https://api.test.com/v1/o/{entry.Object} Bearer old", $"GET https://api.test.com/v1/o/{entry.Object} Bearer new"], fake.Log);

        quota = true;
        fake.Log.Clear();
        Assert.Null(await community.DownloadAsync(entry, Path.Combine(_dir, "g2")));
        Assert.Contains("limit", community.Problem);
        Assert.Null(await community.DownloadAsync(entry, Path.Combine(_dir, "g2")));   // backing off: not asked again
        Assert.Single(fake.Log, l => l.Contains("Bearer old"));
        _clock.Now += TimeSpan.FromMinutes(11);
        quota = false;
        Assert.NotNull(await community.DownloadAsync(entry, Path.Combine(_dir, "g2")));

        static HttpResponseMessage WithRetryAfter(HttpResponseMessage r) { r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10)); return r; }
    }

    /// <summary>A damaged compact recording: a record declaring 2 GB in a stream that can't seek (its Brotli) ends the read
    /// like a torn tail, with nothing allocated from that length.</summary>
    [Fact]
    public void A_record_length_past_a_streams_end_allocates_nothing()
    {
        var packed = new MemoryStream();
        using (var b = new System.IO.Compression.BrotliStream(packed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            b.Write([(byte)'S', 0xff, 0xff, 0xff, 0x7f, 1, 2, 3]);
        packed.Position = 0;
        using var s = new System.IO.Compression.BrotliStream(packed, System.IO.Compression.CompressionMode.Decompress);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Empty(PsoDb.Read(s).ToList());
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 16 << 20);
    }

    [Fact]
    public void An_entry_of_up_to_250k_records_is_read()
    {
        var rs = RootSignature();
        var sha = Sha1(rs);
        using var m = new MemoryStream();
        PsoDb.WriteBlob(m, sha, rs);   // the root signature counts too
        for (var i = 1; i < Core.Planning.HashOnly.MaxEntryRecords; i++) PsoDb.Write(m, 'C', PsoDb.Compute(sha, i.ToString("x40")));
        Assert.Equal(250_000, Core.Planning.HashOnly.MaxEntryRecords);
        Assert.Equal(249_999, Community.Check(m.ToArray()));
        PsoDb.Write(m, 'C', PsoDb.Compute(sha, new string('f', 40)));
        Assert.Null(Community.Check(m.ToArray()));
    }

    [Fact]
    public async Task A_download_over_the_decompressed_cap_is_ignored()
    {
        Assert.Equal(320 << 20, Community.MaxRaw);   // a valid download between 256 and 320 MiB would hold about 1 GB in the test
        var rs = RootSignature(Core.Planning.HashOnly.MaxRootSignature);
        var sha = Sha1(rs);
        void Write(Stream s, long copies)
        {
            for (var i = 0L; i < copies; i++) PsoDb.WriteBlob(s, sha, rs);   // the same root signature again: allowed, kept once
            PsoDb.Write(s, 'C', PsoDb.Compute(sha, new string('1', 40)));
        }
        using (var few = new MemoryStream())
        {
            Write(few, 3);
            Assert.Equal(1, Community.Check(few.ToArray()));
        }
        using var packed = new MemoryStream();
        using (var b = new BrotliStream(packed, CompressionLevel.Fastest, leaveOpen: true)) Write(b, Community.MaxRaw / (25L + rs.Length) + 1);
        var obj = packed.ToArray();
        var entry = new CommunityEntry(Content, PsoDb.Hex(SHA256.HashData(obj)), obj.Length, 1, 2);
        var game = Path.Combine(_dir, "games", "steam_480");
        var community = Make(new Fake(_ => Ours(HttpStatusCode.OK, obj)));

        Assert.Null(await community.DownloadAsync(entry, game));
        Assert.Contains("can't read was ignored", community.Problem);
        Assert.False(File.Exists(Path.Combine(game, "community.db")));
    }

    [Fact]
    public void Only_root_signatures_and_pipelines_are_accepted()
    {
        Assert.Equal(1, Community.Check(HashOnly(new string('1', 40))));
        var shader = "DXBC shader bytes"u8.ToArray();
        using var m = new MemoryStream();
        PsoDb.WriteBlob(m, Sha1(shader), shader);   // shader code: never from the database
        Assert.Null(Community.Check(m.ToArray()));
        var rs = RootSignature();
        m.SetLength(0);
        PsoDb.WriteBlob(m, new string('0', 40), rs);   // a root signature under another hash
        Assert.Null(Community.Check(m.ToArray()));
        m.SetLength(0);
        PsoDb.Write(m, 'P', new byte[48]);   // a plan item
        Assert.Null(Community.Check(m.ToArray()));
        Assert.Null(Community.Check([.. HashOnly(new string('1', 40)), (byte)'S', 9, 0]));   // a torn tail
        // a root-signature-only container whose RTS0 part is empty: nothing a planner could read
        byte[] empty = [.. "DXBC"u8, .. new byte[16], 1, 0, 0, 0, 44, 0, 0, 0, 1, 0, 0, 0, 36, 0, 0, 0, .. "RTS0"u8, 0, 0, 0, 0];
        m.SetLength(0);
        PsoDb.WriteBlob(m, Sha1(empty), empty);
        PsoDb.Write(m, 'C', PsoDb.Compute(Sha1(empty), Sha1(SharingTests.Shader)));
        Assert.Null(Community.Check(m.ToArray()));
    }

    /// <summary>A local recording with one record of every tag the recorder writes (proxy.cpp: 'B' root signature and shader
    /// blobs, 'G' / 'C' / 'S' PSOs, 'R' / 'A' ray tracing state objects, 'N' NVAPI state), and the 'L' flag an upload adds
    /// (<see cref="Core.Planning.HashOnly.LocalOnly"/>): stripped to hash-only (<see cref="Core.Planning.HashOnly.Canonical"/>,
    /// what Sharing and admin import do), compressed and back, checked like a download (<see cref="Community.Check"/>) and by
    /// the server's rule, every tag but the shader blobs is still there; the DXIL library is a hash the install rehydrates.</summary>
    internal static List<PsoDb.Rec> EveryRecorderTag(out string libSha)
    {
        var rs = RootSignature();
        var lib = Planning.RtCollectionTests.Library((10, "MaterialCHS", 64));
        libSha = Sha1(lib);
        var collection = new PsoDb.Rec('R', RtCollections.Collection(lib, libSha, Sha1(rs), Sha1(rs), Sha1(rs), 64, 8, 1, 4)!);
        var compute = new PsoDb.Rec('C', PsoDb.Compute(Sha1(rs), Sha1(SharingTests.Shader)));
        return [new('B', [.. Convert.FromHexString(Sha1(rs)), .. rs]), new('B', [.. Convert.FromHexString(libSha), .. lib]),
            new('B', [.. Convert.FromHexString(Sha1(SharingTests.Shader)), .. SharingTests.Shader]),
            new('G', new byte[616]), compute,
            new('S', PsoDb.Stream(Sha1(rs), new Dictionary<int, string> { [(int)Stage.Vertex] = Sha1(SharingTests.Shader) }, [], 3, [PsoDb.R16G16B16A16Float], 0)),
            collection, new('A', [.. Convert.FromHexString(collection.Key), .. collection.Payload]),
            new PsoDb.NvState(collection.Key, 0, 1001, 2, 0).ToRec(), new PsoDb.NvState(compute.Key, 0, 1001, 3, 0).ToRec(),
            new('L', Convert.FromHexString(compute.Key))];
    }

    [Fact]
    public void Every_recorder_tag_survives_the_hash_only_round_trip()
    {
        var local = EveryRecorderTag(out var libSha);
        Assert.Equal(Core.Planning.HashOnly.Tags.Order(), local.Select(r => r.Tag).Distinct().Order());
        var raw = new MemoryStream();
        var canonical = Core.Planning.HashOnly.Canonical(local, local: true, out var dropped);
        foreach (var r in canonical) PsoDb.Write(raw, r.Tag, r.Payload);
        Assert.Equal((2, 0), (dropped.ShaderBlobs, dropped.StateObjects)); // the shader and the library
        var back = Core.Planning.HashOnly.Decompress(Core.Planning.HashOnly.Compress(PsoDb.Read(new MemoryStream(raw.ToArray()))));
        Assert.Equal(3, Community.Check(raw.ToArray()));
        Assert.Equal(back, Core.Planning.HashOnly.Canonical(back, local: false, out _));
        Assert.Equal(Core.Planning.HashOnly.Tags.Order(), back.Select(r => r.Tag).Distinct().Order());
        Assert.Equal(local.Where(r => r.Tag != 'B').Select(r => r.Key).Order(), back.Where(r => r.Tag != 'B').Select(r => r.Key).Order());
        Assert.DoesNotContain(back, r => r.Tag == 'B' && PsoDb.Hex(r.Payload.AsSpan(0, 20)) == libSha);
        Assert.Contains(libSha, Rehydrate.References(back)); // pulled from the install like any shader
    }

    [Fact]
    public void The_union_keeps_local_records_and_adds_the_communitys_blobs_first()
    {
        Directory.CreateDirectory(_dir);
        string local = Path.Combine(_dir, "recording.db"), community = Path.Combine(_dir, "community.db"), all = Path.Combine(_dir, "merged.db");
        var vs = "local vs"u8.ToArray();
        PsoDb.WriteCompact(local, [new('B', [.. Convert.FromHexString(Sha1(vs)), .. vs]),
            new('S', PsoDb.Stream(PsoDb.Zero, new Dictionary<int, string> { [(int)Stage.Vertex] = Sha1(vs) }, [], 3, [], 0))]);
        File.WriteAllBytes(community, [.. HashOnly(Sha1(vs)), .. HashOnly(new string('2', 40))]);

        Community.Union(local, community, all);
        var recs = PsoDb.Read(all).ToList();
        Assert.False(PsoDb.IsCompact(all));   // a proxy db, for the planner and the warm
        Assert.Equal(PsoDb.Read(local).Concat(PsoDb.Read(community)).Select(r => r.Key).Distinct().Order(), recs.Select(r => r.Key).Order());
        Assert.All(recs.SkipWhile(r => r.Tag == 'B'), r => Assert.NotEqual('B', r.Tag));
        Assert.Equal(vs, recs[0].Payload[20..]);   // local first

        File.Delete(local);
        Community.Union(local, community, all);
        Assert.Equal(PsoDb.Read(community).Select(r => r.Key).Distinct().Order(), PsoDb.Read(all).Select(r => r.Key).Order());
    }
}
