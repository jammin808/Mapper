using PlayMap.Core.Numerics;
using PlayMap.Core.Vision;
using PlayMap.Vision.Drift;

namespace PlayMap.Vision.Tests;

/// <summary>The drift watch sees a 6-pixel shift as a bump (after confirmation) with every region's shift, sits
/// still through sensor noise, holds its state between the thresholds, names the regions when only part of the
/// frame moves, and declines to judge a featureless frame.</summary>
public class DriftWatchTests
{
    private const int W = 320;
    private const int H = 240;
    private static readonly double[] Texture = MakeTexture();

    private static double[] MakeTexture()
    {
        var rng = new Pcg32(21);
        var t = new double[W * H];
        for (var i = 0; i < t.Length; i++)
            t[i] = rng.NextDouble();
        // Blur twice (5×5 box) for structure at a few pixels' scale, then stretch to the byte range.
        for (var pass = 0; pass < 2; pass++)
        {
            var o = new double[t.Length];
            for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++)
                {
                    double s = 0;
                    var n = 0;
                    for (var dy = -2; dy <= 2; dy++)
                        for (var dx = -2; dx <= 2; dx++)
                        {
                            int xx = Math.Clamp(x + dx, 0, W - 1), yy = Math.Clamp(y + dy, 0, H - 1);
                            s += t[yy * W + xx];
                            n++;
                        }
                    o[y * W + x] = s / n;
                }
            t = o;
        }
        double min = t.Min(), max = t.Max();
        return t.Select(v => 20 + 215 * (v - min) / (max - min)).ToArray();
    }

    /// <summary>The texture with its content moved by (dx, dy) — bilinear, so fractions are exact shifts — in the
    /// region x &lt; <paramref name="onlyLeftOf"/> (the whole frame by default), plus sensor noise.</summary>
    private static ImageGray8 Frame(double dx, double dy, double noise, ulong seed, int onlyLeftOf = W)
    {
        var rng = new Pcg32(seed);
        var img = ImageGray8.Create(W, H);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                double sx = x < onlyLeftOf ? x - dx : x, sy = x < onlyLeftOf ? y - dy : y;
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                double fx = sx - x0, fy = sy - y0;
                double At(int xx, int yy) => Texture[Math.Clamp(yy, 0, H - 1) * W + Math.Clamp(xx, 0, W - 1)];
                var v = At(x0, y0) * (1 - fx) * (1 - fy) + At(x0 + 1, y0) * fx * (1 - fy) + At(x0, y0 + 1) * (1 - fx) * fy + At(x0 + 1, y0 + 1) * fx * fy;
                img.Data[y * W + x] = (byte)Math.Clamp(Math.Round(v + rng.NextGaussian() * noise), 0, 255);
            }
        return img;
    }

    [Fact]
    public void ASixPixelShiftIsABumpWithEveryRegionsShift()
    {
        var watch = new DriftWatch(Frame(0, 0, 3, 1));
        var first = watch.Observe(Frame(6, 0, 3, 2));
        Assert.Equal(DriftState.Stable, first.State);
        Assert.False(first.Changed);
        var second = watch.Observe(Frame(6, 0, 3, 3));
        Assert.Equal(DriftState.Bumped, second.State);
        Assert.True(second.Changed);
        Assert.Equal(12, second.Regions.Length);
        Assert.All(second.Regions, r =>
        {
            Assert.True(r.Moved);
            Assert.InRange(r.Shift.X, 5.7, 6.3);
            Assert.InRange(r.Shift.Y, -0.3, 0.3);
        });
        Assert.InRange(second.MedianMagnitude, 5.8, 6.2);
        Assert.StartsWith("bumped", second.Words, StringComparison.Ordinal);
        Assert.Contains("whole frame", second.Words, StringComparison.Ordinal);
    }

    [Fact]
    public void SensorNoiseLeavesItStable()
    {
        var watch = new DriftWatch(Frame(0, 0, 4, 10));
        for (ulong f = 0; f < 8; f++)
        {
            var r = watch.Observe(Frame(0, 0, 4, 11 + f));
            Assert.Equal(DriftState.Stable, r.State);
            Assert.False(r.Changed);
            Assert.All(r.Regions, g => Assert.InRange(g.Shift.Length, 0, 0.3));
            Assert.DoesNotContain(r.Regions, g => g.Moved);
        }
    }

    [Fact]
    public void BetweenTheThresholdsTheStateHolds()
    {
        var watch = new DriftWatch(Frame(0, 0, 2, 30));
        ulong seed = 31;
        DriftReport Run(double shift, int frames)
        {
            DriftReport r = null!;
            for (var i = 0; i < frames; i++)
                r = watch.Observe(Frame(shift, shift * 0.5, 2, seed++));
            return r;
        }
        Assert.Equal(DriftState.Stable, Run(0.9, 3).State);
        Assert.Equal(DriftState.Drifting, Run(2.2, 2).State);
        Assert.Equal(DriftState.Drifting, Run(0.9, 3).State);
        Assert.Equal(DriftState.Stable, Run(0.2, 2).State);
        // A single odd frame does not flip it: confirmation needs two in a row.
        Assert.Equal(DriftState.Stable, Run(2.2, 1).State);
        Assert.Equal(DriftState.Stable, Run(0.2, 1).State);
    }

    [Fact]
    public void WhenPartOfTheFrameMovesTheRegionsAreNamed()
    {
        var watch = new DriftWatch(Frame(0, 0, 3, 40));
        DriftReport r = null!;
        for (ulong f = 0; f < 2; f++)
            r = watch.Observe(Frame(0, 5, 3, 41 + f, onlyLeftOf: 80));
        Assert.Equal(DriftState.Stable, r.State);
        var moved = r.Regions.Where(g => g.Moved).Select(g => g.Index).ToArray();
        Assert.Equal([0, 4, 8], moved);
        Assert.All(r.Regions.Where(g => g.Moved), g => Assert.InRange(g.Shift.Y, 4.7, 5.3));
        Assert.Contains("3 of 12 regions moved (regions 0,4,8)", r.Words, StringComparison.Ordinal);
    }

    [Fact]
    public void AFeaturelessFrameIsNotJudged()
    {
        var flat = ImageGray8.Create(W, H);
        flat.Data.AsSpan().Fill(128);
        var watch = new DriftWatch(flat);
        var r = watch.Observe(flat);
        Assert.Equal(DriftState.Stable, r.State);
        Assert.Contains("too little texture", r.Words, StringComparison.Ordinal);
        Assert.All(r.Regions, g => Assert.Equal(0, g.Confidence));
    }
}
