using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using PlayMap.Media.Ffmpeg;
using PlayMap.Media.Sources;

namespace PlayMap.Media.Tests;

/// <summary>The decoder's hardware ladder on a machine without a device's user-space libraries. This FFmpeg build binds
/// VA-API through stubs that abort the whole process when libva is missing, so the ladder asks the runtime first and
/// skips the device with words; the clip then decodes the same frames on the next rung.</summary>
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

    [Fact]
    public void OnLinuxVaapiAndQsvNeedLibvaAndNothingElseIsGuarded()
    {
        var expected = OperatingSystem.IsLinux() ? new[] { "libva.so.2", "libva-drm.so.2" } : [];
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
            LibraryProbe = name =>
            {
                lock (probed)
                    probed.Add(name);
                return name != "libva-drm.so.2";
            },
        });
        Assert.Equal(25, index);
        Assert.Equal("h264", decoder);
        Assert.Equal("vaapi skipped: libva-drm.so.2 is not on this machine", why);
        Assert.Equal(["libva.so.2", "libva-drm.so.2"], probed);
    }

    [Fact]
    public async Task OnThisMachineVaapiIsAskedForOnlyWhenItsLibrariesLoad()
    {
        // The real machine, nothing injected: CI and a bare Ubuntu have no libva (tools/without-libs.sh makes the same
        // condition on a machine that has it); a machine with a GPU stack has it and VA-API is tried.
        TestMedia.Ensure();
        var (decoder, why, index) = await OpenAndReadFrame(DecoderOptions.Default);
        Assert.Equal(25, index);
        if (!OperatingSystem.IsLinux())
            return;
        var missing = StreamDecoder.HardwareLibraries(AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI).FirstOrDefault(l => !NativeLibrary.TryLoad(l, out _));
        if (missing is not null)
        {
            Assert.StartsWith($"vaapi skipped: {missing} is not on this machine", why, StringComparison.Ordinal);
            Assert.DoesNotContain("(vaapi)", decoder, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("skipped", why, StringComparison.Ordinal);
        }
    }
}
