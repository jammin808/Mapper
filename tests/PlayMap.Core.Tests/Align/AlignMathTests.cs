using PlayMap.Core.Align;
using PlayMap.Core.Calibration;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Align;

/// <summary>The alignment's parts against known answers: the float map file, the warp fit (a homography with a barrel lens, nodes the
/// pairs never reach marked invalid), the rectification from two projectors' priors, and the largest rectangle inside a coverage.</summary>
public sealed class AlignMathTests
{
    [Fact]
    public void AFloatMapRoundTripsAndIsNamedByItsContent()
    {
        var map = new FloatMap(3, 2, 3, [.. Enumerable.Range(0, 18).Select(i => i * 0.25f - 1)]);
        var bytes = Pfm.Write(map);
        var back = Pfm.Read(bytes);
        Assert.Equal((3, 2, 3), (back.Width, back.Height, back.Channels));
        Assert.Equal(map.Values, back.Values);
        Assert.Equal(map.At(2, 1, 2), back.At(2, 1, 2));
        Assert.Equal(16, Pfm.Hash(bytes).Length);
        Assert.Equal(Pfm.Hash(bytes), Pfm.Hash(Pfm.Write(back)));
        Assert.StartsWith("PF\n3 2\n-1.0\n", System.Text.Encoding.ASCII.GetString(bytes, 0, 12), StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => Pfm.Read("P6\n1 1\n255\n"u8));
    }

    [Fact]
    public void TheWarpGridFitsAHomographyThroughABarrelLensAndMarksWhatItNeverSaw()
    {
        var h = Homography.UnitSquareToQuad(new Vec2d(0.05, 0.1), new Vec2d(0.4, 0.02), new Vec2d(0.42, 0.95), new Vec2d(0.02, 0.9));
        var rng = new Pcg32(7);
        const int w = 320, ht = 180;
        var q = new List<Vec2d>();
        var uv = new List<Vec2d>();
        for (var i = 0; i < 40_000; i++)
        {
            var p = new Vec2d(rng.Range(0.0, w), rng.Range(0.0, ht));
            // A barrel of 1.5 px at the raster's corners.
            var d = new Vec2d((p.X - w / 2.0) / (w / 2.0), (p.Y - ht / 2.0) / (ht / 2.0));
            var r2 = d.LengthSquared / 2;
            var bent = p + new Vec2d(d.X * w / 2.0, d.Y * ht / 2.0) * (-1.5 / (w / 2.0) * r2);
            q.Add(p);
            uv.Add(h.TransformPoint(new Vec2d(bent.X / w, bent.Y / ht)));
        }
        var fit = MeasuredWarpFit.Fit(w, ht, 33, 19, q, uv);
        Assert.True(fit.HeldOutRmsPx <= 0.15, fit.Words);
        Assert.Equal(33 * 19, fit.Grid.ValidNodes);

        var left = Enumerable.Range(0, q.Count).Where(i => q[i].X < w / 2.0).ToList();
        var half = MeasuredWarpFit.Fit(w, ht, 33, 19, [.. left.Select(i => q[i])], [.. left.Select(i => uv[i])]);
        Assert.True(half.Grid.Valid[0]);
        Assert.False(half.Grid.Valid[32]);
        Assert.Null(half.Grid.Eval(new Vec2d(w - 1, ht / 2.0)));
        Assert.NotNull(half.Grid.Eval(new Vec2d(10, ht / 2.0)));
        var back = WarpGrid.FromMap(fit.Grid.ToMap(), w, ht);
        Assert.Equal(fit.Grid.U, back.U);
        Assert.Equal(fit.Grid.Valid, back.Valid);
        var target = new Vec2d(0.25, 0.5);
        var at = back.Invert(target, new Vec2d(w / 2.0, ht / 2.0));
        Assert.NotNull(at);
        Assert.True((back.Eval(at!.Value)!.Value - target).Length < 1e-6);
    }

