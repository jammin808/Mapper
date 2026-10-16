using PlayMap.Core.Calibration;
using PlayMap.Core.Mapping;
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

    /// <summary>200 seeded solves; with priors, each trial's lens prior is the truth drawn off by N(0, σ %) — what a datasheet gives.</summary>
    private static Spread Run(double distance, int cols, int rows, double relief, double priorSigmaPct, bool usePriors, int trials = 200, ulong seed = 11)
    {
        var (k, truth) = LongThrowScene.Projector(distance, 3);
        var drawn = new Pcg32(seed, 0x99);
        var focal = new List<double>();
        var axis = new List<double>();
        double wall = 0, reported = 0;
        var locked = 0;
        for (var t = 0; t < trials; t++)
        {
            var pairs = LongThrowScene.GridPins(k, truth, cols, rows, relief, 1, seed + (ulong)t * 7919);
            var priors = usePriors ? new PosePriors(k.Fx * (1 + drawn.NextGaussian() * priorSigmaPct / 100), k.Fx * priorSigmaPct / 100, null, 0, 1) : PosePriors.None;
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
        var s = Run(100, 4, 3, 2, 0, usePriors: false);
        Assert.True(s.FocalPct is >= 3.71 * 0.75 and <= 3.71 * 1.25, $"focal σ {s.FocalPct:0.00} %, axis σ {s.AxisM:0.00} m, reported {s.MeanFocalPctReported:0.00} %");
        Assert.InRange(s.AxisM, 3.67 * 0.75, 3.67 * 1.25);
        // The reported σ agrees with the spread it predicts (first order, so within a third).
        Assert.InRange(s.MeanFocalPctReported, s.FocalPct * 0.67, s.FocalPct * 1.5);
        Assert.Equal(0, s.Locked);
    }

    [Fact]
    public void ALensPriorHoldsA95MetreThrowWithEightPins()
    {
        var without = Run(95, 4, 2, 2, 2, usePriors: false);
        var with = Run(95, 4, 2, 2, 2, usePriors: true);
        var words = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"focal σ {with.FocalPct:0.00} % (without {without.FocalPct:0.00} %), axis σ {with.AxisM:0.00} m, locked {with.Locked} of {with.Solved}");
        // The table's "+ 2 % focal prior" row bounds the focal at 2 % and the distance at 2 m (here at 95 m, 8 pins, 2 m relief),
        // with the prior itself 2 % off in each trial.
        Assert.True(with.FocalPct <= 2.0 * 1.25, words);
        Assert.True(with.AxisM <= 2.0 * 1.25, words);
        Assert.True(with.FocalPct < without.FocalPct, words);
        Assert.True(with.MeanWallPx is > 0 and < 3, words);
        Assert.Equal(0, without.Locked);
    }

    [Fact]
    public void NearCollinearPinsLockTheZoomFromTheLensWithWords()
    {
        var (k, truth) = LongThrowScene.Projector(100, 3);
        // Ten pins along one horizontal line of the facade, a few pixels above and below it.
        var rng = new Pcg32(31, 2);
        var pairs = new List<Correspondence>();
        for (var i = 0; i < 10; i++)
        {
            var px = new Vec2d((0.1 + 0.08 * i) * k.Width, 0.5 * k.Height + rng.Range(-4.0, 4.0));
            var dir = truth.CameraToWorld * Core.Geometry.CameraModel.PixelRayCamera(px, k, LensDistortion.None);
            var world = truth.Position + dir * (-truth.Position.Z / dir.Z);
            var seen = Core.Geometry.CameraModel.Project(world, k, LensDistortion.None, truth)!.Value;
            pairs.Add(new Correspondence(world, seen + new Vec2d(rng.NextGaussian(), rng.NextGaussian())));
        }
        var prior = new PosePriors(k.Fx * 1.01, k.Fx * 0.02, null, 0, 1);
        var r = LongThrowSolve.Solve(pairs, k, LensDistortion.None, PoseSolveOptions.Default with { Locks = Pinhole }, prior);
        Assert.True(r.FocalLocked, r.Words);
        Assert.Contains("zoom locked from the lens", r.Words, StringComparison.Ordinal);
        Assert.Equal(k.Fx * 1.01, r.Solution.Intrinsics.Fx, 6);
    }

    [Fact]
    public void AnOperatorsLockKeepsTheProjectorsOwnFocalAndALensLockIsAskedAgain()
    {
        var (k, truth) = LongThrowScene.Projector(60, 3, 25);
        var measured = k with { Fx = k.Fx * 0.99, Fy = k.Fy * 0.99 };
        var pairs = LongThrowScene.Pins(k, truth, 12, 2, 1, 9);
        var prior = new PosePriors(k.Fx * 1.02, k.Fx * 0.02, null, 0, 1);
        var held = LongThrowSolve.Solve(pairs, measured, LensDistortion.None, PoseSolveOptions.Default with { Locks = Pinhole | SolveLocks.Focal }, prior);
        Assert.Equal(measured.Fx, held.Solution.Intrinsics.Fx);
        Assert.False(held.FocalLocked);

        // A projector the lens locked at its prior in an earlier solve, now with pins on relief at 60 m oblique: asked again, it unlocks.
        var pins = pairs.Select((c, i) => new CalibrationPin("pn" + i.ToString("00000000", System.Globalization.CultureInfo.InvariantCulture), c.World, c.Pixel, true)).ToList();
        var pj = new Projector("pj_relock1", "P", k with { Fx = k.Fx * 1.02, Fy = k.Fy * 1.02 }, LensDistortion.None, truth, 0, "#FFFFFF", false, [.. pins],
            Pinhole | SolveLocks.Focal, 1) { Priors = new SolvePriors(3 * 1.02, 2, null, "", 0, ""), FocalLock = FocalLockSource.Lens };
        var retested = LongThrowSolve.Solve(pj, retestLock: true);
        Assert.Equal(SolveLocks.None, retested.LocksUsed & SolveLocks.Focal);
        Assert.Equal(FocalLockSource.Operator, retested.FocalLockBy);
        Assert.NotEqual(pj.Intrinsics.Fx, retested.Solution.Intrinsics.Fx);
        var kept = LongThrowSolve.Solve(pj, retestLock: false);
        Assert.Equal(SolveLocks.Focal, kept.LocksUsed & SolveLocks.Focal);
        Assert.Equal(FocalLockSource.Operator, kept.FocalLockBy);
        Assert.Equal(pj.Intrinsics.Fx, kept.Solution.Intrinsics.Fx);

        // The same lock put there by the operator, its focal exactly at the lens prior: never asked again.
        var operators = LongThrowSolve.Solve(pj with { FocalLock = FocalLockSource.Operator }, retestLock: true);
        Assert.Equal(SolveLocks.Focal, operators.LocksUsed & SolveLocks.Focal);
        Assert.Equal(FocalLockSource.Operator, operators.FocalLockBy);
        Assert.Equal(pj.Intrinsics.Fx, operators.Solution.Intrinsics.Fx);
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
