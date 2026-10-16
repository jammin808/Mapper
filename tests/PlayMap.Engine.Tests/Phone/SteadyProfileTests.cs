using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using PlayMap.Engine.Scanning;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>The steady profile on the simulator: a camera whose auto-exposure hunts (its gain a 1 %-per-frame random walk) and which
/// is shown no full white or black — its references made from the coarse pairs, its drift levelled — decodes at least 90 % of the
/// pixels a locked camera decodes, and the two projectors are found within twice ScanEngineTests' thresholds (5 mm, 0.05°, 0.2 % of
/// throw).</summary>
public sealed class SteadyProfileTests(ITestOutputHelper output)
{
    private const double PositionToleranceM = 2 * 0.005;

    private const double RotationToleranceDeg = 2 * 0.05;

    private const double ThrowTolerance = 2 * 0.002;

    private static List<Projector> Priors()
    {
        Projector Make(string id, string name, Vec3d eye) => new(id, name, Intrinsics.FromThrowRatio(256, 160, 1.4), LensDistortion.None,
            Pose.LookAt(eye, new Vec3d(0, 0.2, 0), Vec3d.UnitY), 0, "#FF8800", false, [], SolveLocks.None, 0);
        return [Make("pj00000001", "Left", new Vec3d(0.1, 1.5, 2.3)), Make("pj00000002", "Right", new Vec3d(-0.8, 1.6, 2.4))];
    }

    private sealed record Run(ScanSolution Solution, List<double> Decoded, double DriftPct);

    private static Run Scan(SimulatedRig rig, IReadOnlyList<Projector> projectors, bool steady)
    {
        var captures = new List<ProjectorCapture>();
        var decoded = new List<double>();
        SimTruth? first = null;
        var drift = 0.0;
        for (var j = 0; j < projectors.Count; j++)
        {
            var plan = ScanPlan.Create(256, 160);
            var truth = rig.Projector(j);
            var scene = SimScenes.FloorAndBox(256, 160, rig.CameraWidth, rig.CameraHeight) with { Projector = truth };
            var sim = new ScanSimulator(new SimGeometry(scene), plan, new SimOptions { Seed = (ulong)j + 1, GainWalk = steady ? 0.01 : 0 });
            first ??= sim.Truth;
            var frames = sim.RenderAll();
            if (steady)
            {
                // Never shown to a steady camera: whatever it would have recorded is replaced.
                frames[plan.WhiteIndex] = new byte[frames[plan.WhiteIndex].Length];
                frames[plan.BlackIndex] = new byte[frames[plan.BlackIndex].Length];
                drift = Math.Max(drift, GainNormaliser.PrepareSteady(plan, frames));
            }
            var map = new StructuredLightDecoder(plan).Decode(sim.CameraWidth, sim.CameraHeight, frames);
            decoded.Add(map.Stats.Valid);
            captures.Add(new ProjectorCapture(projectors[j], 256, 160, map, ScanQuality.Assess(map)));
        }
        var cam = rig.Camera;
        var view = new ScanCameraView("sim", "Simulated camera", cam.Intrinsics, cam.Lens, cam.Pose, true);
        var solution = ScanSolver.Solve(view, captures, new ScanSolveOptions { KnownDistance = rig.KnownDistance(first!) }, CancellationToken.None);
        return new Run(solution, decoded, drift);
    }

    [Fact]
    public void AHuntingCameraWithoutWhiteOrBlackDecodesNinetyPercentOfTheLockedPixelsAndHoldsThePose()
    {
        var projectors = Priors();
        var rig = new SimulatedRig(SimulatedRig.DefaultScene, 320, 240, [.. projectors.Select(p => p.Intrinsics)]);
        var locked = Scan(rig, projectors, steady: false);
        var steady = Scan(rig, projectors, steady: true);
        Assert.True(steady.DriftPct > 1, $"the walk drifted {steady.DriftPct} %");
        for (var j = 0; j < projectors.Count; j++)
        {
            var share = steady.Decoded[j] / locked.Decoded[j];
            output.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{projectors[j].Name}: steady decodes {steady.Decoded[j]} of the locked {locked.Decoded[j]} pixels ({share * 100:0.0} %), drift {steady.DriftPct:0.0} %"));
            Assert.True(share >= 0.9, $"{projectors[j].Name}: {share * 100:0.0} %");
        }
        foreach (var (run, name) in new[] { (locked, "locked"), (steady, "steady") })
        {
            for (var j = 0; j < projectors.Count; j++)
            {
                var solved = run.Solution.Projectors.Single(p => p.ProjectorId == projectors[j].Id);
                Assert.True(solved.Solved, solved.Words);
                var truth = rig.Projector(j);
                var (position, degrees) = ScanRunner.PoseError(solved.Pose, truth.Pose);
                var throwError = Math.Abs(solved.Intrinsics.ThrowRatio / truth.Intrinsics.ThrowRatio - 1);
                output.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{name} {projectors[j].Name}: {position * 1000:0.00} mm, {degrees:0.0000}°, throw {throwError * 100:0.000} %"));
                Assert.True(position < PositionToleranceM, $"{name} {projectors[j].Name}: {position * 1000:0.00} mm");
                Assert.True(degrees < RotationToleranceDeg, $"{name} {projectors[j].Name}: {degrees:0.0000}°");
                Assert.True(throwError < ThrowTolerance, $"{name} {projectors[j].Name}: throw {throwError * 100:0.000} %");
            }
        }
    }
}
