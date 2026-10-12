using System.Globalization;
using PlayMap.Core.Calibration;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;

namespace PlayMap.Engine.Scanning;

/// <summary>Scan's decode as Calibration's triangulation reads it. Both modules use one pixel convention —
/// continuous coordinates with (0, 0) the raster's top-left corner, so a pixel's centre is at integer + 0.5 —
/// for the camera grid (Calibration puts camera pixel i at its centre) and for the decoded projector point
/// (the decoder places a column's centre at column + 0.5), so the coordinates pass through unchanged.</summary>
public static class CorrespondenceAdapter
{
    /// <summary>The decode's camera grid with each pixel's projector point; valid where the decoder said so, both
    /// coordinates are finite and the confidence reaches <paramref name="minConfidence"/>. The arrays are the
    /// decode's own (shared, not copied) except the validity mask.</summary>
    public static PixelCorrespondenceMap ToPixelMap(CorrespondenceMap map, float minConfidence = 0)
    {
        ArgumentNullException.ThrowIfNull(map);
        var valid = new bool[map.Width * map.Height];
        for (var k = 0; k < valid.Length; k++)
        {
            valid[k] = (map.Flags[k] & DecodeStatus.Valid) != 0 && map.Confidence[k] >= minConfidence
                && float.IsFinite(map.ProjectorX[k]) && float.IsFinite(map.ProjectorY[k]);
        }
        return new PixelCorrespondenceMap(map.Width, map.Height, map.ProjectorX, map.ProjectorY, valid);
    }
}

/// <summary>The camera a scan looked through, as the model has it before the scan (the prior) or after (solved).
/// Its pose anchors the scan in the world: the camera's frame is where self-calibration puts everything.</summary>
public sealed record ScanCameraView(string Id, string Name, Intrinsics Intrinsics, LensDistortion Lens, Pose Pose, bool Calibrated)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public DeviceView View => new(Intrinsics, Lens, Pose);
}

/// <summary>One projector's capture: the model's projector (its prior optics and pose), the raster the patterns
/// were shown at (the output's), the decode and its quality.</summary>
public sealed record ProjectorCapture(Projector Projector, int Width, int Height, CorrespondenceMap Map, ScanQualityReport Quality)
{
    /// <summary>The prior optics at the raster the patterns were shown at.</summary>
    public Intrinsics Prior => Projector.Intrinsics.Width == Width && Projector.Intrinsics.Height == Height
        ? Projector.Intrinsics
        : Projector.Intrinsics.Scaled(Width, Height);
}

/// <summary>One projector after the scan: optics and pose in the world at the model's raster, how well the
/// pairs fit, and in words what happened.</summary>
public sealed record ScannedProjector(
    string ProjectorId,
    string Name,
    bool Solved,
    Intrinsics Intrinsics,
    LensDistortion Lens,
    Pose Pose,
    double RmsPx,
    int Pairs,
    int Inliers,
    int Quality,
    string Words);

/// <summary>A plane the scan found, offered as a surface: what it looks like (floor, wall, top, ceiling, slope),
/// where it is, and the scan mesh's triangles that lie on it.</summary>
public sealed record PlaneSuggestion(string Name, string Kind, Vec3d Normal, Vec3d Centroid, int Points, double RmsDistance, int[] Triangles);

/// <summary>How a scan is solved: the minimum decode confidence a pair needs, how many pairs at most per
/// projector, the triangulation's residual limit, a known distance (metres between the surfaces seen at two
/// camera pixels) for a metric scale, and a model of the stage to bridge into instead of self-calibrating.</summary>
public sealed record ScanSolveOptions
{
    public float MinConfidence { get; init; } = 0.2f;

    public int MaxPairs { get; init; } = 100_000;

    public double MaxResidualPx { get; init; } = 2.0;

    public Func<Vec2d, Vec2d, double?>? KnownDistance { get; init; }

    /// <summary>The stage's mesh: with a calibrated camera each projector is registered into it (bridged).</summary>
    public MeshRaycaster? Model { get; init; }
}

/// <summary>The solved scan: the camera (pose kept, optics solved), each projector, the merged organised cloud on
/// the camera grid with normals, its grid mesh, the planes offered as surfaces, the scale and how it was found.</summary>
public sealed record ScanSolution(
    ScanCameraView Camera,
    IReadOnlyList<ScannedProjector> Projectors,
    PointCloud? Cloud,
    MeshData? Mesh,
    int Points,
    IReadOnlyList<PlaneSuggestion> Planes,
    double Scale,
    bool Metric,
    string Method,
    string Words);

