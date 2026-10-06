using System.Globalization;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Tests.Platform;

public class FormatTests
{
    [Fact]
    public void Bytes_are_whole_megabytes_below_a_gigabyte_and_one_decimal_from_there()
    {
        var was = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(["—", "0 MB", "512 MB", "1024 MB", "1 GB", "3.2 GB"],
                new long?[] { null, 0, 512L << 20, (1L << 30) - 1, 1L << 30, (long)(3.2 * (1L << 30)) }.Select(Format.Bytes));
        }
        finally { CultureInfo.CurrentCulture = was; }
    }

    [Fact]
    public void A_duration_is_ScsKillers_or_a_dash()
    {
        Assert.Equal(Format.Dash, Format.Duration(null));
        Assert.Equal(ScsKiller.Duration(TimeSpan.FromMinutes(125)), Format.Duration(TimeSpan.FromMinutes(125)));
    }

    [Fact]
    public void An_item_runs_from_indexing_until_it_finishes_paused_included()
    {
        QueueStage[] running = [QueueStage.Indexing, QueueStage.Planning, QueueStage.Materializing, QueueStage.Warming, QueueStage.Paused];
        Assert.All(Enum.GetValues<QueueStage>(), s => Assert.Equal(running.Contains(s), Format.Running(new QueueItem("g", s, null, null))));
    }

    [Fact]
    public void A_middleware_tag_says_what_is_compiled()
    {
        Assert.Equal("FSR4: 80 known pipelines, compiled with the game", Format.Middleware(new("FSR4", ["amdxcffx64.dll"], 80)));
        Assert.Equal("DLSS: compiled by the NVIDIA driver itself", Format.Middleware(new("DLSS", ["nvngx_dlss.dll"], 0)));
        Assert.Equal("XeSS: detected; added after a recording sees them", Format.Middleware(new("XeSS", ["libxess.dll"], 0)));
    }

    static readonly EngineInfo Ue = new("Unreal", "4.27", null, "D3D12", false, null);

    static GameState S(GameStatus status, string reason, EngineInfo? engine = null, AntiCheat ac = AntiCheat.None) =>
        new(new Game("steam:1", "Game", Store.Steam, @"X:\g", @"X:\g\g.exe"), engine ?? Ue, ac, status, reason,
            null, null, null, null, "610.88", null, null, false, null);

    /// <summary>A Library row's note is a few words for each reason the core gives (the whole one stays the tooltip's).</summary>
    [Fact]
    public void A_row_note_is_a_short_label_of_the_reason()
    {
        const string packed = "no raw DXBC/DXIL shaders in its files (0.5 GB sampled): shaders are compressed or packed: needs an engine reader";
        var cases = new (GameState State, string? Note)[]
        {
            (S(GameStatus.Ready, Core.Planning.Planner.NoRecording), null),
            (S(GameStatus.Ready, "planned from a recording"), null),
            (S(GameStatus.Ready, Core.Planning.Planner.Untested), "Not tested on this engine version"),
            (S(GameStatus.Ready, "compiles every DirectX 11 shader"), "DirectX 11"),
            (S(GameStatus.Ready, "no recording needed; also compiles every DirectX 11 shader (the game may run on either)"), "DirectX 11 and 12"),
            (S(GameStatus.Ready, "compiles every DirectX 11 shader (the game may run on either); for DirectX 12, turn on recording and play for about 5 minutes"), "DirectX 11; DirectX 12 needs a recording"),
            (S(GameStatus.Ready, "no recording needed; ray-traced effects aren't compiled: they need a recording, which BattlEye blocks", ac: AntiCheat.BattlEye), "Ray tracing blocked by BattlEye"),
            (S(GameStatus.NeedsRecording, Core.Planning.Planner.Record), "Turn on Record and play"),
            (S(GameStatus.NeedsRecording, Core.Planning.Planner.Record + "; " + ScsKiller.InDbNote) with { InCommunityDb = true }, "In the community database"),
            (S(GameStatus.NeedsRecording, ScsKiller.RtNote(false)) with { InCommunityDb = false }, "For ray-traced effects"),
            (S(GameStatus.NeedsRecording, "the recording has no draws: play into the game world"), "Play into the game world"),
            (S(GameStatus.Unsupported, "needs a recording, which EasyAntiCheat blocks", ac: AntiCheat.EasyAntiCheat), "Blocked by EasyAntiCheat"),
            (S(GameStatus.Unsupported, "needs a recording, which its anti-cheat blocks", ac: AntiCheat.Other), "Blocked by anti-cheat"),
            (S(GameStatus.Unsupported, "needs a recording, which its anti-cheat blocks; " + ScsKiller.InDbNote, ac: AntiCheat.Other) with { InCommunityDb = true },
                "Blocked by anti-cheat · in the community database"),
            (S(GameStatus.Unsupported, packed, Ue with { Version = "-", Unsupported = packed }), "Engine not supported yet"),
            (S(GameStatus.Unsupported, "encrypted game files (needs the game's AES key)", Ue with { Encrypted = true, Unsupported = "encrypted game files (needs the game's AES key)" }), null),
            (S(GameStatus.Unsupported, "no D3D shaders (SF_VULKAN_SM5)", Ue with { Unsupported = "no D3D shaders (SF_VULKAN_SM5)" }), "No DirectX shaders"),
            (S(GameStatus.Unsupported, "engine not supported yet") with { Engine = null }, "Engine not supported yet"),
            (S(GameStatus.Unsupported, "Access to the path is denied.") with { Engine = null }, "Couldn't read the game files"),
            (S(GameStatus.Unsupported, "runs on Vulkan"), "Runs on Vulkan"),
            (S(GameStatus.Unsupported, "runs on DirectX 11 (its config)"), "Runs on DirectX 11"),
            (S(GameStatus.Unsupported, "not supported on this GPU yet"), "Not supported on this GPU yet"),
            (S(GameStatus.Stale, "driver changed: 596.36 -> 610.88"), "Driver 610.88 cleared its cache"),
            (S(GameStatus.Stale, ScsKiller.TrimmedPartReason), "The driver trimmed its cache"),
            (S(GameStatus.Stale, ScsKiller.MissesGameReason), "The compile missed the game's cache"),
            (S(GameStatus.Stale, "game updated since the warm (build 1 -> 2)"), "Game updated"),
            (S(GameStatus.Stale, "game shaders changed since the warm"), "Game shaders changed"),
            (S(GameStatus.Stale, "SCSKiller can now compile 120 more pipelines for this game"), "SCSKiller can compile more"),
            (S(GameStatus.Stale, "1,234 new pipelines recorded; compile again to include them"), "1,234 new pipelines recorded"),
            (S(GameStatus.Warmed, "warmed for driver 610.88"), "Driver 610.88"),
        };
        Assert.All(cases, c => Assert.Equal(c.Note, Format.ShortNote(c.State)));
    }

    static GpuInfo Gpu(GpuVendor v, string name, ulong vram) => new(v, name, "1.0", 0, vram);

    [Fact]
    public void The_gpu_notice_shows_on_other_vendors_until_closed_for_that_gpu()
    {
        Assert.Null(Format.GpuNotice(Gpu(GpuVendor.Intel, "Intel Graphics", 0), null));
        Assert.StartsWith("SCSKiller doesn't compile on Qualcomm GPUs yet", Format.GpuNotice(Gpu(GpuVendor.Qualcomm, "Adreno", 0), null));
        Assert.StartsWith("SCSKiller doesn't compile on this GPU yet", Format.GpuNotice(Gpu(GpuVendor.Unknown, "no D3D adapter", 0), null));
        Assert.Null(Format.GpuNotice(Gpu(GpuVendor.Nvidia, "NVIDIA GeForce", 8UL << 30), null));
        Assert.Null(Format.GpuNotice(Gpu(GpuVendor.Amd, "AMD Radeon", 8UL << 30), null));
        Assert.Null(Format.GpuNotice(Gpu(GpuVendor.Qualcomm, "Adreno", 0), "Adreno"));
        Assert.NotNull(Format.GpuNotice(Gpu(GpuVendor.Qualcomm, "Adreno 2", 0), "Adreno"));
        Assert.Null(AppStore.DefaultSettings.GpuNoticeDismissed);
    }

    [Fact]
    public void A_laptop_with_integrated_intel_and_a_discrete_gpu_uses_the_discrete_one_and_gets_no_notice()
    {
        var intel = new DxgiAdapter(Gpu(GpuVendor.Intel, "Intel Graphics", 128UL << 20), 0x46A6, 0);
        foreach (var dgpu in new[] { Gpu(GpuVendor.Nvidia, "NVIDIA GeForce Laptop GPU", 8UL << 30), Gpu(GpuVendor.Amd, "AMD Radeon", 8UL << 30) })
        {
            var discrete = new DxgiAdapter(dgpu, 0x10, 0);
            Assert.Equal(dgpu, GpuBackends.Primary([intel, discrete]));   // DXGI lists the iGPU first on most laptops
            Assert.Equal(dgpu, GpuBackends.Primary([discrete, intel]));
            Assert.Null(Format.GpuNotice(GpuBackends.Primary([intel, discrete])!, null));
        }
        Assert.Equal(intel.Gpu, GpuBackends.Primary([intel]));
    }
}
