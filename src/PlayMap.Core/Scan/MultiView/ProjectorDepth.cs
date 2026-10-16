using PlayMap.Core.Calibration;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Threading;

namespace PlayMap.Core.Scan.MultiView;

/// <summary>How dense per-projector depth is made.</summary>
public sealed record DepthOptions
{
    public static readonly DepthOptions Default = new();

    /// <summary>Grid spacing in projector pixels (2 by default; 1 for a difficult surface).</summary>
    public int Stride { get; init; } = 2;

    /// <summary>A view whose camera ray misses the combined point by more than this (camera pixels) is dropped there.</summary>
    public double MaxViewPx { get; init; } = 2;

    /// <summary>The camera position's 1σ at full confidence and a perfect local fit (camera pixels).</summary>
    public double CameraSigmaPx { get; init; } = 0.1;
}

/// <summary>One projector's dense depth on its own raster, at <see cref="Stride"/>: per cell the depth along the
/// projector's axis (NaN where no view placed it), its 1σ, how many views agreed, and the organised cloud (normals
/// toward the projector) in the solve's frame. Cell (i, j) is projector pixel ((i + ½)·s, (j + ½)·s).</summary>
public sealed record ProjectorDepthMap(string ProjectorId, int Width, int Height, int Stride, float[] Depth, float[] Sigma, byte[] Views,
    PointCloud Cloud)
{
    /// <summary>The projector's centre in the solve's frame (the cloud's normals face it).</summary>
    public Vec3d Origin { get; init; }

    /// <summary>Each cell's surface variation from normal estimation (0 flat, larger at creases; NaN where invalid).</summary>
    public float[] Curvature { get; init; } = [];

    /// <summary>The cells with a depth.</summary>
    public int Valid => Depth.Count(float.IsFinite);

    /// <summary>The median depth 1σ over the valid cells (the merge keeps the steadier projector first).</summary>
    public double MedianSigma
    {
        get
        {
            var s = Sigma.Where(float.IsFinite).Select(x => (double)x).ToArray();
            return s.Length > 0 ? SolverMath.Median(s) : double.PositiveInfinity;
        }
    }

    /// <summary>The projector's mesh from its grid (front faces toward the projector, UVs its own raster).</summary>
    public GridMesh Mesh(GridMeshOptions? options = null) => GridMesher.Build(Cloud, options);
}

/// <summary>A view of a projector as dense depth takes it: the solved camera that saw it and its inverted decode.</summary>
public sealed record DepthView(string ViewId, DeviceView Camera, InverseCorrespondence Inverse);

