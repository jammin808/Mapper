using System.Collections.Immutable;
using PlayMap.Core.Calibration;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Calibration;

/// <summary>The projector pose solver: the DLT and RQ recover a half-image lens shift exactly, locks keep
/// what they lock to the bit, pins on one wall with an unknown focal length converge through Zhang's
/// constraint, a single bad pick among twelve is named, and dense pairs fit k1 and k2 through outliers.</summary>
public class PoseSolverTests
{
    [Fact]
    public void TheDltAndRqRecoverIntrinsicsRotationAndCentreWithHalfImageShift()
    {
        var k = Synthetic.Projector(shiftY: 0.5);
        var pose = Pose.LookAt(new Vec3d(0.4, 0.3, -0.2), new Vec3d(-0.3, 2.0, 5), Vec3d.UnitY);
        var rng = new Pcg32(1);
        var pins = Synthetic.Pins(k, LensDistortion.None, pose, 20, ref rng, 0);
        var p = CameraDlt.Solve(pins.Select(c => c.World).ToList(), pins.Select(c => c.Pixel).ToList());
        Assert.NotNull(p);
        var dec = CameraDlt.Decompose(p);
        Assert.NotNull(dec);
        var d = dec.Value;
        Assert.Equal(k.Fx, d.Fx, 1e-6 * k.Fx);
        Assert.Equal(k.Fy, d.Fy, 1e-6 * k.Fy);
        Assert.Equal(k.Cx, d.Cx, 1e-5);
        Assert.Equal(1080, d.Cy, 1e-5);
        Assert.Equal(0, d.Skew, 1e-5);
        Assert.True(Synthetic.AngleDegrees(pose, d.Pose) < 1e-7);
        Assert.True(Vec3d.Distance(pose.Position, d.Centre) < 1e-8);
        Assert.True(Vec2d.Distance(CameraDlt.Project(p, pins[3].World)!.Value, pins[3].Pixel) < 1e-6);
    }

    [Fact]
    public void AnUnlockedSolveRecoversAShiftedProjectorFromNoisyPins()
    {
        var truth = Synthetic.Projector(shiftY: 0.5);
        var pose = Pose.LookAt(new Vec3d(0.4, 0.3, -0.2), new Vec3d(-0.3, 2.0, 5), Vec3d.UnitY);
        var rng = new Pcg32(2);
        var pins = Synthetic.Pins(truth, LensDistortion.None, pose, 20, ref rng, 0.4);
        var prior = Intrinsics.FromThrowRatio(1920, 1080, 1.3, 0, 0.4);
        var s = PoseSolver.Solve(pins, prior, LensDistortion.None);
        Assert.True(s.RmsPx < 0.8, s.Words);
        Assert.All(s.Inliers, Assert.True);
        Assert.True(Math.Abs(s.Intrinsics.Fx / truth.Fx - 1) < 0.01, s.Words);
        Assert.True(Math.Abs(s.Intrinsics.Cy - truth.Cy) < 15, $"cy {s.Intrinsics.Cy}");
        Assert.True(Synthetic.AngleDegrees(pose, s.Pose) < 0.3);
        Assert.True(Vec3d.Distance(pose.Position, s.Pose.Position) < 0.05);
        Assert.Contains("px RMS over 20 pins", s.Words);
    }

    [Fact]
    public void LockedOpticsComeBackExactlyAndOnlyThePoseMoves()
    {
        var truth = Synthetic.Projector(shiftY: 0.5);
        var lens = new LensDistortion(-0.04, 0.01, 0, 0, 0);
        var pose = Pose.LookAt(new Vec3d(-0.6, 1.1, 0.4), new Vec3d(0.5, 1.8, 6), Vec3d.UnitY);
        var rng = new Pcg32(3);
        var pins = Synthetic.Pins(truth, lens, pose, 12, ref rng, 0.5);
        var options = new PoseSolveOptions { Locks = SolveLocks.Focal | SolveLocks.Shift | SolveLocks.Distortion };
        var s = PoseSolver.Solve(pins, truth, lens, options);
        Assert.Equal(truth, s.Intrinsics);
        Assert.Equal(lens, s.Lens);
        Assert.True(s.RmsPx < 1, s.Words);
        Assert.True(Synthetic.AngleDegrees(pose, s.Pose) < 0.1);
        Assert.True(Vec3d.Distance(pose.Position, s.Pose.Position) < 0.03);

        // Focal free, shift locked: the principal point stays to the bit while the focal length is found.
        var prior = truth with { Fx = truth.Fx * 0.88, Fy = truth.Fy * 0.88 };
        var f = PoseSolver.Solve(pins, prior, lens, new PoseSolveOptions { Locks = SolveLocks.Shift | SolveLocks.Distortion });
        Assert.Equal(prior.Cx, f.Intrinsics.Cx);
        Assert.Equal(prior.Cy, f.Intrinsics.Cy);
        Assert.Equal(lens, f.Lens);
        Assert.True(Math.Abs(f.Intrinsics.Fx / truth.Fx - 1) < 0.01, f.Words);
        Assert.Equal(f.Intrinsics.Fx * truth.Fy / truth.Fx, f.Intrinsics.Fy, 1e-9);
    }

