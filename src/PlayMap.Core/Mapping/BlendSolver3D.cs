using System.Globalization;
using System.Text;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Threading;

namespace PlayMap.Core.Mapping;

/// <summary>What the automatic 3D blend weighs, and how hard.</summary>
public sealed record BlendSolveOptions
{
    public static readonly BlendSolveOptions Default = new();

    /// <summary>The map's size as a fraction of each output's raster (¼: 480 × 270 for 1920 × 1080); the
    /// GPU upsamples it bilinearly.</summary>
    public double Scale { get; init; } = 0.25;

    /// <summary>How far in from an edge of its coverage a projector fades — its raster's border, or an
    /// occlusion edge where its picture leaves a surface — as a fraction of the raster's side.</summary>
    public double EdgeWidth { get; init; } = 0.15;

    /// <summary>The exponent on the cosine of incidence: higher hands a grazing surface to the projector
    /// that faces it more squarely.</summary>
    public double IncidenceExponent { get; init; } = 1;

    /// <summary>The cosine below which incidence counts as this grazing and no worse.</summary>
    public double MinCosine { get; init; } = 0.05;

    /// <summary>Favour the nearer projector (1 / d²).</summary>
    public bool UseDistance { get; init; } = true;

    /// <summary>Favour the brighter projector by its lumens (unknown counts as the mean of the known).</summary>
    public bool UseLumens { get; init; } = true;

    /// <summary>A step in depth between neighbouring map pixels larger than this fraction of the depth is
    /// an occlusion edge (a silhouette), which a projector's weight fades away from.</summary>
    public double DepthJump { get; init; } = 0.03;

    /// <summary>Intensity compensation's uniformity versus brightness: 0 none, 1 every point down to the
    /// dimmest (applied to outputs whose <see cref="BlendSettings.IntensityCompensation"/> is on).</summary>
    public double Uniformity { get; init; } = 1;

    /// <summary>The low percentile of the blended irradiance that full uniformity equalises to.</summary>
    public double TargetPercentile { get; init; }
}

/// <summary>One output's automatic blend: the map for the GPU and what went into it.</summary>
public sealed record OutputBlend(
    string OutputId,
    string ProjectorId,
    /// <summary>Weights (signal multipliers, compensation folded in where on) and the black pedestal.</summary>
    BlendMap Map,
    /// <summary>How many projectors light each map pixel's point (0: the pixel misses the stage).</summary>
    byte[] Coverage,
    /// <summary>The blended light's predicted irradiance at each pixel's point, 1 at the brightest point of
    /// the group of projectors that overlap.</summary>
    float[] Irradiance,
    /// <summary>The intensity-compensation gain (in light) at each pixel; 1 where compensation is off.</summary>
    float[] Gain,
    /// <summary>The fraction of the map's pixels that land on a surface.</summary>
    double CoveredFraction,
    /// <summary>Of those, the fraction another projector also lights.</summary>
    double OverlapFraction,
    /// <summary>Of those, the fraction inside another projector's frame but in its shadow.</summary>
    double ShadowFraction,
    int MaxOverlap,
    IReadOnlyList<string> OverlapsWith,
    string Words)
{
    /// <summary>The blend weight in light at a map pixel (without the compensation gain).</summary>
    public double LightWeightAt(int x, int y, double gamma)
    {
        var k = y * Map.Width + x;
        var g = Gain[k];
        return g > 0 ? BlendMath.ToLight(Map.Weights[k], gamma) / g : 0;
    }
}

/// <summary>The automatic blend of every projector output on the stage.</summary>
public sealed record BlendSolution(IReadOnlyList<OutputBlend> Outputs, IReadOnlyList<string> Problems, string Words)
{
    public OutputBlend? For(string outputId) => Outputs.FirstOrDefault(o => o.OutputId == outputId);
}

