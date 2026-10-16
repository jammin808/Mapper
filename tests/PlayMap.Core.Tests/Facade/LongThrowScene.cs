using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Facade;

/// <summary>The bench's long-throw case: a 4K projector (3840 × 2160) at a given throw and throw ratio, head-on (or turned) to a
/// facade at z = 0 with relief toward the projector, and pins sampled over its picture with Gaussian pixel noise (Pcg32, seeded).</summary>
internal static class LongThrowScene
{
    public const int Width = 3840, Height = 2160;

    public static (Intrinsics K, Pose Pose) Projector(double distanceM, double throwRatio, double obliqueDeg = 0)
    {
        var k = Intrinsics.FromThrowRatio(Width, Height, throwRatio);
        var a = obliqueDeg * Math.PI / 180;
        var target = new Vec3d(0, 10, 0);
        var eye = target + new Vec3d(Math.Sin(a) * distanceM, 0, Math.Cos(a) * distanceM);
        return (k, Pose.LookAt(eye, target, Vec3d.UnitY));
    }

    /// <summary>Pins over the picture: each a pixel picked uniformly in the raster's middle 90 %, its ray met at a depth of the
    /// facade between z = 0 and z = relief (m, toward the projector), the pixel then noised by σ px.</summary>
    public static List<Correspondence> Pins(Intrinsics k, Pose pose, int count, double reliefM, double sigmaPx, ulong seed)
    {
        var rng = new Pcg32(seed, 0x51);
        var pins = new List<Correspondence>(count);
        while (pins.Count < count)
        {
            var px = new Vec2d(rng.Range(0.05, 0.95) * k.Width, rng.Range(0.05, 0.95) * k.Height);
            var z = reliefM > 0 ? rng.Range(0.0, reliefM) : 0;
            var dir = pose.CameraToWorld * CameraModel.PixelRayCamera(px, k, LensDistortion.None);
            if (Math.Abs(dir.Z) < 1e-9)
                continue;
            var t = (z - pose.Position.Z) / dir.Z;
            var world = pose.Position + dir * t;
            var seen = CameraModel.Project(world, k, LensDistortion.None, pose)!.Value;
            pins.Add(new Correspondence(world, seen + new Vec2d(rng.NextGaussian() * sigmaPx, rng.NextGaussian() * sigmaPx)));
        }
        return pins;
    }

    /// <summary>The bench's layout: pins on a cols × rows grid over the raster's middle 80 %, their depths spread evenly from the
    /// facade (z = 0) to the relief's front (z = relief) and scattered over the grid (the k-th pin takes step 5k mod n), the pixels
    /// noised by σ px (only the noise is seeded).</summary>
    public static List<Correspondence> GridPins(Intrinsics k, Pose pose, int cols, int rows, double reliefM, double sigmaPx, ulong seed)
    {
        var rng = new Pcg32(seed, 0x52);
        var pins = new List<Correspondence>(cols * rows);
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                var px = new Vec2d((0.1 + 0.8 * c / Math.Max(1, cols - 1)) * k.Width, (0.1 + 0.8 * r / Math.Max(1, rows - 1)) * k.Height);
                var n = cols * rows;
                var z = n > 1 ? reliefM * ((r * cols + c) * 5 % n) / (n - 1) : 0;
                var dir = pose.CameraToWorld * CameraModel.PixelRayCamera(px, k, LensDistortion.None);
                var world = pose.Position + dir * ((z - pose.Position.Z) / dir.Z);
                var seen = CameraModel.Project(world, k, LensDistortion.None, pose)!.Value;
                pins.Add(new Correspondence(world, seen + new Vec2d(rng.NextGaussian() * sigmaPx, rng.NextGaussian() * sigmaPx)));
            }
        return pins;
    }
}