/// <summary>
/// From decodes to stage content, the same for a real and a simulated scan: per projector the dense pairs; then
/// either self-calibration of the camera and every projector in the camera's frame (scaled by a known distance,
/// else by how far apart the project places the camera and the first projector) carried into the world by the
/// camera's pose — or, when the stage has a model and the camera's pose in it is known, each projector bridged
/// into the model. Then triangulation (camera rays against projector rays, residual-limited), the clouds merged
/// on the camera grid (the best residual wins a pixel), normals toward the camera, the grid mesh, and RANSAC
/// planes as surface suggestions.
/// </summary>
public static class ScanSolver
{
    public static ScanSolution Solve(ScanCameraView camera, IReadOnlyList<ProjectorCapture> captures, ScanSolveOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(captures);
        options ??= new ScanSolveOptions();
        if (captures.Count == 0)
            throw new ArgumentException("A scan needs at least one projector's decode.", nameof(captures));
        var pairs = captures.Select(c => PairsOf(c.Map, options)).ToList();
        ct.ThrowIfCancellationRequested();
        var bridged = options.Model is not null && camera.Calibrated;
        var (solvedCamera, devices, scale, metric, words) = bridged
            ? Bridge(camera, captures, pairs, options.Model!)
            : SelfCalibrate(camera, captures, pairs, options);
        ct.ThrowIfCancellationRequested();

        var cloud = Triangulate(solvedCamera, captures, devices, options, ct);
        var valid = cloud is null ? 0 : cloud.Points.Count(PointCloud.IsValid);
        PointCloud? withNormals = null;
        MeshData? mesh = null;
        var planes = new List<PlaneSuggestion>();
        if (cloud is not null && valid >= 3)
        {
            withNormals = PointCloudOps.EstimateNormals(cloud, solvedCamera.Pose.Position);
            ct.ThrowIfCancellationRequested();
            var grid = GridMesher.Build(withNormals);
            mesh = grid.Mesh.TriangleCount > 0 ? grid.Mesh : null;
            if (mesh is not null)
                planes.AddRange(Planes(withNormals, grid, solvedCamera.Pose.Position, metric || bridged, valid));
        }

        var projectors = new List<ScannedProjector>(captures.Count);
        for (var j = 0; j < captures.Count; j++)
        {
            var c = captures[j];
            var d = devices[j];
            var model = c.Projector.Intrinsics;
            var intrinsics = d.Intrinsics.Width == model.Width && d.Intrinsics.Height == model.Height ? d.Intrinsics : d.Intrinsics.Scaled(model.Width, model.Height);
            projectors.Add(new ScannedProjector(c.Projector.Id, c.Projector.Name, d.Solved, intrinsics, d.Lens, d.Pose, d.RmsPx, pairs[j].Camera.Count, d.Inliers,
                c.Quality.Score, d.Words));
        }
        var solvedCount = projectors.Count(p => p.Solved);
        var summary = string.Create(CultureInfo.InvariantCulture,
            $"{solvedCount} of {projectors.Count} projector{(projectors.Count == 1 ? "" : "s")} solved ({(bridged ? "bridged into the stage's model" : "self-calibrated")}); {valid:N0} points, {mesh?.TriangleCount ?? 0:N0} triangles, {planes.Count} plane{(planes.Count == 1 ? "" : "s")}; {words}");
        return new ScanSolution(solvedCamera, projectors, withNormals ?? cloud, mesh, valid, planes, scale, metric || bridged,
            bridged ? "bridged" : "self-calibration", summary);
    }

    /// <summary>A solved device at the raster its patterns were shown at.</summary>
    private sealed record SolvedDevice(bool Solved, Intrinsics Intrinsics, LensDistortion Lens, Pose Pose, double RmsPx, int Inliers, string Words)
    {
        public DeviceView View => new(Intrinsics, Lens, Pose);
    }

    private sealed record PairSet(List<Vec2d> Camera, List<Vec2d> Projector);

    /// <summary>The decode's trusted pairs, thinned evenly (a stride both ways) to at most the option's count.</summary>
    private static PairSet PairsOf(CorrespondenceMap map, ScanSolveOptions options)
    {
        var valid = 0;
        for (var k = 0; k < map.Flags.Length; k++)
        {
            if ((map.Flags[k] & DecodeStatus.Valid) != 0 && map.Confidence[k] >= options.MinConfidence)
                valid++;
        }
        var stride = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(valid / (double)Math.Max(1, options.MaxPairs))));
        var list = map.Pairs(options.MinConfidence, stride);
        return new PairSet(list.Select(p => p.Camera).ToList(), list.Select(p => p.Projector).ToList());
    }

    private static (ScanCameraView Camera, SolvedDevice[] Devices, double Scale, bool Metric, string Words) SelfCalibrate(
        ScanCameraView camera, IReadOnlyList<ProjectorCapture> captures, List<PairSet> pairs, ScanSolveOptions options)
    {
        var observations = captures.Select((c, j) => new ProjectorObservations(c.Prior, c.Projector.Lens, pairs[j].Camera, pairs[j].Projector, c.Projector.Name)).ToList();
        // A calibrated camera keeps its focal length: when the camera and the projectors all aim at the object (as an
        // operator aims them) their optical axes nearly meet and two views cannot tell focal length from depth.
        var result = SelfCalibration.Solve(camera.Intrinsics, camera.Lens, observations,
            SelfCalibrationOptions.Default with { LockCameraFocal = camera.Calibrated });
        var (scaled, scaleWords) = Scale(result, camera, captures, options);
        // The camera's frame carried into the world by the camera's pose (its prior: the scan does not move it).
        var world = new Similarity3(camera.Pose.CameraToWorld, camera.Pose.Position, 1);
        var devices = scaled.Projectors.Select(p => new SolvedDevice(p.Solved && p.InlierCount >= 8, p.Intrinsics, p.Lens, world.Apply(p.Pose), p.RmsPx, p.InlierCount, p.Words)).ToArray();
        var solvedCamera = camera with { Intrinsics = scaled.Camera, Lens = scaled.CameraLens };
        return (solvedCamera, devices, scaled.Scale, scaled.Metric, scaleWords);
    }

    /// <summary>The scale: one known distance makes the scan metric; without one, the distance the project puts
    /// between the camera and the first solved projector stands in (the words say so); without that, units of the
    /// first baseline.</summary>
    private static (SelfCalibrationResult Result, string Words) Scale(SelfCalibrationResult result, ScanCameraView camera,
        IReadOnlyList<ProjectorCapture> captures, ScanSolveOptions options)
    {
        var first = result.Projectors.Select((p, j) => (p, j)).FirstOrDefault(x => x.p.Solved);
        if (first.p is null)
            return (result, "nothing to scale: no projector solved");
        if (options.KnownDistance is { } known && KnownScale(first.p, known) is { } fromKnown)
        {
            var scaled = result.Scaled(fromKnown.Factor, metric: true);
            return (scaled, fromKnown.Samples == 1
                ? "metric from one known distance"
                : string.Create(CultureInfo.InvariantCulture, $"metric from {fromKnown.Samples} known distances (their median)"));
        }
        var baseline = Vec3d.Distance(captures[first.j].Projector.Pose.Position, camera.Pose.Position);
        var solvedBaseline = first.p.Pose.Position.Length;
        if (baseline > 0.05 && solvedBaseline > 0)
        {
            var scaled = result.Scaled(baseline / solvedBaseline, metric: false);
            return (scaled, string.Create(CultureInfo.InvariantCulture,
                $"scaled by where the project places the camera and {captures[first.j].Projector.Name} ({baseline:0.###} m apart) — measure one distance for a true metric scale"));
        }
        return (result, "not to scale: place the camera and the projectors in the project, or give one known distance");
    }

    /// <summary>The scale from known distances: for seeded pairs of inlier pixels far apart on the camera's picture
    /// (a third of the lit area's extent or more), the known distance over the reconstructed one; the median of
    /// those the source knows. One reconstructed point is noisy at an edge; the median of many is not. Null when the
    /// source knows none.</summary>
    private static (double Factor, int Samples)? KnownScale(ProjectorCalibration p, Func<Vec2d, Vec2d, double?> known)
    {
        var good = Enumerable.Range(0, p.CameraPixels.Count).Where(i => p.Inliers[i] && PointCloud.IsValid(p.Points[i])).ToArray();
        if (good.Length < 2)
            return null;
        var minX = good.Min(i => p.CameraPixels[i].X);
        var maxX = good.Max(i => p.CameraPixels[i].X);
        var minY = good.Min(i => p.CameraPixels[i].Y);
        var maxY = good.Max(i => p.CameraPixels[i].Y);
        var apart = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY)) / 3;
        var rng = new Pcg32(11);
        var ratios = new List<double>();
        for (var attempt = 0; attempt < 4000 && ratios.Count < 200; attempt++)
        {
            var a = good[rng.NextInt(good.Length)];
            var b = good[rng.NextInt(good.Length)];
            if (Vec2d.Distance(p.CameraPixels[a], p.CameraPixels[b]) < apart)
                continue;
            var reconstructed = Vec3d.Distance(p.Points[a], p.Points[b]);
            if (reconstructed > 0 && known(p.CameraPixels[a], p.CameraPixels[b]) is { } metres && metres > 0)
                ratios.Add(metres / reconstructed);
        }
        if (ratios.Count == 0)
            return null;
        ratios.Sort();
        return (ratios[ratios.Count / 2], ratios.Count);
    }

    private static (ScanCameraView Camera, SolvedDevice[] Devices, double Scale, bool Metric, string Words) Bridge(
        ScanCameraView camera, IReadOnlyList<ProjectorCapture> captures, List<PairSet> pairs, MeshRaycaster model)
    {
        var devices = new SolvedDevice[captures.Count];
        for (var j = 0; j < captures.Count; j++)
        {
            var c = captures[j];
            var r = BridgedRegistration.SolveProjector(model, camera.View, pairs[j].Camera, pairs[j].Projector, c.Prior, c.Projector.Lens);
            var s = r.Projector;
            var inliers = s.Inliers.Count(x => x);
            devices[j] = new SolvedDevice(inliers >= 8 && double.IsFinite(s.RmsPx), s.Intrinsics, s.Lens, s.Pose, s.RmsPx, inliers, r.Words);
        }
        return (camera, devices, 1, true, "in the stage model's frame and scale");
    }

    /// <summary>Every solved projector's organised cloud on the camera grid, merged: per camera pixel the point
    /// with the smallest reprojection residual.</summary>
    private static PointCloud? Triangulate(ScanCameraView camera, IReadOnlyList<ProjectorCapture> captures, SolvedDevice[] devices,
        ScanSolveOptions options, CancellationToken ct)
    {
        Vec3d[]? points = null;
        float[]? best = null;
        int w = 0, h = 0;
        for (var j = 0; j < captures.Count; j++)
        {
            if (!devices[j].Solved)
                continue;
            ct.ThrowIfCancellationRequested();
            var map = CorrespondenceAdapter.ToPixelMap(captures[j].Map, options.MinConfidence);
            var organised = Triangulation.Organised(camera.View, devices[j].View, map, options.MaxResidualPx);
            if (points is null)
            {
                w = map.Width;
                h = map.Height;
                points = organised.Cloud.Points.ToArray();
                best = organised.ResidualsPx.ToArray();
                continue;
            }
            if (map.Width != w || map.Height != h)
                continue;
            for (var k = 0; k < points.Length; k++)
            {
                var r = organised.ResidualsPx[k];
                if (float.IsFinite(r) && !(best![k] <= r))
                {
                    points[k] = organised.Cloud.Points[k];
                    best[k] = r;
                }
            }
        }
        return points is null ? null : new PointCloud(points, gridWidth: w, gridHeight: h);
    }

    /// <summary>RANSAC planes on the cloud, named by how they lie (world +Y up): the lowest level one is the floor;
    /// a level one above the camera a ceiling, below it a top; an upright one a wall; anything else a slope.</summary>
    private static List<PlaneSuggestion> Planes(PointCloud cloud, GridMesh grid, Vec3d sensor, bool metric, int valid)
    {
        var (min, max) = cloud.Bounds();
        var diagonal = Vec3d.Distance(min, max);
        var options = new PlaneDetectionOptions
        {
            DistanceThreshold = metric ? Math.Max(0.005, diagonal * 0.002) : diagonal * 0.004,
            MinInliers = Math.Max(100, valid / 100),
            MaxPlanes = 8,
        };
        var found = PlaneDetector.Detect(cloud, options);
        var cellOfVertex = grid.VertexCells;
        var indices = grid.Mesh.Indices;
        var kinds = new List<(DetectedPlane Plane, string Kind)>();
        const double LevelCos = 0.94;
        const double UprightSin = 0.34;
        foreach (var p in found)
        {
            var ny = Math.Abs(p.Plane.Normal.Y);
            var kind = ny >= LevelCos ? (p.Centroid.Y > sensor.Y ? "ceiling" : "level") : ny <= UprightSin ? "wall" : "slope";
            kinds.Add((p, kind));
        }
        var lowest = kinds.Where(k => k.Kind == "level").Select(k => k.Plane.Centroid.Y).DefaultIfEmpty(double.NaN).Min();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<PlaneSuggestion>(kinds.Count);
        foreach (var (plane, k) in kinds)
        {
            var kind = k == "level" ? (Math.Abs(plane.Centroid.Y - lowest) < 1e-9 ? "floor" : "top") : k;
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
            var label = char.ToUpperInvariant(kind[0]) + kind[1..];
            var name = kind == "floor" ? label : label + " " + counts[kind].ToString(CultureInfo.InvariantCulture);
            var on = new bool[cloud.Count];
            foreach (var i in plane.Inliers)
                on[i] = true;
            var triangles = new List<int>();
            for (var t = 0; t < indices.Length / 3; t++)
            {
                if (on[cellOfVertex[indices[3 * t]]] && on[cellOfVertex[indices[3 * t + 1]]] && on[cellOfVertex[indices[3 * t + 2]]])
                    triangles.Add(t);
            }
            if (triangles.Count == 0)
                continue;
            var normal = plane.Plane.Normal;
            if (Vec3d.Dot(normal, sensor - plane.Centroid) < 0)
                normal = -normal;
            result.Add(new PlaneSuggestion(name, kind, normal, plane.Centroid, plane.Count, plane.RmsDistance, [.. triangles]));
        }
        return result;
    }
}
