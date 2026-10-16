using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using PlayMap.Core.Calibration;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using PlayMap.Core.Scan.MultiView;
using PlayMap.Core.Vision;
using PlayMap.Vision.Surfaces;

namespace PlayMap.Engine.Scanning;

/// <summary>
/// One place a camera stood in a scan: its id within the scan ("v1", "v2", …), the camera rig and the optics it had then, the
/// projectors it decoded (their decodes, kept in memory and in the scan's folder), how its pictures were paired ("tag",
/// "timing", or "stream" for a camera that streams frames) and the exposure path. <see cref="Pose"/> and <see cref="Baseline"/>
/// are where the last solve put the camera in the stage's frame and its mean distance to the projectors it solved there — the
/// anchor a later solve that holds no projector keeps the scan's frame and scale by.
/// </summary>
public sealed record ScanViewData(
    string Id,
    string CameraId,
    string CameraName,
    Intrinsics Prior,
    LensDistortion Lens,
    bool Calibrated,
    ImmutableArray<string> ProjectorIds,
    string Pairing,
    string Exposure,
    long CreatedUnixMs)
{
    /// <summary>Each projector's decode from this place (not in view.json: each is its own file).</summary>
    [JsonIgnore]
    public ImmutableDictionary<string, CorrespondenceMap> Maps { get; init; } = ImmutableDictionary<string, CorrespondenceMap>.Empty;

    public Pose? Pose { get; init; }

    /// <summary>The camera's mean distance to the projectors the last solve placed from it (stage units; 0 before a solve).</summary>
    public double Baseline { get; init; }

    /// <summary>The camera residual of this view in the last solve (pixels; 0 before a multi-view solve).</summary>
    public double RmsPx { get; init; }

    /// <summary>The tracks this view observed in the last solve.</summary>
    public int Tracks { get; init; }

    /// <summary>"done" when the last solve used it; "left out" with <see cref="Words"/> when it could not.</summary>
    public string State { get; init; } = "done";

    public string Words { get; init; } = "";
}

/// <summary>A multi-view solve and everything made from it, in the stage's frame: the solve, the projectors held, each
/// projector's dense depth and coverage, the merged stage mesh, the regions across projectors and each view's influence.</summary>
public sealed record MultiViewOutcome(
    MultiViewResult Result,
    ImmutableArray<string> Held,
    IReadOnlyList<ProjectorDepthMap> Depth,
    IReadOnlyList<CoverageMap> Coverage,
    MergedMesh Merged,
    ImmutableArray<MultiProjectorRegion> Regions,
    ImmutableArray<ViewInfluence> Influence)
{
    /// <summary>The views the solve used, in the scan's order.</summary>
    public ImmutableArray<string> ViewIds => [.. Result.Views.Select(v => v.Id)];

    /// <summary>The view whose removal halves the residual or better, when the residual is worth a look (≥ 0.1 px): the one that
    /// disagrees with the rest (the camera moved within it), named by the numbers alone; null when none stands out.</summary>
    public string? Disagreeing =>
        Result.RmsPx >= 0.1 && Influence.Where(i => double.IsFinite(i.RmsWithoutPx)).MaxBy(i => i.Gain) is { } worst && worst.Gain >= 0.5 * worst.RmsWithPx
            ? worst.ViewId
            : null;

    public ProjectorDepthMap? DepthOf(string projectorId) => Depth.FirstOrDefault(d => d.ProjectorId == projectorId);
}