    [Fact]
    public void PinsAllOnOneWallWithAnUnknownFocalLengthConverge()
    {
        var truth = Synthetic.Projector(shiftY: 0.5);
        // The projector looks at the wall 35° off square: one plane, but not head-on.
        var pose = Pose.LookAt(new Vec3d(0, 1.0, 0), new Vec3d(2.8, 2.0, 4), Vec3d.UnitY);
        var rng = new Pcg32(4);
        var wallNormal = new Vec3d(0, 0, 1);
        var pins = Synthetic.PlanePins(truth, pose, new Vec3d(0, 0, 5), wallNormal, 12, ref rng, 0.3);
        var prior = Intrinsics.FromThrowRatio(1920, 1080, 1.3, 0, 0.5);
        var s = PoseSolver.Solve(pins, prior, LensDistortion.None, new PoseSolveOptions { Locks = SolveLocks.Shift });
        Assert.True(s.RmsPx < 1, s.Words);
        Assert.True(Math.Abs(s.Intrinsics.Fx / truth.Fx - 1) < 0.03, s.Words);
        Assert.True(Vec3d.Distance(pose.Position, s.Pose.Position) < 0.1, $"{s.Pose.Position}");
        Assert.True(Synthetic.AngleDegrees(pose, s.Pose) < 0.5);
        Assert.Equal(prior.Cy, s.Intrinsics.Cy);

        // Shift unlocked on one plane: the solver holds it and says so.
        var held = PoseSolver.Solve(pins, prior, LensDistortion.None);
        Assert.Equal(prior.Cy, held.Intrinsics.Cy);
        Assert.Contains("lens shift held", held.Words);
    }

    [Fact]
    public void ASingleBadPinAmongTwelveIsFlaggedAndNamed()
    {
        var truth = Synthetic.Projector(shiftY: -0.5);
        var pose = Synthetic.ProjectorPose;
        var rng = new Pcg32(5);
        var pairs = Synthetic.Pins(truth, LensDistortion.None, pose, 12, ref rng, 0.5);
        var pins = pairs.Select((c, i) => new CalibrationPin("pin" + i, c.World, i == 6 ? c.Pixel + new Vec2d(9, -6) : c.Pixel, true))
            .ToImmutableArray();
        var projector = new Projector("pj", "Left", Intrinsics.FromThrowRatio(1920, 1080, 1.4, 0, -0.5), LensDistortion.None,
            Pose.Identity, 0, "#ffffff", false, pins, SolveLocks.Shift, 0);
        var s = PoseSolver.Solve(projector);
        Assert.False(s.Inliers[6]);
        Assert.Equal(11, s.Inliers.Count(b => b));
        Assert.True(s.ResidualsPx[6] > 6, $"{s.ResidualsPx[6]}");
        Assert.True(s.RmsPx < 1, s.Words);
        Assert.Contains("pin 7 is", s.Words);
        Assert.Contains("check it", s.Words);
        Assert.True(Synthetic.AngleDegrees(pose, s.Pose) < 0.2);
    }

    [Fact]
    public void FourPinsOffOnePlaneSolveACalibratedProjectorThroughThreePointPoses()
    {
        var k = Synthetic.Projector(-0.5);
        var rng = new Pcg32(12);
        var pins = Synthetic.Pins(k, LensDistortion.None, Synthetic.ProjectorPose, 4, ref rng, 0.3, 2.5, 7);
        var s = PoseSolver.Solve(pins, k, LensDistortion.None, new PoseSolveOptions { Locks = SolveLocks.Focal | SolveLocks.Shift | SolveLocks.Distortion });
        Assert.True(s.RmsPx < 0.5, s.Words);
        Assert.True(Synthetic.AngleDegrees(Synthetic.ProjectorPose, s.Pose) < 0.2, s.Words);
        Assert.True(Vec3d.Distance(Synthetic.ProjectorPose.Position, s.Pose.Position) < 0.02, $"{s.Pose.Position}");
    }

