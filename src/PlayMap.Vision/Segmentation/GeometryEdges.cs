using System.Runtime.CompilerServices;
using PlayMap.Core.Geometry;
using PlayMap.Core.Numerics;

namespace PlayMap.Vision.Segmentation;

/// <summary>What counts as an edge in a scan. A depth jump is a step between neighbours at least
/// <see cref="JumpRatio"/> times the steps beside it in the same direction (scale-free: it holds near and far, on
/// a wall seen square-on and on a floor seen at a grazing angle). A crease is a change of surface normal of at
/// least <see cref="CreaseDegrees"/> between neighbours (normals from the points, and from the scan's own normals
/// when supplied). On a noisy scan the normals' tangents span <see cref="TangentBaseline"/> steps (when 0, chosen
/// from the measured noise: 1 for a clean scan, up to 3) so the noise in their angle shrinks while a crease stays
/// sharp — smoothing the points instead would round the creases away.</summary>
public sealed record GeometryEdgeOptions(double JumpRatio = 3.0, double CreaseDegrees = 30.0, bool UseSuppliedNormals = true, int TangentBaseline = 0)
{
    public static readonly GeometryEdgeOptions Default = new();
}

/// <summary>The edge costs between 4-neighbours of an organised cloud: for each pixel, the cost of the link to its
/// right and to its lower neighbour, scaled so 1 is exactly an edge (a jump of <see cref="GeometryEdgeOptions.JumpRatio"/>
/// or a crease of <see cref="GeometryEdgeOptions.CreaseDegrees"/>), 0 is a flat continuation, and +∞ is a link to
/// an invalid point or off the grid. Two pixels are on one surface when a path of links below 1 joins them.
/// The normals are taken so they never straddle a crease: along each grid axis a pixel's tangent is the one-sided
/// step whose own continuation runs straight (the central difference where both do), so a pixel beside a crease
/// gets its own face's normal and every link across the crease — even a staircase step along an edge nearly
/// parallel to the grid — sees the full turn.</summary>
internal sealed class GeometryEdges
{
    private GeometryEdges(int width, int height, Vec3d[] points, bool[] valid, float[] right, float[] down, double medianStep, Vec3d[] normals)
    {
        Normals = normals;
        Width = width;
        Height = height;
        Points = points;
        Valid = valid;
        Right = right;
        Down = down;
        MedianStep = medianStep;
    }

    public int Width { get; }
    public int Height { get; }
    public Vec3d[] Points { get; }
    public bool[] Valid { get; }
    public float[] Right { get; }
    public float[] Down { get; }
    public double MedianStep { get; }

    /// <summary>Unit normals from the points (zero where unknown), oriented by the grid: x-tangent × y-tangent.</summary>
    public Vec3d[] Normals { get; }

    /// <summary>The cost of the link between 4-neighbours <paramref name="a"/> and <paramref name="b"/>.</summary>
    public float Link(int a, int b)
    {
        if (b == a + 1)
            return Right[a];
        if (b == a - 1)
            return Right[b];
        return b > a ? Down[a] : Down[b];
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static GeometryEdges Build(PointCloud cloud, GeometryEdgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentNullException.ThrowIfNull(options);
        if (!cloud.IsOrganised)
            throw new ArgumentException("Edges need an organised cloud.", nameof(cloud));
        int w = cloud.GridWidth, h = cloud.GridHeight, n = w * h;
        var p = cloud.Points;
        var valid = new bool[n];
        for (var i = 0; i < n; i++)
            valid[i] = PointCloud.IsValid(p[i]);
        var noise = NoiseRatio(p, valid, w, h);
        var baseline = options.TangentBaseline > 0 ? options.TangentBaseline : noise > 0.45 ? 3 : noise > 0.2 ? 2 : 1;

        var stepR = new float[n];
        var stepD = new float[n];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                stepR[i] = x + 1 < w && valid[i] && valid[i + 1] ? (float)(p[i + 1] - p[i]).Length : float.NaN;
                stepD[i] = y + 1 < h && valid[i] && valid[i + w] ? (float)(p[i + w] - p[i]).Length : float.NaN;
            }
        }
        var median = MedianOf(stepR, stepD);
        var floor = (float)Math.Max(median * 0.25, 1e-9);

