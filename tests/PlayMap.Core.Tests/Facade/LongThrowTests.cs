using PlayMap.Core.Calibration;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Facade;

/// <summary>Long-throw solves against the bench's table (research/facade.md §7: a 4K projector, pins with σ = 1 px, pinhole, square
/// pixels, shift locked): a seeded Monte Carlo of 200 solves per case. Without priors 12 pins on 2 m of relief at 100 m, TR 3 leave
/// the focal σ at 3.7 % (within ±25 %); a 2 % lens prior holds a 95 m throw with 8 pins to the prior's bounds; a flat facade seen
/// head-on locks the focal from the lens with the words; the uncertainty is reported and follows the spread it predicts.</summary>
public sealed class LongThrowTests
{
    private const SolveLocks Pinhole = SolveLocks.Shift | SolveLocks.Distortion;

    private sealed record Spread(double FocalPct, double AxisM, double MeanWallPx, double MeanFocalPctReported, int Locked, int Solved);

    private static Spread Run(double distance, int cols, int rows, double relief, PosePriors priorsFor, bool usePriors, int trials = 200, ulong seed = 11)
    {
        var (k, truth) = LongThrowScene.Projector(distance, 3);
        var focal = new List<double>();
        var axis = new List<double>();
        double wall = 0, reported = 0;
        var locked = 0;
        for (var t = 0; t < trials; t++)
        {
            var pairs = LongThrowScene.GridPins(k, truth, cols, rows, relief, 1, seed + (ulong)t * 7919);
            var priors = usePriors ? priorsFor : PosePriors.None;
            var r = LongThrowSolve.Solve(pairs, k, LensDistortion.None, PoseSolveOptions.Default with { Locks = Pinhole, Robust = false }, priors);
            Assert.True(double.IsFinite(r.Solution.RmsPx), r.Words);
            if (r.FocalLocked)
                locked++;
            focal.Add((r.Solution.Intrinsics.Fx - k.Fx) / k.Fx * 100);
            axis.Add(Vec3d.Dot(r.Solution.Pose.Position - truth.Position, truth.Forward));
            if (r.Uncertainty is { } u)
            {
                wall += u.WallPx;
                reported += u.FocalPct;
            }
        }
        static double Std(List<double> v)
        {
            var mean = v.Average();
            return Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Count - 1));
        }
        return new Spread(Std(focal), Std(axis), wall / trials, reported / trials, locked, trials);
    }

    [Fact]
    public void TwelvePinsOnRelief_LeaveTheFocalAsUncertainAsTheBenchSays()
    {
        var s = Run(100, 4, 3, 2, PosePriors.None, usePriors: false);
        Assert.True(s.FocalPct is >= 3.71 * 0.75 and <= 3.71 * 1.25, $"focal σ {s.FocalPct:0.00} %, axis σ {s.AxisM:0.00} m, reported {s.MeanFocalPctReported:0.00} %");
        Assert.InRange(s.AxisM, 3.67 * 0.75, 3.67 * 1.25);
        // The reported σ agrees with the spread it predicts (first order, so within a third).
        Assert.InRange(s.MeanFocalPctReported, s.FocalPct * 0.67, s.FocalPct * 1.5);
        Assert.Equal(0, s.Locked);
    }

    [Fact]
    public void ALensPriorHoldsA95MetreThrowWithEightPins()
    {
        var (k, _) = LongThrowScene.Projector(95, 3);
        var prior = new PosePriors(k.Fx, k.Fx * 0.02, null, 0, 1);
        var without = Run(95, 4, 2, 2, prior, usePriors: false);
        var with = Run(95, 4, 2, 2, prior, usePriors: true);
        // The table's "+ 2 % focal prior" row bounds the focal at 2 % and the distance at 2 m (here at 95 m, 8 pins, 2 m relief).
        Assert.True(with.FocalPct <= 2.0 * 1.25, $"focal σ {with.FocalPct:0.00} %");
        Assert.True(with.AxisM <= 2.0 * 1.25, $"axis σ {with.AxisM:0.00} m");
        Assert.True(with.FocalPct < without.FocalPct, $"{with.FocalPct:0.00} vs {without.FocalPct:0.00}");
        Assert.True(with.MeanWallPx is > 0 and < 3, $"wall {with.MeanWallPx:0.00} px");
    }

    [Fact]
    public void AFlatFacadeSeenHeadOnLocksTheZoomFromTheLens()
    {
        var (k, truth) = LongThrowScene.Projector(100, 3);
        var pairs = LongThrowScene.Pins(k, truth, 12, 0, 1, 5);
        var prior = new PosePriors(k.Fx * 1.01, k.Fx * 0.02, null, 0, 1);
        var r = LongThrowSolve.Solve(pairs, k, LensDistortion.None, PoseSolveOptions.Default with { Locks = Pinhole }, prior);
        Assert.True(r.FocalLocked, r.Words);
        Assert.Contains(LongThrowSolve.LockedWords, r.Words, StringComparison.Ordinal);
        Assert.Equal(k.Fx * 1.01, r.Solution.Intrinsics.Fx, 6);
        Assert.Equal(SolveLocks.Focal, r.LocksUsed & SolveLocks.Focal);
        Assert.NotNull(r.Uncertainty);
        Assert.Equal(0, r.Uncertainty!.FocalPct);
        // With the focal locked the distance is held by it: the bench's "focal locked" row (0.03 m) within ×4 for a 1 % wrong lens.
        Assert.True(Math.Abs(Vec3d.Dot(r.Solution.Pose.Position - truth.Position, truth.Forward)) < 1.5,
            $"axis {Vec3d.Dot(r.Solution.Pose.Position - truth.Position, truth.Forward):0.00} m");

        var unaided = LongThrowSolve.Solve(pairs, k, LensDistortion.None, PoseSolveOptions.Default with { Locks = Pinhole }, PosePriors.None);
        Assert.False(unaided.FocalLocked);
        Assert.Equal("zoom↔distance", unaided.Weak);
        Assert.Contains("PROJECTOR PRIOR", unaided.Words, StringComparison.Ordinal);
    }

    [Fact]
    public void WithReliefTheFocalIsNotLocked()
    {
        var (k, truth) = LongThrowScene.Projector(60, 3, 25);
        var pairs = LongThrowScene.Pins(k, truth, 12, 2, 1, 9);
        var prior = new PosePriors(k.Fx, k.Fx * 0.02, null, 0, 1);
        var r = LongThrowSolve.Solve(pairs, k, LensDistortion.None, PoseSolveOptions.Default with { Locks = Pinhole }, prior);
        Assert.False(r.FocalLocked, r.Words);
        Assert.NotEqual("zoom↔distance", r.Weak);
        Assert.NotNull(r.Uncertainty);
        Assert.True(r.Uncertainty!.FocalPct is > 0 and < 2, $"{r.Uncertainty.FocalPct}");
    }

    [Fact]
    public void APositionPriorPullsTheCentreAndNarrowsItsUncertainty()
    {
        var (k, truth) = LongThrowScene.Projector(100, 3);
        var pairs = LongThrowScene.Pins(k, truth, 12, 2, 1, 21);
        var options = PoseSolveOptions.Default with { Locks = Pinhole, Robust = false };
        var free = LongThrowSolve.Solve(pairs, k, LensDistortion.None, options, PosePriors.None);
        var at = LongThrowSolve.Solve(pairs, k, LensDistortion.None, options, new PosePriors(null, 0, truth.Position, 0.05, 1));
        Assert.True((at.Solution.Pose.Position - truth.Position).Length < (free.Solution.Pose.Position - truth.Position).Length);
        Assert.True((at.Solution.Pose.Position - truth.Position).Length < 0.2);
        Assert.True(at.Uncertainty!.AxisM < free.Uncertainty!.AxisM / 10, $"{at.Uncertainty.AxisM} vs {free.Uncertainty.AxisM}");
    }
}
