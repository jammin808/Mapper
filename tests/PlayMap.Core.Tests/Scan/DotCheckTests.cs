using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;

namespace PlayMap.Core.Tests.Scan;

/// <summary>The dot check's pieces: a jittered pattern never repeats itself, detection centres each spot to a small fraction of a
/// pixel, and matching finds the dots that moved further than half their spacing (the consensus shift), leaving none mismatched.</summary>
public sealed class DotCheckTests
{
    [Fact]
    public void APatternIsSeededAndInsideTheRaster()
    {
        var a = DotCheck.Pattern(1920, 1080, 64, 7);
        var b = DotCheck.Pattern(1920, 1080, 64, 7);
        Assert.Equal(a.ToArray(), b.ToArray());
        Assert.NotEqual(a.ToArray(), DotCheck.Pattern(1920, 1080, 64, 8).ToArray());
        Assert.Equal(30 * 16, a.Length);
        Assert.All(a, d => Assert.True(d.X > 0 && d.Y > 0 && d.X < 1920 && d.Y < 1080));
    }

    [Fact]
    public void DetectionCentresEverySpot()
    {
        var dots = DotCheck.Pattern(640, 360, 40, 3);
        var frame = DotCheck.Render(dots.AsSpan(), 640, 360, 2.5);
        var blobs = DotCheck.Detect(frame, new byte[640 * 360], 640, 360);
        Assert.Equal(dots.Length, blobs.Count);
        foreach (var d in dots)
        {
            var nearest = blobs.Min(b => Vec2d.Distance(b.At, d));
            Assert.True(nearest < 0.05, $"{nearest} px");
        }
    }

    [Fact]
    public void MatchingFindsAShiftBeyondHalfTheSpacing()
    {
        var predicted = DotCheck.Pattern(1280, 720, 32, 11).Select(d => (Vec2d?)d).ToList();
        var rng = new Pcg32(5);
        // Seen 21 px right and 9 px down (the spacing is 32), slightly turned, with every 20th dot lost and a few stray spots.
        var turn = 0.004;
        var observed = new List<DotBlob>();
        var truth = new Dictionary<int, int>();
        for (var i = 0; i < predicted.Count; i++)
        {
            if (i % 20 == 0)
                continue;
            var p = predicted[i]!.Value;
            var q = new Vec2d(p.X - turn * (p.Y - 360) + 21, p.Y + turn * (p.X - 640) + 9) + new Vec2d(rng.NextGaussian() * 0.1, rng.NextGaussian() * 0.1);
            truth[i] = observed.Count;
            observed.Add(new DotBlob(q, 100, 9));
        }
        for (var k = 0; k < 10; k++)
            observed.Add(new DotBlob(new Vec2d(rng.Range(0.0, 1280.0), rng.Range(0.0, 720.0)), 100, 9));
        var m = DotCheck.Match(predicted, observed, 128);
        Assert.Equal(21, m.Shift.X, 2.5);
        Assert.Equal(9, m.Shift.Y, 2.5);
        var right = truth.Count(t => m.Observed[t.Key] == t.Value);
        Assert.True(right >= 0.98 * truth.Count, $"{right} of {truth.Count}");
        // No dot takes another dot's spot (a stray spot may stand in for a lost dot: it sits where that dot should be).
        var real = truth.Count;
        var wrong = Enumerable.Range(0, predicted.Count).Count(i => m.Observed[i] >= 0 && m.Observed[i] < real && (!truth.TryGetValue(i, out var j) || j != m.Observed[i]));
        Assert.Equal(0, wrong);
    }
}
