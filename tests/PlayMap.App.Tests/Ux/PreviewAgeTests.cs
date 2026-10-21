using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PlayMap.App.Controls;

namespace PlayMap.App.Tests.Ux;

/// <summary>Preview age, on a manual desk clock: a preview's rate comes from its ring slots' stamps and its age from when its newest
/// picture arrived; a ring that freezes is stale once its picture is older than two frames of its own rate (a badge with the age) and
/// greyed at one second with the reason — the engine quiet, or no new picture; a ring gone takes its picture away at once.</summary>
public class PreviewAgeTests
{
    /// <summary>A source that hands the surface a picture whenever a test gives it a stamp.</summary>
    private sealed class Frames : IPreviewSource
    {
        private readonly WriteableBitmap _bitmap = new(new PixelSize(16, 9), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        private long? _next;

        public bool Live { get; set; } = true;

        public long LastStampNs { get; private set; }

        public void Next(long stampNs) => _next = stampNs;

        public WriteableBitmap? Poll()
        {
            if (_next is not { } stamp)
                return null;
            _next = null;
            LastStampNs = stamp;
            return _bitmap;
        }

        public void Dispose() => _bitmap.Dispose();
    }

    [Fact]
    public void TheModelReadsItsRateFromTheStampsAndItsAgeFromTheDesksClock()
    {
        var age = new PreviewAge();
        Assert.Null(age.AgeMs(0));
        Assert.False(age.IsStale(0));
        // 30 frames a second: 33.3 ms between stamps, whatever the node's clock reads.
        for (var i = 0; i < 3; i++)
            age.Arrived(9_000_000_000 + (i * 33_333_333), 100 + (i * 33));
        Assert.Equal(33.333, age.PeriodMs, 2);
        Assert.False(age.IsStale(166 + 66));
        Assert.True(age.IsStale(166 + 67));
        Assert.Equal("stale 67 ms", age.Badge(166 + 67));
        Assert.False(age.IsFrozen(166 + 999));
        Assert.True(age.IsFrozen(166 + 1000));
        Assert.Equal("Frozen 1.0 s: the ring brings no new picture", age.FrozenWords(166 + 1000, engineLive: true));
        Assert.Equal("Frozen 1.0 s: the engine is quiet", age.FrozenWords(166 + 1000, engineLive: false));
        Assert.Equal("stale 1.0 s", age.Badge(166 + 1000));
        // A new picture: fresh again.
        age.Arrived(9_100_000_000, 1200);
        Assert.False(age.IsStale(1200));
        Assert.Null(age.FrozenWords(1200, engineLive: true));
    }

    [AvaloniaFact]
    public void AFrozenRingIsStaleAfterTwoFramesAndGreyAtOneSecondWithTheReason()
    {
        var now = 50_000L;
        var clock = PreviewSurface.Clock;
        PreviewSurface.Clock = () => now;
        var frames = new Frames();
        var feeds = PreviewFeeds.Resolve;
        PreviewFeeds.Resolve = (_, _, _) => frames;
        try
        {
            var surface = new PreviewSurface { NodeId = "demo0001", ViewId = "op-op_1", Label = "Main" };
            var window = new Window { Content = surface, Width = 480, Height = 270 };
            window.Show();
            DeskHarness.Pump();
            for (var i = 0; i < 3; i++)
            {
                frames.Next(i * 16_666_667L);
                surface.PollFrame();
                now += 17;
            }
            Assert.True(surface.HasFrame);
            Assert.Null(surface.AgeBadge);

            // The ring freezes: within two frames (33 ms) of its rate it is fresh, past them stale with its age.
            now += 16;
            surface.PollFrame();
            Assert.Null(surface.AgeBadge);
            now += 2;
            surface.PollFrame();
            Assert.Equal("stale 35 ms", surface.AgeBadge);
            Assert.Null(surface.FrozenWords);

            // At one second it is greyed, and says why; on a quiet link the engine is quiet.
            now += 965;
            surface.PollFrame();
            Assert.Equal("Frozen 1.0 s: the ring brings no new picture", surface.FrozenWords);
            PreviewSurface.SetLinkLive(window, false);
            surface.PollFrame();
            Assert.Equal("Frozen 1.0 s: the engine is quiet", surface.FrozenWords);

            // A new picture: fresh again.
            PreviewSurface.SetLinkLive(window, true);
            frames.Next(5_000_000_000);
            surface.PollFrame();
            Assert.Null(surface.AgeBadge);
            Assert.Null(surface.FrozenWords);

            // A ring gone takes its picture away at once: the stand-in, not a frozen picture.
            frames.Live = false;
            surface.PollFrame();
            Assert.False(surface.HasFrame);
            Assert.Null(surface.FrozenWords);
            window.Close();
        }
        finally
        {
            PreviewSurface.Clock = clock;
            PreviewFeeds.Resolve = feeds;
        }
    }
}