/// <summary>The automatic 3D blend. For every pixel of every projector output (at a reduced resolution): the
/// pixel's ray is cast into the stage; at the point it lands, every projector that sees the point — inside
/// its raster, the surface facing it, nothing in between (a shadow ray back to its lens) — gets a weight:
/// edge falloff (the distance from the point's pixel to that projector's raster edge and to its occlusion
/// edges, over the band, through the output's curve) × incidence (cos θ, clamped, to an exponent) × distance
/// (1/d²) × lumens. The weights are normalised to sum to one in light and the map stores this projector's
/// share through its inverse gamma. Overlapping projectors therefore sum to one everywhere they overlap; a
/// point only one projector reaches (outside the others' frames, or in their shadows) gets all of it; and
/// because each weight falls to nothing at the edges of its own coverage, the hand-over is smooth even at a
/// shadow's edge. The black pedestal raises every point to the deepest floor of its group of overlapping
/// projectors (each covering projector adding (deepest − floor) / count blacks, in light, through the inverse
/// gamma), and intensity compensation evens the predicted irradiance where asked.
/// Deterministic: every pixel is a pure function of the stage; rows run in parallel.</summary>
public static class BlendSolver3D
{
    /// <summary>The most projectors one solve weighs together (a pixel's set is a 64-bit mask).</summary>
    public const int MaxProjectors = 64;

    /// <summary>Solves every enabled output in Projector mode against the stage's visible surfaces. Progress (0..1)
    /// is reported after each projector's pass of each step, on the solving thread.</summary>
    public static BlendSolution Solve(Stage stage, IReadOnlyList<Output> outputs, Func<string, MeshData?> meshes,
        BlendSolveOptions? options = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        var scene = StageScene.Build(stage, meshes);
        return Solve(scene, stage, outputs, options, progress, cancellationToken);
    }

    /// <summary>Solves against a scene already built (the planner keeps one while the stage is unchanged).</summary>
    public static BlendSolution Solve(StageScene scene, Stage stage, IReadOnlyList<Output> outputs,
        BlendSolveOptions? options = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(outputs);
        options ??= BlendSolveOptions.Default;
        var problems = new List<string>(scene.Problems);
        var views = new List<View>();
        foreach (var o in outputs)
        {
            if (!o.Enabled || o.Mode != OutputMode.Projector)
                continue;
            var projector = o.ProjectorId is null ? null : stage.Projectors.FirstOrDefault(p => p.Id == o.ProjectorId);
            if (projector is null)
            {
                problems.Add(Words($"{o.Name} is in Projector mode but drives no projector on the stage."));
                continue;
            }
            if (views.Count == MaxProjectors)
            {
                problems.Add(Words($"{o.Name} is left out: one solve weighs at most {MaxProjectors} projectors."));
                continue;
            }
            views.Add(new View(views.Count, o, projector, options));
        }
        if (views.Count == 0)
            return new BlendSolution([], problems, problems.Count > 0 ? string.Join(" ", problems) : "No output drives a projector, so there is nothing to blend.");
        if (scene.World.TriangleCount == 0)
            problems.Add("The stage has no visible surface, so no projector lands anywhere.");

        var stacked = Stack(stage, views);
        var known = views.Where(v => v.Projector.Lumens > 0).Select(v => v.Projector.Lumens).ToList();
        var meanLumens = known.Count > 0 ? known.Average() : 1;
        foreach (var v in views)
            v.Lumens = options.UseLumens ? (v.Projector.Lumens > 0 ? v.Projector.Lumens : meanLumens) / meanLumens : 1;

        var po = Compute.With(cancellationToken);
        // Three passes over every projector, then the finish: each pass of each projector is one step.
        var steps = 3 * views.Count + 1;
        var done = 0;
        void Step() => progress?.Report(Math.Min(1.0, ++done / (double)steps));
        foreach (var v in views)
        {
            CastPrimary(v, scene, po);
            Step();
        }
        foreach (var v in views)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BuildEdgeDistance(v, options);
            Step();
        }
        foreach (var v in views)
        {
            Weigh(v, views, scene, options, po, stacked);
            Step();
        }

