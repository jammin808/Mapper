using PlayMap.Core.Media;
using PlayMap.Core.Time;
using PlayMap.Media.Sources;

namespace PlayMap.Media.Tests;

/// <summary>Stills decode once into RGBA (half floats when deep or float) and answer every time; sequences play
/// numbered images at a rate, frame-exact, seekable and looping.</summary>
public sealed class StillAndSequenceTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task APngBecomesOneRgbaFrameWithItsAlpha()
    {
        using var still = new ImageSource(TestMedia.Clip("still.png"));
        Assert.True(await still.Opened, still.Health.Status);
        Assert.True(still.IsReadyAt(0));
        Assert.True(still.IsReadyAt(long.MaxValue / 2));
        using var lease = still.Acquire(123_456_789)!;
        Assert.Equal(PixelLayout.Rgba8, lease.Layout);
        Assert.Equal((64, 48), (lease.Width, lease.Height));
        Assert.True(lease.HasAlpha);
        Assert.Equal(ColorRange.Full, lease.Range);
        Assert.Equal(1, lease.Generation);
        Assert.Equal((51, 102, 153, 255), Pixel(lease, 10, 10));
        Assert.Equal(128, Pixel(lease, 40, 10).A);
        Assert.True(still.Health.Ready);
        Assert.Equal(1, still.Health.FramesDecoded);
        Assert.True(still.Info.HasAlpha);
    }

    /// <summary>The air waits for a still's first picture, so its decode is not queued behind CPU jobs: with every compute thread held
    /// by a job as long as a solve (here, until this test lets them go), a job queued on the compute threads waits for them all, and a
    /// still opens all the same — its decode runs on the pool. The decode's time is recorded beside the measurement.</summary>
    [Fact]
    public async Task AStillOpensWhileEveryComputeThreadIsHeldByASolve()
    {
        using var held = new ManualResetEventSlim(false);
        var holding = Enumerable.Range(0, Core.Threading.Compute.Threads).Select(_ => Core.Threading.Compute.RunAsync(() => held.Wait())).ToList();
        try
        {
            var queued = Core.Threading.Compute.RunAsync(() => 1);
            var started = System.Diagnostics.Stopwatch.StartNew();
            using var still = new ImageSource(TestMedia.Clip("still.png"));
            Assert.True(await still.Opened.WaitAsync(TimeSpan.FromMinutes(1)), still.Health.Status);
            output.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"a still opened in {started.Elapsed.TotalMilliseconds:0.0} ms (its decode {still.Health.DecodeMsP95:0.000} ms) beside {Core.Threading.Compute.Threads} held compute threads"));
            Assert.False(queued.IsCompleted, "a job queued on the held compute threads ran");
        }
        finally
        {
            held.Set();
            await Task.WhenAll(holding);
        }
    }

    [Fact]
    public async Task ASixteenBitPngBecomesHalfFloats()
    {
        using var still = new ImageSource(TestMedia.Clip("still16.png"));
        Assert.True(await still.Opened, still.Health.Status);
        using var lease = still.Acquire(0)!;
        Assert.Equal(PixelLayout.RgbaHalf, lease.Layout);
        var (r, g, b, a) = HalfPixel(lease, 5, 5);
        Assert.Equal(40000 / 65535.0, r, 3);
        Assert.Equal(20000 / 65535.0, g, 3);
        Assert.Equal(1.0, b, 3);
        Assert.Equal(1.0, a, 3);
    }

    [Fact]
    public async Task AnExrKeepsItsFloatValues()
    {
        using var still = new ImageSource(TestMedia.Clip("still.exr"));
        Assert.True(await still.Opened, still.Health.Status);
        using var lease = still.Acquire(0)!;
        Assert.Equal(PixelLayout.RgbaHalf, lease.Layout);
        var (r, g, b, a) = HalfPixel(lease, 63, 47);
        Assert.Equal((2.5, 0.25, 1.0, 1.0), (Math.Round(r, 3), Math.Round(g, 3), Math.Round(b, 3), Math.Round(a, 3)));
        Assert.Equal(TransferFunction.Linear, lease.Transfer);
    }

    [Fact]
    public async Task AJpegBecomesRgbaOnce()
    {
        using var still = new ImageSource(TestMedia.Clip("still.jpg"));
        Assert.True(await still.Opened, still.Health.Status);
        using var lease = still.Acquire(0)!;
        Assert.Equal(PixelLayout.Rgba8, lease.Layout);
        Assert.False(lease.HasAlpha);
        Assert.InRange(Pixel(lease, 32, 24).R, 120, 136);
    }

    [Fact]
    public async Task AMissingStillSaysWhy()
    {
        using var still = new ImageSource(Path.Combine(TestMedia.Dir, "nope.png"));
        Assert.False(await still.Opened);
        Assert.Contains("cannot open", still.Health.Status, StringComparison.Ordinal);
        Assert.Null(still.Acquire(0));
    }

    [Fact]
    public async Task ASequencePlaysFrameExactFromAFolderOrAPattern()
    {
        var folder = TestMedia.Clip("seq");
        foreach (var location in new[] { folder, Path.Combine(folder, "frame_%04d.png") })
        {
            using var seq = new ImageSequenceSource(location, FrameRate.Fps25);
            Assert.True(await seq.Opened, seq.Health.Status);
            Assert.Equal(12, seq.FrameCount);
            Assert.Equal(480_000_000, seq.Info.DurationNs);
            foreach (var n in new long[] { 0, 1, 2, 7, 11, 3, 0, 9 })
            {
                var t = FrameRate.Fps25.TimeOf(n) + 1;
                seq.Prepare(t, false, 1);
                Assert.True(seq.WaitUntilReady(t, Wait), $"frame {n} of {location}: {seq.Health}");
                using var lease = seq.Acquire(t)!;
                Assert.Equal(PixelLayout.Rgba8, lease.Layout);
                Assert.Equal(n, TestMedia.ReadIndex(lease));
            }
            Assert.True(seq.Health.Ready);
        }
    }

    [Fact]
    public async Task ASequenceAtAnotherRateKeepsItsFramesOnThatGrid()
    {
        using var seq = new ImageSequenceSource(TestMedia.Clip("seq"), FrameRate.Fps2997);
        Assert.True(await seq.Opened);
        for (var n = 0L; n < 12; n++)
        {
            var t = FrameRate.Fps2997.TimeOf(n);
            seq.Prepare(t, true, 1);
            Assert.True(seq.WaitUntilReady(t, Wait));
            using var lease = seq.Acquire(t)!;
            Assert.Equal(n, TestMedia.ReadIndex(lease));
            Assert.Equal(t, lease.PtsNs);
        }
    }

    [Fact]
    public async Task ALoopingSequenceContinuesItsTimesAcrossTheLoopPoint()
    {
        var rate = FrameRate.Fps25;
        using var seq = new ImageSequenceSource(TestMedia.Clip("seq"), rate, new ImageSequenceOptions { Loop = true });
        Assert.True(await seq.Opened);
        var len = rate.TimeOf(12);
        long last = -1;
        for (var step = 0; step < 40; step++)
        {
            var continuous = rate.TimeOf(step) + 5;
            var wrapped = continuous % len;
            seq.Prepare(wrapped, true, 1);
            Assert.True(seq.WaitUntilReady(wrapped, Wait));
            using var lease = seq.Acquire(wrapped)!;
            Assert.Equal(step % 12, TestMedia.ReadIndex(lease));
            Assert.Equal(rate.TimeOf(step), lease.PtsNs);
            Assert.True(lease.PtsNs > last);
            last = lease.PtsNs;
        }
    }

    private static unsafe (int R, int G, int B, int A) Pixel(VideoFrameLease lease, int x, int y)
    {
        var p = (byte*)lease.Planes[0].Data + (nint)y * lease.Planes[0].Stride + x * 4;
        return (p[0], p[1], p[2], p[3]);
    }

    private static unsafe (double R, double G, double B, double A) HalfPixel(VideoFrameLease lease, int x, int y)
    {
        var p = (Half*)((byte*)lease.Planes[0].Data + (nint)y * lease.Planes[0].Stride) + x * 4;
        return ((double)p[0], (double)p[1], (double)p[2], (double)p[3]);
    }
}
