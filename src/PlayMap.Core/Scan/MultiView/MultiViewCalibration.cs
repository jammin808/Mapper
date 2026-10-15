using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using PlayMap.Core.Calibration;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using static PlayMap.Core.Scan.MultiView.MultiViewBundle;

namespace PlayMap.Core.Scan.MultiView;

/// <summary>
/// Many views of many projectors solved together, anchored on the projectors: a projector does not move between
/// views, and every decoded camera pixel names a projector pixel exactly, so a projector pixel is a surface point's
/// name across views. The start: each view self-calibrated on its own (<see cref="SelfCalibration"/>), the views
/// chained along a maximum spanning tree whose edges are shared projectors (each link a similarity fitted to the
/// shared tracks' points), held projectors carried onto the stage. Then one robust bundle adjustment of every view,
/// projector, rig and track (<see cref="MultiViewBundle"/>), a pass without the outliers, and a second adjustment.
/// </summary>
public static class MultiViewCalibration
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static MultiViewResult Solve(MultiViewInput input, MultiViewOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Run(input, options ?? MultiViewOptions.Default, null, ct);
    }

    /// <summary>Drops a view and solves again from <paramref name="previous"/> (its frame kept where the gauge allows):
    /// the result is the solve of <c>input.WithoutView(viewId)</c>. Throws when the scan has no such view.</summary>
    public static MultiViewResult DropView(MultiViewInput input, MultiViewResult previous, string viewId, MultiViewOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(previous);
        if (!input.Views.Any(v => v.Id == viewId))
            throw new ArgumentException($"This scan has no view {viewId}.", nameof(viewId));
        if (input.Views.Length < 2)
            throw new ArgumentException($"View {viewId} is the scan's only view: there would be nothing left to solve.", nameof(viewId));
        return Run(input.WithoutView(viewId), options ?? MultiViewOptions.Default, previous, ct);
    }

    /// <summary>What each view does to the solve: the RMS the scan would have without it (a warm-started
    /// <see cref="DropView"/> per view). The view whose removal lowers the residual most is the one that disagrees with
    /// the rest — a camera that moved during its runs; a view without which the solve cannot run gets NaN.</summary>
    public static ImmutableArray<ViewInfluence> Influence(MultiViewInput input, MultiViewResult result, MultiViewOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Views.Length < 2)
            return [.. result.Views.Select(v => new ViewInfluence(v.Id, result.RmsPx, double.NaN))];
        return [.. result.Views.Select(v =>
        {
            var without = DropView(input, result, v.Id, options, ct);
            return new ViewInfluence(v.Id, result.RmsPx, without.Solved ? without.RmsPx : double.NaN);
        })];
    }

    /// <summary>The start of one entity set: poses and optics in one frame, plus what was left out and why.</summary>
    private sealed record Start(
        Dictionary<string, Pose> Views,
        Dictionary<string, (Pose Pose, Intrinsics Intrinsics)> Projectors,
        Dictionary<string, (Intrinsics Intrinsics, LensDistortion Lens)> Rigs,
        string Root,
        List<LeftOutView> LeftOut,
        List<string> Words);

    private static MultiViewResult Run(MultiViewInput input, MultiViewOptions o, MultiViewResult? previous, CancellationToken ct)
    {
        var rigs = input.Rigs.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var projectors = input.Projectors.ToDictionary(p => p.Id, StringComparer.Ordinal);
        if (input.Projectors.FirstOrDefault(p => p.Held && p.Stage is null) is { } unplaced)
            throw new ArgumentException($"Projector {unplaced.Id} is held but has no stage pose.", nameof(input));
        var start = previous is null ? Initialise(input, rigs, projectors, o, ct) : WarmStart(input, previous);
        if (start.Views.Count == 0 || start.Projectors.Count == 0)
        {
            var why = start.Words.Count > 0 ? string.Join("; ", start.Words) : "no view solved";
            return new MultiViewResult([], [], [], [], double.NaN, false, [.. start.LeftOut], 0, "not solved: " + why);
        }
        ct.ThrowIfCancellationRequested();
        if (previous is null)
            CarryOntoStage(start, input, projectors);

        // Entities in input order, only those the start placed and that still see each other: a view must observe a
        // placed projector's track, and a projector must have a track such a view observed (after a drop, either can
        // be left with nothing, and a parameter nothing observes cannot be solved).
        var seenPairs = input.Tracks.Tracks.Where(t => start.Projectors.ContainsKey(t.ProjectorId))
            .SelectMany(t => t.Seen.Where(s => start.Views.ContainsKey(s.ViewId)).Select(s => (View: s.ViewId, Projector: t.ProjectorId)))
            .ToHashSet();
        var viewList = input.Views.Where(v => start.Views.ContainsKey(v.Id) && seenPairs.Any(x => x.View == v.Id)).ToList();
        var projList = input.Projectors.Where(p => start.Projectors.ContainsKey(p.Id) && seenPairs.Any(x => x.Projector == p.Id)).ToList();
        foreach (var v in input.Views.Where(v => start.Views.ContainsKey(v.Id) && !viewList.Contains(v)))
            start.LeftOut.Add(new LeftOutView(v.Id, $"view {v.Id} sees none of the solved projectors' tracks"));
        foreach (var p in input.Projectors.Where(p => start.Projectors.ContainsKey(p.Id) && !projList.Contains(p)))
            start.Words.Add($"projector {p.Id} is seen by no view left in the scan");
        if (viewList.Count == 0 || projList.Count == 0)
            return new MultiViewResult([], [], [], [], double.NaN, false, [.. start.LeftOut], 0, "not solved: no view sees a solved projector");
        var rigList = input.Rigs.Where(r => viewList.Any(v => v.RigId == r.Id)).ToList();
        var viewIndex = viewList.Select((v, i) => (v.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        var projIndex = projList.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        var rigIndex = rigList.Select((r, i) => (r.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);

        var state = new State
        {
            VR = [.. viewList.Select(v => start.Views[v.Id].CameraToWorld)],
            VC = [.. viewList.Select(v => start.Views[v.Id].Position)],
            RF = [.. rigList.Select(r => start.Rigs[r.Id].Intrinsics.Fx)],
            RCx = [.. rigList.Select(r => start.Rigs[r.Id].Intrinsics.Cx)],
            RCy = [.. rigList.Select(r => start.Rigs[r.Id].Intrinsics.Cy)],
            RK1 = [.. rigList.Select(r => start.Rigs[r.Id].Lens.K1)],
            RK2 = [.. rigList.Select(r => start.Rigs[r.Id].Lens.K2)],
            PR = [.. projList.Select(p => start.Projectors[p.Id].Pose.CameraToWorld)],
            PC = [.. projList.Select(p => start.Projectors[p.Id].Pose.Position)],
            PF = [.. projList.Select(p => start.Projectors[p.Id].Intrinsics.Fx)],
            PCx = [.. projList.Select(p => start.Projectors[p.Id].Intrinsics.Cx)],
            PCy = [.. projList.Select(p => start.Projectors[p.Id].Intrinsics.Cy)],
        };

        // Tracks of placed projectors, observed by placed views; each starts at the median of its triangulated depths.
        var tracks = new List<TrackSpec>();
        var trackSource = new List<Track>();
        var observations = new List<ObsSpec>();
        var depths = new List<double>();
        foreach (var t in input.Tracks.Tracks)
        {
            if (!projIndex.TryGetValue(t.ProjectorId, out var p))
                continue;
            var seen = t.Seen.Where(s => viewIndex.ContainsKey(s.ViewId)).ToList();
            if (seen.Count == 0)
                continue;
            var pv = new DeviceView(start.Projectors[t.ProjectorId].Intrinsics, projectors[t.ProjectorId].Lens, start.Projectors[t.ProjectorId].Pose);
            var ds = new List<double>();
            foreach (var s in seen)
            {
                var view = viewList[viewIndex[s.ViewId]];
                var rig = start.Rigs[view.RigId];
                var cam = new DeviceView(rig.Intrinsics, rig.Lens, start.Views[s.ViewId]);
                var tp = Triangulation.Triangulate(pv, t.Projector, cam, s.Camera);
                if (!tp.Valid)
                    continue;
                var z = pv.Pose.WorldToCamera(tp.Point).Z;
                if (z > 0 && double.IsFinite(z))
                    ds.Add(z);
            }
            if (ds.Count == 0)
                continue;
            var index = tracks.Count;
            tracks.Add(new TrackSpec(p, t.Projector));
            trackSource.Add(t);
            depths.Add(SolverMath.Median(ds.ToArray()));
            foreach (var s in seen)
                observations.Add(new ObsSpec(index, viewIndex[s.ViewId], s.Camera));
        }
        state.D = [.. depths];

        // The gauge. No held projector: the root view is fixed and the first projector it sees keeps its distance
        // from it (the scale). One held: the held projector fixes the frame and the root view keeps its distance
        // from it. Two or more: the stage fixes everything.
        var held = projList.Where(p => p.Held).ToList();
        var root = viewIndex.TryGetValue(start.Root, out var ri) ? ri : 0;
        var rootViewId = viewList[root].Id;
        var anchorProjector = -1;
        if (held.Count == 0)
        {
            var counts = projList.Select(p => trackSource.Count(t => t.ProjectorId == p.Id && t.Seen.Any(s => s.ViewId == rootViewId))).ToList();
            anchorProjector = counts.IndexOf(counts.Max());
        }
        var viewSpecs = viewList.Select((v, i) =>
        {
            var fixedView = held.Count == 0 && i == root;
            var sphere = held.Count == 1 && i == root;
            var anchor = sphere ? start.Projectors[held[0].Id].Pose.Position : Vec3d.Zero;
            return new ViewSpec(rigIndex[v.RigId], fixedView, sphere, anchor, sphere ? (state.VC[i] - anchor).Length : 0);
        }).ToArray();
        var projSpecs = projList.Select((p, j) =>
        {
            var sphere = j == anchorProjector;
            var anchor = sphere ? state.VC[root] : Vec3d.Zero;
            return new ProjSpec(p.Prior, p.Lens, p.Held, p.Held || (p.Locks & SolveLocks.Focal) != 0, p.Held || (p.Locks & SolveLocks.Shift) != 0,
                sphere, anchor, sphere ? (state.PC[j] - anchor).Length : 0);
        }).ToArray();
        var rigSpecs = rigList.Select(r => new RigSpec(r.Prior, r.Lens, r.Calibrated)).ToArray();

        var bundle = new MultiViewBundle(o, rigSpecs, viewSpecs, projSpecs, [.. tracks], [.. observations], state);
        bundle.Adjust(ct);
        if (bundle.Trim())
            bundle.Adjust(ct);
        return Result(bundle, viewList, projList, rigList, trackSource, held.Count, start, previous);
    }

    // ---- the start ----

    private sealed record ViewSolve(string ViewId, SelfCalibrationResult Result, Dictionary<string, int> Linked);

    private static Start Initialise(MultiViewInput input, Dictionary<string, MultiViewRig> rigs, Dictionary<string, MultiViewProjector> projectors,
        MultiViewOptions o, CancellationToken ct)
    {
        var left = new List<LeftOutView>();
        var words = new List<string>();
        var solves = new Dictionary<string, ViewSolve>(StringComparer.Ordinal);
        var points = new Dictionary<string, Dictionary<(string Projector, Vec2d Node), Vec3d>>(StringComparer.Ordinal);
        foreach (var view in input.Views)
        {
            ct.ThrowIfCancellationRequested();
            if (!rigs.TryGetValue(view.RigId, out var rig))
            {
                left.Add(new LeftOutView(view.Id, $"view {view.Id} names camera {view.RigId}, which is not in the scan"));
                continue;
            }
            var obs = new List<ProjectorObservations>();
            var ids = new List<string>();
            foreach (var p in input.Projectors)
            {
                List<Vec2d> cam;
                List<Vec2d> prj;
                if (view.Pairs.TryGetValue(p.Id, out var pairs) && pairs.Count > 0)
                {
                    cam = [.. pairs.Select(x => x.Camera)];
                    prj = [.. pairs.Select(x => x.Projector)];
                }
                else
                {
                    var seen = input.Tracks.Tracks.Where(t => t.ProjectorId == p.Id)
                        .SelectMany(t => t.Seen.Where(s => s.ViewId == view.Id).Select(s => (t.Projector, s.Camera))).ToList();
                    cam = [.. seen.Select(x => x.Camera)];
                    prj = [.. seen.Select(x => x.Projector)];
                }
                if (cam.Count < 16)
                    continue;
                obs.Add(new ProjectorObservations(p.Prior, p.Lens, cam, prj, p.Id));
                ids.Add(p.Id);
            }
            if (obs.Count == 0)
            {
                left.Add(new LeftOutView(view.Id, $"view {view.Id} saw no projector well enough to use: aim the camera at the light"));
                continue;
            }
            var lockFocal = ids.All(id => (projectors[id].Locks & SolveLocks.Focal) != 0);
            var result = SelfCalibration.Solve(rig.Prior, rig.Lens, obs, new SelfCalibrationOptions
            {
                LockCameraFocal = rig.Calibrated,
                LockProjectorFocal = lockFocal,
                Seed = o.Seed,
            });
            var linked = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var j = 0; j < result.Projectors.Count; j++)
            {
                var pc = result.Projectors[j];
                if (pc.Solved && (j == 0 || pc.ScaleLinked))
                    linked[ids[j]] = j;
            }
            if (linked.Count == 0)
            {
                left.Add(new LeftOutView(view.Id, $"view {view.Id} did not solve on its own: {result.Projectors[0].Words}"));
                continue;
            }
            solves[view.Id] = new ViewSolve(view.Id, result, linked);
            // The shared tracks' points in this view's frame, for chaining the views.
            var camera = result.CameraView;
            var map = new Dictionary<(string, Vec2d), Vec3d>();
            foreach (var t in input.Tracks.Tracks)
            {
                if (!linked.TryGetValue(t.ProjectorId, out var j))
                    continue;
                var s = t.Seen.FirstOrDefault(x => x.ViewId == view.Id);
                if (s is null)
                    continue;
                var tp = Triangulation.Triangulate(camera, s.Camera, result.Projectors[j].View, t.Projector);
                if (tp.Valid && tp.ResidualPx <= o.OutlierPx)
                    map[(t.ProjectorId, t.Projector)] = tp.Point;
            }
            points[view.Id] = map;
        }
        if (solves.Count == 0)
            return new Start([], [], [], "", left, ["no view solved on its own"]);

        // The root: the view that solved the most projectors, then the most track points, then input order.
        var order = input.Views.Select(v => v.Id).Where(solves.ContainsKey).ToList();
        var root = order.OrderByDescending(v => solves[v].Linked.Count).ThenByDescending(v => points[v].Count).First();

        // Prim's maximum spanning tree over shared track points; each link a similarity w → v.
        // The shared points of every pair of views, found once: (point in v's frame, point in w's frame).
        var sharedBy = new Dictionary<(string V, string W), List<(Vec3d A, Vec3d B)>>();
        foreach (var v in order)
        {
            foreach (var w in order)
            {
                if (v == w)
                    continue;
                var shared = new List<(Vec3d, Vec3d)>();
                foreach (var (key, b) in points[w])
                    if (points[v].TryGetValue(key, out var a))
                        shared.Add((a, b));
                sharedBy[(v, w)] = shared;
            }
        }
        var placed = new Dictionary<string, Similarity3>(StringComparer.Ordinal) { [root] = Similarity3.Identity };
        var tree = new List<string> { root };
        while (true)
        {
            (string From, string To, List<(Vec3d A, Vec3d B)> Shared)? best = null;
            foreach (var v in tree)
            {
                foreach (var w in order)
                {
                    if (placed.ContainsKey(w))
                        continue;
                    var shared = sharedBy[(v, w)];
                    if (shared.Count >= Math.Max(3, o.MinSharedTracks) && (best is null || shared.Count > best.Value.Shared.Count))
                        best = (v, w, shared);
                }
            }
            if (best is not { } link)
                break;
            var fit = RobustSimilarity(link.Shared.Select(x => x.B).ToList(), link.Shared.Select(x => x.A).ToList());
            placed[link.To] = fit.Then(placed[link.From]);
            tree.Add(link.To);
        }
        foreach (var v in order.Where(v => !placed.ContainsKey(v)))
            left.Add(new LeftOutView(v, $"view {v} shares no projector with the others; include part of a projector the other views saw"));

        // Views, then projectors from the first view in tree order that solved them, then the rigs.
        var views = tree.ToDictionary(v => v, v => placed[v].Apply(Pose.Identity), StringComparer.Ordinal);
        var projectorStart = new Dictionary<string, (Pose, Intrinsics)>(StringComparer.Ordinal);
        foreach (var v in tree)
        {
            foreach (var (id, j) in solves[v].Linked)
            {
                if (projectorStart.ContainsKey(id))
                    continue;
                var pc = solves[v].Result.Projectors[j];
                var prior = projectors[id].Prior;
                // The focal length the view found, the principal point the prior gives (lens shift is not seen by one view).
                var k = (projectors[id].Locks & SolveLocks.Focal) != 0 ? prior : prior with { Fx = pc.Intrinsics.Fx, Fy = pc.Intrinsics.Fx * (prior.Fy / prior.Fx) };
                projectorStart[id] = (placed[v].Apply(pc.Pose), k);
            }
        }
        foreach (var p in input.Projectors.Where(p => !projectorStart.ContainsKey(p.Id)))
            words.Add($"projector {p.Id} was solved by no chained view: include it in a view that sees another projector too");
        var rigStart = new Dictionary<string, (Intrinsics, LensDistortion)>(StringComparer.Ordinal);
        foreach (var rig in input.Rigs)
        {
            var mine = tree.Where(v => input.Views.First(x => x.Id == v).RigId == rig.Id).ToList();
            if (mine.Count == 0)
                continue;
            if (rig.Calibrated)
            {
                rigStart[rig.Id] = (rig.Prior, rig.Lens);
                continue;
            }
            var f = SolverMath.Median(mine.Select(v => solves[v].Result.Camera.Fx).ToArray());
            rigStart[rig.Id] = (rig.Prior with { Fx = f, Fy = f * (rig.Prior.Fy / rig.Prior.Fx) }, rig.Lens);
        }
        return new Start(views, projectorStart, rigStart, root, left, words);
    }

    /// <summary>The similarity source → target, refitted without the pairs beyond three times the median distance.</summary>
    private static Similarity3 RobustSimilarity(List<Vec3d> source, List<Vec3d> target)
    {
        var fit = Umeyama.Fit(source, target);
        for (var pass = 0; pass < 2; pass++)
        {
            var d = source.Select((s, i) => (fit.Apply(s) - target[i]).Length).ToArray();
            var limit = 3 * SolverMath.Median(d);
            var keep = Enumerable.Range(0, d.Length).Where(i => d[i] <= limit).ToList();
            if (keep.Count < 3 || keep.Count == d.Length)
                break;
            fit = Umeyama.Fit(keep.Select(i => source[i]).ToList(), keep.Select(i => target[i]).ToList());
        }
        return fit;
    }

    /// <summary>With held projectors, the chained frame is carried onto the stage: the rotation averaged over the held
    /// projectors, the scale from their spread (two or more) or from the stage's baselines between the held projector
    /// and the others (one), the translation through their centroid. Held projectors then sit exactly at the stage.</summary>
    private static void CarryOntoStage(Start start, MultiViewInput input, Dictionary<string, MultiViewProjector> projectors)
    {
        var held = input.Projectors.Where(p => p.Held && start.Projectors.ContainsKey(p.Id)).ToList();
        if (held.Count == 0)
            return;
        var sum = new Mat3d(0, 0, 0, 0, 0, 0, 0, 0, 0);
        foreach (var p in held)
            sum += p.Stage!.CameraToWorld * start.Projectors[p.Id].Pose.CameraToWorld.Transposed();
        var rotation = sum.NearestRotation();
        double scale;
        if (held.Count >= 2)
        {
            var cs = held.Select(p => start.Projectors[p.Id].Pose.Position).ToList();
            var ss = held.Select(p => p.Stage!.Position).ToList();
            var mc = cs.Aggregate(Vec3d.Zero, (a, b) => a + b) / cs.Count;
            var ms = ss.Aggregate(Vec3d.Zero, (a, b) => a + b) / ss.Count;
            scale = ss.Sum(x => (x - ms).Length) / Math.Max(cs.Sum(x => (x - mc).Length), 1e-300);
        }
        else
        {
            var h = held[0];
            var ratios = input.Projectors
                .Where(p => !p.Held && p.Stage is not null && start.Projectors.ContainsKey(p.Id))
                .Select(p => (p.Stage!.Position - h.Stage!.Position).Length / Math.Max((start.Projectors[p.Id].Pose.Position - start.Projectors[h.Id].Pose.Position).Length, 1e-300))
                .Where(double.IsFinite).ToArray();
            scale = ratios.Length > 0 ? SolverMath.Median(ratios) : 1;
            start.Words.Add(ratios.Length > 0
                ? "one projector held: the scale comes from the stage's distances to the others — hold two for a metric solve"
                : "one projector held and no stage distance to scale by: lengths are in the first view's baselines");
        }
        var chainCentroid = held.Select(p => start.Projectors[p.Id].Pose.Position).Aggregate(Vec3d.Zero, (a, b) => a + b) / held.Count;
        var stageCentroid = held.Select(p => p.Stage!.Position).Aggregate(Vec3d.Zero, (a, b) => a + b) / held.Count;
        var s = new Similarity3(rotation, stageCentroid - rotation * chainCentroid * scale, scale);
        foreach (var v in start.Views.Keys.ToList())
            start.Views[v] = s.Apply(start.Views[v]);
        foreach (var p in start.Projectors.Keys.ToList())
        {
            var spec = projectors[p];
            start.Projectors[p] = spec.Held ? (spec.Stage!, spec.Prior) : (s.Apply(start.Projectors[p].Pose), start.Projectors[p].Intrinsics);
        }
    }

    /// <summary>The start from an earlier result of the same scan: every view, projector and rig it placed.</summary>
    private static Start WarmStart(MultiViewInput input, MultiViewResult previous)
    {
        var views = previous.Views.Where(v => input.Views.Any(x => x.Id == v.Id)).ToDictionary(v => v.Id, v => v.Pose, StringComparer.Ordinal);
        var projectors = previous.Projectors.ToDictionary(p => p.Id, p => (p.Pose, p.Intrinsics), StringComparer.Ordinal);
        var rigs = previous.Rigs.ToDictionary(r => r.Id, r => (r.Intrinsics, r.Lens), StringComparer.Ordinal);
        var left = input.Views.Where(v => !views.ContainsKey(v.Id))
            .Select(v => previous.LeftOut.FirstOrDefault(l => l.ViewId == v.Id) ?? new LeftOutView(v.Id, $"view {v.Id} was not in the solve this one starts from"))
            .ToList();
        var root = input.Views.Select(v => v.Id).FirstOrDefault(views.ContainsKey) ?? "";
        return new Start(views, projectors, rigs, root, left, []);
    }

    // ---- the result ----

    private static MultiViewResult Result(MultiViewBundle bundle, List<MultiViewView> viewList, List<MultiViewProjector> projList,
        List<MultiViewRig> rigList, List<Track> trackSource, int heldCount, Start start, MultiViewResult? previous)
    {
        var s = bundle.S;
        var obs = bundle.Observations;
        var res = new double[obs.Count];
        var ok = new bool[obs.Count];
        for (var i = 0; i < obs.Count; i++)
        {
            ok[i] = bundle.Residual(s, i, out var r) && bundle.IsActive(i);
            res[i] = ok[i] ? r.Length : double.NaN;
        }
        static double Rms(IEnumerable<double> values)
        {
            var list = values.ToList();
            return list.Count > 0 ? Math.Sqrt(list.Sum(x => x * x) / list.Count) : double.NaN;
        }

        // Observations indexed once by view, projector and track.
        var byView = Enumerable.Range(0, viewList.Count).Select(_ => new List<int>()).ToArray();
        var byProjectorId = projList.ToDictionary(p => p.Id, _ => new List<int>(), StringComparer.Ordinal);
        var byTrack = Enumerable.Range(0, trackSource.Count).Select(_ => new List<int>()).ToArray();
        for (var k = 0; k < obs.Count; k++)
        {
            byView[obs[k].View].Add(k);
            byProjectorId[trackSource[obs[k].Track].ProjectorId].Add(k);
            byTrack[obs[k].Track].Add(k);
        }

        var cov = bundle.Covariance();
        var views = viewList.Select((v, i) =>
        {
            var mine = byView[i];
            var byProjector = mine.Where(k => ok[k]).GroupBy(k => trackSource[obs[k].Track].ProjectorId, StringComparer.Ordinal)
                .ToImmutableDictionary(g => g.Key, g => Rms(g.Select(k => res[k])), StringComparer.Ordinal);
            return new SolvedView(v.Id, v.RigId, PoseOf(s.VR[i], s.VC[i]), Rms(mine.Where(k => ok[k]).Select(k => res[k])), mine.Count, mine.Count(k => ok[k]))
            {
                RmsByProjector = byProjector,
            };
        }).ToImmutableArray();

        var projectors = projList.Select((p, j) =>
        {
            var mine = byProjectorId[p.Id];
            var trackCount = trackSource.Count(t => t.ProjectorId == p.Id);
            var inlierTracks = mine.Where(k => ok[k]).Select(k => obs[k].Track).Distinct().Count();
            var rms = Rms(mine.Where(k => ok[k]).Select(k => res[k]));
            if (p.Held)
            {
                return new SolvedProjector(p.Id, p.Prior, p.Lens, p.Stage!, true, rms, trackCount, inlierTracks, 0,
                    string.Create(Inv, $"projector {p.Id}: held at the stage; {rms:0.00} px RMS over {inlierTracks} tracks"));
            }
            var aspect = p.Prior.Fy / p.Prior.Fx;
            var k2 = p.Prior with { Fx = s.PF[j], Fy = s.PF[j] * aspect, Cx = s.PCx[j], Cy = s.PCy[j] };
            var pose = PoseOf(s.PR[j], s.PC[j]);
            var axial = double.NaN;
            if (cov is not null)
            {
                var c = bundle.CentreCovariance(cov, j);
                var f = pose.Forward;
                axial = Math.Sqrt(Math.Max(0, Vec3d.Dot(f, c * f)));
            }
            return new SolvedProjector(p.Id, k2, p.Lens, pose, false, rms, trackCount, inlierTracks, axial,
                string.Create(Inv, $"projector {p.Id}: {rms:0.00} px RMS over {inlierTracks} tracks, throw ratio {k2.ThrowRatio:0.000} (guess {p.Prior.ThrowRatio:0.000})"));
        }).ToImmutableArray();

        var rigs = rigList.Select((r, i) =>
        {
            var aspect = r.Prior.Fy / r.Prior.Fx;
            var k = r.Prior with { Fx = s.RF[i], Fy = s.RF[i] * aspect, Cx = s.RCx[i], Cy = s.RCy[i] };
            var lens = r.Lens with { K1 = s.RK1[i], K2 = s.RK2[i] };
            var sigma = r.Calibrated || cov is null ? double.NaN : Math.Sqrt(Math.Max(0, cov(bundle.RigFocalIndex(i), bundle.RigFocalIndex(i)))) / s.RF[i];
            return r.Calibrated ? new SolvedRig(r.Id, r.Prior, r.Lens, true, double.NaN) : new SolvedRig(r.Id, k, lens, false, sigma);
        }).ToImmutableArray();

        var tracks = trackSource.Select((t, i) =>
        {
            var mine = byTrack[i].Where(k => ok[k]).ToList();
            return new SolvedTrack(t.ProjectorId, t.Projector, bundle.Point(s, i, s.D[i]), Rms(mine.Select(k => res[k])), mine.Count);
        }).ToImmutableArray();

        var all = Rms(Enumerable.Range(0, obs.Count).Where(k => ok[k]).Select(k => res[k]));
        var sb = new StringBuilder();
        sb.Append(string.Create(Inv, $"{views.Length} views, {projectors.Length} projectors, {tracks.Length} tracks: {all:0.00} px RMS"));
        foreach (var w in start.Words)
            sb.Append("; ").Append(w);
        foreach (var l in start.LeftOut)
            sb.Append("; ").Append(l.Words);
        // A warm start keeps its predecessor's frame and scale (the root view and its distance to a projector are held).
        var metric = heldCount >= 2 || (heldCount == 0 && previous is { Metric: true });
        if (!metric && heldCount == 0)
            sb.Append("; lengths in the first view's baselines — hold two projectors or set a known distance for metres");
        return new MultiViewResult(views, projectors, rigs, tracks, all, metric, [.. start.LeftOut], bundle.Iterations, sb.ToString());
    }

    private static Pose PoseOf(Mat3d cameraToWorld, Vec3d centre) => new(centre, cameraToWorld.ToRotationVector());
}