        // Groups of projectors that overlap anywhere (directly or through a chain) share one black floor and
        // one compensation target.
        var parent = Enumerable.Range(0, views.Count).ToArray();
        int Find(int a)
        {
            while (parent[a] != a)
                a = parent[a] = parent[parent[a]];
            return a;
        }
        foreach (var v in views)
        {
            var mask = v.SeenUnion;
            for (var j = 0; j < views.Count; j++)
                if ((mask & (1UL << j)) != 0)
                    parent[Find(j)] = Find(v.Index);
        }
        var floorTarget = new double[views.Count];
        var brightest = new double[views.Count];
        var irradianceByGroup = new Dictionary<int, List<float>>();
        foreach (var v in views)
        {
            var g = Find(v.Index);
            if (!irradianceByGroup.TryGetValue(g, out var list))
                irradianceByGroup[g] = list = [];
            for (var k = 0; k < v.Tri.Length; k++)
            {
                if (v.Tri[k] < 0)
                    continue;
                floorTarget[g] = Math.Max(floorTarget[g], v.Floor[k]);
                brightest[g] = Math.Max(brightest[g], v.Emix[k]);
                list.Add(v.Emix[k]);
            }
        }
        var target = new double[views.Count];
        foreach (var (g, list) in irradianceByGroup)
            target[g] = IntensityCompensation.Target(list, options.TargetPercentile);

