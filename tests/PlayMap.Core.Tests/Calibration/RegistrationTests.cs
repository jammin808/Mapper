using PlayMap.Core.Calibration;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Calibration;

/// <summary>Registration: Umeyama's closed form is exact (three pairs included), and point-to-plane ICP
/// pulls a room scan home from a 5° / 5 cm start and reports its overlap.</summary>
public class RegistrationTests
{
    private static readonly Similarity3 Offset = new(Mat3d.FromRotationVector(new Vec3d(0.3, -1.1, 0.4).Normalized() * 0.7),
        new Vec3d(1.5, -0.4, 2.2), 1.7);

    [Fact]
    public void UmeyamaRecoversASimilarityExactly()
    {
        var rng = new Pcg32(1);
        var src = Enumerable.Range(0, 10).Select(_ => new Vec3d(rng.Range(-2, 2), rng.Range(-2, 2), rng.Range(-2, 2))).ToList();
        var dst = src.Select(Offset.Apply).ToList();
        var s = Umeyama.Fit(src, dst);
        Assert.Equal(Offset.Scale, s.Scale, 1e-10);
        Assert.True(Synthetic.AngleDegrees(Offset.Rotation, s.Rotation) < 1e-8);
        Assert.True(Vec3d.Distance(Offset.Translation, s.Translation) < 1e-9);
        Assert.True(Umeyama.Rms(s, src, dst) < 1e-9);

        // Three picked points are enough (the coplanar case completes the rotation).
        var three = Umeyama.Fit(src.Take(3).ToList(), dst.Take(3).ToList());
        Assert.True(Vec3d.Distance(three.Apply(src[7]), dst[7]) < 1e-8);

        // Rigid only: scale stays one.
        var rigid = Umeyama.Fit(src, src.Select(p => Offset.Rotation * p + Offset.Translation).ToList(), allowScale: false);
        Assert.Equal(1, rigid.Scale);
        Assert.True(Synthetic.AngleDegrees(Offset.Rotation, rigid.Rotation) < 1e-8);
    }

    [Fact]
    public void ASimilarityInvertsComposesAndMovesPoses()
    {
        var p = new Vec3d(0.3, 2, -1);
        Assert.True(Vec3d.Distance(Offset.Inverse().Apply(Offset.Apply(p)), p) < 1e-12);
        var twice = Offset.Then(Offset);
        Assert.True(Vec3d.Distance(twice.Apply(p), Offset.Apply(Offset.Apply(p))) < 1e-12);
        var pose = Pose.LookAt(new Vec3d(1, 2, 3), new Vec3d(0, 1, 7), Vec3d.UnitY);
        var moved = Offset.Apply(pose);
        var world = new Vec3d(0.2, 1.4, 6);
        var k = Synthetic.Projector();
        Assert.True(Vec2d.Distance(
            CameraModel.Project(world, k, LensDistortion.None, pose)!.Value,
            CameraModel.Project(Offset.Apply(world), k, LensDistortion.None, moved)!.Value) < 1e-8);
        var t = Similarity3.FromTransform3(Offset.ToTransform3());
        Assert.True(Vec3d.Distance(t.Apply(p), Offset.Apply(p)) < 1e-9);
    }

    [Fact]
    public void PointToPlaneIcpConvergesFromFiveDegreesAndFiveCentimetres()
    {
        var (targetPts, targetNormals) = Synthetic.RoomSamples(20000, 1);
        var (srcPts, _) = Synthetic.RoomSamples(4000, 2);
        // The scan sits off by 5° about a skew axis and 5 cm; ICP starts from identity.
        var truth = new Similarity3(Mat3d.FromRotationVector(new Vec3d(1, 2, -1).Normalized() * (5 * Math.PI / 180)),
            new Vec3d(0.03, -0.03, 0.025), 1);
        var rng = new Pcg32(3);
        var scan = srcPts.Select(p => truth.Inverse().Apply(p) + new Vec3d(rng.NextGaussian(), rng.NextGaussian(), rng.NextGaussian()) * 0.002)
            .ToList();
        var target = new PointCloud(targetPts, targetNormals);
        var r = Icp.PointToPlane(scan, target, Similarity3.Identity);
        Assert.True(r.Converged, r.Words);
        Assert.True(Synthetic.AngleDegrees(truth.Rotation, r.Transform.Rotation) < 0.05, r.Words);
        Assert.True(Vec3d.Distance(truth.Translation, r.Transform.Translation) < 0.003, $"{r.Transform.Translation}");
        Assert.True(r.OverlapFraction > 0.85, r.Words);
        Assert.True(r.RmsDistance < 0.004, r.Words);

        // Without target normals they are estimated from the neighbourhoods and the answer holds.
        var bare = Icp.PointToPlane(scan, new PointCloud(targetPts), Similarity3.Identity);
        Assert.True(Synthetic.AngleDegrees(truth.Rotation, bare.Transform.Rotation) < 0.1, bare.Words);
    }
}
