using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Calibration;

/// <summary>Synthetic devices and scenes with known truth: pins made by casting a device's own pixel rays
/// to a depth or a plane (so they project back exactly), and a small room (floor, back wall, side wall, a box)
/// seen by a camera and a ceiling-hung projector with half-image lens shift.</summary>
internal static class Synthetic
{
    /// <summary>A 1920×1080 projector, throw 1.5, the image entirely below the axis (shift −0.5, hung
    /// inverted) or above it (+0.5, on a table).</summary>
    public static Intrinsics Projector(double shiftY = -0.5, double throwRatio = 1.5) =>
        Intrinsics.FromThrowRatio(1920, 1080, throwRatio, 0, shiftY);

    public static Pose ProjectorPose => Pose.LookAt(new Vec3d(0.2, 2.6, 0.3), new Vec3d(0.2, 1.2, 6.0), Vec3d.UnitY);

    public static Intrinsics Camera(double fovDegrees = 62) => Intrinsics.FromFieldOfView(1280, 720, fovDegrees);

    public static Pose CameraPose => Pose.LookAt(new Vec3d(1.3, 1.6, 0.2), new Vec3d(0.0, 0.5, 5.0), Vec3d.UnitY);

    /// <summary>A world point on the pixel's ray at the given depth along the device's axis.</summary>
    public static Vec3d PointAtDepth(Vec2d pixel, Intrinsics k, LensDistortion lens, Pose pose, double depth)
    {
        var ray = CameraModel.PixelRay(pixel, k, lens, pose);
        var t = depth / Vec3d.Dot(ray.Direction, pose.Forward);
        return ray.At(t);
    }

    /// <summary>Pins off one plane: pixels spread over the raster, depths between the bounds, pixel noise.</summary>
    public static List<Correspondence> Pins(Intrinsics k, LensDistortion lens, Pose pose, int count, ref Pcg32 rng,
        double noisePx, double minDepth = 3, double maxDepth = 7)
    {
        var pins = new List<Correspondence>(count);
        for (var i = 0; i < count; i++)
        {
            var pixel = new Vec2d(rng.Range(0.05, 0.95) * k.Width, rng.Range(0.05, 0.95) * k.Height);
            var world = PointAtDepth(pixel, k, lens, pose, rng.Range(minDepth, maxDepth));
            pins.Add(new Correspondence(world, pixel + Noise(ref rng, noisePx)));
        }
        return pins;
    }

    /// <summary>Pins on one plane (point and normal): each pixel's ray meets the plane.</summary>
    public static List<Correspondence> PlanePins(Intrinsics k, Pose pose, Vec3d planePoint, Vec3d planeNormal, int count,
        ref Pcg32 rng, double noisePx)
    {
        var pins = new List<Correspondence>(count);
        while (pins.Count < count)
        {
            var pixel = new Vec2d(rng.Range(0.05, 0.95) * k.Width, rng.Range(0.05, 0.95) * k.Height);
            var ray = CameraModel.PixelRay(pixel, k, LensDistortion.None, pose);
            var den = Vec3d.Dot(ray.Direction, planeNormal);
            if (Math.Abs(den) < 1e-6)
                continue;
            var t = Vec3d.Dot(planePoint - ray.Origin, planeNormal) / den;
            if (t <= 0)
                continue;
            pins.Add(new Correspondence(ray.At(t), pixel + Noise(ref rng, noisePx)));
        }
        return pins;
    }

    public static Vec2d Noise(ref Pcg32 rng, double sigma) =>
        sigma > 0 ? new Vec2d(rng.NextGaussian() * sigma, rng.NextGaussian() * sigma) : Vec2d.Zero;

    /// <summary>The angle between two device orientations, degrees.</summary>
    public static double AngleDegrees(Pose a, Pose b) =>
        (a.CameraToWorld.Transposed() * b.CameraToWorld).ToRotationVector().Length * 180 / Math.PI;

    public static double AngleDegrees(Mat3d a, Mat3d b) => (a.Transposed() * b).ToRotationVector().Length * 180 / Math.PI;

    // ---- the room ----

