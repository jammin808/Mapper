using System.Diagnostics;
using PlayMap.Core.Calibration;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Calibration;

/// <summary>Board-free calibration of a camera and projectors from dense pairs on a synthetic room (floor, back
/// wall, side wall, a box): the epipolar start, the Schur-complement bundle adjustment's accuracy and speed,
/// the scale from one known distance, multi-projector scale linking, and triangulation from the result.</summary>
public class SelfCalibrationTests
{
    private static readonly MeshRaycaster Room = new(Synthetic.Room());

    /// <summary>The projector's pose in the camera's frame — what self-calibration reports.</summary>
    private static Pose InCameraFrame(Pose camera, Pose device)
    {
        var rc = camera.CameraToWorld.Transposed();
        return new Pose(rc * (device.Position - camera.Position), (rc * device.CameraToWorld).ToRotationVector());
    }

    [Fact]
    public void TheEightPointFundamentalMatrixSeesThroughOutliersAndDecomposesToTheTruePose()
    {
        var camK = Synthetic.Camera();
        var projK = Synthetic.Projector();
        var (cam, proj, _) = Synthetic.RoomPairs(Room, camK, Synthetic.CameraPose, projK, Synthetic.ProjectorPose, 1500, 3, 0.2, 0.1);
        var fit = Epipolar.FitRobust(cam, proj, 1.0, seed: 4);
        Assert.True(fit.Succeeded);
        Assert.True(fit.InlierCount > 1250 && fit.InlierCount < 1400, $"{fit.InlierCount}");
        var e = Epipolar.EssentialFromFundamental(fit.F, camK, projK);
        var na = new List<Vec2d>();
        var nb = new List<Vec2d>();
        for (var i = 0; i < cam.Count; i++)
        {
            if (!fit.Inliers[i])
                continue;
            na.Add(new Vec2d((cam[i].X - camK.Cx) / camK.Fx, (cam[i].Y - camK.Cy) / camK.Fy));
            nb.Add(new Vec2d((proj[i].X - projK.Cx) / projK.Fx, (proj[i].Y - projK.Cy) / projK.Fy));
        }
        var rel = Epipolar.SelectByCheirality(e, na, nb);
        Assert.NotNull(rel);
        var truth = InCameraFrame(Synthetic.CameraPose, Synthetic.ProjectorPose);
        var rTrue = truth.CameraToWorld.Transposed();
        var tTrue = (rTrue * truth.Position * -1).Normalized();
        Assert.True(Synthetic.AngleDegrees(rTrue, rel.Value.Rotation) < 0.2);
        Assert.True(Vec3d.Distance(tTrue, rel.Value.Translation) < 0.01);
    }

    [Fact]
    public void SelfCalibrationRecoversFocalAndRotationFromNoisyRoomPairsWithinASecond()
    {
        var camK = Synthetic.Camera(62);
        var projK = Synthetic.Projector(-0.5);
        var (cam, proj, world) = Synthetic.RoomPairs(Room, camK, Synthetic.CameraPose, projK, Synthetic.ProjectorPose, 5000, 1, 0.3, 0.01);
        var camPrior = Intrinsics.FromFieldOfView(1280, 720, 68);
        var projPrior = Intrinsics.FromThrowRatio(1920, 1080, 1.38, 0, -0.5);
        // Warm the code paths so the timing measures the solve, not the first-call compilation.
        SelfCalibration.Solve(camPrior, LensDistortion.None, [new ProjectorObservations(projPrior, LensDistortion.None, cam.Take(400).ToList(), proj.Take(400).ToList())]);
        var clock = Stopwatch.StartNew();
        var r = SelfCalibration.Solve(camPrior, LensDistortion.None, [new ProjectorObservations(projPrior, LensDistortion.None, cam, proj)]);
        clock.Stop();
        var p = r.Projectors[0];
        var truth = InCameraFrame(Synthetic.CameraPose, Synthetic.ProjectorPose);
        var info = $"{r.Words} | {clock.ElapsedMilliseconds} ms, {r.Iterations} iterations, f {p.Intrinsics.Fx:0.0} vs {projK.Fx:0.0}, " +
            $"cam f {r.Camera.Fx:0.0} vs {camK.Fx:0.0}, rot {Synthetic.AngleDegrees(truth, p.Pose):0.000}°";
        Assert.True(p.Solved, info);
        Assert.True(Math.Abs(p.Intrinsics.Fx / projK.Fx - 1) < 0.02, info);
        Assert.True(Synthetic.AngleDegrees(truth, p.Pose) < 0.3, info);
        Assert.True(Vec3d.Distance(truth.Position.Normalized(), p.Pose.Position.Normalized()) < 0.01, info);
        Assert.True(p.RmsPx < 0.45, info);
        Assert.True(p.InlierCount > 4800, info);
        Assert.True(clock.ElapsedMilliseconds < 1000, info);
        Assert.False(r.Metric);

        // One known distance makes it metric: the points then land within 0.5% of the truth.
        var rc = Synthetic.CameraPose.CameraToWorld.Transposed();
        var trueCam = world.Select(x => rc * (x - Synthetic.CameraPose.Position)).ToArray();
        var (a, b) = FarApart(p.Inliers, trueCam);
        var scaled = r.SetScale(p.Points[a], p.Points[b], Vec3d.Distance(trueCam[a], trueCam[b]));
        Assert.True(scaled.Metric);
        var sp = scaled.Projectors[0];
        var errors = Enumerable.Range(0, cam.Count).Where(i => sp.Inliers[i])
            .Select(i => Vec3d.Distance(sp.Points[i], trueCam[i]) / trueCam[i].Length).ToArray();
        Array.Sort(errors);
        var median = errors[errors.Length / 2];
        var p95 = errors[(int)(errors.Length * 0.95)];
        Assert.True(median < 0.005 && p95 < 0.01, $"median {median:P2}, 95% {p95:P2}; {info}");
        Assert.True(Vec3d.Distance(sp.Pose.Position, truth.Position) < 0.01 * truth.Position.Length, $"{sp.Pose.Position} vs {truth.Position}");

        // Triangulating with the calibrated devices gives the same points.
        var tri = Triangulation.Batch(scaled.CameraView, sp.View, cam, proj, refine: true);
        var triErr = Enumerable.Range(0, cam.Count).Where(i => sp.Inliers[i] && tri[i].Valid)
            .Select(i => Vec3d.Distance(tri[i].Point, trueCam[i]) / trueCam[i].Length).OrderBy(x => x).ToArray();
        Assert.True(triErr[triErr.Length / 2] < 0.005, $"triangulated median {triErr[triErr.Length / 2]:P2}");
    }

