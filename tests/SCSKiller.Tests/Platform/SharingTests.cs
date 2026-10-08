using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;
using static SCSKiller.Tests.Platform.CommunityTests;

namespace SCSKiller.Tests.Platform;

// Anonymous uploads of this PC's recordings against a fake handler: no network,
// synthetic recordings only.
public class SharingTests : IDisposable
{
    static readonly Uri Com = new("https://api.test.com/"), Io = new("https://api.test.io/");
    const string Content = "0123456789abcdef0123456789abcdef01234567", Anon = "sd1_test-anon", Patreon = "sd1_patreon-member-device";
    internal static readonly byte[] Shader = "DXBC not a root signature: shader code that must never leave the PC"u8.ToArray();
    readonly string _dir = Path.Combine(Path.GetTempPath(), "scskiller-sharing-test-" + Guid.NewGuid().ToString("N")[..8]);
    readonly Clock _clock = new();
    readonly List<(string Line, byte[] Body, string? Meta)> _sent = [];
    Func<HttpRequestMessage, HttpResponseMessage>? _upload;
    bool _enabled = true;

    string GameDir => Path.Combine(_dir, "games", "steam_480");

    public SharingTests() => Directory.CreateDirectory(GameDir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    /// <summary>A recording as the recorder writes it: shader bytes, a root signature and a pipeline per vertex shader.</summary>
    internal static byte[] LocalRecording(params string[] vsShas)
    {
        var rs = RootSignature();
        using var m = new MemoryStream();
        PsoDb.WriteBlob(m, Sha1(rs), rs);
        PsoDb.WriteBlob(m, Sha1(Shader), Shader);
        foreach (var vs in vsShas.DefaultIfEmpty(Sha1(Shader)))
            PsoDb.Write(m, 'S', PsoDb.Stream(Sha1(rs), new Dictionary<int, string> { [(int)Stage.Vertex] = vs }, [], 3, [PsoDb.R16G16B16A16Float], 0));
        return m.ToArray();
    }

    /// <summary><paramref name="psos"/> distinct pipelines, 1.26 KB each hash-only (real ones average 0.8 KB).</summary>
    static byte[] BigRecording(int psos)
    {
        var rs = RootSignature();
        PsoDb.LayoutElem[] layout = [.. Enumerable.Range(0, 16).Select(i => new PsoDb.LayoutElem("TEXCOORD", i, 2, (uint)i * 16))];
        using var m = new MemoryStream();
        PsoDb.WriteBlob(m, Sha1(rs), rs);
        PsoDb.WriteBlob(m, Sha1(Shader), Shader);
        for (var i = 0; i < psos; i++)
            PsoDb.Write(m, 'S', PsoDb.Stream(Sha1(rs), new Dictionary<int, string> { [(int)Stage.Vertex] = Sha1(BitConverter.GetBytes(i)), [(int)Stage.Pixel] = Sha1(BitConverter.GetBytes(~i)) },
                layout, 3, [PsoDb.R16G16B16A16Float], 0));
        return m.ToArray();
    }

    void Record(byte[] db, int ageMinutes = 0)
    {
        var path = Path.Combine(GameDir, "recording.db");
        File.WriteAllBytes(path, db);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-ageMinutes));
    }

    Sharing Make() => new(_dir, () => _enabled, new RouteFailover(new Fake(Answer), [Com, Io], _clock), _clock);

    HttpResponseMessage Answer(HttpRequestMessage r)
    {
        var body = r.Content?.ReadAsByteArrayAsync().Result ?? [];
        var meta = r.Headers.TryGetValues("X-SCSK-Upload", out var v) ? Encoding.UTF8.GetString(Base64Url.DecodeFromChars(v.Single())) : null;
        lock (_sent) _sent.Add(($"{r.Method} {r.RequestUri}{(r.Headers.Authorization is { } a ? " " + a : "")}", body, meta));
        return r.RequestUri!.AbsolutePath switch
        {
            "/v1/devices" => Ours(HttpStatusCode.OK, """{"device_token":"sd1_test-anon","device_id":"dev1"}"""u8.ToArray()),
            "/v1/upload" => _upload?.Invoke(r) ?? Ours(HttpStatusCode.Accepted, """{"upload_id":"up1","records":1,"new_records":1}"""u8.ToArray()),
            _ => Ours(HttpStatusCode.NotFound),
        };
    }

    static UploadMeta Meta() => new("steam:480@1", Content, "Unreal 4.26", "nvidia", "1.4.0", [new("amdxcffx64.dll", new string('a', 40))]);
    List<string> Uploads => _sent.Where(s => s.Line.StartsWith("POST https://api.test.com/v1/upload")).Select(s => s.Line).ToList();

    [Fact]
    public async Task The_upload_is_hash_only_with_the_anonymous_device_and_never_the_patreon_one()
    {
        // signed in with Patreon: auth.dat holds the member device token, which uploads must never carry
        var auth = Path.Combine(_dir, "auth.dat");
        File.WriteAllBytes(auth, Dpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(new { Member = Patreon })));
        var authBefore = File.ReadAllBytes(auth);
        Record(LocalRecording());

        var got = await Make().ShareAsync(GameDir, Content, Meta);

        Assert.Equal(("up1", 1, 1), (got!.UploadId, got.Psos, got.NewPsos));
        Assert.Equal(["POST https://api.test.com/v1/devices", $"POST https://api.test.com/v1/upload Bearer {Anon}"], _sent.Select(s => s.Line));
        Assert.DoesNotContain(_sent, s => s.Line.Contains(Patreon));
        Assert.Equal(authBefore, File.ReadAllBytes(auth));   // untouched

        // the body: Brotli of a canonical hash-only recording; Canonical(local: false) is the server's check and throws on shader bytes
        var body = _sent[1].Body;
        var records = Core.Planning.HashOnly.Decompress(body);
        Assert.Equal(records, Core.Planning.HashOnly.Canonical(records, local: false, out _));
        Assert.Equal(["B", "S"], records.Select(r => r.Tag.ToString()));
        Assert.Equal(PsoDb.Hex(System.Security.Cryptography.SHA1.HashData(RootSignature())), PsoDb.Hex(records[0].Payload.AsSpan(0, 20)));
        Assert.DoesNotContain(records, r => r.Payload.AsSpan().IndexOf(Shader) >= 0);

        // the metadata header: the contract's snake_case keys
        var meta = JsonDocument.Parse(_sent[1].Meta!).RootElement;
        Assert.Equal(["store_build_key", "content_hash", "engine", "vendor", "app_version", "middleware"], meta.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("steam:480@1", Content, "amdxcffx64.dll"), (meta.GetProperty("store_build_key").GetString(), meta.GetProperty("content_hash").GetString(),
            meta.GetProperty("middleware")[0].GetProperty("name").GetString()));

        // the upload device lives in its own file, DPAPI-protected, apart from auth.dat
        var dat = File.ReadAllBytes(Path.Combine(_dir, "upload.dat"));
        Assert.Equal(-1, dat.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Anon)));
        Assert.Contains(Anon, Encoding.UTF8.GetString(Dpapi.Unprotect(dat)));
        Assert.Equal("dev1", Make().DeviceId);
    }

    /// <summary>Every recorder tag reaches the upload, ray tracing state objects included; shader and DXIL library bytes don't.</summary>
    [Fact]
    public async Task Ray_tracing_state_objects_are_uploaded_hash_only()
    {
        var local = EveryRecorderTag(out var libSha);
        using (var f = File.Create(Path.Combine(GameDir, "recording.db")))
            foreach (var r in local) PsoDb.Write(f, r.Tag, r.Payload);
        Assert.NotNull(await Make().ShareAsync(GameDir, Content, Meta));
        var records = Core.Planning.HashOnly.Decompress(_sent.Single(s => s.Line.Contains("/v1/upload")).Body);
        Assert.Equal(local.Where(r => r.Tag is 'R' or 'A').Select(r => r.Key), records.Where(r => r.Tag is 'R' or 'A').Select(r => r.Key));
        Assert.Equal(Core.Planning.HashOnly.Tags.Order(), records.Select(r => r.Tag).Distinct().Order());
        Assert.DoesNotContain(records, r => r.Tag == 'B' && PsoDb.Hex(r.Payload.AsSpan(0, 20)) == libSha);
        Assert.DoesNotContain(records, r => r.Payload.AsSpan().IndexOf(Shader) >= 0);
    }

    /// <summary>Under a layer wrapping the device (a mod), only the game's records are shared: neither the 'W' records, nor
    /// the records the driver got from the layer (changed or its own, with their 'N'), nor root signatures only those name.
    /// The server's check still refuses a 'W'.</summary>
    [Fact]
    public async Task A_layer_s_records_are_never_uploaded()
    {
        var (rs, layerRs) = (RootSignature(), SCSKiller.Tests.Planning.MiddlewarePackTests.Container("RTS0", "layer root signature"));
        var mod = "DXBC the layer's replacement shader"u8.ToArray();
        PsoDb.Rec Vs(byte[] root, byte[] vs) => new('S', PsoDb.Stream(Sha1(root), new Dictionary<int, string> { [(int)Stage.Vertex] = Sha1(vs) }, [], 3, [PsoDb.R16G16B16A16Float], 0));
        var (game, driver, own) = (Vs(rs, Shader), Vs(layerRs, mod), new PsoDb.Rec('C', PsoDb.Compute(Sha1(layerRs), Sha1(mod))));
        using (var f = File.Create(Path.Combine(GameDir, "recording.db")))
            foreach (var r in new[] { Blob(rs), Blob(Shader), game, Blob(layerRs),
                         Blob(mod), driver, new PsoDb.NvState(driver.Key, 0, 1001, 3, 0).ToRec(), RecordingsTests.W(driver, game), own, RecordingsTests.W(own, null) })
                PsoDb.Write(f, r.Tag, r.Payload);

        Assert.NotNull(await Make().ShareAsync(GameDir, Content, Meta));
        var records = Core.Planning.HashOnly.Decompress(_sent.Single(s => s.Line.Contains("/v1/upload")).Body);
        Assert.Equal([Sha1(rs), game.Key], records.Select(r => r.Tag == 'B' ? PsoDb.Hex(r.Payload.AsSpan(0, 20)) : r.Key));
        Assert.Throws<InvalidDataException>(() => Core.Planning.HashOnly.Canonical([.. records, RecordingsTests.W(game, null)], local: false, out _));
    }

    /// <summary>A 'W' names pipeline and state object records only (the recorder writes one per create the layer changed);
    /// root signatures go by what names them. One only the layer's records name is dropped with them; one a game's record
    /// names too is the game's (the layer passed it on unchanged) and is shared; a 'W' naming a root signature's hash names
    /// no record and changes nothing.</summary>
    [Fact]
    public void A_layer_s_root_signatures_go_by_the_records_naming_them()
    {
        var (rs, layerRs) = (RootSignature(), SCSKiller.Tests.Planning.MiddlewarePackTests.Container("RTS0", "layer root signature"));
        PsoDb.Rec Cs(byte[] root, string cs) => new('C', PsoDb.Compute(Sha1(root), Sha1(System.Text.Encoding.UTF8.GetBytes(cs))));
        var (game, driver, passed) = (Cs(rs, "game"), Cs(layerRs, "game"), Cs(rs, "the layer's own on the game's root signature"));
        List<string> Shared(params PsoDb.Rec[] recs) =>
            [.. Core.Planning.HashOnly.Canonical(recs, local: true, out _).Select(r => r.Tag == 'B' ? PsoDb.Hex(r.Payload.AsSpan(0, 20)) : r.Key)];

        Assert.Equal([Sha1(rs), game.Key], Shared(Blob(rs), Blob(layerRs), game, driver, RecordingsTests.W(driver, game)));
        Assert.Equal([Sha1(rs), game.Key], Shared(Blob(rs), game, passed, RecordingsTests.W(passed, null)));
        var named = new PsoDb.Rec('W', [.. Convert.FromHexString(Sha1(rs)), .. new byte[20]]);
        Assert.Equal([Sha1(rs), game.Key], Shared(Blob(rs), game, named));
    }

    static PsoDb.Rec Blob(byte[] b) => new('B', [.. System.Security.Cryptography.SHA1.HashData(b), .. b]);

    /// <summary>With the build's shader list (the index's, <see cref="Sharing.ShippedFile"/>), a pipeline naming a shader
    /// neither it nor a middleware DLL ships is flagged 'L' in the upload; one the DLLs ship isn't. Without the list (or for
    /// another build) nothing is flagged, and the list arriving re-examines the recording.</summary>
    [Fact]
    public async Task A_pipeline_with_a_shader_the_build_doesnt_ship_is_flagged()
    {
        string runtime = Sha1("built at run time"u8.ToArray()), dll = Sha1("in a DLL next to the exe"u8.ToArray());
        Record(LocalRecording(Sha1(Shader), runtime, dll));
        async Task<List<PsoDb.Rec>> Share(bool expectUpload)
        {
            _sent.Clear();
            var got = await Make().ShareAsync(GameDir, Content, Meta, () => [dll]);
            Assert.Equal(expectUpload, got != null);
            return expectUpload ? Core.Planning.HashOnly.Decompress(_sent.Single(s => s.Line.Contains("/v1/upload")).Body) : [];
        }
        Assert.DoesNotContain(await Share(true), r => r.Tag == 'L');

        // both lists are 40 bytes: only their write times tell them apart, and back-to-back writes can share a file-time tick
        var list = Path.Combine(GameDir, Sharing.ShippedFile);
        File.WriteAllBytes(list, [.. Convert.FromHexString("fedcba9876543210fedcba9876543210fedcba98"), .. Convert.FromHexString(Sha1(Shader))]);
        File.SetLastWriteTimeUtc(list, DateTime.UtcNow.AddMinutes(-5));
        await Share(false);   // another build's list: no flags, so the same upload, already sent

        File.WriteAllBytes(list, [.. Convert.FromHexString(Content), .. Convert.FromHexString(Sha1(Shader))]);
        var records = await Share(true);
        var flagged = records.Where(r => r.Tag == 'L').Select(Core.Planning.HashOnly.Target).ToList();
        var pso = Assert.Single(records, r => r.Tag == 'S' && PsoDb.Parse(r).Stages.ContainsValue(runtime));
        Assert.Equal([pso.Key], flagged);
        Assert.Equal(records, Core.Planning.HashOnly.Canonical(records, local: false, out _));
    }

    [Fact]
    public async Task A_recording_is_uploaded_once_per_change_and_the_device_registered_once()
    {
        var sharing = Make();
        Record(LocalRecording("a".PadLeft(40, 'a')), ageMinutes: 10);
        Assert.NotNull(await sharing.ShareAsync(GameDir, Content, Meta));
        Assert.Null(await sharing.ShareAsync(GameDir, Content, Meta));   // unchanged
        Assert.Null(await Make().ShareAsync(GameDir, Content, Meta));    // after a restart too (shared.json)

        // the recorder appended shader bytes only: the hash-only form is the same, so nothing new to share
        var db = LocalRecording("a".PadLeft(40, 'a'));
        using (var m = new MemoryStream())
        {
            m.Write(db);
            PsoDb.WriteBlob(m, Sha1("DXBC another shader"u8.ToArray()), "DXBC another shader"u8);
            Record(m.ToArray(), ageMinutes: 5);
        }
        Assert.Null(await sharing.ShareAsync(GameDir, Content, Meta));

        Record(LocalRecording("a".PadLeft(40, 'a'), "b".PadLeft(40, 'b')));   // a new pipeline
        Assert.NotNull(await sharing.ShareAsync(GameDir, Content, Meta));
        Assert.Single(_sent, s => s.Line.Contains("/v1/devices"));
        Assert.Equal(2, Uploads.Count);
        Assert.NotNull(Sharing.Shared(GameDir)!.At);
    }

    [Fact]
    public async Task Nothing_is_uploaded_that_the_downloaded_entry_of_the_same_build_already_has()
    {
        const string vs = "cccccccccccccccccccccccccccccccccccccccc";
        File.WriteAllBytes(Path.Combine(GameDir, "community.db"), CommunityTests.HashOnly(vs));
        File.WriteAllText(Path.Combine(GameDir, "community.json"), JsonSerializer.Serialize(new CommunityDownload("obj", Content, 1, DateTimeOffset.Now)));
        Record(LocalRecording(vs), ageMinutes: 10);
        Assert.Null(await Make().ShareAsync(GameDir, Content, Meta));
        Assert.Empty(_sent);

        Record(LocalRecording(vs, "dddddddddddddddddddddddddddddddddddddddd"));   // one pipeline the community lacks
        Assert.NotNull(await Make().ShareAsync(GameDir, Content, Meta));
        Assert.Single(Uploads);

        // the same records for another build are new there (the download is of the other build)
        Assert.NotNull(await Make().ShareAsync(GameDir, "fedcba9876543210fedcba9876543210fedcba98", Meta));
        Assert.Equal(2, Uploads.Count);
    }

    [Fact]
    public async Task A_429_backs_off_for_its_retry_after_then_retries()
    {
        var sharing = Make();
        _upload = _ =>
        {
            var r = Ours(HttpStatusCode.TooManyRequests, """{"error":"rate_limited"}"""u8.ToArray());
            r.Headers.RetryAfter = new(TimeSpan.FromHours(2));
            return r;
        };
        Record(LocalRecording());
        Assert.Null(await sharing.ShareAsync(GameDir, Content, Meta));
        Assert.NotNull(sharing.Problem);
        _clock.Now += TimeSpan.FromHours(1);
        Assert.Null(await sharing.ShareAsync(GameDir, Content, Meta));
        Assert.Single(Uploads);   // no request while backing off

        _upload = null;
        _clock.Now += TimeSpan.FromHours(1.1);
        Assert.NotNull(await sharing.ShareAsync(GameDir, Content, Meta));   // not marked done by the 429
        Assert.Equal(2, Uploads.Count);
        Assert.Null(sharing.Problem);
    }

    [Fact]
    public async Task A_refused_recording_is_not_retried_and_an_unknown_device_registers_again()
    {
        _upload = _ => Ours(HttpStatusCode.BadRequest, """{"error":"invalid_recording","detail":"x"}"""u8.ToArray());
        Record(LocalRecording(), ageMinutes: 10);
        Assert.Null(await Make().ShareAsync(GameDir, Content, Meta));
        Assert.Null(await Make().ShareAsync(GameDir, Content, Meta));
        Assert.Single(Uploads);

        // the device expired (90 days unused): a new one, once
        var calls = 0;
        _upload = _ => ++calls == 1 ? Ours(HttpStatusCode.Unauthorized, """{"error":"invalid_token"}"""u8.ToArray()) : null!;
        Record(LocalRecording("e".PadLeft(40, 'e')));
        Assert.NotNull(await Make().ShareAsync(GameDir, Content, Meta));
        Assert.Equal(2, _sent.Count(s => s.Line.Contains("/v1/devices")));
    }

    [Fact]
    public async Task A_long_session_over_the_old_8_MB_is_uploaded_even_after_the_old_silent_skip()
    {
        Record(BigRecording(7_000));
        var db = new FileInfo(Path.Combine(GameDir, "recording.db"));
        File.WriteAllText(Path.Combine(GameDir, "shared.json"),   // a pre-"|2" stamp
            $$"""{"Stamp":"{{db.Length}}:{{db.LastWriteTimeUtc.Ticks}}|{{Content}}","Body":null,"At":null,"UploadId":null,"Psos":0,"NewPsos":0}""");

        Assert.NotNull(await Make().ShareAsync(GameDir, Content, Meta));

        var body = _sent.Single(s => s.Line.Contains("/v1/upload")).Body;
        var records = Core.Planning.HashOnly.Decompress(body);
        Assert.True(records.Sum(r => 5L + r.Payload.Length) > 8 << 20);
        Assert.True(body.Length <= Sharing.MaxBody);
        Assert.Equal(7_000, records.Count(r => r.Tag == 'S'));
    }

    /// <summary>Past one upload's 20k records: several uploads, each valid on its own; a pass cut short by a 429 resumes with
    /// the ones the server doesn't have yet.</summary>
    /// <summary>What a layer made is read again before each upload of a long session: a record learned as a layer's during
    /// the first upload is left out of the second; a list that can't be read then stops the rest, which stays unmarked.</summary>
    [Fact]
    public async Task Each_upload_of_a_long_session_reads_what_a_layer_made_again()
    {
        Record(BigRecording(25_000));
        var last = PsoDb.Read(Path.Combine(GameDir, "recording.db")).Where(r => r.Tag == 'S').Select(r => r.Key).Max(StringComparer.Ordinal)!;   // in the second upload
        var known = new HashSet<string>();
        _upload = _ => { lock (known) known.Add(last); return null!; };
        Assert.NotNull(await Make().ShareAsync(GameDir, Content, Meta, layerMade: () => { lock (known) return new HashSet<string>(known); }));
        var chunks = _sent.Where(s => s.Line.Contains("/v1/upload")).Select(s => Core.Planning.HashOnly.Decompress(s.Body)).ToList();
        Assert.Equal(2, chunks.Count);
        Assert.DoesNotContain(chunks[1], r => r.Key == last);
        Assert.Equal(24_999, chunks.Sum(c => c.Count(r => r.Tag == 'S')));

        File.Delete(Path.Combine(GameDir, "shared.json"));
        _sent.Clear();
        var reads = 0;
        var sharing = Make();
        Assert.Null(await sharing.ShareAsync(GameDir, Content, Meta, layerMade: () => ++reads <= 2 ? new HashSet<string>() : throw new IOException("locked")));
        Assert.Single(_sent, s => s.Line.Contains("/v1/upload"));
        Assert.Contains("locked", sharing.Problem);
        Assert.Single(Sharing.Shared(GameDir)!.Sent!);   // the first upload, kept; the recording isn't marked shared

        File.Delete(Path.Combine(GameDir, "shared.json"));   // a damaged recording behind the list: the same
        _sent.Clear();
        reads = 0;
        sharing = Make();
        Assert.Null(await sharing.ShareAsync(GameDir, Content, Meta, layerMade: () => ++reads <= 2 ? new HashSet<string>() : throw new InvalidDataException("damaged")));
        Assert.Single(_sent, s => s.Line.Contains("/v1/upload"));
        Assert.Contains("damaged", sharing.Problem);
        Assert.Single(Sharing.Shared(GameDir)!.Sent!);
        Assert.Equal("", Sharing.Shared(GameDir)!.Stamp);   // not marked shared
    }

    [Fact]
    public async Task A_long_session_goes_in_several_uploads_and_a_pass_cut_short_resumes()
    {
        Record(BigRecording(45_000));   // 57 MB hash-only: over the origin's 32 MB for one upload
        var calls = 0;
        _upload = _ =>
        {
            if (++calls != 2) return null!;
            var r = Ours(HttpStatusCode.TooManyRequests, """{"error":"rate_limited"}"""u8.ToArray());
            r.Headers.RetryAfter = new(TimeSpan.FromHours(1));
            return r;
        };
        var sharing = Make();
        Assert.Null(await sharing.ShareAsync(GameDir, Content, Meta));
        Assert.NotNull(sharing.Problem);
        Assert.Single(Sharing.Shared(GameDir)!.Sent!);   // the first one, kept; the recording isn't stamped done

        _clock.Now += TimeSpan.FromHours(1.1);
        var got = await sharing.ShareAsync(GameDir, Content, Meta);
        Assert.Equal(2, got!.Psos);   // the fake's one record per accepted upload: the two left, the first not again
        Assert.Equal(4, Uploads.Count);
        var chunks = _sent.Where(s => s.Line.Contains("/v1/upload")).Select(s => s.Body).Where((_, i) => i != 1)
            .Select(b => Core.Planning.HashOnly.Decompress(b)).ToList();
        foreach (var c in chunks)
        {
            Assert.Equal(c, Core.Planning.HashOnly.Canonical(c, local: false, out _));   // what the server checks, per upload
            Assert.InRange(c.Count(r => r.Tag != 'B'), 1, Sharing.ChunkRecords);
            Assert.True(c.Sum(r => 5L + r.Payload.Length) <= Sharing.ChunkRaw + 4096);
        }
        Assert.Equal(45_000, chunks.SelectMany(c => c.Where(r => r.Tag == 'S').Select(r => r.Key)).Distinct().Count());
        Assert.Null(await sharing.ShareAsync(GameDir, Content, Meta));   // done
        Assert.Equal(4, Uploads.Count);
    }

    [Fact]
    public async Task A_blocked_device_stops_the_pass_and_the_recording_is_done_until_it_changes()
    {
        _upload = _ => Ours(HttpStatusCode.Forbidden, """{"error":"blocked"}"""u8.ToArray());
        Record(BigRecording(25_000), ageMinutes: 10);   // two uploads' worth
        Assert.Null(await Make().ShareAsync(GameDir, Content, Meta));
        Assert.Null(await Make().ShareAsync(GameDir, Content, Meta));
        Assert.Single(Uploads);
        Assert.Single(_sent, s => s.Line.Contains("/v1/devices"));   // a 403 is not a 401: no new device
    }

    [Fact]
    public async Task Sharing_turned_off_during_a_pass_sends_nothing_more()
    {
        Record(BigRecording(25_000));   // two uploads' worth
        _upload = _ =>
        {
            _enabled = false;   // the user turns it off while the first upload is on its way
            return null!;
        };
        var sharing = Make();
        Assert.Null(await sharing.ShareAsync(GameDir, Content, Meta));
        Assert.Single(Uploads);
        Assert.Single(Sharing.Shared(GameDir)!.Sent!);   // the one sent is kept; the recording isn't stamped done

        _enabled = true;
        _upload = null;
        Assert.NotNull(await sharing.ShareAsync(GameDir, Content, Meta));
        Assert.Equal(2, Uploads.Count);   // the rest, once it is on again
    }

    [Fact]
    public async Task Sharing_turned_off_before_the_retry_of_a_401_sends_it_no_more()
    {
        Record(LocalRecording());
        _upload = _ =>
        {
            _enabled = false;
            return Ours(HttpStatusCode.Unauthorized, """{"error":"invalid_token"}"""u8.ToArray());
        };
        Assert.Null(await Make().ShareAsync(GameDir, Content, Meta));
        Assert.Single(Uploads);
        Assert.Single(_sent, s => s.Line.Contains("/v1/devices"));   // no new device either
        Assert.Null(Sharing.Shared(GameDir));
    }

    [Fact]
    public async Task Sharing_off_sends_nothing_and_registers_nothing()
    {
        _enabled = false;
        Record(LocalRecording());
        Assert.Null(await Make().ShareAsync(GameDir, Content, Meta));
        Assert.Empty(_sent);
        Assert.False(File.Exists(Path.Combine(_dir, "upload.dat")));
        Assert.Null(Sharing.Shared(GameDir));
    }

    /// <summary>A recording that has a layer's pipeline without its 'W' (from before the recorder wrote one) uploads without
    /// it once another recording's 'W' named it (<see cref="MiddlewarePacks.LayerMade"/>), and without its root signature.</summary>
    [Fact]
    public async Task A_recording_upload_leaves_out_what_another_recording_knows_a_layer_made()
    {
        var (rs, layerRs) = (RootSignature(), SCSKiller.Tests.Planning.MiddlewarePackTests.Container("RTS0", "layer root signature"));
        var (game, layer) = (new PsoDb.Rec('C', PsoDb.Compute(Sha1(rs), Sha1(Shader))), new PsoDb.Rec('C', PsoDb.Compute(Sha1(layerRs), Sha1(Shader))));
        using (var f = File.Create(Path.Combine(GameDir, "recording.db")))
            foreach (var r in new[] { Blob(rs), Blob(Shader), game, Blob(layerRs), layer })
                PsoDb.Write(f, r.Tag, r.Payload);

        await Assert.ThrowsAsync<IOException>(() => Make().ShareAsync(GameDir, Content, Meta, layerMade: () => throw new IOException("locked")));
        Assert.Empty(_sent.Where(s => s.Line.Contains("/v1/upload")));   // the list unreadable: nothing shared, and not marked shared
        Assert.NotNull(await Make().ShareAsync(GameDir, Content, Meta, layerMade: () => new HashSet<string> { layer.Key }));
        var records = Core.Planning.HashOnly.Decompress(_sent.Single(s => s.Line.Contains("/v1/upload")).Body);
        Assert.Equal([Sha1(rs), game.Key], records.Select(r => r.Tag == 'B' ? PsoDb.Hex(r.Payload.AsSpan(0, 20)) : r.Key));
    }

    /// <summary>A pack's upload leaves out the records a layer made and the root signature only they name, whatever the pack
    /// file still holds (a share between a recording's import and the packs' exclusion, or after a crash there). The list is
    /// read again before each pack's payload: one learned during the pass applies to the next pack. A list that can't be read
    /// ends the pass with nothing shared.</summary>
    [Fact]
    public async Task A_pack_upload_leaves_out_a_layer_s_records_the_pack_still_holds()
    {
        var packs = Path.Combine(_dir, "packs");
        var (rs, layerRs) = (RootSignature(), SCSKiller.Tests.Planning.MiddlewarePackTests.Container("RTS0", "layer root signature"));
        var (game, layer) = (new PsoDb.Rec('C', PsoDb.Compute(Sha1(rs), Sha1(Shader))), new PsoDb.Rec('C', PsoDb.Compute(Sha1(layerRs), Sha1(Shader))));
        List<string> paths = [];
        foreach (var dllSha in new[] { new string('d', 40), new string('e', 40) })
        {
            var fsr = new MiddlewarePack("amd", "amd_fidelityfx_dx12.dll", dllSha, 1000, gpu: "nvidia");
            (fsr.RootSignatures[Sha1(rs)], fsr.RootSignatures[Sha1(layerRs)]) = (rs, layerRs);
            fsr.Add(game, "steam:480");
            fsr.Add(layer, "steam:480");
            paths.Add(Path.Combine(packs, "amd", MiddlewarePack.FileName(fsr.Header.Dll, dllSha)));
            fsr.Write(paths[^1]);
        }
        var sharing = Make();
        var known = new HashSet<string>();
        IReadOnlySet<string> Known() { var now = new HashSet<string>(known); known.Add(layer.Key); return now; }   // learned after the first read
        Assert.Equal(2, (await sharing.SharePacksAsync(packs, "nvidia", "1.4.0", Known)).Count);
        var bodies = _sent.Where(s => s.Line.Contains("/v1/upload")).Select(s => Core.Planning.HashOnly.Decompress(s.Body)
            .Select(r => r.Tag == 'B' ? PsoDb.Hex(r.Payload.AsSpan(0, 20)) : r.Key).ToList()).ToList();
        Assert.Equal(3 + 1, bodies[0].Count);   // the first pack: both root signatures and both pipelines
        Assert.Equal([Sha1(rs), game.Key], bodies[1]);

        var more = MiddlewarePack.Read(paths[0]);
        more.Add(new PsoDb.Rec('C', PsoDb.Compute(Sha1(rs), Sha1("another"u8.ToArray()))), "steam:480");
        more.Write(paths[0]);
        Assert.Empty(await sharing.SharePacksAsync(packs, "nvidia", "1.4.0", () => throw new IOException("locked")));
        Assert.Equal(2, _sent.Count(s => s.Line.Contains("/v1/upload")));
        Assert.Equal("locked", sharing.Problem);
    }

    [Fact]
    public async Task Upscaler_packs_upload_under_their_pack_key_once_per_change_and_only_for_the_shared_vendors()
    {
        var packs = Path.Combine(_dir, "packs");
        var rs = RootSignature();
        var dllSha = new string('d', 40);
        var fsr = new MiddlewarePack("amd", "amd_fidelityfx_dx12.dll", dllSha, 1000, gpu: "nvidia");
        fsr.RootSignatures[Sha1(rs)] = rs;
        fsr.Add(new PsoDb.Rec('C', PsoDb.Compute(Sha1(rs), Sha1(Shader))), "steam:480");
        var path = Path.Combine(packs, "amd", MiddlewarePack.FileName(fsr.Header.Dll, dllSha));
        fsr.Write(path);
        var opti = new MiddlewarePack("optiscaler", "OptiScaler.dll", dllSha, 1000, gpu: "nvidia");   // stays on this PC
        opti.Add(new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, Sha1(Shader))), "steam:480");
        opti.Write(Path.Combine(packs, "optiscaler", MiddlewarePack.FileName(opti.Header.Dll, dllSha)));
        var sharing = Make();

        Assert.Equal([("amd_fidelityfx_dx12.dll", 1)], await sharing.SharePacksAsync(packs, "nvidia", "1.4.0"));
        var up = _sent.Single(s => s.Line.Contains("/v1/upload"));
        Assert.EndsWith(" Bearer " + Anon, up.Line);
        var key = $"nvidia:amd:amd_fidelityfx_dx12.dll:{dllSha}";
        using (var meta = JsonDocument.Parse(up.Meta!))
            Assert.Equal((key, Core.Planning.HashOnly.PackHash(key), "nvidia"),
                (meta.RootElement.GetProperty("store_build_key").GetString(), meta.RootElement.GetProperty("content_hash").GetString(), meta.RootElement.GetProperty("vendor").GetString()));
        Assert.Equal("BC", string.Concat(Core.Planning.HashOnly.Decompress(up.Body).Select(r => r.Tag)));   // hash-only: the root signature and the PSO
        Assert.True(up.Body.AsSpan().IndexOf(Shader) < 0);

        Assert.Empty(await sharing.SharePacksAsync(packs, "nvidia", "1.4.0"));   // unchanged: not again
        fsr.Add(new PsoDb.Rec('C', PsoDb.Compute(Sha1(rs), Sha1("another"u8.ToArray()))), "steam:480");
        fsr.Write(path);
        Assert.Single(await sharing.SharePacksAsync(packs, "nvidia", "1.4.0"));   // it gained a record
        Assert.Equal(2, Uploads.Count);

        // a pack another GPU vendor filled (this PC before a GPU change), or one from before packs kept their GPU: not shared
        var xess = new MiddlewarePack("intel", "libxess.dll", dllSha, 1000, gpu: "amd");
        xess.Add(new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, Sha1(Shader))), "steam:480");
        xess.Write(Path.Combine(packs, "intel", MiddlewarePack.FileName(xess.Header.Dll, dllSha)));
        var old = new MiddlewarePack("amd", "ffx_fsr2_api_dx12_x64.dll", dllSha, 1000);
        old.Add(new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, Sha1(Shader))), "steam:480");
        old.Write(Path.Combine(packs, "amd", MiddlewarePack.FileName(old.Header.Dll, dllSha)));
        Assert.Empty(await sharing.SharePacksAsync(packs, "nvidia", "1.4.0"));
        Assert.Equal(2, Uploads.Count);

        _enabled = false;
        File.Delete(Path.Combine(_dir, "packs-shared.json"));
        Assert.Empty(await sharing.SharePacksAsync(packs, "nvidia", "1.4.0"));
        Assert.Equal(2, Uploads.Count);

        // turned off while the first of two changed packs is on its way: not the second
        var fsr2 = new MiddlewarePack("amd", "amd_fidelityfx_dx12.dll", new string('e', 40), 1000, gpu: "nvidia");
        fsr2.Add(new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, Sha1(Shader))), "steam:480");
        fsr2.Write(Path.Combine(packs, "amd", MiddlewarePack.FileName(fsr2.Header.Dll, fsr2.Header.ContentHash)));
        File.Delete(Path.Combine(_dir, "packs-shared.json"));
        _upload = _ =>
        {
            _enabled = false;
            return null!;
        };
        _enabled = true;
        await sharing.SharePacksAsync(packs, "nvidia", "1.4.0");
        Assert.Equal(3, Uploads.Count);
    }
}