        var results = new List<OutputBlend>(views.Count);
        foreach (var v in views)
            results.Add(Finish(v, views, floorTarget[Find(v.Index)], target[Find(v.Index)], brightest[Find(v.Index)], options));
        Step();
        return new BlendSolution(results, problems, Summary(results, views, problems, options));
    }

    /// <summary>A projector's optics with its rotation built once (the camera helpers rebuild it per call).</summary>
    private readonly record struct Optics(Mat3d CamToWorld, Mat3d WorldToCam, Vec3d Centre, Intrinsics K, LensDistortion Lens)
    {
        public static Optics Of(Projector p)
        {
            var r = p.Pose.CameraToWorld;
            return new Optics(r, r.Transposed(), p.Pose.Position, p.Intrinsics, p.Lens);
        }

        public Vec3d Forward => CamToWorld.Column(2);

        public Vec2d? Project(Vec3d world) => CameraModel.ProjectCamera(WorldToCam * (world - Centre), K, Lens);

        public Ray PixelRay(Vec2d pixel) => new(Centre, (CamToWorld * CameraModel.PixelRayCamera(pixel, K, Lens)).Normalized());
    }

    private sealed class View
    {
        public readonly int Index;
        public readonly Output Output;
        public readonly Projector Projector;
        public readonly Optics Optics;
        public readonly int W;
        public readonly int H;
        public readonly double Sx;
        public readonly double Sy;
        public readonly int[] Tri;
        public readonly Vec3d[] Point;
        public readonly Vec3d[] Face;   // the geometric normal, turned toward this projector
        public readonly Vec3d[] Shade;  // the shading normal, turned toward this projector
        public readonly float[] Depth;
        public float[] EdgeDist = [];   // projector pixels to the nearest occlusion edge
        public readonly float[] Light;  // this projector's normalised weight, in light
        public readonly float[] Floor;  // the black floor: Σ black of the projectors lighting the point
        public readonly float[] Emix;   // the blended irradiance at the point
        public readonly byte[] Count;
        public readonly bool[] Shadowed;
        public ulong SeenUnion;
        public double Lumens = 1;
        /// <summary>The view whose group this one blends in: a stack's master for its members, else itself.</summary>
        public int Group;

        public View(int index, Output output, Projector projector, BlendSolveOptions options)
        {
            Index = index;
            Group = index;
            Output = output;
            Projector = projector;
            Optics = Optics.Of(projector);
            var scale = Math.Clamp(options.Scale, 1e-3, 1);
            W = Math.Max(1, (int)Math.Round(Math.Max(1, output.Width) * scale));
            H = Math.Max(1, (int)Math.Round(Math.Max(1, output.Height) * scale));
            Sx = projector.Intrinsics.Width / (double)W;
            Sy = projector.Intrinsics.Height / (double)H;
            var n = W * H;
            Tri = new int[n];
            Point = new Vec3d[n];
            Face = new Vec3d[n];
            Shade = new Vec3d[n];
            Depth = new float[n];
            Light = new float[n];
            Floor = new float[n];
            Emix = new float[n];
            Count = new byte[n];
            Shadowed = new bool[n];
        }

        public Vec3d Centre => Optics.Centre;
    }

    private static void CastPrimary(View v, StageScene scene, ParallelOptions po)
    {
        var optics = v.Optics;
        Parallel.For(0, v.H, po, y =>
        {
            for (var x = 0; x < v.W; x++)
            {
                var i = y * v.W + x;
                var ray = optics.PixelRay(new Vec2d((x + 0.5) * v.Sx, (y + 0.5) * v.Sy));
                if (scene.Raycaster.Cast(ray) is not { } hit)
                {
                    v.Tri[i] = -1;
                    continue;
                }
                v.Tri[i] = hit.Triangle;
                v.Point[i] = hit.Point;
                v.Depth[i] = (float)hit.Distance;
                var toEye = -ray.Direction;
                var face = scene.FaceNormal(hit.Triangle);
                if (Vec3d.Dot(face, toEye) < 0)
                    face = -face;
                var shade = scene.NormalAt(hit);
                if (Vec3d.Dot(shade, face) < 0)
                    shade = -shade;
                v.Face[i] = face;
                v.Shade[i] = shade;
            }
        });
    }

    /// <summary>Occlusion edges are pixels that land on a surface beside one that misses, or beside one
    /// whose depth jumps; the exact distance transform from them, in projector pixels.</summary>
    private static void BuildEdgeDistance(View v, BlendSolveOptions options)
    {
        var w = v.W;
        var h = v.H;
        var seed = new bool[w * h];
        var any = false;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (v.Tri[i] < 0)
                    continue;
                var edge = (x > 0 && Breaks(v, i, i - 1, options.DepthJump)) || (x < w - 1 && Breaks(v, i, i + 1, options.DepthJump))
                    || (y > 0 && Breaks(v, i, i - w, options.DepthJump)) || (y < h - 1 && Breaks(v, i, i + w, options.DepthJump));
                seed[i] = edge;
                any |= edge;
            }
        if (!any)
        {
            v.EdgeDist = [];
            return;
        }
        var d = DistanceTransform.Euclidean(seed, w, h);
        var px = (v.Sx + v.Sy) / 2;
        for (var i = 0; i < d.Length; i++)
            d[i] = d[i] >= DistanceTransform.Far ? DistanceTransform.Far : (float)(d[i] * px);
        v.EdgeDist = d;
    }

    private static bool Breaks(View v, int a, int b, double jump)
    {
        if (v.Tri[b] < 0)
            return true;
        var da = v.Depth[a];
        var db = v.Depth[b];
        return Math.Abs(da - db) > jump * Math.Min(da, db);
    }

    /// <summary>The edge falloff of projector <paramref name="v"/> at its pixel <paramref name="p"/>: the
    /// raster's border (separable, x and y) times its occlusion edges, each through the output's curve.</summary>
    private static double EdgeFactor(View v, Vec2d p, double edgeWidth)
    {
        if (edgeWidth <= 0)
            return 1;
        var kw = v.Projector.Intrinsics.Width;
        var kh = v.Projector.Intrinsics.Height;
        var curve = v.Output.Blend.Curve;
        var tx = Math.Min(p.X, kw - p.X) / (edgeWidth * kw);
        var ty = Math.Min(p.Y, kh - p.Y) / (edgeWidth * kh);
        var e = Falloff(curve, tx) * Falloff(curve, ty);
        if (v.EdgeDist.Length > 0 && e > 0)
            e *= Falloff(curve, SampleBilinear(v.EdgeDist, v.W, v.H, p.X / v.Sx - 0.5, p.Y / v.Sy - 0.5) / (edgeWidth * Math.Min(kw, kh)));
        return e;
    }

    /// <summary>Every blend curve is flat at its foot (∝ t²), and where two projectors' edges nearly meet the
    /// ratio of two such small weights swings faster than a map pixel can hold. A linear floor (¼·t) keeps
    /// the shares well-conditioned there; beyond the band's outer tenth the curve rules unchanged.</summary>
    private static double Falloff(BlendCurve curve, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Math.Max(BlendMath.Curve(curve, t), EdgeFloorSlope * t);
    }

    private const double EdgeFloorSlope = 0.25;

    private static double SampleBilinear(float[] map, int w, int h, double x, double y)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        var x0 = Math.Min((int)x, w - 1);
        var y0 = Math.Min((int)y, h - 1);
        var x1 = Math.Min(x0 + 1, w - 1);
        var y1 = Math.Min(y0 + 1, h - 1);
        var fx = x - x0;
        var fy = y - y0;
        var top = map[y0 * w + x0] * (1 - fx) + map[y0 * w + x1] * fx;
        var bottom = map[y1 * w + x0] * (1 - fx) + map[y1 * w + x1] * fx;
        return top * (1 - fy) + bottom * fy;
    }

    /// <summary>True when projector <paramref name="j"/>'s own depth map settles that it sees the point at
    /// its <paramref name="pixel"/>, <paramref name="distance"/> away: the four map samples around the pixel
    /// all land, agree with each other (no occlusion edge between them) and with the distance. False means
    /// "not settled" — a shadow ray decides. Most of a smooth overlap is settled here without a cast.</summary>
    private static bool SeenBy(View j, Vec2d pixel, double distance, double jump)
    {
        var fx = pixel.X / j.Sx - 0.5;
        var fy = pixel.Y / j.Sy - 0.5;
        var x0 = (int)Math.Floor(fx);
        var y0 = (int)Math.Floor(fy);
        if (x0 < 0 || y0 < 0 || x0 + 1 >= j.W || y0 + 1 >= j.H)
            return false;
        var i00 = y0 * j.W + x0;
        var i10 = i00 + 1;
        var i01 = i00 + j.W;
        var i11 = i01 + 1;
        if (j.Tri[i00] < 0 || j.Tri[i10] < 0 || j.Tri[i01] < 0 || j.Tri[i11] < 0)
            return false;
        double d00 = j.Depth[i00], d10 = j.Depth[i10], d01 = j.Depth[i01], d11 = j.Depth[i11];
        var lo = Math.Min(Math.Min(d00, d10), Math.Min(d01, d11));
        var hi = Math.Max(Math.Max(d00, d10), Math.Max(d01, d11));
        if (hi - lo > jump * lo)
            return false;
        var tx = fx - x0;
        var ty = fy - y0;
        var expected = (d00 * (1 - tx) + d10 * tx) * (1 - ty) + (d01 * (1 - tx) + d11 * tx) * ty;
        // The first hit along j's ray is at the interpolated depth; the point is that hit when it is not
        // farther than it (an occluder would make the map shallower), within the samples' own spread.
        return distance <= expected + Math.Max(hi - lo, 1e-3 * expected);
    }

    /// <summary>The stacks' collapse (facade §3.9): each stack with two or more members on outputs here becomes one group, led by its
    /// master (else its first member there). True when any stack does.</summary>
    private static bool Stack(Stage stage, List<View> views)
    {
        var any = false;
        foreach (var sk in stage.Stacks)
        {
            var members = views.Where(v => sk.Members.Contains(v.Projector.Id, StringComparer.Ordinal)).ToList();
            if (members.Count < 2)
                continue;
            var lead = members.FirstOrDefault(v => v.Projector.Id == sk.MasterId) ?? members[0];
            foreach (var v in members)
                v.Group = lead.Index;
            any = true;
        }
        return any;
    }

    private static void Weigh(View v, List<View> views, StageScene scene, BlendSolveOptions o, ParallelOptions po, bool stacked)
    {
        var unions = new ulong[v.H];
        Parallel.For(0, v.H, po, y =>
        {
            ulong rowUnion = 0;
            Span<double> edges = stackalloc double[stacked ? views.Count : 0];
            Span<double> geometry = stackalloc double[stacked ? views.Count : 0];
            Span<double> light = stackalloc double[stacked ? views.Count : 0];
            for (var x = 0; x < v.W; x++)
            {
                var i = y * v.W + x;
                if (v.Tri[i] < 0)
                {
                    v.Light[i] = 1;
                    continue;
                }
                var point = v.Point[i];
                var face = v.Face[i];
                var shade = v.Shade[i];
                double sum = 0, own = 0, emix = 0, floor = 0;
                var count = 0;
                ulong seen = 0;
                var shadowed = false;
                foreach (var j in views)
                {
                    Vec2d pj;
                    double d;
                    Vec3d dir;
                    if (j == v)
                    {
                        pj = new Vec2d((x + 0.5) * v.Sx, (y + 0.5) * v.Sy);
                        d = v.Depth[i];
                        dir = (v.Centre - point) / Math.Max(d, 1e-12);
                    }
                    else
                    {
                        var proj = j.Optics.Project(point);
                        if (proj is not { } pixel || !CameraModel.InRaster(pixel, j.Optics.K))
                            continue;
                        var to = j.Centre - point;
                        d = to.Length;
                        dir = to / Math.Max(d, 1e-12);
                        // The other side of a thin surface is another point as far as light goes.
                        if (Vec3d.Dot(face, dir) <= 1e-9)
                            continue;
                        if (!SeenBy(j, pixel, d, o.DepthJump) && !scene.Sees(point, j.Centre))
                        {
                            shadowed = true;
                            continue;
                        }
                        pj = pixel;
                    }
                    var cos = Math.Abs(Vec3d.Dot(shade, dir));
                    var incidence = Math.Pow(Math.Max(cos, o.MinCosine), o.IncidenceExponent);
                    var falloff = o.UseDistance ? 1 / Math.Max(d * d, 1e-12) : 1;
                    var edge = EdgeFactor(j, pj, o.EdgeWidth);
                    var weight = Math.Max(edge * incidence * falloff * j.Lumens, 1e-30);
                    sum += weight;
                    if (j == v)
                        own = weight;
                    var cosAlpha = Math.Max(0, -Vec3d.Dot(dir, j.Optics.Forward));
                    var c2 = cosAlpha * cosAlpha;
                    emix += weight * j.Lumens * cos / Math.Max(d * d, 1e-12) * c2 * c2;
                    if (stacked)
                    {
                        edges[j.Index] = edge;
                        geometry[j.Index] = incidence * falloff;
                        light[j.Index] = j.Lumens * cos / Math.Max(d * d, 1e-12) * c2 * c2;
                    }
                    floor += Math.Clamp(j.Output.Blend.BlackLevel, 0, 1);
                    count++;
                    seen |= 1UL << j.Index;
                }
                if (stacked)
                    (sum, own, emix) = Collapse(v, views, seen, edges, geometry, light);
                v.Light[i] = (float)(own / sum);
                v.Emix[i] = (float)(emix / sum);
                v.Floor[i] = (float)floor;
                v.Count[i] = (byte)Math.Min(count, 255);
                v.Shadowed[i] = shadowed;
                rowUnion |= seen;
            }
            unions[y] = rowUnion;
        });
        foreach (var u in unions)
            v.SeenUnion |= u;
    }

    /// <summary>A point's weights with each stack as one projector: the group's weight is its lead's geometry (incidence and
    /// distance; the first member seeing the point when the lead does not), the least edge factor of its members that see the point,
    /// and their summed lumens. Every member gets its group's share, so stacked light adds and the stack hands over to its
    /// neighbours as one projector.</summary>
    private static (double Sum, double Own, double Emix) Collapse(View v, List<View> views, ulong seen, ReadOnlySpan<double> edges,
        ReadOnlySpan<double> geometry, ReadOnlySpan<double> light)
    {
        Span<double> groupWeight = stackalloc double[views.Count];
        Span<double> groupEdge = stackalloc double[views.Count];
        Span<double> groupGeometry = stackalloc double[views.Count];
        Span<double> groupLumens = stackalloc double[views.Count];
        groupEdge.Fill(double.PositiveInfinity);
        groupGeometry.Fill(-1);
        foreach (var j in views)
        {
            if ((seen & (1UL << j.Index)) == 0)
                continue;
            var g = j.Group;
            groupEdge[g] = Math.Min(groupEdge[g], edges[j.Index]);
            groupLumens[g] += j.Lumens;
            if (j.Index == g || groupGeometry[g] < 0)
                groupGeometry[g] = geometry[j.Index];
        }
        double sum = 0;
        for (var g = 0; g < views.Count; g++)
        {
            if (groupGeometry[g] < 0)
                continue;
            groupWeight[g] = Math.Max(groupEdge[g] * groupGeometry[g] * groupLumens[g], 1e-30);
            sum += groupWeight[g];
        }
        double emix = 0;
        foreach (var j in views)
            if ((seen & (1UL << j.Index)) != 0)
                emix += groupWeight[j.Group] * light[j.Index];
        return (sum, groupWeight[v.Group], emix);
    }

    private static OutputBlend Finish(View v, List<View> views, double floorTarget, double target, double brightest, BlendSolveOptions o)
    {
        var n = v.W * v.H;
        var weights = new float[n];
        var pedestal = new float[n];
        var irradiance = new float[n];
        var gain = new float[n];
        var gamma = v.Output.Blend.Gamma;
        var compensate = v.Output.Blend.IntensityCompensation && o.Uniformity > 0;
        int hits = 0, overlap = 0, shadow = 0, maxOverlap = 0;
        var minGain = 1.0;
        for (var i = 0; i < n; i++)
        {
            if (v.Tri[i] < 0)
            {
                weights[i] = 1;
                gain[i] = 1;
                continue;
            }
            hits++;
            if (v.Count[i] >= 2)
                overlap++;
            if (v.Shadowed[i])
                shadow++;
            maxOverlap = Math.Max(maxOverlap, v.Count[i]);
            var g = compensate ? IntensityCompensation.Gain(v.Emix[i], target, o.Uniformity) : 1;
            minGain = Math.Min(minGain, g);
            gain[i] = (float)g;
            irradiance[i] = brightest > 0 ? (float)(v.Emix[i] / brightest) : 0;
            weights[i] = (float)BlendMath.ToSignal(v.Light[i] * g, gamma);
            var lift = (floorTarget - v.Floor[i]) / Math.Max(1, (int)v.Count[i]);
            pedestal[i] = lift > 1e-9 ? (float)BlendMath.ToSignal(lift, gamma) : 0;
        }
        var names = new List<string>();
        for (var j = 0; j < views.Count; j++)
            if (j != v.Index && (v.SeenUnion & (1UL << j)) != 0)
                names.Add(views[j].Output.Name);
        var covered = n > 0 ? hits / (double)n : 0;
        var overlapFraction = hits > 0 ? overlap / (double)hits : 0;
        var shadowFraction = hits > 0 ? shadow / (double)hits : 0;
        var words = new StringBuilder();
        words.Append(CultureInfo.InvariantCulture, $"{v.Output.Name} ({v.Projector.Name}), {v.W}×{v.H} map: ");
        if (hits == 0)
        {
            words.Append("none of its pixels land on a surface — check its pose and the surfaces' visibility.");
        }
        else
        {
            words.Append(CultureInfo.InvariantCulture, $"{covered * 100:0}% of its pixels land on the stage");
            if (names.Count > 0)
                words.Append(CultureInfo.InvariantCulture, $"; {overlapFraction * 100:0}% of those are shared with {JoinNames(names)} (up to {maxOverlap} at once)");
            else
                words.Append("; it overlaps no other projector");
            if (shadow > 0)
                words.Append(CultureInfo.InvariantCulture, $"; {shadowFraction * 100:0}% lie in another projector's shadow, where it lights alone");
            words.Append('.');
            if (compensate)
                words.Append(CultureInfo.InvariantCulture, $" Intensity compensation takes up to {(1 - minGain) * 100:0}% off its brightest areas.");
        }
        var map = new BlendMap(v.Output.Id, v.W, v.H, weights, pedestal);
        return new OutputBlend(v.Output.Id, v.Projector.Id, map, v.Count, irradiance, gain, covered, overlapFraction, shadowFraction,
            maxOverlap, names, words.ToString());
    }

    private static string Summary(List<OutputBlend> results, List<View> views, List<string> problems, BlendSolveOptions o)
    {
        var sb = new StringBuilder();
        var deepest = results.Count > 0 ? results.Max(r => r.MaxOverlap) : 0;
        sb.Append(CultureInfo.InvariantCulture, $"{views.Count} projector{(views.Count == 1 ? "" : "s")} blended in 3D");
        if (deepest > 1)
            sb.Append(CultureInfo.InvariantCulture, $"; the deepest overlap is {deepest}");
        var black = views.Any(v => v.Output.Blend.BlackLevel > 0);
        if (black && deepest > 1)
            sb.Append(CultureInfo.InvariantCulture, $", so the black floor is lifted to {deepest} projectors' black");
        sb.Append('.');
        if (views.Any(v => v.Output.Blend.IntensityCompensation))
            sb.Append(CultureInfo.InvariantCulture, $" Intensity compensation at uniformity {o.Uniformity:0.##}.");
        foreach (var r in results)
            sb.Append(' ').Append(r.Words);
        foreach (var p in problems)
            sb.Append(' ').Append(p);
        return sb.ToString();
    }

    private static string JoinNames(List<string> names) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    private static string Words(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