    private static (int A, int B) FarApart(bool[] inliers, Vec3d[] pts)
    {
        var idx = Enumerable.Range(0, pts.Length).Where(i => inliers[i]).ToArray();
        var a = idx.MinBy(i => pts[i].X + pts[i].Z);
        var b = idx.MaxBy(i => pts[i].X - pts[i].Y);
        return (a, b);
    }

    [Fact]
    public void TwoProjectorsShareTheCameraFocalAndTheirScalesAreLinked()
    {
        var camK = Synthetic.Camera(62);
        var projK = Synthetic.Projector(-0.5);
        var pose2 = Pose.LookAt(new Vec3d(-0.9, 2.4, 0.6), new Vec3d(0.6, 1.0, 6.0), Vec3d.UnitY);
        var projK2 = Intrinsics.FromThrowRatio(1280, 800, 1.2, 0, -0.4);
        var (cam1, proj1, _) = Synthetic.RoomPairs(Room, camK, Synthetic.CameraPose, projK, Synthetic.ProjectorPose, 3000, 5, 0.3);
        // The second projector is decoded at the same camera pixels where it lights the scene.
        var cam2 = new List<Vec2d>();
        var proj2 = new List<Vec2d>();
        var rng = new Pcg32(9);
        foreach (var c in cam1.Concat(Synthetic.RoomPairs(Room, camK, Synthetic.CameraPose, projK2, pose2, 2000, 6, 0).Camera))
        {
            var ray = CameraModel.PixelRay(c, camK, LensDistortion.None, Synthetic.CameraPose);
            if (Room.Cast(ray) is not { } hit || CameraModel.Project(hit.Point, projK2, LensDistortion.None, pose2) is not { } q
                || !CameraModel.InRaster(q, projK2))
                continue;
            var to = hit.Point - pose2.Position;
            if (Room.Cast(new Ray(pose2.Position, to.Normalized()), to.Length - 1e-4) is not null)
                continue;
            cam2.Add(c);
            proj2.Add(q + Synthetic.Noise(ref rng, 0.3));
        }
        var camPrior = Intrinsics.FromFieldOfView(1280, 720, 66);
        var r = SelfCalibration.Solve(camPrior, LensDistortion.None,
        [
            new ProjectorObservations(Intrinsics.FromThrowRatio(1920, 1080, 1.4, 0, -0.5), LensDistortion.None, cam1, proj1, "Left"),
            new ProjectorObservations(Intrinsics.FromThrowRatio(1280, 800, 1.1, 0, -0.4), LensDistortion.None, cam2, proj2, "Right"),
        ]);
        Assert.All(r.Projectors, p => Assert.True(p.Solved, p.Words));
        Assert.True(r.Projectors[1].ScaleLinked, r.Words);
        Assert.True(Math.Abs(r.Camera.Fx / camK.Fx - 1) < 0.02, r.Words);
        Assert.True(Math.Abs(r.Projectors[1].Intrinsics.Fx / projK2.Fx - 1) < 0.03, r.Words);
        var t1 = InCameraFrame(Synthetic.CameraPose, Synthetic.ProjectorPose);
        var t2 = InCameraFrame(Synthetic.CameraPose, pose2);
        // Linked: the baselines keep their true ratio.
        var ratio = r.Projectors[1].Pose.Position.Length / r.Projectors[0].Pose.Position.Length;
        Assert.Equal(t2.Position.Length / t1.Position.Length, ratio, 0.02 * ratio);
        Assert.True(Synthetic.AngleDegrees(t2, r.Projectors[1].Pose) < 0.5, r.Words);
        Assert.Contains("projector Right:", r.Words);
    }

