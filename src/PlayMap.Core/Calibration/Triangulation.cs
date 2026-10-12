using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Calibration;

/// <summary>A triangulated point: where it is, its reprojection RMS over both views (pixels), the gap between
/// the two rays at their closest approach (world units), and whether it lies in front of both devices.</summary>
public readonly record struct TriangulatedPoint(Vec3d Point, double ResidualPx, double RayGap, bool Valid)
{
    public static readonly TriangulatedPoint Invalid = new(PointCloud.Invalid, double.PositiveInfinity, double.PositiveInfinity, false);
}

/// <summary>A decoded camera-grid map: for each camera pixel (row-major, Width × Height) the projector
/// x and y it saw, and whether the decode is valid there. Camera pixel (i, j)'s centre is at (i + 0.5,
/// j + 0.5) — continuous coordinates with (0, 0) the raster's top-left corner, as everywhere else.</summary>
public sealed record PixelCorrespondenceMap(int Width, int Height, float[] ProjectorX, float[] ProjectorY, bool[] Valid)
{
    public int Length => Width * Height;

    public Vec2d CameraPixel(int index) => new(index % Width + 0.5, index / Width + 0.5);

    public Vec2d ProjectorPixel(int index) => new(ProjectorX[index], ProjectorY[index]);

    /// <summary>The valid pairs, every <paramref name="stride"/>-th pixel in both directions.</summary>
    public (List<Vec2d> Camera, List<Vec2d> Projector) Pairs(int stride = 1)
    {
        Validate();
        stride = Math.Max(1, stride);
        var cam = new List<Vec2d>();
        var proj = new List<Vec2d>();
        for (var y = 0; y < Height; y += stride)
        {
            for (var x = 0; x < Width; x += stride)
            {
                var i = y * Width + x;
                if (!Valid[i])
                    continue;
                cam.Add(CameraPixel(i));
                proj.Add(ProjectorPixel(i));
            }
        }
        return (cam, proj);
    }

    internal void Validate()
    {
        if (ProjectorX.Length != Length || ProjectorY.Length != Length || Valid.Length != Length)
            throw new ArgumentException("The map's arrays do not match its size.");
    }
}

/// <summary>An organised cloud from a camera-grid map: one point per camera pixel (invalid ones NaN), and
/// each point's reprojection residual in pixels (NaN where invalid).</summary>
public sealed record OrganisedPoints(PointCloud Cloud, float[] ResidualsPx, int ValidCount);

/// <summary>Triangulation of camera↔projector (or any two-view) correspondences: the ray–ray midpoint, an
/// optional Gauss–Newton polish to the point that minimises reprojection in both views, batches, and the
/// organised cloud of a decoded camera-grid map.</summary>
public static class Triangulation
{
    /// <summary>The midpoint of the closest approach of two rays and the gap between them; null when the
    /// rays are parallel or meet behind either origin.</summary>
    public static (Vec3d Point, double Gap)? Midpoint(Ray a, Ray b)
    {
        var w0 = a.Origin - b.Origin;
        var aa = Vec3d.Dot(a.Direction, a.Direction);
        var bb = Vec3d.Dot(a.Direction, b.Direction);
        var cc = Vec3d.Dot(b.Direction, b.Direction);
        var d = Vec3d.Dot(a.Direction, w0);
        var e = Vec3d.Dot(b.Direction, w0);
        var den = aa * cc - bb * bb;
        if (den < 1e-14 * aa * cc)
            return null;
        var s = (bb * e - cc * d) / den;
        var t = (aa * e - bb * d) / den;
        if (s <= 0 || t <= 0)
            return null;
        var pa = a.At(s);
        var pb = b.At(t);
        return ((pa + pb) * 0.5, Vec3d.Distance(pa, pb));
    }

