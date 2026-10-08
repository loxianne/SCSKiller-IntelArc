using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Planning;

public class RehydrateTests(ITestOutputHelper output)
{
    static readonly EngineInfo Engine = new("Fake", "1", null, "D3D12", false, null);
    static readonly Game Game = new("test:1", "Fake", Store.Other, @"C:\nowhere", @"C:\nowhere\fake.exe");

    /// <summary>The blobs the records of these dbs name that none of them carries (sorted); empty = replayable as is.</summary>
    internal static List<string> Unresolved(params string[] dbs)
    {
        var recs = dbs.SelectMany(db => PsoDb.Read(db)).ToList();
        var refs = Rehydrate.References(recs);
        refs.ExceptWith(recs.Where(r => r.Tag == 'B').Select(r => PsoDb.Hex(r.Payload.AsSpan(0, 20))));
        return [.. refs.Order(StringComparer.Ordinal)];
    }

    /// <summary>Serves fixed bytes by SHA-1, like an engine reader over an install.</summary>
    sealed class FakeReader(Dictionary<string, byte[]> install) : IEngineReader
    {
        public readonly List<IReadOnlySet<string>> Requests = [];
        public EngineInfo? Detect(Game game) => Engine;
        public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) =>
            new("content-1", ["PCD3D_SM6"], new Dictionary<string, ShaderInfo>(), []);
        public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
        {
            Requests.Add(sha1s);
            foreach (var (h, b) in install) if (sha1s.Contains(h)) sink(h, b);
        }
    }

    static (string Sha, byte[] Bytes) Blob(string s) { var b = System.Text.Encoding.ASCII.GetBytes(s); return (PsoDb.Hex(SHA1.HashData(b)), b); }

    [Fact]
    public void AddsABlobForEveryReferencedShaderAndReportsTheMissingOnes()
    {
        var rs = Blob("root signature");
        var (vs, ps, cs, gone) = (Blob("vs"), Blob("ps"), Blob("cs"), Blob("not in this build"));
        var stream = PsoDb.Stream(rs.Sha, new Dictionary<int, string> { [(int)Stage.Vertex] = vs.Sha, [(int)Stage.Pixel] = ps.Sha }, [], 3, [PsoDb.R16G16B16A16Float], 0);
        byte[] compute = [.. Convert.FromHexString(rs.Sha), .. Convert.FromHexString(cs.Sha)];
        byte[] compute2 = [.. Convert.FromHexString(rs.Sha), .. Convert.FromHexString(gone.Sha)];

        var dir = Ff7.TempDir("rehydrate");
        var input = Path.Combine(dir, "hashonly.db");
        using (var f = File.Create(input))
        {
            PsoDb.Write(f, 'S', stream);
            PsoDb.WriteBlob(f, rs.Sha, rs.Bytes); // a recording keeps its root signatures
            PsoDb.Write(f, 'C', compute);
            PsoDb.Write(f, 'C', compute2);
        }
        var reader = new FakeReader(new[] { vs, ps, cs, Blob("unreferenced") }.ToDictionary(x => x.Sha, x => x.Bytes));
        var outDb = Path.Combine(dir, "out", "recording.db");

        var r = Rehydrate.Run(input, outDb, Game, Engine, reader, reader.Index(Game, Engine, null, default), "content-1");

        Assert.Equal(5, r.Referenced);
        Assert.Equal(1, r.AlreadyPresent);
        Assert.Equal(3, r.Found);
        Assert.Equal([gone.Sha], r.Missing);
        Assert.False(r.Complete);
        Assert.True(r.ContentHashMatches);
        Assert.Equal(new FileInfo(outDb).Length, r.OutputBytes);
        Assert.Equal(new[] { vs.Sha, ps.Sha, cs.Sha, gone.Sha }.Order(), Assert.Single(reader.Requests).Order()); // not the root signature it has

        var recs = PsoDb.Read(outDb).ToList();
        Assert.Equal(PsoDb.Read(input).Where(x => x.Tag != 'B').Select(x => x.Key), recs.Where(x => x.Tag != 'B').Select(x => x.Key)); // same records, same order
        Assert.All(recs.SkipWhile(x => x.Tag == 'B'), x => Assert.NotEqual('B', x.Tag)); // blobs first
        var blobs = recs.Where(x => x.Tag == 'B').ToDictionary(x => PsoDb.Hex(x.Payload.AsSpan(0, 20)), x => x.Payload[20..]);
        Assert.Equal(4, blobs.Count);
        foreach (var b in new[] { rs, vs, ps, cs }) Assert.Equal(b.Bytes, blobs[b.Sha]);
        Assert.Equal([gone.Sha], RehydrateTests.Unresolved(outDb));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(outDb)!, "*.tmp"));

        Assert.Null(Rehydrate.Run(input, outDb, Game, Engine, reader).ContentHashMatches);
        Assert.False(Rehydrate.Run(input, outDb, Game, Engine, reader, reader.Index(Game, Engine, null, default), "content-2").ContentHashMatches);
    }

    [Fact]
    public void PlanAndD3D11ItemsAreReferencesToo()
    {
        var (a, b, c) = (Blob("a"), Blob("b"), Blob("c"));
        var recs = new[]
        {
            new PsoDb.Rec('P', PsoDb.Item(new string('1', 40), PsoDb.Zero, new Dictionary<int, string> { [(int)Stage.Compute] = a.Sha }, null)),
            new PsoDb.Rec('1', PsoDb.D3D11Item(Stage.Pixel, b.Sha)),
            new PsoDb.Rec('B', [.. Convert.FromHexString(c.Sha), .. c.Bytes]),
        };
        Assert.Equal(new[] { a.Sha, b.Sha }.Order(), Rehydrate.References(recs).Order()); // zero root signature and template key aren't blobs
    }

    /// <summary>A hash-only FF7 Rebirth recording (NVIDIA PC) against this machine's install: every referenced
    /// shader is in it when the builds match. Runs only where both exist.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void Ff7HashOnlyRecordingRehydratesFromTheInstall()
    {
        var hashOnly = Path.Combine(TestEnv.DevDir, "ff7", "ff7rebirth-recording-hashonly.db");
        if (!File.Exists(hashOnly)) return;
        Game? game;
        try { game = new SteamSource().Discover().FirstOrDefault(g => g.Id == Ff7.Game.Id); }
        catch (Exception) { return; } // no Steam
        if (game == null) return;
        var dir = Ff7.TempDir("rehydrate-ff7");
        var reader = new UnrealReader(Path.Combine(dir, "data"));
        var engine = reader.Detect(game)!;
        var index = reader.Index(game, engine, null, CancellationToken.None);
        var outDb = Path.Combine(dir, "recording.db");
        var r = Rehydrate.Run(hashOnly, outDb, game, engine, reader, index);
        output.WriteLine($"content hash {index.ContentHash}: {r}");
        Assert.Equal(994, PsoDb.Read(outDb).Count(x => x.Tag != 'B'));
        Assert.Equal(142, r.AlreadyPresent);
        Assert.Empty(r.Missing);
        Assert.Empty(RehydrateTests.Unresolved(outDb));
        Assert.All(PsoDb.Read(outDb).Where(x => x.Tag != 'B'), x => PsoDb.Parse(x));
    }
}
