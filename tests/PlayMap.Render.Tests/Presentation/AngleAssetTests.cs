using System.Text.Json;
using PlayMap.Render.Platform;

namespace PlayMap.Render.Tests.Presentation;

/// <summary>The ANGLE loader asks for the file the package ships: its name is the package's one native asset, and the loader's file
/// list equals the asset list of Avalonia.Angle.Windows.Natives as the desk's restore recorded it (PlayMap.App's project.assets.json),
/// each of which is in the NuGet cache. The three extension lists refuse with the first missing name and the list it is missing from.</summary>
public sealed class AngleAssetTests
{
    private const string Package = "Avalonia.Angle.Windows.Natives/";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no repository root above the test folder");
    }

    [Fact]
    public void TheLoadersFilesAreThePackagesAssetsInTheNugetCache()
    {
        var assets = Path.Combine(RepoRoot(), "src", "PlayMap.App", "obj", "project.assets.json");
        Assert.True(File.Exists(assets), $"the desk's restore writes {assets}");
        using var doc = JsonDocument.Parse(File.ReadAllText(assets));
        var root = doc.RootElement;
        var library = root.GetProperty("libraries").EnumerateObject().Single(p => p.Name.StartsWith(Package, StringComparison.OrdinalIgnoreCase));
        var files = library.Value.GetProperty("files").EnumerateArray().Select(f => f.GetString()!)
            .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(AngleRequirements.Files.Order(StringComparer.Ordinal), files);
        foreach (var f in files)
            Assert.Equal(AngleRequirements.Library + ".dll", Path.GetFileName(f));

        var cache = root.GetProperty("project").GetProperty("restore").GetProperty("packagesPath").GetString()!;
        var folder = Path.Combine(cache, library.Value.GetProperty("path").GetString()!);
        foreach (var f in files)
            Assert.True(File.Exists(Path.Combine(folder, f)), $"{f} is in the NuGet cache at {folder}");
    }

    [Fact]
    public void TheExtensionListsNameWhatIsMissingAndFromWhichList()
    {
        Assert.Equal(["EGL_EXT_platform_base", "EGL_EXT_platform_device", "EGL_EXT_device_query", "EGL_ANGLE_device_creation", "EGL_ANGLE_device_creation_d3d11"],
            AngleRequirements.Client);
        Assert.Equal(["EGL_ANGLE_device_d3d11"], AngleRequirements.Device);
        Assert.Equal(["EGL_ANGLE_d3d_texture_client_buffer", "EGL_KHR_surfaceless_context"], AngleRequirements.Display);

        var client = string.Join(' ', AngleRequirements.Client);
        Assert.Null(AngleRequirements.Missing("client extensions", AngleRequirements.Client, client + " EGL_KHR_debug"));
        Assert.Equal("ANGLE's client extensions lacks EGL_ANGLE_device_creation_d3d11",
            AngleRequirements.Missing("client extensions", AngleRequirements.Client, client.Replace(" EGL_ANGLE_device_creation_d3d11", "", StringComparison.Ordinal)));
        Assert.Equal("ANGLE's device lacks EGL_ANGLE_device_d3d11", AngleRequirements.Missing("device", AngleRequirements.Device, null));
        // A display string carries display extensions only: the device's never satisfies it.
        Assert.Equal("ANGLE's display lacks EGL_ANGLE_d3d_texture_client_buffer",
            AngleRequirements.Missing("display", AngleRequirements.Display, "EGL_ANGLE_device_d3d11 EGL_KHR_surfaceless_context"));
        // A name is matched whole, never as a prefix of a longer one.
        Assert.Equal("ANGLE's device lacks EGL_ANGLE_device_d3d11",
            AngleRequirements.Missing("device", AngleRequirements.Device, "EGL_ANGLE_device_d3d11_x"));
        Assert.True(AngleRequirements.HasImageRoute("EGL_KHR_image EGL_ANGLE_image_d3d11_texture"));
        Assert.False(AngleRequirements.HasImageRoute("EGL_ANGLE_d3d_texture_client_buffer"));
    }

    [Fact]
    public void QpcToNanosecondsNeverOverflows()
    {
        Assert.Equal(1_000_000_000L, QpcTime.ToNs(10_000_000, 10_000_000));
        Assert.Equal(1_500, QpcTime.ToNs(15, 10_000_000));
        // qpc = 2^62: qpc · 10⁹ overflows Int64 at any frequency; the split form is exact (floor(qpc · 10⁹ / f)).
        const long Big = 1L << 62;
        Assert.Equal(Big, QpcTime.ToNs(Big, 1_000_000_000));
        Assert.Equal(Big / 3, QpcTime.ToNs(Big, 3_000_000_000));
        Assert.Equal((long)((System.Numerics.BigInteger)Big * 1_000_000_000 / 24_000_000_000), QpcTime.ToNs(Big, 24_000_000_000));
        // A day of uptime at 10 MHz, where the plain product already overflows: exact.
        const long Day = 864_000_000_000;
        Assert.Equal(86_400_000_000_000L, QpcTime.ToNs(Day, 10_000_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => QpcTime.ToNs(1, 0));
    }

    private sealed class Wrapper(nint texture) : IDisposable
    {
        public nint Texture { get; } = texture;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void BufferZeroIsWrappedOncePerSwapChainAndAgainAfterAResize()
    {
        using var cache = new BackBufferCache<Wrapper>();
        Assert.Equal(0, cache.Count);
        var made = 0;
        Wrapper Wrap(nint t)
        {
            made++;
            return new Wrapper(t);
        }

        var first = cache.Get(0x1000, Wrap);
        for (var i = 0; i < 120; i++)
            Assert.Same(first, cache.Get(0x1000, Wrap));
        Assert.Equal(1, made);
        Assert.Equal(1, cache.Count);
        Assert.Equal(1, cache.Wraps);

        // A different texture with no resize: the flip model did not rename buffer 0 — said, not wrapped silently.
        var ex = Assert.Throws<InvalidOperationException>(() => cache.Get(0x2000, Wrap));
        Assert.Contains("buffer 0", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, cache.Count);

        // ResizeBuffers (or a device loss): the old wrapper goes, the next present wraps once more.
        cache.Invalidate();
        Assert.True(first.Disposed);
        Assert.Equal(0, cache.Count);
        var second = cache.Get(0x2000, Wrap);
        Assert.Equal(0x2000, second.Texture);
        Assert.Equal(2, cache.Wraps);
        Assert.Equal(1, cache.Count);
    }
}