    /// <summary>One correspondence between two devices.</summary>
    public static TriangulatedPoint Triangulate(DeviceView a, Vec2d pixelA, DeviceView b, Vec2d pixelB, bool refine = true)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Solve(Frame.Of(a), pixelA, Frame.Of(b), pixelB, refine);
    }

    /// <summary>Every pair of a camera↔projector set, in input order (in parallel for large sets).</summary>
    public static TriangulatedPoint[] Batch(DeviceView camera, DeviceView projector, IReadOnlyList<Vec2d> cameraPixels,
        IReadOnlyList<Vec2d> projectorPixels, bool refine = false)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(cameraPixels);
        ArgumentNullException.ThrowIfNull(projectorPixels);
        if (cameraPixels.Count != projectorPixels.Count)
            throw new ArgumentException("The pixel lists differ in length.", nameof(projectorPixels));
        var fa = Frame.Of(camera);
        var fb = Frame.Of(projector);
        var result = new TriangulatedPoint[cameraPixels.Count];
        Parallel.For(0, result.Length, new ParallelOptions { MaxDegreeOfParallelism = result.Length > 4096 ? -1 : 1 },
            i => result[i] = Solve(fa, cameraPixels[i], fb, projectorPixels[i], refine));
        return result;
    }

    /// <summary>The organised cloud of a decoded camera-grid map: valid pixels whose reprojection residual is
    /// within <paramref name="maxResidualPx"/> become points (their camera grid kept), the rest NaN.</summary>
    public static OrganisedPoints Organised(DeviceView camera, DeviceView projector, PixelCorrespondenceMap map,
        double maxResidualPx = 2, bool refine = false)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(map);
        map.Validate();
        var fa = Frame.Of(camera);
        var fb = Frame.Of(projector);
        var points = new Vec3d[map.Length];
        var residuals = new float[map.Length];
        var valid = 0;
        Parallel.For(0, map.Height, () => 0, (y, _, local) =>
        {
            for (var x = 0; x < map.Width; x++)
            {
                var i = y * map.Width + x;
                points[i] = PointCloud.Invalid;
                residuals[i] = float.NaN;
                if (!map.Valid[i] || !float.IsFinite(map.ProjectorX[i]) || !float.IsFinite(map.ProjectorY[i]))
                    continue;
                var tp = Solve(fa, map.CameraPixel(i), fb, map.ProjectorPixel(i), refine);
                if (!tp.Valid || tp.ResidualPx > maxResidualPx)
                    continue;
                points[i] = tp.Point;
                residuals[i] = (float)tp.ResidualPx;
                local++;
            }
            return local;
        }, local => Interlocked.Add(ref valid, local));
        return new OrganisedPoints(new PointCloud(points, gridWidth: map.Width, gridHeight: map.Height), residuals, valid);
    }

    /// <summary>A device unpacked for the inner loop: world→camera rotation, centre, intrinsics, lens.</summary>
    internal readonly record struct Frame(Mat3d Rwc, Vec3d Centre, Intrinsics K, LensDistortion Lens)
    {
        public static Frame Of(DeviceView d) => new(d.Pose.CameraToWorld.Transposed(), d.Pose.Position, d.Intrinsics, d.Lens);

        public Ray Ray(Vec2d pixel)
        {
            var dir = CameraModel.PixelRayCamera(pixel, K, Lens);
            return new Ray(Centre, Rwc.Transposed() * dir);
        }

        public bool Project(Vec3d world, out Vec2d pixel, out Vec3d du, out Vec3d dv)
        {
            var ok = Projection.ProjectCamera(Rwc * (world - Centre), K.Fx, K.Fy, K.Cx, K.Cy, Lens, out pixel, out var dcu, out var dcv);
            // Chain through the rotation: ∂/∂X = ∂/∂Xc · Rwc, i.e. Rwcᵀ applied to the row.
            var rt = Rwc.Transposed();
            du = rt * dcu;
            dv = rt * dcv;
            return ok;
        }
    }

    internal static TriangulatedPoint Solve(Frame a, Vec2d pa, Frame b, Vec2d pb, bool refine)
    {
        if (Midpoint(a.Ray(pa), b.Ray(pb)) is not { } mid)
            return TriangulatedPoint.Invalid;
        var x = mid.Point;
        if (refine)
            x = Polish(a, pa, b, pb, x);
        if (!a.Project(x, out var qa, out _, out _) || !b.Project(x, out var qb, out _, out _))
            return TriangulatedPoint.Invalid;
        var res = Math.Sqrt(((qa - pa).LengthSquared + (qb - pb).LengthSquared) / 2);
        return new TriangulatedPoint(x, res, mid.Gap, double.IsFinite(res));
    }

    /// <summary>Gauss–Newton on the four reprojection residuals (the two-view optimum under pixel noise).</summary>
    private static Vec3d Polish(Frame a, Vec2d pa, Frame b, Vec2d pb, Vec3d x)
    {
        for (var it = 0; it < 6; it++)
        {
            if (!a.Project(x, out var qa, out var dua, out var dva) || !b.Project(x, out var qb, out var dub, out var dvb))
                return x;
            Span<Vec3d> rows = [dua, dva, dub, dvb];
            Span<double> r = [qa.X - pa.X, qa.Y - pa.Y, qb.X - pb.X, qb.Y - pb.Y];
            var n = new Mat3d(0, 0, 0, 0, 0, 0, 0, 0, 0);
            var g = Vec3d.Zero;
            for (var k = 0; k < 4; k++)
            {
                var j = rows[k];
                n += new Mat3d(j.X * j.X, j.X * j.Y, j.X * j.Z, j.Y * j.X, j.Y * j.Y, j.Y * j.Z, j.Z * j.X, j.Z * j.Y, j.Z * j.Z);
                g += j * r[k];
            }
            if (!n.TryInverse(out var inv))
                return x;
            var step = inv * g;
            x -= step;
            if (step.Length < 1e-12 * (1 + x.Length))
                break;
        }
        return x;
    }
}
