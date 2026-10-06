using SCSKiller.Core;
using SCSKiller.Core.Vendors;
using Xunit;

namespace SCSKiller.Tests.Platform;

public class IntelVendorTests
{
    static GpuInfo IntelGpu(string driver = "32.0.101.9034", ulong vram = 16UL << 30) =>
        new(GpuVendor.Intel, "Intel(R) Arc(TM) B580 Graphics", driver, 0x1234, vram);

    [Fact]
    public void Intel_backend_initializes_with_correct_caps_and_vendor()
    {
        var gpu = IntelGpu();
        var backend = new IntelBackend(gpu);

        Assert.Equal(GpuVendor.Intel, backend.Vendor);
        Assert.Equal("intel-1", backend.Caps.Profile);
        Assert.True(backend.Caps.CacheKeyedByExeName);
        Assert.True(backend.Caps.StateIndependentCache);
        Assert.False(backend.Caps.CacheSizeConfigurable);
        Assert.False(backend.Caps.PerStageCache);
        Assert.Equal(RtCacheGranularity.WholeObject, backend.Caps.RtCacheGranularity);
        Assert.NotNull(backend.AppCache);
        Assert.Equal(new CacheLimit(null, false), backend.GetCacheLimit());
        Assert.Throws<NotSupportedException>(() => backend.SetCacheLimit(new CacheLimit(100, false)));
    }

    [Fact]
    public void Intel_driver_version_formatting()
    {
        // 4-part UMD version e.g. 32.0.101.9034
        Assert.Equal("101.9034 (32.0.101.9034)", IntelBackend.FormatVersion("32.0.101.9034"));
        Assert.Equal("101.6881 (32.0.101.6881)", IntelBackend.FormatVersion("32.0.101.6881"));

        // Fallbacks for unusual strings
        Assert.Equal("custom-driver", IntelBackend.FormatVersion("custom-driver"));
        Assert.Equal("", IntelBackend.FormatVersion(""));
    }

    [Fact]
    public void Intel_backend_refresh_updates_driver_version()
    {
        var backend = new IntelBackend(IntelGpu("32.0.101.6881"));
        Assert.Equal("101.6881 (32.0.101.6881)", backend.Gpu.DriverVersion);

        Assert.True(backend.Refresh(backend.Gpu with { DriverVersion = "32.0.101.9034" }));
        Assert.Equal("101.9034 (32.0.101.9034)", backend.Gpu.DriverVersion);
    }

    [Fact]
    public void Intel_app_cache_keys_and_files()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "intel-cache-test-" + Guid.NewGuid().ToString("N")[..8]);
        var d3ds = Path.Combine(tempDir, "D3DSCache");
        var intel = Path.Combine(tempDir, "Intel", "ShaderCache");

        try
        {
            var appFolder = Path.Combine(d3ds, "bff55fa8eb08f7dc");
            Directory.CreateDirectory(appFolder);
            var dbFile = Path.Combine(appFolder, "6F75932F-7DFC-4FB0-B4B8-12DE1AC415DA_VEN_8086&DEV_E20B&SUBSYS_6020&REV_0.dxcache");
            File.WriteAllBytes(dbFile, new byte[1024]);
            var walFile = Path.Combine(appFolder, "6F75932F-7DFC-4FB0-B4B8-12DE1AC415DA_VEN_8086&DEV_E20B&SUBSYS_6020&REV_0.dxcache-wal");
            File.WriteAllBytes(walFile, new byte[256]);

            var cache = new IntelAppCache(d3ds, intel);

            // FilesOf by prefixed key or directory name
            var filesPrefixed = cache.FilesOf(["d3ds:bff55fa8eb08f7dc"]);
            Assert.Equal(2, filesPrefixed.Count);
            Assert.Equal(1280, cache.SizeOf(["d3ds:bff55fa8eb08f7dc"]));

            var filesPlain = cache.FilesOf(["bff55fa8eb08f7dc"]);
            Assert.Equal(2, filesPlain.Count);

            // Deletion
            var deleted = cache.Delete(["d3ds:bff55fa8eb08f7dc"]);
            Assert.Equal(2, deleted);
            Assert.False(File.Exists(dbFile));
            Assert.False(File.Exists(walFile));
            Assert.False(Directory.Exists(appFolder));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GpuBackends_detects_Intel_as_IntelBackend()
    {
        var dxgi = new DxgiAdapter(IntelGpu(), 0xE20B, 0x60201849);
        var primary = GpuBackends.Primary([dxgi]);
        Assert.NotNull(primary);
        Assert.Equal(GpuVendor.Intel, primary.Vendor);
    }

    [Trait("Needs", "Gpu")]
    [Fact]
    public void Intel_real_hardware_detect()
    {
        var backend = GpuBackends.Detect();
        if (backend.Vendor != GpuVendor.Intel) return;
        Assert.IsType<IntelBackend>(backend);
        Assert.Equal(GpuVendor.Intel, backend.Vendor);
        var usage = backend.GetCacheUsage();
        Assert.NotNull(usage.Path);
    }
}
