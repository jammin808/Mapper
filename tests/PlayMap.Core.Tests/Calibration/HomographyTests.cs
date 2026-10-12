using PlayMap.Core.Calibration;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Calibration;

/// <summary>Plane-to-plane maps: the normalised DLT is exact on four pairs, RANSAC sees through 30% outliers,
/// LM refinement and the inverse hold, and Heckbert's corner pin agrees with the DLT.</summary>
public class HomographyTests
{
    private static readonly Mat3d Truth = new(1.2, 0.15, 320, -0.08, 0.95, 180, 2e-4, -1.5e-4, 1);

    [Fact]
    public void FourPairsGiveTheExactHomography()
    {
        Vec2d[] from = [new(0, 0), new(1000, 30), new(950, 700), new(-20, 650)];
        var to = from.Select(Truth.TransformPoint).ToArray();
        var h = Homography.Fit(from, to);
        Assert.NotNull(h);
        var rng = new Pcg32(3);
        for (var i = 0; i < 50; i++)
        {
            var p = new Vec2d(rng.Range(0, 1000), rng.Range(0, 700));
            Assert.True(Vec2d.Distance(Truth.TransformPoint(p), h.Value.TransformPoint(p)) < 1e-7);
        }
    }

    [Fact]
    public void ThreeCollinearPointsAreRefused()
    {
        Vec2d[] from = [new(0, 0), new(1, 1), new(2, 2), new(0, 5)];
        Vec2d[] to = [new(0, 0), new(1, 2), new(2, 4), new(3, 1)];
        Assert.True(Homography.FitRobust(from, to).InlierCount == 0);
    }

    [Fact]
    public void ThirtyPercentOutliersAreRejectedAndTheRestFitToTheNoise()
    {
        var rng = new Pcg32(7);
        var from = new List<Vec2d>();
        var to = new List<Vec2d>();
        var outlier = new List<bool>();
        for (var i = 0; i < 200; i++)
        {
            var p = new Vec2d(rng.Range(0, 1200), rng.Range(0, 800));
            var bad = i % 10 < 3;
            var q = bad ? new Vec2d(rng.Range(0, 2000), rng.Range(0, 1500)) : Truth.TransformPoint(p) + Synthetic.Noise(ref rng, 0.5);
            from.Add(p);
            to.Add(q);
            outlier.Add(bad && Vec2d.Distance(q, Truth.TransformPoint(p)) > 5);
        }
        var fit = Homography.FitRobust(from, to, thresholdPx: 3, seed: 11);
        Assert.True(fit.Succeeded);
        for (var i = 0; i < from.Count; i++)
            if (outlier[i])
                Assert.False(fit.Inliers[i]);
        Assert.True(fit.InlierCount >= 135, $"{fit.InlierCount} inliers");
        Assert.True(fit.RmsPx < 1.0, $"rms {fit.RmsPx}");
        for (var i = 0; i < 40; i++)
        {
            var p = new Vec2d(rng.Range(0, 1200), rng.Range(0, 800));
            Assert.True(Vec2d.Distance(Truth.TransformPoint(p), fit.H.TransformPoint(p)) < 0.5);
        }
    }

    [Fact]
    public void RefinementLowersTheSymmetricTransferErrorOfANudgedStart()
    {
        var rng = new Pcg32(5);
        var from = Enumerable.Range(0, 60).Select(_ => new Vec2d(rng.Range(0, 1000), rng.Range(0, 800))).ToArray();
        var to = from.Select(p => Truth.TransformPoint(p) + Synthetic.Noise(ref rng, 0.3)).ToArray();
        var nudged = Truth + new Mat3d(0.01, 0, 3, 0, -0.01, -2, 1e-5, 0, 0);
        var refined = Homography.Refine(nudged, from, to);
        double Err(Mat3d h) => from.Select((p, i) => Homography.TransferError(h, p, to[i])).Average();
        Assert.True(Err(refined) < 0.5, $"{Err(refined)}");
        Assert.True(Err(refined) < Err(nudged) / 10);
        var inv = Homography.Inverse(refined);
        Assert.True(Vec2d.Distance(inv.TransformPoint(refined.TransformPoint(new Vec2d(400, 300))), new Vec2d(400, 300)) < 1e-9);
    }

    [Fact]
    public void HeckbertsCornerPinPutsTheUnitSquareOnTheQuadAndAgreesWithTheDlt()
    {
        Vec2d[] quad = [new(100, 80), new(1700, 140), new(1820, 1000), new(60, 930)];
        var h = Homography.UnitSquareToQuad(quad[0], quad[1], quad[2], quad[3]);
        Vec2d[] square = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        for (var i = 0; i < 4; i++)
            Assert.True(Vec2d.Distance(h.TransformPoint(square[i]), quad[i]) < 1e-9);
        var dlt = Homography.Fit(square, quad);
        Assert.NotNull(dlt);
        Assert.True(Vec2d.Distance(h.TransformPoint(new Vec2d(0.3, 0.7)), dlt.Value.TransformPoint(new Vec2d(0.3, 0.7))) < 1e-7);

        // A parallelogram takes the affine branch.
        var affine = Homography.UnitSquareToQuad(new Vec2d(0, 0), new Vec2d(2, 0), new Vec2d(3, 1), new Vec2d(1, 1));
        Assert.Equal(0, affine.M20);
        Assert.Equal(new Vec2d(3, 1), affine.TransformPoint(new Vec2d(1, 1)));

        // Quad to quad composes two corner pins.
        Vec2d[] other = [new(0, 0), new(800, 20), new(820, 610), new(-10, 600)];
        var q2q = Homography.QuadToQuad(quad, other);
        for (var i = 0; i < 4; i++)
            Assert.True(Vec2d.Distance(q2q.TransformPoint(quad[i]), other[i]) < 1e-7);
    }
}
