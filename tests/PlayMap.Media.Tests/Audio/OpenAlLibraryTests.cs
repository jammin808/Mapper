using PlayMap.Media.Audio;

namespace PlayMap.Media.Tests.Audio;

/// <summary>Which OpenAL the process speaks to: the OpenAL Soft that ships beside PlayMap, found through the app's own
/// native folders whatever the machine has (CI and a bare Ubuntu have no system OpenAL; a desk machine may), and words —
/// never an exception — when no OpenAL loads at all.</summary>
public class OpenAlLibraryTests
{
    [Fact]
    public void TheBundledOpenAlSoftIsTheOneLoaded()
    {
        Assert.True(OpenAlLibrary.Loaded, OpenAlLibrary.Words);
        Assert.Equal(OpenAlLibrary.CandidateNames()[0], OpenAlLibrary.Words);
        if (!OperatingSystem.IsLinux())
            return;
        // The mapping the loader made, as the kernel lists it: the file under the app's runtimes folder, not a system one.
        var mapped = File.ReadAllLines("/proc/self/maps")
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(f => f.Length >= 6 && Path.GetFileName(f[5]).StartsWith("libopenal.so", StringComparison.Ordinal))
            .Select(f => f[5])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var file = Assert.Single(mapped);
        Assert.StartsWith(Path.Combine(AppContext.BaseDirectory, "runtimes"), file, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLoopbackOutputRendersThroughTheLoadedLibrary()
    {
        using var output = AudioOut.OpenLoopback();
        Assert.Equal(AudioOutMode.Loopback, output.Mode);
        var block = new float[AudioOut.BlockFrames * 2];
        output.Render(block);
        Assert.All(block, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void WhenNoOpenAlLoadsTheLoadSaysWhichNamesItTriedAndTheDeviceOutputIsTheNullOneWithThoseWords()
    {
        var failed = OpenAlLibrary.Load(["libplaymap-no-openal.so", "libc.so.6"]);
        Assert.Equal(0, failed.Handle);
        Assert.Null(failed.Alc);
        Assert.Equal("no OpenAL library loads (libplaymap-no-openal.so not found, libc.so.6 is not OpenAL)", failed.Words);

        using var output = AudioOut.OpenDevice(null, failed);
        Assert.Equal(AudioOutMode.Null, output.Mode);
        Assert.Equal(failed.Words, output.Status);
    }
}
