using System.Diagnostics;
using SCSKiller.Core;
using SCSKiller.Core.Games;
using SCSKiller.Core.Unreal;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Unreal;

/// <summary>Encrypted games on the dev machine (skipped when absent). Keys go to temp data dirs; no test prints a key.</summary>
[Trait("Needs", "Game")]
public class UnrealKeysTests(ITestOutputHelper output)
{
    static Game? Installed(string name)
    {
        try { return new SteamSource().Discover().FirstOrDefault(g => g.Name.Contains(name)); }
        catch (Exception) { return null; }
    }

    /// <summary>Windrose Demo (UE 5.6): the key is in its exe as 8 x mov dword, imm32; with it the files open, and they show
    /// the shaders live inside the materials (bShareMaterialShaderCode=False): indexable, see InlineShadersTests.</summary>
    [Fact]
    public void KeyFoundStaticallyStoredAndReused()
    {
        if (Installed("Windrose Demo") is not { } game) return;
        var data = Ff7.TempDir("keys-windrose");
        var sw = Stopwatch.StartNew();
        var e = new UnrealReader(data).Detect(game, out var notes)!;
        output.WriteLine($"{sw.Elapsed.TotalSeconds:F1} s: {e}\n{notes}");
        Assert.Contains("AES key: found in the exe", notes);
        Assert.False(e.Encrypted);
        Assert.Null(e.Unsupported);
        Assert.Equal("5.6", e.Version); // TOC version 8 says 5.5+; the package header layout says 5.6
        var keyFile = Path.Combine(data, "games", "steam_4291770", "aes.key");
        var key = File.ReadAllText(keyFile);
        Assert.DoesNotContain(key.Replace("0x", ""), notes, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("AES key: stored key", (new UnrealReader(data).Detect(game, out notes), notes).notes);

        // manual fallback: a wrong key is refused, the right one is stored
        var other = new UnrealReader(Ff7.TempDir("keys-manual"));
        Assert.False(other.SetKey(game, "0x" + new string('7', 64)));
        Assert.False(other.SetKey(game, "not a key"));
        Assert.True(other.SetKey(game, key));
        Assert.Contains("AES key: stored key", (other.Detect(game, out notes), notes).notes);
    }

    /// <summary>DRAGON BALL: Sparking! ZERO (UE 5.1): the fourth key dword ends in 0x41, the byte before the fifth store,
    /// which only reads as a REX prefix. An earlier scanner's remembered failure for the same exe is scanned again.</summary>
    [Fact]
    public void KeyFoundWhenAnImmediateEndsInARexByte()
    {
        if (Installed("Sparking! ZERO") is not { } game) return;
        var data = Ff7.TempDir("keys-dbsz");
        var exe = new FileInfo(game.ExePath);
        Directory.CreateDirectory(Path.Combine(data, "games", "steam_1790600"));
        File.WriteAllLines(Path.Combine(data, "games", "steam_1790600", "aes.scan"), [$"{exe.Length}:{exe.LastWriteTimeUtc.Ticks}", "not found in the exe's code"]);
        var e = new UnrealReader(data).Detect(game, out var notes)!;
        output.WriteLine($"{e}\n{notes}");
        Assert.Contains("AES key: found in the exe", notes);
        Assert.False(e.Encrypted);
        Assert.True(e is { NoRayTracing: true, NoRtPipelines: true });   // r.RayTracing=False in its DefaultEngine.ini
    }

    /// <summary>Mafia: The Old Country (UE 5.3): its exe keeps its code outside .text (a protector), so the static scan finds
    /// no key; the verdict is remembered per exe build.</summary>
    [Fact]
    public void ProtectedExeIsReportedAndNotRescanned()
    {
        if (Installed("Mafia: The Old Country") is not { } game) return;
        var data = Ff7.TempDir("keys-mafia");
        var e = new UnrealReader(data).Detect(game, out var notes)!;
        output.WriteLine($"{e}\n{notes}");
        Assert.True(e.Encrypted);
        Assert.Contains("looks protected", notes);
        var sw = Stopwatch.StartNew();
        new UnrealReader(data).Detect(game, out var again);
        output.WriteLine($"again: {sw.Elapsed.TotalSeconds:F1} s");
        Assert.Contains("looks protected", again);
        Assert.True(File.Exists(Path.Combine(data, "games", "steam_1941540", "aes.scan")));
    }
}

/// <summary>The static key scan over a synthetic exe: one executable section holding a key callback's stores.</summary>
public class UnrealKeyScanTests
{
    /// <summary>8 x mov dword [rbp+disp8], imm32 as Sparking! ZERO compiles them, with another instruction between the second
    /// and third: the fourth dword's last byte is 0x41, which also reads as a REX.B prefix of the fifth store.</summary>
    [Fact]
    public void KeyFoundWhenAnImmediateEndsInARexByte()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)(i * 37 + 11)).ToArray();
        key[15] = 0x41;
        var code = new List<byte> { 0x48, 0x8D, 0x55, 0xEF };
        for (var k = 0; k < 8; k++)
        {
            if (k == 2) code.AddRange([0x48, 0x8D, 0x41, 0x1F]);
            code.AddRange([0xC7, 0x45, (byte)(0xD0 + 4 * k), .. key.AsSpan(4 * k, 4)]);
        }
        var exe = Path.Combine(Ff7.TempDir("keys-synthetic"), "game.exe");
        File.WriteAllBytes(exe, Pe([.. code]));
        var expected = "0x" + Convert.ToHexString(key);
        var found = UnrealKeys.Scan(exe, k => k.KeyString == expected, out var how);
        Assert.Equal(expected, found?.KeyString);
        Assert.Contains("8 x mov dword, imm32", how);
    }

    /// <summary>A PE32+ image with one executable section (.text, file offset 0x200) holding <paramref name="code"/>.</summary>
    static byte[] Pe(byte[] code)
    {
        var b = new byte[0x400];
        void U16(int at, int v) => BitConverter.TryWriteBytes(b.AsSpan(at), (ushort)v);
        void U32(int at, uint v) => BitConverter.TryWriteBytes(b.AsSpan(at), v);
        U16(0, 0x5A4D);
        U32(0x3C, 0x80);
        U32(0x80, 0x4550);
        U16(0x84, 0x8664);   // machine
        U16(0x86, 1);        // sections
        U16(0x94, 0xF0);     // optional header size
        U16(0x96, 0x22);
        const int opt = 0x98;
        U16(opt, 0x20B);
        U32(opt + 32, 0x1000);   // section alignment
        U32(opt + 36, 0x200);    // file alignment
        U32(opt + 56, 0x2000);   // image size
        U32(opt + 60, 0x200);    // headers size
        U32(opt + 108, 16);      // data directories
        var sec = opt + 0xF0;
        ".text"u8.CopyTo(b.AsSpan(sec));
        U32(sec + 8, 0x200);
        U32(sec + 12, 0x1000);
        U32(sec + 16, 0x200);
        U32(sec + 20, 0x200);
        U32(sec + 36, 0x60000020);   // code, execute, read
        code.CopyTo(b, 0x210);
        return b;
    }
}
