using System.Globalization;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Vision;

namespace PlayMap.Vision.Previs;

/// <summary>How a rough mesh is made from one photo.</summary>
public sealed record PrevisOptions(
    /// <summary>The distance (m) to the subject: the median depth of a relative map, or the flat plane's.</summary>
    double DistanceMetres = 3,
    double HorizontalFovDegrees = 60,
    /// <summary>Grid columns (rows follow the photo's aspect).</summary>
    int GridColumns = 96,
    /// <summary>A cell whose corners' depths differ by more than this ratio is a silhouette and is left open.</summary>
    double TearRatio = 1.25,
    /// <summary>Relative depths are clamped to this ratio either side of the median (a sky, a lens flare).</summary>
    double MaxDepthRatio = 8)
{
    public static readonly PrevisOptions Default = new();
}

/// <summary>A previs mesh: in a world where the photo's camera sits at the origin looking along −Z with +Y up
/// (<see cref="Camera"/>), UVs the photo's own coordinates (0..1, top-left origin), whether a model shaped it,
/// and the words.</summary>
public sealed record PrevisResult(MeshData Mesh, Intrinsics Intrinsics, Pose Camera, bool FromModel, string Words);

/// <summary>A rough mesh from one photo for planning before anyone is on site: a depth model's map turned into a
/// grid mesh (relative depth scaled so its median sits at the given distance, metric depth kept), torn open at
/// silhouettes. Without a model — or when the model fails — a flat plane at that distance, so previs never
/// fails. For previs only: calibration comes from structured light.</summary>
public static class DepthPrevis
{
    /// <summary>Builds the previs mesh on the compute threads.</summary>
    public static Task<PrevisResult> BuildAsync(ImageRgb8 photo, IDepthEstimator? estimator, PrevisOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(photo);
        var o = options ?? PrevisOptions.Default;
        return BuildCoreAsync(photo, estimator, o, cancellationToken);
    }

    private static async Task<PrevisResult> BuildCoreAsync(ImageRgb8 photo, IDepthEstimator? estimator, PrevisOptions options, CancellationToken cancellationToken)
    {
        await PlayMap.Core.Threading.Compute.Hop();
        cancellationToken.ThrowIfCancellationRequested();
        if (estimator is null)
            return Flat(photo, options, string.Create(CultureInfo.InvariantCulture, $"no depth model installed: a flat plane at {options.DistanceMetres:0.##} m"));
        DepthEstimate depth;
        try
        {
            depth = await estimator.EstimateAsync(photo, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Previs never fails: the model's failure is reported and a plane stands in.
            await PlayMap.Core.Threading.Compute.Hop();
            return Flat(photo, options, string.Create(CultureInfo.InvariantCulture, $"the depth model failed ({ex.Message}): a flat plane at {options.DistanceMetres:0.##} m"));
        }
        await PlayMap.Core.Threading.Compute.Hop();
        var z = ToDepth(depth, photo.Width, photo.Height, options, out var usable);
        if (!usable)
            return Flat(photo, options, string.Create(CultureInfo.InvariantCulture, $"the depth map was empty: a flat plane at {options.DistanceMetres:0.##} m"));
        var mesh = Grid(photo.Width, photo.Height, options, z, out var k);
        return new PrevisResult(mesh, k, CameraPose, true, string.Create(CultureInfo.InvariantCulture,
            $"shaped by {estimator.Id} ({depth.Kind}), median {options.DistanceMetres:0.##} m"));
    }

    /// <summary>The camera of the previs world: at the origin, looking along −Z, +Y up.</summary>
    public static readonly Pose CameraPose = Pose.LookAt(Vec3d.Zero, new Vec3d(0, 0, -1), Vec3d.UnitY);

    public static PrevisResult Flat(ImageRgb8 photo, PrevisOptions options, string words)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(options);
        var z = new float[photo.Width * photo.Height];
        Array.Fill(z, (float)options.DistanceMetres);
        var mesh = Grid(photo.Width, photo.Height, options, z, out var k);
        return new PrevisResult(mesh, k, CameraPose, false, words);
    }