    [Fact]
    public void TwoProjectorsPriorsRectifyASlantedPlane()
    {
        // A camera slanted 25° to the plane z = 0; two projectors off its axis. The plane's own metres are (x, −y).
        var camK = Intrinsics.FromFieldOfView(800, 450, 62);
        var cam = Pose.LookAt(new Vec3d(1.6, -0.3, 3.4), Vec3d.Zero, Vec3d.UnitY);
        var lens = CameraLens.For(800, 450);
        var priors = new List<ProjectorPrior>();
        var hs = new List<Mat3d>();
        foreach (var (eye, target) in new[] { (new Vec3d(-0.8, 0.1, 2.0), new Vec3d(-0.6, 0, 0)), (new Vec3d(0.9, -0.1, 2.1), new Vec3d(0.6, 0, 0)) })
        {
            var k = Intrinsics.FromThrowRatio(320, 180, 1.5);
            var pose = Pose.LookAt(eye, target, Vec3d.UnitY);
            var from = new List<Vec2d>();
            var to = new List<Vec2d>();
            for (var y = -0.5; y <= 0.5; y += 0.1)
            {
                for (var x = -1.4; x <= 1.4; x += 0.1)
                {
                    var world = new Vec3d(x, y, 0);
                    if (CameraModel.Project(world, camK, LensDistortion.None, cam) is not { } c || CameraModel.Project(world, k, LensDistortion.None, pose) is not { } p)
                        continue;
                    from.Add(lens.Undistort(c));
                    to.Add(p);
                }
            }
            hs.Add(Homography.Fit(from, to)!.Value);
            priors.Add(new ProjectorPrior(320, 180, k.Cx, k.Cy, k.Fx * 1.02, 0.05));
        }
        var coverage = new[] { new Vec2d(0, 0), new Vec2d(0.3, 0.1), new Vec2d(-0.3, -0.1) };
        var rect = PlaneRectify.FromProjectors(hs, priors, coverage);
        Assert.Equal("projectors", rect.Source);
        Vec2d Plane(Vec3d world) => rect.Apply(lens.Undistort(CameraModel.Project(world, camK, LensDistortion.None, cam)!.Value));
        var a = Plane(new Vec3d(-1, 0.4, 0));
        var b = Plane(new Vec3d(1, 0.4, 0));
        var c2 = Plane(new Vec3d(1, -0.4, 0));
        var d = Plane(new Vec3d(-1, -0.4, 0));
        var angle = Math.Acos(Vec2d.Dot(b - a, d - a) / ((b - a).Length * (d - a).Length)) * 180 / Math.PI;
        Assert.True(Math.Abs(angle - 90) < 0.2, $"{angle:0.000}°");
        var aspect = (b - a).Length / (d - a).Length;
        Assert.True(Math.Abs(aspect / 2.5 - 1) < 0.005, $"{aspect:0.0000}");
        Assert.True(b.X > a.X && d.Y > a.Y, "right is +x, down is +y");
    }

    [Fact]
    public void TheLargestRectangleOfTheAspectFitsInsideTheCoverage()
    {
        // A 4 × 1 band with a 1 × 1 tab below it: a 2:1 canvas fits the band, full height.
        static bool Covered(Vec2d p) => (p.X is >= 0 and <= 4 && p.Y is >= 0 and <= 1) || (p.X is >= 1 and <= 2 && p.Y is > 1 and <= 2);
        var t = CanvasTarget.Inside(Covered, new Vec2d(0, 0), new Vec2d(4, 2), 2, 0)!;
        Assert.InRange(t.Height, 0.98, 1.0);
        Assert.InRange(t.Width, 1.96, 2.0);
        Assert.Equal(1, CanvasTarget.CoverageOf(t, Covered), 6);
        var margin = CanvasTarget.Inside(Covered, new Vec2d(0, 0), new Vec2d(4, 2), 2, 10)!;
        Assert.InRange(margin.Height, 0.78, 0.8);
    }
}