    [Fact]
    public void ThreePointPosesIncludeTheTruthAndTheQuarticSolverFindsEveryRealRoot()
    {
        var pose = Synthetic.ProjectorPose;
        var rwc = pose.CameraToWorld.Transposed();
        Vec3d[] world = [new(0.3, 0.2, 4.5), new(-0.6, 0.9, 5.2), new(0.8, 0.0, 3.9)];
        var bearings = world.Select(x => pose.WorldToCamera(x).Normalized()).ToArray();
        var solutions = P3P.Solve(world[0], world[1], world[2], bearings[0], bearings[1], bearings[2]);
        Assert.InRange(solutions.Count, 1, 4);
        Assert.Contains(solutions, s => Synthetic.AngleDegrees(rwc, s.R) < 1e-6 && Vec3d.Distance(s.R.Transposed() * s.T * -1, pose.Position) < 1e-8);

        // (x − 1)(x + 2)(x − 3)(x + 0.5) and a quartic with two real roots and a complex pair.
        var roots = Polynomial.RealRoots(1, -1.5, -6, 3.5, 3).OrderBy(x => x).ToArray();
        Assert.Equal(4, roots.Length);
        Assert.Equal(-2, roots[0], 1e-10);
        Assert.Equal(-0.5, roots[1], 1e-10);
        Assert.Equal(1, roots[2], 1e-10);
        Assert.Equal(3, roots[3], 1e-10);
        // (x² + 1)(x − 2)(x − 4) = x⁴ − 6x³ + 9x² − 6x + 8.
        var two = Polynomial.RealRoots(1, -6, 9, -6, 8).OrderBy(x => x).ToArray();
        Assert.Equal(2, two.Length);
        Assert.Equal(2, two[0], 1e-10);
        Assert.Equal(4, two[1], 1e-10);
        Assert.Equal([-1.5], Polynomial.RealRoots(0, 0, 0, 2, 3));
    }

    [Fact]
    public void TooFewPinsAreRefusedWithTheReason()
    {
        var rng = new Pcg32(6);
        var pins = Synthetic.Pins(Synthetic.Projector(), LensDistortion.None, Synthetic.ProjectorPose, 3, ref rng, 0);
        var s = PoseSolver.Solve(pins, Synthetic.Projector(), LensDistortion.None);
        Assert.False(s.IsGood);
        Assert.StartsWith("not solved: need at least 6 pins", s.Words);
        Assert.Equal(3, s.ResidualsPx.Count);
    }

    [Fact]
    public void DensePairsFitRadialDistortionThroughOutliers()
    {
        var truth = Synthetic.Projector(shiftY: -0.5);
        var lens = new LensDistortion(-0.08, 0.03, 0, 0, 0);
        var pose = Synthetic.ProjectorPose;
        var rng = new Pcg32(8);
        var pairs = Synthetic.Pins(truth, lens, pose, 3000, ref rng, 0.3, 2, 8);
        var bad = 0;
        for (var i = 0; i < pairs.Count; i += 20)
        {
            pairs[i] = pairs[i] with { Pixel = new Vec2d(rng.Range(0, 1920), rng.Range(0, 1080)) };
            bad++;
        }
        var prior = Intrinsics.FromThrowRatio(1920, 1080, 1.4, 0, -0.5);
        var s = PoseSolver.Solve(pairs, prior, LensDistortion.None,
            new PoseSolveOptions { Locks = SolveLocks.Shift, Noun = "point", OutlierThresholdPx = 3 });
        Assert.True(s.RmsPx < 0.5, s.Words);
        Assert.True(Math.Abs(s.Lens.K1 - lens.K1) < 0.01, $"k1 {s.Lens.K1}");
        Assert.True(Math.Abs(s.Intrinsics.Fx / truth.Fx - 1) < 0.005, s.Words);
        Assert.True(s.Inliers.Count(b => !b) >= bad - 3);
        Assert.Contains("points", s.Words);
    }

    [Fact]
    public void ReprojectionErrorIsInfiniteBehindTheDevice()
    {
        var k = Synthetic.Projector();
        var behind = new Correspondence(Synthetic.ProjectorPose.Position - Synthetic.ProjectorPose.Forward, new Vec2d(10, 10));
        Assert.True(double.IsPositiveInfinity(PoseSolver.ReprojectionError(behind, k, LensDistortion.None, Synthetic.ProjectorPose)));
        Assert.NotNull(CameraModel.Project(Synthetic.ProjectorPose.Position + Synthetic.ProjectorPose.Forward, k, LensDistortion.None, Synthetic.ProjectorPose));
    }
}