/// <summary>
/// Dense geometry per projector from every view that decoded it. For each projector pixel on the grid, each view's
/// camera position comes from its inverted decode (a local fit at that pixel); the projector's ray is intersected
/// with each camera's by moving along the projector's ray to the depth the camera sees best (a few Gauss–Newton steps
/// on the camera's reprojection, from the rays' closest approach). Views combine by inverse variance — a view's
/// variance is its pixel uncertainty (confidence, fit residual) over how fast its pixel moves with depth, so a
/// grazing or short-baseline view weighs little; views more than <see cref="DepthOptions.MaxViewPx"/> out are
/// dropped and the rest combined again. Curved surfaces need nothing special: the grid follows whatever the projector
/// lights.
/// </summary>
public static class ProjectorDepth
{
    public static ProjectorDepthMap Build(SolvedProjector projector, IReadOnlyList<DepthView> seenBy, DepthOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(seenBy);
        options ??= DepthOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Stride, 1);
        var k = projector.Intrinsics;
        if (seenBy.FirstOrDefault(v => v.Inverse.ProjectorWidth != k.Width || v.Inverse.ProjectorHeight != k.Height) is { } other)
        {
            throw new ArgumentException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"View {other.ViewId} decoded a {other.Inverse.ProjectorWidth} × {other.Inverse.ProjectorHeight} raster; projector {projector.Id} is {k.Width} × {k.Height}."), nameof(seenBy));
        }
        var s = options.Stride;
        var w = (k.Width + s - 1) / s;
        var h = (k.Height + s - 1) / s;
        var depth = new float[w * h];
        var sigma = new float[w * h];
        var views = new byte[w * h];
        var points = new Vec3d[w * h];
        Array.Fill(depth, float.NaN);
        Array.Fill(sigma, float.NaN);
        Array.Fill(points, PointCloud.Invalid);
        var pose = projector.Pose;
        var r = pose.CameraToWorld;
        Parallel.For(0, h, Compute.With(ct), j =>
        {
            Span<double> d = stackalloc double[seenBy.Count];
            Span<double> vr = stackalloc double[seenBy.Count];
            Span<bool> use = stackalloc bool[seenBy.Count];
            Span<Vec2d> seenAt = stackalloc Vec2d[seenBy.Count];
            for (var i = 0; i < w; i++)
            {
                var u = new Vec2d(Math.Min((i + 0.5) * s, k.Width - 0.5), Math.Min((j + 0.5) * s, k.Height - 0.5));
                var local = CameraModel.PixelRayCamera(u, k, projector.Lens);
                var ray = r * local;
                // Along the axis: a unit ray's point at distance t has axial depth t·ray_z(camera frame).
                var axial = local.Z;
                var n = 0;
                for (var v = 0; v < seenBy.Count; v++)
                {
                    use[v] = false;
                    if (!seenBy[v].Inverse.TryAt(u, out var sample) || !Along(pose.Position, ray, seenBy[v].Camera, sample.Camera, out var t, out var rate))
                        continue;
                    var px = Math.Max(options.CameraSigmaPx, sample.FitResidualPx) / Math.Sqrt(Math.Max(sample.Confidence, 0.1f));
                    d[v] = t;
                    seenAt[v] = sample.Camera;
                    var sd = px / Math.Max(rate, 1e-12);
                    vr[v] = sd * sd;
                    use[v] = true;
                    n++;
                }
                if (n == 0)
                    continue;
                var (best, bestVar, count) = Combine(d, vr, use);
                // Drop the views that miss the combined point and combine the rest again.
                var p = pose.Position + ray * best;
                var dropped = false;
                for (var v = 0; v < seenBy.Count; v++)
                {
                    if (!use[v])
                        continue;
                    if (seenBy[v].Camera.Project(p) is not { } seen || (seen - seenAt[v]).Length > options.MaxViewPx)
                    {
                        use[v] = false;
                        dropped = true;
                    }
                }
                if (dropped)
                {
                    (best, bestVar, count) = Combine(d, vr, use);
                    if (count == 0)
                        continue;
                    p = pose.Position + ray * best;
                }
                var cell = j * w + i;
                depth[cell] = (float)(best * axial);
                sigma[cell] = (float)(Math.Sqrt(bestVar) * axial);
                views[cell] = (byte)Math.Min(count, 255);
                points[cell] = p;
            }
        });
        var cloud = PointCloudOps.EstimateNormals(new PointCloud(points, gridWidth: w, gridHeight: h), pose.Position, out var curvature);
        return new ProjectorDepthMap(projector.Id, w, h, s, depth, sigma, views, cloud) { Origin = pose.Position, Curvature = curvature };
    }

    private static (double Value, double Variance, int Count) Combine(ReadOnlySpan<double> d, ReadOnlySpan<double> vr, ReadOnlySpan<bool> use)
    {
        double sw = 0, swd = 0;
        var count = 0;
        for (var v = 0; v < d.Length; v++)
        {
            if (!use[v])
                continue;
            sw += 1 / vr[v];
            swd += d[v] / vr[v];
            count++;
        }
        return count == 0 ? (double.NaN, double.NaN, 0) : (swd / sw, 1 / sw, count);
    }

    /// <summary>The distance along the projector's unit ray at which the camera sees the point at
    /// <paramref name="pixel"/>, and how many camera pixels the point moves per unit of that distance.</summary>
    private static bool Along(Vec3d origin, Vec3d ray, DeviceView camera, Vec2d pixel, out double t, out double rate)
    {
        rate = 0;
        t = double.NaN;
        var cam = camera.Ray(pixel);
        // Closest approach of the two lines.
        var b = Vec3d.Dot(ray, cam.Direction);
        var w0 = origin - cam.Origin;
        var denom = 1 - b * b;
        if (denom < 1e-10)
            return false;
        t = (b * Vec3d.Dot(cam.Direction, w0) - Vec3d.Dot(ray, w0)) / denom;
        if (!(t > 0))
            return false;
        // Gauss–Newton on the camera's reprojection, by a central difference along the ray.
        for (var it = 0; it < 4; it++)
        {
            var h = Math.Max(1e-6, t * 1e-5);
            if (camera.Project(origin + ray * t) is not { } p0 || camera.Project(origin + ray * (t + h)) is not { } p1
                || camera.Project(origin + ray * (t - h)) is not { } pm)
                return false;
            var g = (p1 - pm) / (2 * h);
            var gg = g.LengthSquared;
            if (!(gg > 0))
                return false;
            var step = -Vec2d.Dot(g, p0 - pixel) / gg;
            t += step;
            rate = Math.Sqrt(gg);
            if (!(t > 0) || !double.IsFinite(t))
                return false;
            if (Math.Abs(step) < 1e-9 * t)
                break;
        }
        return true;
    }
}
