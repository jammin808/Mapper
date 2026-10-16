using PlayMap.Core.Time;
using PlayMap.Media.Sources;

namespace PlayMap.Media.Tests;

/// <summary>Live input: a synthetic device (FFmpeg's lavfi input, paced in real time) stands in for a camera.
/// The newest frame wins whatever the media time, frames are stamped on arrival, and a missing device says
/// so.</summary>
public class CaptureTests
{
    private static CaptureOptions FakeCamera(int fps) => new()
    {
        Format = "lavfi",
        Device = $"nullsrc=s=320x240:r={fps},format=yuv420p,{TestMedia.IndexPainter},realtime",
    };

    [Fact]
    public async Task TheNewestFrameWinsWhateverTheMediaTime()
    {
        TestMedia.Ensure();
        using var camera = new CaptureSource(FakeCamera(50));
        Assert.True(await camera.Opened, camera.Health.Status);
        Assert.True(camera.WaitForFrame(0, TimeSpan.FromSeconds(5)), camera.Health.Status);
        var lastIndex = -1;
        long lastGeneration = 0;
        for (var i = 0; i < 10; i++)
        {
            Assert.True(camera.WaitForFrame(lastGeneration, TimeSpan.FromSeconds(5)));
            using var a = camera.Acquire(0)!;
            using var b = camera.Acquire(long.MaxValue / 4)!;
            Assert.Equal(a.Generation, b.Generation);
            var index = TestMedia.ReadIndex(a);
            Assert.True(index > lastIndex, $"frame {index} after {lastIndex}");
            lastIndex = index;
            lastGeneration = a.Generation;
        }
        var health = camera.Health;
        Assert.True(health.Ready);
        Assert.Equal("live", health.Status);
        Assert.True(health.FramesDecoded >= 10);
        Assert.True(camera.IsReadyAt(0));
        Assert.Equal((320, 240), (camera.Info.Width, camera.Info.Height));
    }

    [Fact]
    public async Task EachFrameIsStampedOnArrival()
    {
        TestMedia.Ensure();
        using var camera = new CaptureSource(FakeCamera(25));
        Assert.True(await camera.Opened, camera.Health.Status);
        Assert.True(camera.WaitForFrame(0, TimeSpan.FromSeconds(5)));
        using var lease = (Frames.MediaFrameLease)camera.Acquire(0)!;
        var now = MonoClock.Instance.NowNs;
        Assert.InRange(lease.ArrivalNs, now - 200_000_000, now);
        Assert.Equal(lease.ArrivalNs, lease.PtsNs);
        Assert.InRange(camera.NewestAgeNs, 0, 200_000_000);
    }

    [Fact]
    public async Task ClosingALiveSourceStopsItCleanly()
    {
        TestMedia.Ensure();
        for (var i = 0; i < 5; i++)
        {
            var camera = new CaptureSource(FakeCamera(60));
            Assert.True(await camera.Opened);
            var lease = camera.WaitForFrame(0, TimeSpan.FromSeconds(5)) ? camera.Acquire(0) : null;
            camera.Dispose();
            Assert.False(camera.Health.Ready);
            if (lease is not null)
            {
                Assert.True(TestMedia.ReadIndex(lease) >= 0);
                lease.Dispose();
            }
        }
    }

    [Fact]
    public async Task AMissingDeviceSaysSo()
    {
        TestMedia.Ensure();
        // The platform's own capture format, with a device that is not there.
        var missing = CaptureDevices.PlatformFormat == "dshow" ? "video=PlayMap no such camera" : "/dev/video-none";
        using var camera = new CaptureSource(new CaptureOptions { Format = CaptureDevices.PlatformFormat, Device = missing });
        Assert.False(await camera.Opened);
        Assert.Contains("cannot open", camera.Health.Status, StringComparison.Ordinal);
        Assert.Null(camera.Acquire(0));
        using var nothing = new CaptureSource(new CaptureOptions { Format = "no-such-format", Device = "x" });
        Assert.False(await nothing.Opened);
        Assert.Contains("no input format", nothing.Health.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDeviceListerAnswersWithoutDevices()
    {
        TestMedia.Ensure();
        var formats = CaptureDevices.InputFormats();
        Assert.Contains("lavfi", formats);
        if (OperatingSystem.IsLinux())
            Assert.Contains("video4linux2,v4l2", formats);
        // This machine has no cameras: the platform's listing is empty, not an error, and so is a format that
        // cannot list.
        Assert.All(CaptureDevices.List(withFormats: true), d => Assert.False(string.IsNullOrEmpty(d.Name)));
        Assert.Empty(CaptureDevices.List("lavfi"));
        Assert.Empty(CaptureDevices.Formats("v4l2", "/dev/video-none"));
        Assert.Empty(CaptureDevices.Formats("lavfi", "anything"));
    }
}
