using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using PlayMap.Media.Ffmpeg;
using PlayMap.Media.Sources;

namespace PlayMap.Media.Tests;

/// <summary>The decoder's hardware ladder on a machine without a device's user-space libraries, or with ones older than this
/// FFmpeg calls into. This build binds VA-API through stubs that abort the whole process when libva is missing or lacks a
/// symbol, so the ladder asks the runtime first and skips the device with words; the clip then decodes the same frames on the
/// next rung.</summary>
public class DecodePathTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static async Task<(string Decoder, string Why, int Index)> OpenAndReadFrame(DecoderOptions decoder)
    {
        using var source = new FfmpegVideoSource(TestMedia.Clip("h264_25.mp4"), new VideoSourceOptions { Decoder = decoder });
        Assert.True(await source.Opened, source.Health.Status);
        const long t = 1_000_000_000;   // frame 25 at 25 fps
        source.Prepare(t, false, 1);
        Assert.True(source.WaitUntilReady(t, Wait), source.Health.Status);
        using var lease = source.Acquire(t);
        Assert.NotNull(lease);
        var health = source.Health;
        return (health.Decoder, health.Why, TestMedia.ReadIndex(lease));
    }

    private static LibraryFound OnThisMachine(HardwareLibrary l)
    {
        if (!NativeLibrary.TryLoad(l.Library, out var handle))
            return LibraryFound.Missing;
        var has = NativeLibrary.TryGetExport(handle, l.Export, out _);
        NativeLibrary.Free(handle);
        return has ? LibraryFound.Usable : LibraryFound.TooOld;
    }

    [Fact]
    public void OnLinuxVaapiAndQsvNeedLibvaWithVaMapBuffer2AndNothingElseIsGuarded()
    {
        HardwareLibrary[] expected = OperatingSystem.IsLinux()
            ? [new("libva.so.2", "vaMapBuffer2", "libva 2.21"), new("libva-drm.so.2", "vaGetDisplayDRM", "libva 2.0")]
            : [];
        Assert.Equal(expected, StreamDecoder.HardwareLibraries(AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI));
        Assert.Equal(expected, StreamDecoder.HardwareLibraries(AVHWDeviceType.AV_HWDEVICE_TYPE_QSV));
        Assert.Empty(StreamDecoder.HardwareLibraries(AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA));
        Assert.Empty(StreamDecoder.HardwareLibraries(AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN));
    }

    [Fact]
    public async Task AVaapiWhoseLibraryIsMissingIsSkippedWithWordsAndTheClipDecodesTheSameFrame()
    {
        if (!OperatingSystem.IsLinux())
            return;
        TestMedia.Ensure();
        var probed = new List<string>();
        var (decoder, why, index) = await OpenAndReadFrame(new DecoderOptions
        {
            HardwareDevice = "vaapi",
            LibraryProbe = l =>
            {
                lock (probed)
                    probed.Add(l.Library);
                return l.Library == "libva-drm.so.2" ? LibraryFound.Missing : LibraryFound.Usable;
            },
        });
        Assert.Equal(25, index);
        Assert.Equal("h264", decoder);
        Assert.Equal("vaapi skipped: libva-drm.so.2 is not on this machine", why);
        Assert.Equal(["libva.so.2", "libva-drm.so.2"], probed);
    }

    [Fact]
    public async Task ALibvaOlderThanThisFfmpegCallsIntoIsSkippedWithWordsAndTheClipDecodesTheSameFrame()
    {
        // Ubuntu 24.04's libva is 2.20: it loads, but has no vaMapBuffer2, which the stub would fail to resolve (and abort the
        // process) on the first frame read back from the device.
        if (!OperatingSystem.IsLinux())
            return;
        TestMedia.Ensure();
        var (decoder, why, index) = await OpenAndReadFrame(new DecoderOptions
        {
            HardwareDevice = "vaapi",
            LibraryProbe = l => l.Export == "vaMapBuffer2" ? LibraryFound.TooOld : LibraryFound.Usable,
        });
        Assert.Equal(25, index);
        Assert.Equal("h264", decoder);
        Assert.Equal("vaapi skipped: libva.so.2 has no vaMapBuffer2 (this FFmpeg needs libva 2.21 or later)", why);
    }

    [Fact]
    public async Task OnThisMachineVaapiIsAskedForOnlyWhenItsLibrariesAreUsable()
    {
        // The real machine, nothing injected: CI and a bare Ubuntu have no libva (tools/without-libs.sh makes the same
        // condition on a machine that has it), Ubuntu 24.04's own libva is too old, and a machine with a newer GPU stack has
        // one VA-API is tried with.
        TestMedia.Ensure();
        var (decoder, why, index) = await OpenAndReadFrame(DecoderOptions.Default);
        Assert.Equal(25, index);
        if (!OperatingSystem.IsLinux())
            return;
        var unusable = StreamDecoder.HardwareLibraries(AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI)
            .Select(l => (l, Found: OnThisMachine(l))).FirstOrDefault(f => f.Found != LibraryFound.Usable);
        if (unusable.Found != LibraryFound.Usable)
        {
            var l = unusable.l;
            Assert.StartsWith(unusable.Found == LibraryFound.Missing
                ? $"vaapi skipped: {l.Library} is not on this machine"
                : $"vaapi skipped: {l.Library} has no {l.Export}", why, StringComparison.Ordinal);
            Assert.DoesNotContain("(vaapi)", decoder, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("skipped", why, StringComparison.Ordinal);
        }
    }
}