    /// <summary>Floor (y = 0), back wall (z = 6), side wall (x = −1.4), and a 0.9 × 0.6 × 0.7 m box on the floor.</summary>
    public static MeshData Room()
    {
        var pos = new List<float>();
        var idx = new List<uint>();
        void Quad(Vec3d a, Vec3d b, Vec3d c, Vec3d d)
        {
            var o = (uint)(pos.Count / 3);
            foreach (var v in new[] { a, b, c, d })
            {
                pos.Add((float)v.X);
                pos.Add((float)v.Y);
                pos.Add((float)v.Z);
            }
            idx.AddRange([o, o + 1, o + 2, o, o + 2, o + 3]);
        }
        Quad(new(-1.4, 0, 0), new(3, 0, 0), new(3, 0, 6), new(-1.4, 0, 6));
        Quad(new(-1.4, 0, 6), new(3, 0, 6), new(3, 3, 6), new(-1.4, 3, 6));
        Quad(new(-1.4, 0, 0), new(-1.4, 0, 6), new(-1.4, 3, 6), new(-1.4, 3, 0));
        // The box: x −0.5..0.4, y 0..0.6, z 4.3..5.0 (five faces).
        Vec3d b000 = new(-0.5, 0, 4.3), b100 = new(0.4, 0, 4.3), b110 = new(0.4, 0.6, 4.3), b010 = new(-0.5, 0.6, 4.3);
        Vec3d b001 = new(-0.5, 0, 5.0), b101 = new(0.4, 0, 5.0), b111 = new(0.4, 0.6, 5.0), b011 = new(-0.5, 0.6, 5.0);
        Quad(b000, b100, b110, b010);
        Quad(b101, b001, b011, b111);
        Quad(b001, b000, b010, b011);
        Quad(b100, b101, b111, b110);
        Quad(b010, b110, b111, b011);
        return new MeshData([.. pos], [], [], [.. idx]);
    }

    /// <summary>Dense camera↔projector pairs on the room: random camera pixels cast into the room, the hit
    /// projected into the projector, kept when the projector sees that point (not occluded, in raster).
    /// Projector pixels get Gaussian noise; a fraction become outliers (uniform in the raster).</summary>
    public static (List<Vec2d> Camera, List<Vec2d> Projector, List<Vec3d> World) RoomPairs(
        MeshRaycaster room, Intrinsics camK, Pose camPose, Intrinsics projK, Pose projPose, int count, ulong seed,
        double noisePx, double outlierFraction = 0)
    {
        var rng = new Pcg32(seed);
        var cam = new List<Vec2d>(count);
        var proj = new List<Vec2d>(count);
        var world = new List<Vec3d>(count);
        var guard = 0;
        while (cam.Count < count && guard++ < count * 200)
        {
            var c = new Vec2d(rng.Range(0, camK.Width), rng.Range(0, camK.Height));
            var ray = CameraModel.PixelRay(c, camK, LensDistortion.None, camPose);
            if (room.Cast(ray) is not { } hit)
                continue;
            if (CameraModel.Project(hit.Point, projK, LensDistortion.None, projPose) is not { } p || !CameraModel.InRaster(p, projK))
                continue;
            var toPoint = hit.Point - projPose.Position;
            var dist = toPoint.Length;
            if (room.Cast(new Ray(projPose.Position, toPoint / dist), dist - 1e-4) is not null)
                continue;
            if (outlierFraction > 0 && rng.NextDouble() < outlierFraction)
                p = new Vec2d(rng.Range(0, projK.Width), rng.Range(0, projK.Height));
            else
                p += Noise(ref rng, noisePx);
            cam.Add(c);
            proj.Add(p);
            world.Add(hit.Point);
        }
        return (cam, proj, world);
    }

    /// <summary>Points sampled on the room's surfaces with their exact normals.</summary>
    public static (Vec3d[] Points, Vec3d[] Normals) RoomSamples(int count, ulong seed)
    {
        var mesh = Room();
        var rng = new Pcg32(seed);
        var areas = new double[mesh.TriangleCount];
        double total = 0;
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.Triangle(t);
            total += Vec3d.Cross(mesh.Position(b) - mesh.Position(a), mesh.Position(c) - mesh.Position(a)).Length / 2;
            areas[t] = total;
        }
        var pts = new Vec3d[count];
        var nrm = new Vec3d[count];
        for (var i = 0; i < count; i++)
        {
            var pick = rng.NextDouble() * total;
            var t = Array.BinarySearch(areas, pick);
            if (t < 0)
                t = ~t;
            t = Math.Min(t, mesh.TriangleCount - 1);
            var (a, b, c) = mesh.Triangle(t);
            var u = rng.NextDouble();
            var v = rng.NextDouble();
            if (u + v > 1)
            {
                u = 1 - u;
                v = 1 - v;
            }
            var pa = mesh.Position(a);
            var pb = mesh.Position(b);
            var pc = mesh.Position(c);
            pts[i] = pa + (pb - pa) * u + (pc - pa) * v;
            nrm[i] = Vec3d.Cross(pb - pa, pc - pa).Normalized();
        }
        return (pts, nrm);
    }
}