/// <summary>
/// The engine's side of multi-view scanning over Core's solver (<see cref="MultiViewCalibration"/>): a scan's views become the
/// solve's input (projector priors from the model at the raster the patterns were shown at, its locks honoured, a held
/// projector at its stage pose; camera priors from the rig as it was at each view), the result is carried onto the stage —
/// with no projector held, by the anchor view's camera pose and baseline from the scan's last solve, so the frame and scale do
/// not jump as views are added — and from it come each projector's dense depth, coverage, the merged mesh and the regions
/// across projectors, and the <see cref="ScanSolution"/> SCAN APPLY reads.
/// </summary>
public static class MultiViewScan
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A region of fewer depth cells than this is not offered (a stray patch at a depth edge).</summary>
    public const int MinRegionCells = 60;

    /// <summary>Solves every view together (fresh), or — with <paramref name="drop"/> — drops a view and solves the rest warm from
    /// the previous solve. Views already dropped take no part and stay dropped. <paramref name="anchorMetric"/> says whether the anchor view's pose and baseline are metric (the scan's
    /// last solve was). Answers the outcome and the views with their places, residuals and states written back.</summary>
    public static (MultiViewOutcome Outcome, ImmutableArray<ScanViewData> Views) Solve(IReadOnlyList<ScanViewData> views, Project project,
        IReadOnlyCollection<string> hold, bool anchorMetric, (MultiViewOutcome Previous, string ViewId)? drop, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(hold);
        // A dropped view stays in the scan's folder (its decodes are kept) but takes no part.
        var active = views.Where(v => v.State != "dropped").ToList();
        var (input, inverses) = Input(active, project, hold);
        if (input.Projectors.IsDefaultOrEmpty)
            throw new InvalidOperationException("none of the scan's projectors is in the project any more");
        MultiViewResult result;
        if (drop is { } d)
        {
            // The warm drop keeps the previous frame and its scale (the root view stays where it was).
            result = MultiViewCalibration.DropView(input, d.Previous.Result, d.ViewId, null, ct);
        }
        else
        {
            result = MultiViewCalibration.Solve(input, null, ct);
            if (result.Solved && hold.Count == 0 && Anchor(active, result) is { } toStage)
                result = result.Transformed(toStage, anchorMetric);
        }
        if (!result.Solved)
            throw new InvalidOperationException("the views could not be solved together — " + result.Words);
        var solvedInput = drop is { } dd ? input.WithoutView(dd.ViewId) : input;
        var influence = result.Views.Length >= 2 ? MultiViewCalibration.Influence(solvedInput, result, null, ct) : [];
        var depth = new List<ProjectorDepthMap>();
        var coverage = new List<CoverageMap>();
        foreach (var p in result.Projectors)
        {
            ct.ThrowIfCancellationRequested();
            var seen = result.Views.Where(v => inverses.ContainsKey((v.Id, p.Id)))
                .Select(v => new DepthView(v.Id, result.CameraOf(v.Id), inverses[(v.Id, p.Id)])).ToList();
            if (seen.Count == 0)
                continue;
            var stride = Math.Max(2, (int)Math.Ceiling(p.Intrinsics.Width / 512.0));
            depth.Add(ProjectorDepth.Build(p, seen, new DepthOptions { Stride = stride }, ct));
            coverage.Add(CoverageMap.Build(p.Id, [.. seen.Select(s => s.Inverse.Map)]));
        }
        var merged = ScanMerge.Merge(depth);
        var regions = ProjectorRegions.Build(depth);
        var outcome = new MultiViewOutcome(result, [.. hold.Order(StringComparer.Ordinal)], depth, coverage, merged, regions, influence);
        return (outcome, WriteBack(views, result, drop?.ViewId));
    }

    /// <summary>The solve's input from a scan's views: the projectors every view decoded that the project still has (prior optics at
    /// the decoded raster, lens, locks, stage pose, held or not), one rig per camera (its prior from the first view that used it),
    /// each view's dense pairs to start from, and the tracks; with each (view, projector) decode inverted.</summary>
    public static (MultiViewInput Input, Dictionary<(string View, string Projector), InverseCorrespondence> Inverses) Input(
        IReadOnlyList<ScanViewData> views, Project project, IReadOnlyCollection<string> hold)
    {
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(hold);
        var rasters = new Dictionary<string, (int W, int H)>(StringComparer.Ordinal);
        foreach (var v in views)
        {
            foreach (var pj in v.ProjectorIds)
            {
                if (v.Maps.TryGetValue(pj, out var map) && !rasters.ContainsKey(pj))
                    rasters[pj] = (map.ProjectorWidth, map.ProjectorHeight);
            }
        }
        var projectors = ImmutableArray.CreateBuilder<MultiViewProjector>();
        foreach (var (id, raster) in rasters.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (project.FindProjector(id) is not { } pj)
                continue;
            var prior = pj.Intrinsics.Width == raster.W && pj.Intrinsics.Height == raster.H ? pj.Intrinsics : pj.Intrinsics.Scaled(raster.W, raster.H);
            projectors.Add(new MultiViewProjector(id, prior, pj.Lens, pj.Locks) { Stage = pj.Pose, Held = hold.Contains(id, StringComparer.Ordinal) });
        }
        var usable = projectors.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var rigs = new Dictionary<string, MultiViewRig>(StringComparer.Ordinal);
        var inverses = new Dictionary<(string, string), InverseCorrespondence>();
        var mvViews = ImmutableArray.CreateBuilder<MultiViewView>();
        var decodes = new List<ViewDecode>();
        foreach (var v in views)
        {
            if (!rigs.ContainsKey(v.CameraId))
                rigs[v.CameraId] = new MultiViewRig(v.CameraId, v.Prior, v.Lens, v.Calibrated);
            var pairs = ImmutableDictionary.CreateBuilder<string, IReadOnlyList<CameraProjectorPair>>(StringComparer.Ordinal);
            foreach (var (pj, map) in v.Maps.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (!usable.Contains(pj) || map.ProjectorWidth != rasters[pj].W || map.ProjectorHeight != rasters[pj].H)
                    continue;
                var inverse = new InverseCorrespondence(map);
                inverses[(v.Id, pj)] = inverse;
                decodes.Add(new ViewDecode(v.Id, pj, inverse));
                // About forty thousand pairs per decode start a view well; a bigger camera takes every n-th pixel.
                var stride = Math.Max(2, (int)Math.Ceiling(Math.Sqrt(Math.Max(1, map.Stats.Valid) / 40_000.0)));
                pairs[pj] = map.Pairs(minConfidence: 0.3f, stride: stride);
            }
            mvViews.Add(new MultiViewView(v.Id, v.CameraId) { Pairs = pairs.ToImmutable() });
        }
        var input = new MultiViewInput([.. rigs.Values.OrderBy(r => r.Id, StringComparer.Ordinal)], projectors.ToImmutable(), mvViews.ToImmutable(),
            TrackSet.Build(decodes));
        return (input, inverses);
    }

    /// <summary>The similarity that carries a fresh solve onto the stage: the first view of the scan the solve used that has a place
    /// from the scan's last solve keeps that place (its camera's orientation and centre) and its baseline (the mean distance to the
    /// projectors it solved). Null when no view has one.</summary>
    public static Similarity3? Anchor(IReadOnlyList<ScanViewData> views, MultiViewResult result)
    {
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(result);
        foreach (var v in views)
        {
            if (v.Pose is not { } stage || !(v.Baseline > 0) || result.Views.FirstOrDefault(x => x.Id == v.Id) is not { } solved)
                continue;
            var near = result.Projectors.Where(p => v.ProjectorIds.Contains(p.Id, StringComparer.Ordinal)).ToList();
            if (near.Count == 0)
                continue;
            var baseline = near.Average(p => (p.Pose.Position - solved.Pose.Position).Length);
            if (!(baseline > 0))
                continue;
            var rotation = (stage.CameraToWorld * solved.Pose.CameraToWorld.Transposed()).NearestRotation();
            var scale = v.Baseline / baseline;
            var translation = stage.Position - rotation * solved.Pose.Position * scale;
            return new Similarity3(rotation, translation, scale);
        }
        return null;
    }

    /// <summary>The views with what the solve says of each written back: its place and baseline, residual and tracks, or "left out"
    /// and why; a dropped view is "dropped".</summary>
    private static ImmutableArray<ScanViewData> WriteBack(IReadOnlyList<ScanViewData> views, MultiViewResult result, string? dropped)
    {
        var list = ImmutableArray.CreateBuilder<ScanViewData>();
        foreach (var v in views)
        {
            if (v.Id == dropped)
            {
                list.Add(v with { State = "dropped", Words = "dropped by SCAN DROP" });
                continue;
            }
            if (result.Views.FirstOrDefault(x => x.Id == v.Id) is { } s)
            {
                var near = result.Projectors.Where(p => v.ProjectorIds.Contains(p.Id, StringComparer.Ordinal)).ToList();
                var baseline = near.Count > 0 ? near.Average(p => (p.Pose.Position - s.Pose.Position).Length) : 0;
                list.Add(v with { Pose = s.Pose, Baseline = baseline, RmsPx = Math.Round(s.RmsPx, 4), Tracks = s.Observations, State = "done", Words = "" });
                continue;
            }
            var why = result.LeftOut.FirstOrDefault(l => l.ViewId == v.Id)?.Words ?? "the solve did not use it";
            list.Add(v with { State = v.State == "dropped" ? "dropped" : "left out", Words = v.State == "dropped" ? v.Words : why });
        }
        return list.ToImmutable();
    }

    /// <summary>The solution SCAN APPLY reads: the anchor view's camera (the rig's solved optics at that view), every projector of
    /// the scan (solved at the model's raster, or not and why), the merged mesh, the planar regions as plane suggestions.</summary>
    public static ScanSolution ToSolution(MultiViewOutcome outcome, IReadOnlyList<ScanViewData> views, Project project, bool metric)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(project);
        var r = outcome.Result;
        var root = r.Views[0];
        var rig = r.Rig(root.RigId);
        var rootView = views.First(v => v.Id == root.Id);
        var camera = new ScanCameraView(rig.Id, rootView.CameraName, rig.Intrinsics, rig.Lens, root.Pose, rootView.Calibrated);
        var projectorIds = views.Where(v => v.State != "dropped").SelectMany(v => v.ProjectorIds).Distinct(StringComparer.Ordinal).ToList();
        var scanned = new List<ScannedProjector>();
        foreach (var id in projectorIds)
        {
            var model = project.FindProjector(id);
            var name = model?.Name ?? id;
            if (model is null || r.Projectors.FirstOrDefault(p => p.Id == id) is not { } solved)
            {
                scanned.Add(new ScannedProjector(id, name, false, model?.Intrinsics ?? Intrinsics.FromThrowRatio(1920, 1080, 1.5), model?.Lens ?? LensDistortion.None,
                    model?.Pose ?? Pose.Identity, 0, 0, 0, 0, model is null ? "no longer in the project" : "no view placed it with another — " + r.Words));
                continue;
            }
            var k = solved.Intrinsics.Width == model.Intrinsics.Width && solved.Intrinsics.Height == model.Intrinsics.Height
                ? solved.Intrinsics : solved.Intrinsics.Scaled(model.Intrinsics.Width, model.Intrinsics.Height);
            var decoded = DecodedShare(views, id);
            scanned.Add(new ScannedProjector(id, name, true, k, solved.Lens, solved.Pose, solved.RmsPx, solved.Tracks, solved.Inliers,
                (int)Math.Round(decoded * 100), string.Create(Inv,
                    $"{(solved.Held ? "held at its stage pose" : "solved")} from {r.Views.Count(v => v.RmsByProjector.ContainsKey(id))} view(s): {solved.RmsPx:0.00} px over {solved.Inliers:N0} of {solved.Tracks:N0} tracks")));
        }
        var planes = Planes(outcome);
        var points = outcome.Depth.Sum(d => d.Valid);
        return new ScanSolution(camera, scanned, null, outcome.Merged.Mesh, points, planes, 1, metric, "multi-view", Words(outcome))
        {
            VertexCells = outcome.Merged.VertexCells,
        };
    }

    /// <summary>The share of the scanned raster the views decoded, averaged over the views that saw the projector (0–1).</summary>
    public static double DecodedShare(IReadOnlyList<ScanViewData> views, string projectorId)
    {
        ArgumentNullException.ThrowIfNull(views);
        var shares = views.Where(v => v.State == "done" && v.Maps.ContainsKey(projectorId)).Select(v => v.Maps[projectorId].Stats.DecodedFraction).ToList();
        return shares.Count > 0 ? shares.Average() : 0;
    }

    /// <summary>The solve in words: views, residual, coverage per projector, what was left out, the view to drop.</summary>
    public static string Words(MultiViewOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var r = outcome.Result;
        var parts = new List<string>
        {
            string.Create(Inv, $"{r.Views.Length} view{(r.Views.Length == 1 ? "" : "s")} solved together: {r.RmsPx:0.00} px RMS over {r.Tracks.Length:N0} tracks"),
        };
        if (outcome.Coverage.Count > 0)
            parts.Add("seen " + string.Join(", ", outcome.Coverage.Select(c => string.Create(Inv, $"{c.ProjectorId} {c.Seen * 100:0} % ({c.Twice * 100:0} % twice)"))));
        if (!outcome.Held.IsDefaultOrEmpty)
            parts.Add("held at their stage poses: " + string.Join(", ", outcome.Held));
        if (!r.LeftOut.IsDefaultOrEmpty)
            parts.Add(string.Join("; ", r.LeftOut.Select(l => l.Words)));
        if (outcome.Disagreeing is { } bad)
            parts.Add($"view {bad} disagrees with the others — SCAN DROP it");
        return string.Join(". ", parts) + ".";
    }

    /// <summary>A planar region as a plane suggestion: its normal, centroid and the merged mesh's triangles on it.</summary>
    private static List<PlaneSuggestion> Planes(MultiViewOutcome outcome)
    {
        var planes = new List<PlaneSuggestion>();
        foreach (var region in outcome.Regions.Where(r => r.Planar && Cells(r) >= MinRegionCells))
        {
            var faces = Faces(outcome, region, null);
            if (faces.Length == 0)
                continue;
            var kind = Kind(region.Normal);
            planes.Add(new PlaneSuggestion(char.ToUpperInvariant(kind[0]) + kind[1..] + " " + region.Id, kind, region.Normal, region.Centroid, Cells(region),
                region.RmsDistance, faces));
        }
        return planes;
    }

    /// <summary>The region's depth cells over every projector.</summary>
    public static int Cells(MultiProjectorRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        return region.Parts.Sum(p => p.Cells.Length);
    }

    /// <summary>What a plane is by its normal in a +Y-up world: floor, ceiling, wall, or slope.</summary>
    public static string Kind(Vec3d normal)
    {
        var up = Math.Abs(normal.Normalized().Y);
        return up > 0.9 ? (normal.Y > 0 ? "floor" : "ceiling") : up < 0.3 ? "wall" : "slope";
    }

    /// <summary>The merged mesh's triangles on a region: those whose three vertices came from the region's cells. With
    /// <paramref name="on"/>, only what that projector lights of it: the triangles whose centre that projector sees on a cell of its
    /// own part of the region (from its own grid or another projector's, whichever the merge kept there).</summary>
    public static int[] Faces(MultiViewOutcome outcome, MultiProjectorRegion region, string? on)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(region);
        var merged = outcome.Merged;
        var mesh = merged.Mesh;
        var cellsByProjector = region.Parts.ToDictionary(p => p.ProjectorId, p => p.Cells.ToHashSet(), StringComparer.Ordinal);
        bool InRegion(int vertex)
        {
            var pj = merged.ProjectorIds[merged.VertexProjector[vertex]];
            return cellsByProjector.TryGetValue(pj, out var cells) && cells.Contains(merged.VertexCells[vertex]);
        }
        ProjectorDepthMap? onMap = null;
        HashSet<int>? onCells = null;
        DeviceView? onDevice = null;
        if (on is not null)
        {
            onMap = outcome.DepthOf(on);
            onCells = cellsByProjector.GetValueOrDefault(on);
            onDevice = outcome.Result.Projectors.FirstOrDefault(p => p.Id == on)?.View;
            if (onMap is null || onCells is null || onDevice is null)
                return [];
        }
        var faces = new List<int>();
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.Triangle(t);
            if (!InRegion(a) || !InRegion(b) || !InRegion(c))
                continue;
            if (onMap is not null && !LitBy(onMap, onCells!, onDevice!, (mesh.Position(a) + mesh.Position(b) + mesh.Position(c)) / 3))
                continue;
            faces.Add(t);
        }
        return [.. faces];
    }

    /// <summary>Whether a projector lights a point on its part of a region: the point projects into its raster onto a cell of the part
    /// whose own depth agrees (within 3 σ, or 2 % of the depth), so a surface behind the lit one is not counted.</summary>
    private static bool LitBy(ProjectorDepthMap map, HashSet<int> cells, DeviceView device, Vec3d point)
    {
        if (device.Project(point) is not { } q)
            return false;
        var i = (int)Math.Floor(q.X / map.Stride);
        var j = (int)Math.Floor(q.Y / map.Stride);
        if (i < 0 || j < 0 || i >= map.Width || j >= map.Height)
            return false;
        var cell = j * map.Width + i;
        if (!cells.Contains(cell) || !PointCloud.IsValid(map.Cloud.Points[cell]))
            return false;
        var there = (map.Cloud.Points[cell] - map.Origin).Length;
        var here = (point - map.Origin).Length;
        var tolerance = Math.Max(3 * (float.IsFinite(map.Sigma[cell]) ? map.Sigma[cell] : 0), 0.02 * there);
        return Math.Abs(there - here) <= tolerance;
    }

    /// <summary>The region at a point of a projector's smart scan (0–1 across and down its raster): the region whose part on that
    /// projector holds the depth cell there; null where it lights nothing the regions hold.</summary>
    public static MultiProjectorRegion? RegionAt(MultiViewOutcome outcome, string projectorId, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.DepthOf(projectorId) is not { } map)
            return null;
        var k = outcome.Result.Projectors.First(p => p.Id == projectorId).Intrinsics;
        var i = Math.Clamp((int)Math.Floor(x * k.Width / map.Stride), 0, map.Width - 1);
        var j = Math.Clamp((int)Math.Floor(y * k.Height / map.Stride), 0, map.Height - 1);
        var cell = j * map.Width + i;
        return outcome.Regions.FirstOrDefault(r => r.On(projectorId) is { } part && Array.BinarySearch(part.Cells, cell) >= 0);
    }

    /// <summary>The regions worth offering, as STATE shows them: each placed on the smart scan of its first projector (0–1, the 2nd to
    /// the 98th percentile of its cells each way), named from its own geometry (floor, wall, box face, curved surface…), with every
    /// projector that lights it.</summary>
    public static IReadOnlyList<ScanRegion> Regions(MultiViewOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var named = Proposals(outcome);
        var list = new List<ScanRegion>();
        foreach (var region in outcome.Regions.Where(r => Cells(r) >= MinRegionCells))
        {
            var part = region.Parts.MaxBy(p => p.Cells.Length)!;
            if (outcome.DepthOf(part.ProjectorId) is not { } map)
                continue;
            var xs = part.Cells.Select(c => (c % map.Width + 0.5) / map.Width).Order().ToList();
            var ys = part.Cells.Select(c => (c / map.Width + 0.5) / map.Height).Order().ToList();
            double At(List<double> v, double f) => v[(int)Math.Clamp(Math.Round(f * (v.Count - 1)), 0, v.Count - 1)];
            var (x0, x1, y0, y1) = (At(xs, 0.02), At(xs, 0.98), At(ys, 0.02), At(ys, 0.98));
            var proposal = named.GetValueOrDefault(region.Id);
            var label = proposal?.Name is { Length: > 0 } n ? n : region.Planar ? Kind(region.Normal) : "curved surface";
            var tags = proposal?.Tags ?? [region.Planar ? "plane" : "curved"];
            var mask = Mask(map, part.Cells, label);
            list.Add(new ScanRegion(region.Id, label, tags, Math.Round(x0, 4), Math.Round(y0, 4), Math.Round(Math.Max(x1 - x0, 1e-4), 4),
                Math.Round(Math.Max(y1 - y0, 1e-4), 4), Math.Round(Math.Clamp(proposal?.Confidence ?? 0.5, 0, 1), 3), mask)
            {
                Projector = part.ProjectorId,
                Projectors = region.Projectors,
            });
        }
        return list;
    }

    /// <summary>Each region named and judged from its largest part's own cloud (the projector's depth grid): the same judgement a
    /// single-view scan's regions get.</summary>
    public static Dictionary<string, SurfaceProposal> Proposals(MultiViewOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var named = new Dictionary<string, SurfaceProposal>(StringComparer.Ordinal);
        var regions = outcome.Regions.Where(r => Cells(r) >= MinRegionCells).ToList();
        foreach (var group in regions.GroupBy(r => r.Parts.MaxBy(p => p.Cells.Length)!.ProjectorId))
        {
            if (outcome.DepthOf(group.Key) is not { } map)
                continue;
            var list = group.ToList();
            var masks = list.Select(r => Mask(map, r.On(group.Key)!.Cells, "")).ToList();
            var proposals = SurfaceSuggestion.Propose(masks, map.Cloud);
            foreach (var p in proposals)
            {
                var at = masks.IndexOf(p.Mask);
                if (at >= 0)
                    named[list[at].Id] = p;
            }
        }
        return named;
    }

    private static SegmentationMask Mask(ProjectorDepthMap map, int[] cells, string label)
    {
        var coverage = new byte[map.Width * map.Height];
        foreach (var c in cells)
            coverage[c] = 255;
        return new SegmentationMask(map.Width, map.Height, coverage, 1, label);
    }
}
