using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Vision;

namespace PlayMap.Vision.Pins;

/// <summary>What a pin candidate pairs with on the mesh.</summary>
public enum PinFeatureKind
{
    /// <summary>A vertex where three or more face directions meet (a box corner).</summary>
    Corner,
    /// <summary>A vertex on the mesh's open border where the border turns (the corner of a floor or a sheet).</summary>
    BorderCorner,
    /// <summary>A point on a crease edge (a weaker pin: it fixes the pixel across the edge, not along it).</summary>
    Edge,
}

/// <summary>A proposed calibration pin: the picture's feature (sub-pixel), the mesh point it lights, the welded
/// vertex (−1 for an edge point), how far the approximate pose put them apart, and the rank score (0…1).</summary>
public sealed record PinSuggestion(Vec2d Pixel, Vec3d World, int Vertex, PinFeatureKind Kind, double DistancePx, double Score);

public sealed record PinSuggesterOptions(
    int MaxPins = 12,
    /// <summary>How far (pixels) the approximate pose may be off.</summary>
    double SearchRadiusPx = 12,
    /// <summary>Faces meeting at this angle or more make a crease.</summary>
    double CreaseDegrees = 30,
    int FeatureCandidates = 300,
    /// <summary>Also pair corners with points on crease edges when no mesh corner is near. Off by default: such a
    /// pin fixes the pixel only across the edge, and a picture "corner" on an edge is often a staircase.</summary>
    bool IncludeEdgePins = false)
{
    public static readonly PinSuggesterOptions Default = new();
}

/// <summary>Auto-suggested pins: strong, well-spread picture corners (the smart scan, so picture pixels are
/// projector pixels) paired with the mesh corners and crease edges that the approximate pose projects nearby and
/// that are visible from it (ray cast). A pair ranks high when the corner is strong, the projection close, and
/// no other mesh feature competes for it; one feature per corner, then a greedy pick that keeps the pins spread
/// over the frame, as a pose solve wants. The operator accepts, drags or drops each.</summary>
public sealed class PinSuggester
{
    private readonly IFeatureSuggester _features;
    private readonly PinSuggesterOptions _options;

    public PinSuggester(IFeatureSuggester? features = null, PinSuggesterOptions? options = null)
    {
        _features = features ?? new CornerDetector();
        _options = options ?? PinSuggesterOptions.Default;
    }