        // Jump ratio of each link against the same-direction links beside it; a turn at each pixel along each
        // axis where neither adjacent step is a jump (a jump is its own edge, and a turn across it is noise).
        var jumpR = new float[n];
        var jumpD = new float[n];
        Ratios(stepR, jumpR, w, h, 1, median, floor);
        Ratios(stepD, jumpD, w, h, w, median, floor);
        var jump = (float)options.JumpRatio;
        var crease = (float)options.CreaseDegrees;
        var tx = new Vec3d[n];
        var ty = new Vec3d[n];
        Tangents(p, stepR, jumpR, tx, w, h, 1, jump, crease, baseline);
        Tangents(p, stepD, jumpD, ty, w, h, w, jump, crease, baseline);
        var normals = new Vec3d[n];
        for (var i = 0; i < n; i++)
            normals[i] = Vec3d.Cross(tx[i], ty[i]).Normalized();

        var supplied = options.UseSuppliedNormals && cloud.HasNormals ? cloud.Normals : null;
        var right = new float[n];
        var down = new float[n];
        for (var i = 0; i < n; i++)
        {
            right[i] = float.IsNaN(stepR[i]) ? float.PositiveInfinity : Cost(jumpR[i], normals, supplied, i, i + 1, jump, crease);
            down[i] = float.IsNaN(stepD[i]) ? float.PositiveInfinity : Cost(jumpD[i], normals, supplied, i, i + w, jump, crease);
        }
        return new GeometryEdges(w, h, p, valid, right, down, median, normals);
    }

    private static float Cost(float ratio, Vec3d[] normals, Vec3d[]? supplied, int a, int b, float jump, float crease)
    {
        var c = Math.Max(0f, (ratio - 1f) / (jump - 1f));
        c = Math.Max(c, Angle(normals[a], normals[b]) / crease);
        if (supplied is not null)
            c = Math.Max(c, Angle(supplied[a], supplied[b]) / crease);
        return c;
    }

    /// <summary>The angle between two directions in degrees; 0 when either is unknown (zero or not finite).</summary>
    private static float Angle(Vec3d a, Vec3d b)
    {
        var la = a.Length;
        var lb = b.Length;
        if (!(la > 1e-12) || !(lb > 1e-12) || !double.IsFinite(la) || !double.IsFinite(lb))
            return 0;
        var cos = Math.Clamp(Vec3d.Dot(a, b) / (la * lb), -1.0, 1.0);
        return (float)(Math.Acos(cos) * 180 / Math.PI);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Ratios(float[] step, float[] ratio, int w, int h, int stride, double median, float floor)
    {
        var n = w * h;
        for (var i = 0; i < n; i++)
        {
            var s = step[i];
            if (float.IsNaN(s))
            {
                ratio[i] = float.NaN;
                continue;
            }
            // The link before starts one pixel back; the link after starts at the far end. Off-grid or invalid
            // neighbours read as NaN and are ignored; with no neighbour at all the cloud's median step stands in.
            var before = InLine(i, -stride, w, n) ? step[i - stride] : float.NaN;
            var after = InLine(i, stride, w, n) ? step[i + stride] : float.NaN;
            float reference;
            if (float.IsNaN(before))
                reference = float.IsNaN(after) ? (float)median : after;
            else
                reference = float.IsNaN(after) ? before : Math.Min(before, after);
            ratio[i] = s / Math.Max(reference, floor);
        }
    }

    private static bool InLine(int i, int offset, int w, int n)
    {
        var j = i + offset;
        if (j < 0 || j >= n)
            return false;
        // A horizontal neighbour must stay on the same row.
        return Math.Abs(offset) != 1 || j / w == i / w;
    }

    /// <summary>Per pixel, the surface tangent along one grid axis over <paramref name="k"/> steps. A side (the k
    /// steps forward or the k steps back) is usable when every step in it is valid and none is a jump; its
    /// straightness is the turn between it and the next k steps beyond it. Both sides straight and agreeing: the
    /// central difference; otherwise the straighter side, so the tangent stays on the pixel's own face (a pixel
    /// whose point lies on the crease itself may take either face's). Falls back to shorter spans near borders
    /// and edges; zero when no side is usable.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Tangents(Vec3d[] p, float[] step, float[] ratio, Vec3d[] tangent, int w, int h, int stride, float jump, float crease, int k)
    {
        var n = w * h;
        var half = crease / 2;
        for (var i = 0; i < n; i++)
        {
            for (var span = k; span >= 1; span--)
            {
                var fwd = Run(i, stride, span);
                var bwd = Run(i, -stride, span);
                if (!fwd && !bwd)
                    continue;
                var tf = fwd ? p[i + span * stride] - p[i] : Vec3d.Zero;
                var tb = bwd ? p[i] - p[i - span * stride] : Vec3d.Zero;
                if (fwd && bwd)
                {
                    var af = Run(i + span * stride, stride, span) ? Angle(tf, p[i + 2 * span * stride] - p[i + span * stride]) : half;
                    var ab = Run(i - span * stride, -stride, span) ? Angle(tb, p[i - span * stride] - p[i - 2 * span * stride]) : half;
                    tangent[i] = af < half && ab < half && Angle(tf, tb) < half ? (tf + tb) * 0.5 : af <= ab ? tf : tb;
                }
                else
                    tangent[i] = fwd ? tf : tb;
                break;
            }
        }

        // The span steps from j in direction d (±stride) all exist, stay on the grid line and are no jump.
        bool Run(int j, int d, int span)
        {
            for (var t = 0; t < span; t++)
            {
                var a = d > 0 ? j + t * d : j + (t + 1) * d;
                if (!InLine(j + t * d, d, w, n) || a < 0 || a >= n || float.IsNaN(step[a]) || ratio[a] >= jump)
                    return false;
            }
            return true;
        }
    }

    /// <summary>The scan's noise as a fraction of its typical step: the median second difference along rows
    /// (about √6 σ on a plane; near zero on a clean surface, curved or not) over the median step.</summary>
    internal static double NoiseRatio(Vec3d[] p, bool[] valid, int w, int h)
    {
        var second = new List<double>();
        var steps = new List<double>();
        var stride = Math.Max(1, w * h / 20_000);
        for (var i = 1; i < w * h - 1; i += stride)
        {
            var x = i % w;
            if (x == 0 || x == w - 1 || !valid[i - 1] || !valid[i] || !valid[i + 1])
                continue;
            second.Add((p[i + 1] - p[i] * 2 + p[i - 1]).Length);
            steps.Add((p[i + 1] - p[i]).Length);
        }
        if (steps.Count < 16)
            return 0;
        second.Sort();
        steps.Sort();
        return second[second.Count / 2] / Math.Max(steps[steps.Count / 2], 1e-12);
    }

    private static double MedianOf(float[] a, float[] b)
    {
        // A strided sample is enough for a scale; the median ignores the few huge steps at jumps.
        var sample = new List<float>(Math.Min(a.Length * 2, 40_000));
        var stride = Math.Max(1, a.Length / 20_000);
        for (var i = 0; i < a.Length; i += stride)
        {
            if (!float.IsNaN(a[i]))
                sample.Add(a[i]);
            if (!float.IsNaN(b[i]))
                sample.Add(b[i]);
        }
        if (sample.Count == 0)
            return 1;
        sample.Sort();
        return sample[sample.Count / 2];
    }
}
