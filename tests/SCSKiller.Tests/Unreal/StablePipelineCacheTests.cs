using SCSKiller.Core;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using SCSKiller.Tests.Planning;
using static SCSKiller.Tests.Planning.ExactLayoutsTests;

namespace SCSKiller.Tests.Unreal;

public class StablePipelineCacheTests
{
    /// <summary>A file laid out as UE writes it: header, PSO bodies (only their type is read), TOC, EOF-MARK.</summary>
    internal static byte[] File(uint version, params (uint Key, uint Type, byte[][] Shaders)[] psos)
    {
        var body = new MemoryStream();
        var w = new BinaryWriter(body);
        w.Write(0x5049504543414348UL); w.Write(version); w.Write(0u); w.Write((byte)49); w.Write(new byte[16]);
        var v17 = version == 17; // UE 4.25: no last GC time, TOCSTART, no shared guid, no last used time
        w.Write(0UL); // table offset (patched below)
        if (!v17) w.Write(0L); // last GC time
        var offsets = psos.Select(p => { var o = body.Position; w.Write(p.Type); w.Write(new byte[60]); return o; }).ToList();
        var toc = body.Position;
        w.Write(v17 ? 0x544F435354415254UL : 0x544F435354415232UL);
        if (!v17) { w.Write((byte)1); w.Write(new byte[16]); }
        w.Write(2u); w.Write(psos.Length);
        for (var i = 0; i < psos.Length; i++)
        {
            w.Write(psos[i].Key); w.Write((ulong)offsets[i]); w.Write(64UL); w.Write(new byte[16]); w.Write(new byte[36]);
            w.Write(psos[i].Shaders.Length);
            foreach (var s in psos[i].Shaders) w.Write(s);
            w.Write(0UL); w.Write((ushort)0);
            if (!v17) w.Write(0L);
        }
        w.Write(0x454F462D4D41524BUL);
        var b = body.ToArray();
        BitConverter.GetBytes((ulong)toc).CopyTo(b, 33);
        return b;
    }

    static byte[] H(byte b) => Enumerable.Repeat(b, 20).ToArray();
    static string Hex(byte b) => Convert.ToHexStringLower(H(b));

    [Fact]
    public void ReadsEachPsosTypeAndShaderHashes()
    {
        var psos = StablePipelineCache.Read(File(28, (7, 1, [H(1), H(2)]), (8, 0, [H(3)]), (9, 2, [H(4)])))!;
        Assert.Equal([7u, 8u, 9u], psos.Select(p => p.Key));
        Assert.Equal([StablePipelineCache.PsoType.Graphics, StablePipelineCache.PsoType.Compute, StablePipelineCache.PsoType.RayTracing], psos.Select(p => p.Type));
        Assert.Equal([Hex(1), Hex(2)], psos[0].Shaders);
        Assert.Equal([Hex(4)], psos[2].Shaders);
    }

    /// <summary>UE 4.25 writes version 17 (Returnal).</summary>
    [Fact]
    public void ReadsUe425sVersion17()
    {
        var psos = StablePipelineCache.Read(File(17, (7, 1, [H(1), H(2)]), (8, 0, [H(3)])))!;
        Assert.Equal([7u, 8u], psos.Select(p => p.Key));
        Assert.Equal([StablePipelineCache.PsoType.Graphics, StablePipelineCache.PsoType.Compute], psos.Select(p => p.Type));
        Assert.Equal([Hex(1), Hex(2)], psos[0].Shaders);
    }

    [Fact]
    public void RefusesOtherVersionsAndBrokenFiles()
    {
        Assert.NotNull(StablePipelineCache.Read(File(22, (1, 1, [H(1)]))));
        Assert.Null(StablePipelineCache.Read(File(21, (1, 1, [H(1)]))));
        Assert.Null(StablePipelineCache.Read(File(16, (1, 1, [H(1)]))));
        Assert.Null(StablePipelineCache.Read(File(29, (1, 1, [H(1)]))));
        var f = File(28, (1, 1, [H(1)]));
        Assert.Null(StablePipelineCache.Read(f.AsSpan(0, f.Length - 1)));
        Assert.Null(StablePipelineCache.Read(f.Concat(new byte[8]).ToArray())); // no EOF-MARK at the end
    }

    /// <summary>UE's draw-rectangle VS writes interpolants a post-process PS doesn't read, so no signature pairs them: only
    /// the shipped pipeline (an IsPipeline map) puts that PS in the plan with the VS's resources in its root signature.</summary>
    [Fact]
    public void AShippedPipelinePairsWhatSignaturesDont()
    {
        var pos = In("SV_Position", 0, 0, 0xF, 3, 1);
        var vs = Shader("rect-vs", Stage.Vertex, [In("SV_VertexID", 0, 0, 1, 1, 6)], [pos, In("TEXCOORD", 0, 1), In("TEXCOORD", 1, 2)]) with { Counts = new(1, 1, 0, 0) };
        var ps = Shader("depth-ps", Stage.Pixel, [pos], [new SigElement("SV_Depth", 0, -1, 1, 0, 3)]);
        var global = new ShaderMap("g", "Global", "PCD3D_SM6", [vs.Sha1, ps.Sha1]);
        var shipped = new ShaderMap("SHf_PCD3D_SM6:00000001", "PipelineCache", "PCD3D_SM6", [vs.Sha1, ps.Sha1], IsPipeline: true);
        var dir = Ff7.TempDir("stable-cache-pair");
        bool Pairs(params ShaderMap[] maps)
        {
            var index = new ShaderIndex("stable", ["PCD3D_SM6"], new[] { vs, ps }.ToDictionary(s => s.Sha1), maps);
            var plan = new Planner().Build(Ff7.Game, new EngineInfo("Unreal", "5.4", null, "D3D12", false, null), index, null, Ff7.Nvidia,
                Path.Combine(dir, maps.Length.ToString()), null, CancellationToken.None);
            return PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag is 'S' or 'P').Select(r => r.Tag == 'P' ? PsoDb.ParseItem(r.Payload).Stages : PsoDb.Parse(r).Stages)
                .Any(s => s.GetValueOrDefault((int)Stage.Vertex) == vs.Sha1 && s.GetValueOrDefault((int)Stage.Pixel) == ps.Sha1);
        }
        Assert.False(Pairs(global));
        Assert.True(Pairs(global, shipped));
    }
}
