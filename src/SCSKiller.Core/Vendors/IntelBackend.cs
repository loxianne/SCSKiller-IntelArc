namespace SCSKiller.Core.Vendors;

/// <summary>Intel GPU backend (Intel Arc, Iris Xe, UHD).
/// Direct3D 12 caches are stored per application in Windows' Direct3D Shader Cache (<c>%LOCALAPPDATA%\D3DSCache</c>).
/// The driver has no configurable size setting. Whole pipeline state objects are replayed.</summary>
public sealed class IntelBackend : IGpuVendorBackend, IRefreshableGpu
{
    public GpuVendor Vendor => GpuVendor.Intel;
    public GpuInfo Gpu { get; private set; }

    public VendorCaps Caps { get; } = new(
        Profile: "intel-1",
        CacheKeyedByExeName: true,
        StateIndependentCache: true,
        CacheSizeConfigurable: false,
        PerStageCache: false,
        RtCacheGranularity: RtCacheGranularity.WholeObject);

    static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string D3DSCacheDir => Path.Combine(LocalAppData, "D3DSCache");
    public static string IntelShaderCacheDir =>
        Directory.Exists(Path.Combine(UserProfile, "AppData", "LocalLow", "Intel", "ShaderCache"))
            ? Path.Combine(UserProfile, "AppData", "LocalLow", "Intel", "ShaderCache")
            : Path.Combine(LocalAppData, "Intel", "ShaderCache");

    public IntelAppCache AppCache { get; }
    IAppCache? IGpuVendorBackend.AppCache => AppCache;

    public IntelBackend(GpuInfo dxgi)
    {
        Gpu = dxgi with { DriverVersion = FormatVersion(dxgi.DriverVersion) };
        AppCache = new IntelAppCache(D3DSCacheDir, IntelShaderCacheDir);
    }

    /// <summary>Formats Intel UMD version (e.g. "32.0.101.9034" -> "101.9034 (32.0.101.9034)");
    /// preserves original string when format does not match.</summary>
    public static string FormatVersion(string umd)
    {
        if (string.IsNullOrWhiteSpace(umd)) return "";
        var parts = umd.Split('.');
        if (parts.Length == 4 && parts[2].Length > 0 && parts[3].Length > 0)
            return $"{parts[2]}.{parts[3]} ({umd})";
        return umd;
    }

    public bool Refresh(GpuInfo adapter)
    {
        Gpu = adapter with { DriverVersion = FormatVersion(adapter.DriverVersion) };
        return true;
    }

    public string FallbackVersion(string umd) => FormatVersion(umd);

    public CacheUsage GetCacheUsage()
    {
        long bytes = 0;
        if (Directory.Exists(D3DSCacheDir))
        {
            bytes += Directory.EnumerateFiles(D3DSCacheDir, "*VEN_8086*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Sum(f => new FileInfo(f).Length);
        }
        if (Directory.Exists(IntelShaderCacheDir))
        {
            bytes += Directory.EnumerateFiles(IntelShaderCacheDir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Sum(f => new FileInfo(f).Length);
        }
        return new CacheUsage(D3DSCacheDir, bytes, UpperBound: false);
    }

    /// <summary>Intel graphics driver's internal shader cache container ceiling (~512 MiB per container).</summary>
    public const long ContainerCeilingBytes = 512L * 1024 * 1024;

    public long LargestContainerBytes() => AppCache.LargestContainerBytes();

    public CacheLimit? GetCacheLimit() => new(Bytes: ContainerCeilingBytes, IsDriverDefault: false);

    public void SetCacheLimit(CacheLimit limit) =>
        throw new NotSupportedException($"the Intel shader cache size is not configurable ({Gpu.Name})");
}