    /// <summary>Depth along the optical axis in metres per photo pixel (NaN unknown).</summary>
    internal static float[] ToDepth(DepthEstimate d, int w, int h, PrevisOptions o, out bool usable)
    {
        var src = d.Values;
        if (d.Width != w || d.Height != h)
        {
            src = new float[w * h];
            Imaging.Resampler.Channel(d.Values, d.Width, d.Height, new RectD(0, 0, d.Width, d.Height), src, w, h);
        }
        var z = new float[w * h];
        for (var i = 0; i < z.Length; i++)
        {
            var v = src[i];
            z[i] = !float.IsFinite(v) ? float.NaN : d.Kind switch
            {
                DepthScaleKind.RelativeInverse => v > 1e-6f ? 1f / v : float.NaN,
                _ => v > 0 ? v : float.NaN,
            };
        }
        var finite = new float[z.Length];
        var count = 0;
        foreach (var v in z)
            if (float.IsFinite(v))
                finite[count++] = v;
        usable = count > 0;
        if (!usable || d.Kind == DepthScaleKind.Metric)
            return z;
        Array.Sort(finite, 0, count);
        var median = finite[count / 2];
        var scale = (float)(o.DistanceMetres / median);
        float lo = (float)(o.DistanceMetres / o.MaxDepthRatio), hi = (float)(o.DistanceMetres * o.MaxDepthRatio);
        for (var i = 0; i < z.Length; i++)
            if (float.IsFinite(z[i]))
                z[i] = Math.Clamp(z[i] * scale, lo, hi);
        return z;
    }

    /// <summary>A grid mesh: each vertex the photo pixel's ray at its depth, cells torn where depth jumps.</summary>
    private static MeshData Grid(int w, int h, PrevisOptions o, float[] z, out Intrinsics k)
    {
        k = Intrinsics.FromFieldOfView(w, h, o.HorizontalFovDegrees);
        var cols = Math.Clamp(o.GridColumns, 2, w);
        var rows = Math.Clamp((int)Math.Round(cols * (double)h / w), 2, h);
        var pos = new float[(cols + 1) * (rows + 1) * 3];
        var uv = new float[(cols + 1) * (rows + 1) * 2];
        var depth = new float[(cols + 1) * (rows + 1)];
        for (var r = 0; r <= rows; r++)
        {
            for (var c = 0; c <= cols; c++)
            {
                var v = r * (cols + 1) + c;
                double u = c * (double)w / cols, t = r * (double)h / rows;
                int px = Math.Clamp((int)u, 0, w - 1), py = Math.Clamp((int)t, 0, h - 1);
                var d = z[py * w + px];
                depth[v] = d;
                // Camera frame (x right, y down, z forward) to the previs world (x right, y up, looking along −z).
                double xn = (u - k.Cx) / k.Fx, yn = (t - k.Cy) / k.Fy;
                pos[v * 3] = (float)(xn * d);
                pos[v * 3 + 1] = (float)(-yn * d);
                pos[v * 3 + 2] = -d;
                uv[v * 2] = (float)(u / w);
                uv[v * 2 + 1] = (float)(t / h);
            }
        }
        var idx = new List<uint>(cols * rows * 6);
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                uint a = (uint)(r * (cols + 1) + c), b = a + 1, cc = a + (uint)(cols + 1), dd = cc + 1;
                float d0 = depth[a], d1 = depth[b], d2 = depth[cc], d3 = depth[dd];
                if (!float.IsFinite(d0) || !float.IsFinite(d1) || !float.IsFinite(d2) || !float.IsFinite(d3))
                    continue;
                var min = Math.Min(Math.Min(d0, d1), Math.Min(d2, d3));
                var max = Math.Max(Math.Max(d0, d1), Math.Max(d2, d3));
                if (max > min * o.TearRatio)
                    continue;
                // Counter-clockwise seen from the camera (the front faces it).
                idx.AddRange([a, cc, b, b, cc, dd]);
            }
        }
        // Unreferenced vertices with NaN depth would poison the bounds; park them at the origin.
        for (var v = 0; v < depth.Length; v++)
            if (!float.IsFinite(depth[v]))
                pos[v * 3] = pos[v * 3 + 1] = pos[v * 3 + 2] = 0;
        return new MeshData(pos, [], uv, [.. idx]).WithComputedNormals();
    }
}