    public async Task<IReadOnlyList<PinSuggestion>> SuggestAsync(
        ImageGray8 image, MeshData mesh, Intrinsics intrinsics, LensDistortion lens, Pose approximatePose, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(intrinsics);
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(approximatePose);
        var features = await _features.SuggestAsync(image, _options.FeatureCandidates, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Pair(features, image.Width, image.Height, mesh, intrinsics, lens, approximatePose, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private sealed record Projected(Vec2d Pixel, Vec3d World, int Vertex, PinFeatureKind Kind);

    private List<PinSuggestion> Pair(IReadOnlyList<FeaturePoint> features, int w, int h, MeshData mesh, Intrinsics k, LensDistortion lens, Pose pose, CancellationToken ct)
    {
        var graph = MeshFeatures.Build(mesh, _options.CreaseDegrees);
        var caster = new MeshRaycaster(mesh);
        var visible = new List<Projected>();
        foreach (var (v, kind) in graph.Vertices)
        {
            var world = graph.Position(v);
            if (Visible(world, caster, pose) && CameraModel.Project(world, k, lens, pose) is { } px && CameraModel.InRaster(px, k))
                visible.Add(new Projected(px, world, v, kind));
        }
        ct.ThrowIfCancellationRequested();
        var edges = new List<(Vec2d A, Vec2d B, Vec3d Wa, Vec3d Wb, double Za, double Zb)>();
        foreach (var (a, b) in graph.Edges)
        {
            Vec3d wa = graph.Position(a), wb = graph.Position(b);
            var ca = pose.WorldToCamera(wa);
            var cb = pose.WorldToCamera(wb);
            if (ca.Z <= 0 || cb.Z <= 0)
                continue;
            if (CameraModel.Project(wa, k, lens, pose) is { } pa && CameraModel.Project(wb, k, lens, pose) is { } pb)
                edges.Add((pa, pb, wa, wb, ca.Z, cb.Z));
        }

        var radius = _options.SearchRadiusPx;
        var maxStrength = features.Count == 0 ? 1 : features.Max(f => f.Strength);
        var candidates = new List<PinSuggestion>();
        foreach (var f in features)
        {
            Projected? nearest = null;
            double d1 = double.MaxValue, d2 = double.MaxValue;
            foreach (var p in visible)
            {
                var d = Vec2d.Distance(p.Pixel, f.Pixel);
                if (d < d1)
                {
                    d2 = d1;
                    d1 = d;
                    nearest = p;
                }
                else if (d < d2)
                    d2 = d;
            }
            var strength = Math.Sqrt(Math.Max(f.Strength, 0) / maxStrength);
            if (nearest is not null && d1 <= radius)
            {
                var unique = double.IsFinite(d2) ? Math.Clamp((d2 - d1) / (0.5 * radius), 0, 1) : 1;
                var kindWeight = nearest.Kind == PinFeatureKind.Corner ? 1.0 : 0.85;
                var score = strength * Math.Exp(-(d1 * d1) / (0.25 * radius * radius)) * (0.25 + 0.75 * unique) * kindWeight;
                candidates.Add(new PinSuggestion(f.Pixel, nearest.World, nearest.Vertex, nearest.Kind, d1, score));
                continue;
            }
            if (!_options.IncludeEdgePins)
                continue;
            // No vertex near: the closest crease edge, at the 3D point under the foot of the perpendicular.
            var bestEdge = double.MaxValue;
            PinSuggestion? edgePin = null;
            foreach (var e in edges)
            {
                var ab = e.B - e.A;
                var l2 = ab.LengthSquared;
                if (l2 < 1e-9)
                    continue;
                var t = Math.Clamp(Vec2d.Dot(f.Pixel - e.A, ab) / l2, 0, 1);
                var d = Vec2d.Distance(e.A + ab * t, f.Pixel);
                if (d >= bestEdge || d > radius * 0.5)
                    continue;
                // Perspective-correct: the 3D fraction along the edge for the picture fraction t.
                var s = t * e.Za / (t * e.Za + (1 - t) * e.Zb);
                var world = Vec3d.Lerp(e.Wa, e.Wb, s);
                if (!Visible(world, caster, pose))
                    continue;
                bestEdge = d;
                edgePin = new PinSuggestion(f.Pixel, world, -1, PinFeatureKind.Edge, d, strength * Math.Exp(-(d * d) / (0.25 * radius * radius)) * 0.35);
            }
            if (edgePin is not null)
                candidates.Add(edgePin);
        }

        // One feature per mesh vertex (the best), then a greedy pick that discounts candidates near chosen ones.
        var byVertex = candidates.Where(c => c.Vertex >= 0).GroupBy(c => c.Vertex).Select(g => g.MaxBy(c => c.Score)!)
            .Concat(candidates.Where(c => c.Vertex < 0)).ToList();
        var chosen = new List<PinSuggestion>();
        var spread = 0.1 * Math.Sqrt(w * (double)w + h * (double)h);
        while (chosen.Count < _options.MaxPins && byVertex.Count > 0)
        {
            PinSuggestion? pick = null;
            var pickScore = -1.0;
            foreach (var c in byVertex)
            {
                var near = chosen.Count == 0 ? double.MaxValue : chosen.Min(o => Vec2d.Distance(o.Pixel, c.Pixel));
                var effective = c.Score * Math.Min(1, near / spread);
                if (effective > pickScore)
                {
                    pickScore = effective;
                    pick = c;
                }
            }
            if (pick is null || pickScore <= 0)
                break;
            var taken = pick;
            chosen.Add(taken);
            byVertex.Remove(taken);
            byVertex.RemoveAll(c => Vec2d.Distance(c.Pixel, taken.Pixel) < 1.5);
        }
        return chosen;
    }

    /// <summary>Nothing of the mesh lies between the device and the point (a small margin for the point's own faces).</summary>
    private static bool Visible(Vec3d world, MeshRaycaster caster, Pose pose)
    {
        var to = world - pose.Position;
        var d = to.Length;
        if (d < 1e-9)
            return false;
        return !caster.Occluded(new Ray(pose.Position, to / d), d - Math.Max(1e-3 * d, 1e-3));
    }
}

/// <summary>A mesh's pin-worthy features after welding coincident vertices: corner vertices (three or more face
/// directions), border corners (where the open border turns), and crease edges (faces meeting at the crease
/// angle or more, or the open border).</summary>
internal sealed class MeshFeatures
{
    private readonly Vec3d[] _positions;

    private MeshFeatures(Vec3d[] positions, List<(int, PinFeatureKind)> vertices, List<(int, int)> edges)
    {
        _positions = positions;
        Vertices = vertices;
        Edges = edges;
    }

    public List<(int Vertex, PinFeatureKind Kind)> Vertices { get; }
    public List<(int A, int B)> Edges { get; }

    public Vec3d Position(int v) => _positions[v];

    public static MeshFeatures Build(MeshData mesh, double creaseDegrees)
    {
        // Weld vertices that coincide (to a micrometre): quads exported with their own corners share them here.
        var weld = new int[mesh.VertexCount];
        var map = new Dictionary<(long, long, long), int>();
        var positions = new List<Vec3d>();
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var p = mesh.Position(v);
            var key = ((long)Math.Round(p.X * 1e6), (long)Math.Round(p.Y * 1e6), (long)Math.Round(p.Z * 1e6));
            if (!map.TryGetValue(key, out var id))
            {
                id = positions.Count;
                positions.Add(p);
                map[key] = id;
            }
            weld[v] = id;
        }
        var faceNormals = new Vec3d[mesh.TriangleCount];
        var incident = new List<int>[positions.Count];
        var edgeFaces = new Dictionary<(int, int), List<int>>();
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.Triangle(t);
            int wa = weld[a], wb = weld[b], wc = weld[c];
            faceNormals[t] = Vec3d.Cross(positions[wb] - positions[wa], positions[wc] - positions[wa]).Normalized();
            foreach (var v in (ReadOnlySpan<int>)[wa, wb, wc])
                (incident[v] ??= []).Add(t);
            foreach (var (p, q) in new[] { (wa, wb), (wb, wc), (wc, wa) })
            {
                var e = p < q ? (p, q) : (q, p);
                if (!edgeFaces.TryGetValue(e, out var list))
                    edgeFaces[e] = list = [];
                list.Add(t);
            }
        }
        var cosCrease = Math.Cos(creaseDegrees * Math.PI / 180);
        var edges = new List<(int, int)>();
        var borderNeighbours = new Dictionary<int, List<int>>();
        foreach (var (e, faces) in edgeFaces)
        {
            var border = faces.Count == 1;
            var crease = faces.Count == 2 && Vec3d.Dot(faceNormals[faces[0]], faceNormals[faces[1]]) < cosCrease;
            if (border || crease)
                edges.Add(e);
            if (border)
            {
                Neighbours(e.Item1).Add(e.Item2);
                Neighbours(e.Item2).Add(e.Item1);
            }
        }
        var vertices = new List<(int, PinFeatureKind)>();
        for (var v = 0; v < positions.Count; v++)
        {
            if (incident[v] is null)
                continue;
            // Distinct face directions at the vertex (within the crease angle of each other count as one).
            var directions = new List<Vec3d>();
            foreach (var t in incident[v])
                if (!directions.Any(d => Vec3d.Dot(d, faceNormals[t]) >= cosCrease))
                    directions.Add(faceNormals[t]);
            if (directions.Count >= 3)
            {
                vertices.Add((v, PinFeatureKind.Corner));
                continue;
            }
            if (borderNeighbours.TryGetValue(v, out var nb) && nb.Count == 2)
            {
                var u1 = (positions[nb[0]] - positions[v]).Normalized();
                var u2 = (positions[nb[1]] - positions[v]).Normalized();
                // The border turns here (not a straight run along it): an angle well short of 180°.
                if (Vec3d.Dot(u1, u2) > -cosCrease)
                    vertices.Add((v, directions.Count >= 2 ? PinFeatureKind.Corner : PinFeatureKind.BorderCorner));
            }
        }
        return new MeshFeatures([.. positions], vertices, edges);

        List<int> Neighbours(int v)
        {
            if (!borderNeighbours.TryGetValue(v, out var list))
                borderNeighbours[v] = list = [];
            return list;
        }
    }
}