    [Fact]
    public void AProjectorWithTooFewPairsIsReportedNotSolved()
    {
        var r = SelfCalibration.Solve(Synthetic.Camera(), LensDistortion.None,
            [new ProjectorObservations(Synthetic.Projector(), LensDistortion.None, [new Vec2d(1, 1)], [new Vec2d(2, 2)])]);
        Assert.False(r.Projectors[0].Solved);
        Assert.Contains("not solved: too few pairs", r.Projectors[0].Words);
        Assert.Throws<ArgumentOutOfRangeException>(() => r.Scaled(-1, true));
    }

    [Fact]
    public void TheRayMidpointAndTheOrganisedMapTriangulateKnownGeometry()
    {
        var camera = new DeviceView(Synthetic.Camera(), LensDistortion.None, Synthetic.CameraPose);
        var projector = new DeviceView(Synthetic.Projector(), new LensDistortion(-0.05, 0.01, 0, 0, 0), Synthetic.ProjectorPose);
        var target = new Vec3d(0.1, 0.3, 4.6);
        var pc = camera.Project(target)!.Value;
        var pp = projector.Project(target)!.Value;
        var exact = Triangulation.Triangulate(camera, pc, projector, pp);
        Assert.True(exact.Valid);
        Assert.True(Vec3d.Distance(exact.Point, target) < 1e-9);
        Assert.True(exact.ResidualPx < 1e-6 && exact.RayGap < 1e-9);

        // A projector pixel nudged by a pixel: the midpoint has a gap, the polish lowers the reprojection.
        var nudged = pp + new Vec2d(1, 0.5);
        var mid = Triangulation.Triangulate(camera, pc, projector, nudged, refine: false);
        var polished = Triangulation.Triangulate(camera, pc, projector, nudged, refine: true);
        Assert.True(mid.RayGap > 0);
        Assert.True(polished.ResidualPx < mid.ResidualPx, $"{polished.ResidualPx} vs {mid.ResidualPx}");
        Assert.True(polished.ResidualPx < 0.6);

        // Parallel rays meet nowhere.
        Assert.Null(Triangulation.Midpoint(new Ray(Vec3d.Zero, Vec3d.UnitZ), new Ray(Vec3d.UnitX, Vec3d.UnitZ)));

        // An organised map: a 64×36 camera grid over the room, every third pixel decoded.
        const int w = 64, h = 36;
        var small = camera with { Intrinsics = camera.Intrinsics.Scaled(w, h) };
        var px = new float[w * h];
        var py = new float[w * h];
        var valid = new bool[w * h];
        var truth = new Vec3d?[w * h];
        for (var i = 0; i < w * h; i += 3)
        {
            var c = new Vec2d(i % w + 0.5, i / w + 0.5);
            if (Room.Cast(small.Ray(c)) is not { } hit || projector.Project(hit.Point) is not { } q || !CameraModel.InRaster(q, projector.Intrinsics))
                continue;
            px[i] = (float)q.X;
            py[i] = (float)q.Y;
            valid[i] = true;
            truth[i] = hit.Point;
        }
        var map = new PixelCorrespondenceMap(w, h, px, py, valid);
        var organised = Triangulation.Organised(small, projector, map, maxResidualPx: 1);
        Assert.True(organised.Cloud.IsOrganised);
        Assert.Equal(valid.Count(v => v), organised.ValidCount);
        for (var i = 0; i < w * h; i++)
        {
            if (truth[i] is { } t)
                Assert.True(Vec3d.Distance(organised.Cloud.Points[i], t) < 2e-3, $"{i}: {organised.Cloud.Points[i]} vs {t}");
            else
                Assert.False(PointCloud.IsValid(organised.Cloud.Points[i]));
        }
        Assert.Equal(organised.ValidCount, map.Pairs().Camera.Count);
    }
}
