using System.Globalization;
using PlayMap.Core.Calibration;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Rendering;

namespace PlayMap.Engine;

/// <summary>The mapping verbs that call the solvers. A solver that is not on this node yet refuses with the
/// reason rather than pretending.</summary>
public sealed partial class EngineCore
{
    /// <summary>PROJECTOR SOLVE: the pose (and the unlocked optics) from the projector's pins. "LOCK SHIFT|FOCAL|
    /// NONE" overrides the projector's own locks for this solve and is kept on it. A solve worse than the one the
    /// projector has is still applied — the operator asked — but the words say it got worse. The calibration ledger records
    /// the result, scored, in the same edit (one undo step); the first result of a projector is its fallback. The round-one
    /// router hands this verb no session, so the result's "who" is the verb's own name.</summary>
    private CommandReply SolveProjector(List<string> args)
    {
        var pj = Edits.Project.FindProjector(args[0]) ?? Edits.Project.Stage.Projectors.FirstOrDefault(x => x.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));
        if (pj is null)
            return CommandReply.Fail($"no projector {args[0]}");
        var locks = pj.Locks;
        var li = args.FindIndex(a => a.Equals("LOCK", StringComparison.OrdinalIgnoreCase));
        if (li >= 0)
        {
            locks = SolveLocks.None;
            foreach (var word in args.Skip(li + 1))
            {
                locks |= word.ToUpperInvariant() switch
                {
                    "SHIFT" => SolveLocks.Shift,
                    "FOCAL" or "ZOOM" => SolveLocks.Focal,
                    "DISTORTION" or "LENS" => SolveLocks.Distortion,
                    "NONE" => SolveLocks.None,
                    _ => throw new EditRefusedException($"LOCK takes SHIFT, FOCAL, DISTORTION or NONE — not {word}"),
                };
            }
        }
        var pins = pj.Pins.Count(x => x.Enabled);
        if (pins < 4)
            return CommandReply.Fail($"{pj.Name} has {pins} pin{(pins == 1 ? "" : "s")} — a solve needs six or more spread out (four with the optics locked)");
        var solution = PoseSolver.Solve(pj with { Locks = locks }, PoseSolveOptions.Default with { Locks = locks, InitialPose = pj.Pose });
        if (solution.Inliers.Count(x => x) < 4 || double.IsNaN(solution.RmsPx))
            return CommandReply.Fail($"{pj.Name}: no solve — {solution.Words}");
        var before = pj.SolveResidualPx;
        var (r, snapshot, score, _) = RecordSolve(pj, locks, solution, "PROJECTOR SOLVE");
        if (!r.Changed)
            return FromEdit(r);
        var worse = before > 0 && solution.RmsPx > before * 1.5 ? $" (worse than the {before:0.0} px it had — check the pins)" : "";
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("snapshot", snapshot);
            w.WriteStartObject("score");
            w.WriteNumber("value", score.Value);
            w.WriteString("grade", score.Grade);
            w.WriteString("words", score.Words);
            w.WriteEndObject();
            w.WriteNumber("rmsPx", Math.Round(solution.RmsPx, 3));
            w.WriteStartArray("residualsPx");
            foreach (var e in solution.ResidualsPx)
                w.WriteNumberValue(Math.Round(e, 2));
            w.WriteEndArray();
            w.WriteStartArray("inliers");
            foreach (var i in solution.Inliers)
                w.WriteBooleanValue(i);
            w.WriteEndArray();
            w.WriteString("words", solution.Words);
            w.WriteEndObject();
        }), $"{pj.Name}: {solution.Words}{worse} — {score.Words} ({snapshot})");
    }

    private StageMeshes? _meshes;
    private BlendSolveResult? _lastBlendSolve;

    /// <summary>The project's meshes, loaded once per asset and shared by the solvers and the render host.</summary>
    public StageMeshes Meshes => LazyInitializer.EnsureInitialized(ref _meshes, () => new StageMeshes(MeshFolder));

    /// <summary>The last blend BLEND SOLVE computed (null before the first).</summary>
    public BlendSolveResult? LastBlendSolve => Volatile.Read(ref _lastBlendSolve);

    /// <summary>Raised on the control lane when a BLEND SOLVE's result is applied; the render host installs its
    /// maps at once (while the project's blend inputs are still the ones solved).</summary>
    public event Action<BlendSolveResult>? BlendSolved;

    /// <summary>The folder a project's relative mesh paths are read from: the open project file's, else the
    /// node's project folder.</summary>
    private string? MeshFolder() => _projectPath is { } path ? Path.GetDirectoryName(path) : Services.ProjectFolder;

    /// <summary>A finished scan's solve: in memory, or read back from its folder (and then kept); null when neither.</summary>
    private Scanning.ScanOutcome? OutcomeOf(ScanRecord scan)
    {
        var runner = Services.Scan as Scanning.ScanRunner;
        if (runner?.Find(scan.Id) is { } kept)
            return kept;
        if (ProjectFolderForFiles is not { } folder || Scanning.ScanArchive.Load(folder, scan.DataPath) is not { } loaded)
            return null;
        runner?.Remember(loaded);
        return loaded;
    }

    private static string NoData(ScanRecord scan) =>
        $"{scan.Name}'s data is not on this node{(scan.DataPath.Length > 0 ? $" (no {scan.DataPath}/scan.json)" : " (it was kept in memory by a node that has stopped)")}";

    /// <summary>The scan's mesh written beside the project (meshes/, binary PLY, written beside and moved over), as
    /// a mesh asset with its content hash.</summary>
    private static MeshAsset WriteScanMesh(string folder, ScanRecord scan, string meshId, MeshData mesh)
    {
        var relative = $"meshes/{scan.Id}-{meshId}.ply";
        var full = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".saving";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            MeshIO.WritePly(stream, mesh);
        string hash;
        using (var read = File.OpenRead(temp))
            hash = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(read));
        File.Move(temp, full, overwrite: true);
        return new MeshAsset(meshId, "Scan mesh — " + scan.Name, MeshOrigin.Scanned, relative, hash, "", ParamSet.Empty, mesh.VertexCount, mesh.TriangleCount);
    }

    /// <summary>The scan's mesh is on the stage already (a surface was made from it, or it was applied).</summary>
    private static MeshAsset? ScanMesh(Project p, ScanRecord scan) => scan.MeshId is { } id ? p.FindMesh(id) : null;

    /// <summary>SCAN APPLY: a finished scan onto the stage, as one undoable edit. The scan's mesh is written beside the
    /// project (meshes/, binary PLY) and becomes a mesh asset — or the one SCAN SURFACE already made is kept — with
    /// one visible surface over all of it; every solved projector takes the scan's optics and pose (as a solve does — a
    /// locked projector is left as it is); the camera takes its solved optics and is marked calibrated (the
    /// simulator's camera is added as a rig); each detected plane is offered as a hidden surface tagged "suggested"
    /// (SURFACE SHOW makes it real, REMOVE drops it). A scan already applied is refused: UNDO takes it back.</summary>
    private CommandReply ApplyScan(ScanRecord scan)
    {
        var existing = ScanMesh(Edits.Project, scan);
        if (existing is not null && Edits.Project.Stage.Surfaces.Any(s => s.MeshId == existing.Id && s.Faces.IsEmpty && s.Tags.Contains("scan")))
            return CommandReply.Fail($"{scan.Name} is applied already (mesh {existing.Id}) — UNDO takes it back");
        if (ProjectFolderForFiles is not { } folder)
            return CommandReply.Fail("the project has no folder yet — PROJECT SAVE it first: the scan's mesh is written beside it");
        if (OutcomeOf(scan) is not { } outcome)
            return CommandReply.Fail(NoData(scan));
        if (outcome.Solution.Mesh is not { } mesh)
            return CommandReply.Fail($"{scan.Name} has no mesh — nothing was triangulated; scan again with the camera seeing more of the projection");

        var p = Edits.Project;
        MeshAsset asset;
        try
        {
            asset = existing ?? WriteScanMesh(folder, scan, p.NewId(Ids.Mesh), mesh);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CommandReply.Fail($"the scan's mesh could not be written beside the project: {ex.Message}");
        }
        var meshId = asset.Id;
        var relative = asset.Path;
        var hash = asset.ContentHash;
        var solution = outcome.Solution;
        var applyTo = solution.Projectors.Where(x => x.Solved && p.FindProjector(x.ProjectorId) is { Locked: false }).ToList();
        var locked = solution.Projectors.Where(x => x.Solved && p.FindProjector(x.ProjectorId) is { Locked: true }).Select(x => x.Name).ToList();
        var rig = p.Stage.Cameras.Find(outcome.CameraId);
        var cameraId = rig?.Id ?? p.NewId(Ids.Camera);
        string surfaceId = "";
        var suggestions = new List<(string Id, Scanning.PlaneSuggestion Plane)>();
        var snapshots = new Dictionary<string, (string Id, QualityScore Score)>(StringComparer.Ordinal);
        var r = Edits.Apply($"apply {scan.Name}", project =>
        {
            surfaceId = project.NewId(Ids.Surface);
            var taken = new HashSet<string>(project.AllIds(), StringComparer.Ordinal) { meshId, cameraId, surfaceId };
            suggestions.Clear();
            foreach (var plane in solution.Planes)
            {
                string id;
                do
                    id = Ids.New(Ids.Surface);
                while (!taken.Add(id));
                suggestions.Add((id, plane));
            }
            var placed = project.WithStage(st =>
            {
                var projectors = st.Projectors;
                foreach (var x in applyTo)
                {
                    projectors = projectors.Update(x.ProjectorId, pj => pj with
                    {
                        Intrinsics = x.Intrinsics,
                        Lens = x.Lens,
                        Pose = x.Pose,
                        SolveResidualPx = Math.Max(x.RmsPx, 1e-6),
                    });
                }
                var c = solution.Camera;
                var cameras = rig is not null
                    ? st.Cameras.Update(rig.Id, cam => cam with { Intrinsics = c.Intrinsics, Lens = c.Lens, Pose = c.Pose, Calibrated = true })
                    : st.Cameras.Add(new CameraRig(cameraId, "Simulated camera", CameraSourceKind.Simulated, outcome.Scene, c.Intrinsics, c.Lens, c.Pose, true));
                var surfaces = st.Surfaces.Add(new Surface(surfaceId, scan.Name, meshId, Transform3.Identity, [], SurfaceContent.Empty, 1, [], true, false, "#3AA0FF", ["scan"]));
                foreach (var (id, plane) in suggestions)
                {
                    surfaces = surfaces.Add(new Surface(id, plane.Name, meshId, Transform3.Identity, [.. plane.Triangles], SurfaceContent.Empty, 1, [],
                        false, false, PlaneColour(plane.Kind), ["suggested", "plane", plane.Kind]));
                }
                return st with
                {
                    Meshes = existing is null ? st.Meshes.Add(asset) : st.Meshes,
                    Surfaces = surfaces,
                    Projectors = projectors,
                    Cameras = cameras,
                    Scans = st.Scans.Update(scan.Id, s => s with { MeshId = meshId, CameraId = cameraId }),
                };
            });
            return RecordScanResults(placed, scan, outcome, applyTo, snapshots);
        });
        if (!r.Changed)
            return FromEdit(r);

        var words = string.Create(CultureInfo.InvariantCulture,
            $"applied {scan.Name}: mesh {meshId} ({mesh.TriangleCount:N0} triangles), {applyTo.Count} projector{(applyTo.Count == 1 ? "" : "s")} placed ({string.Join(", ", applyTo.Select(x => $"{x.Name} {x.RmsPx:0.00} px"))}), surface {surfaceId}")
            + (suggestions.Count > 0 ? $"; {suggestions.Count} plane{(suggestions.Count == 1 ? "" : "s")} offered as hidden surfaces ({string.Join(", ", suggestions.Select(x => $"{x.Plane.Name} {x.Id}"))}) — SURFACE SHOW makes one real" : "")
            + (locked.Count > 0 ? $"; locked, left as they were: {string.Join(", ", locked)}" : "");
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("scan", scan.Id);
            w.WriteStartObject("mesh");
            w.WriteString("id", meshId);
            w.WriteString("path", relative);
            w.WriteString("hash", hash);
            w.WriteNumber("vertices", mesh.VertexCount);
            w.WriteNumber("triangles", mesh.TriangleCount);
            w.WriteEndObject();
            w.WriteString("surface", surfaceId);
            w.WriteString("camera", cameraId);
            w.WriteStartArray("projectors");
            foreach (var x in solution.Projectors)
            {
                w.WriteStartObject();
                w.WriteString("id", x.ProjectorId);
                w.WriteString("name", x.Name);
                w.WriteBoolean("applied", applyTo.Contains(x));
                w.WriteBoolean("solved", x.Solved);
                w.WriteNumber("rmsPx", Math.Round(x.RmsPx, 3));
                w.WriteNumber("throwRatio", Math.Round(x.Intrinsics.ThrowRatio, 4));
                if (snapshots.TryGetValue(x.ProjectorId, out var recorded))
                {
                    w.WriteString("snapshot", recorded.Id);
                    w.WriteStartObject("score");
                    w.WriteNumber("value", recorded.Score.Value);
                    w.WriteString("grade", recorded.Score.Grade);
                    w.WriteString("words", recorded.Score.Words);
                    w.WriteEndObject();
                }
                w.WriteString("words", x.Words);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("suggestions");
            foreach (var (id, plane) in suggestions)
            {
                w.WriteStartObject();
                w.WriteString("surface", id);
                w.WriteString("name", plane.Name);
                w.WriteString("kind", plane.Kind);
                w.WriteNumber("points", plane.Points);
                w.WriteNumber("triangles", plane.Triangles.Length);
                w.WriteStartArray("normal");
                w.WriteNumberValue(Math.Round(plane.Normal.X, 4));
                w.WriteNumberValue(Math.Round(plane.Normal.Y, 4));
                w.WriteNumberValue(Math.Round(plane.Normal.Z, 4));
                w.WriteEndArray();
                w.WriteStartArray("centroid");
                w.WriteNumberValue(Math.Round(plane.Centroid.X, 4));
                w.WriteNumberValue(Math.Round(plane.Centroid.Y, 4));
                w.WriteNumberValue(Math.Round(plane.Centroid.Z, 4));
                w.WriteEndArray();
                w.WriteString("show", $"SURFACE SHOW {id} ON");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteBoolean("metric", solution.Metric);
            w.WriteNumber("scale", Math.Round(solution.Scale, 6));
            w.WriteString("method", solution.Method);
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    /// <summary>SCAN SURFACE &lt;scan&gt; &lt;x&gt; &lt;y&gt; [&lt;name&gt;]: a click on the smart scan (0–1 across and down) becomes
    /// a surface. The first projector's ray at that point is cast into the scan mesh and seen by the camera; the
    /// segmenter (Vision's router: a model when one is installed, the classical one otherwise) grows a region there
    /// over the scanned surface, stopping at depth jumps; the region's mesh faces and its outline (pen-tool masks on
    /// the mesh's UVs) make the surface, named as the scan reads it unless a name is given. The scan's mesh joins the
    /// stage with it when it is not there yet; one undoable edit. Answers the surface's id.</summary>
    private async Task<CommandReply> ScanSurfaceAsync(List<string> args, WireLine line, SessionInfo session, CancellationToken ct)
    {
        // wp2-multiscan: ON <projector>, and a scan of many views, are answered there.
        if (await ScanSurfaceMultiscanAsync(args, line, session, ct).ConfigureAwait(false) is { } multiscan)
            return multiscan;
        const string Syntax = "SCAN SURFACE <scan> <x> <y> [<name>] — x and y from 0 to 1 across and down the scan's smart-scan picture — or SCAN SURFACE <scan> REGION <region> [<name>]";
        var byRegion = args.Count >= 3 && args[1].Equals("REGION", StringComparison.OrdinalIgnoreCase);
        double x = 0, y = 0;
        if (args.Count < 3 || (!byRegion && (!double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
            || !double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
            || x is < 0 or > 1 || y is < 0 or > 1)))
            return CommandReply.Fail(Syntax);
        if (Store.Project.Stage.Scans.Find(args[0]) is not { } scan)
            return CommandReply.Fail($"no scan {args[0]}");
        if (ProjectFolderForFiles is not { } folder)
            return CommandReply.Fail("the project has no folder yet — PROJECT SAVE it first: the scan's mesh is written beside it");
        var outcome = await Task.Run(() => OutcomeOf(scan), ct).ConfigureAwait(false);
        if (outcome is null)
            return CommandReply.Fail(NoData(scan));
        if (outcome.Geometry is not { } g)
            return CommandReply.Fail($"{scan.Name} has no scanned surface to select from — nothing was triangulated");
        Core.Vision.SegmentationMask mask;
        if (byRegion)
        {
            if (outcome.Regions.FirstOrDefault(r => r.Id.Equals(args[2], StringComparison.OrdinalIgnoreCase)) is not { } region)
                return CommandReply.Fail($"{scan.Name} suggests no region {args[2]} — STATE.scan.regions lists them");
            mask = region.Mask;
        }
        else
        {
            if (Scanning.ScanSurfaces.CameraPixelAt(g, x, y) is not { } pixel)
                return CommandReply.Fail("the projector lights nothing the scan reached there — click on the scanned picture");
            var segmenter = (Services.Scan as Scanning.ScanRunner)?.Segmenter
                ?? new Vision.Segmentation.SegmenterRouter([], new Vision.Segmentation.ClassicalSegmenter(), null);
            var result = await segmenter.SegmentAsync(new Core.Vision.VisionFrame(g.Image, g.Cloud), Core.Vision.SegmentationPrompt.Click(pixel.X, pixel.Y), ct)
                .ConfigureAwait(false);
            if (result.Best is not { } best)
                return CommandReply.Fail("nothing to select there — " + (result.Words.Length > 0 ? result.Words : "the segmenter found no region"));
            mask = best;
        }
        var faces = Scanning.ScanSurfaces.Faces(g, mask);
        if (faces.Length == 0)
            return CommandReply.Fail("the region there covers no whole triangle of the scan mesh — click nearer its middle");
        var proposals = Vision.Surfaces.SurfaceSuggestion.Propose([mask], g.Cloud);
        var proposal = proposals.Count > 0 ? proposals[0] : null;
        var given = string.Join(' ', args.Skip(3)).Trim();
        var name = given.Length > 0 ? given : proposal?.Name is { Length: > 0 } guess ? char.ToUpperInvariant(guess[0]) + guess[1..] : "Scanned surface";
        var tags = new List<string> { "scan" };
        if (proposal is not null)
            tags.AddRange(proposal.Tags.Where(t => !tags.Contains(t, StringComparer.Ordinal)));

        MeshAsset? written = null;
        if (ScanMesh(Store.Project, scan) is null)
        {
            if (outcome.Solution.Mesh is not { } mesh)
                return CommandReply.Fail($"{scan.Name} has no mesh");
            try
            {
                var meshId = Store.Project.NewId(Ids.Mesh);
                written = await Task.Run(() => WriteScanMesh(folder, scan, meshId, mesh), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return CommandReply.Fail($"the scan's mesh could not be written beside the project: {ex.Message}");
            }
        }
        return await _lane.RunAsync(() =>
        {
            string surfaceId = "";
            var paths = 0;
            var r = EditsFor(session).Apply($"surface {name} from {scan.Name}", p =>
            {
                var current = p.Stage.Scans.Find(scan.Id) ?? throw new EditRefusedException($"no scan {scan.Id}");
                var asset = ScanMesh(p, current) ?? written ?? throw new EditRefusedException($"{scan.Name}'s mesh is not on the stage");
                var taken = new HashSet<string>(p.AllIds(), StringComparer.Ordinal) { asset.Id };
                string Fresh(string prefix)
                {
                    string id;
                    do
                        id = Ids.New(prefix);
                    while (!taken.Add(id));
                    return id;
                }
                surfaceId = Fresh(Ids.Surface);
                var masks = Scanning.ScanSurfaces.Masks(mask, () => Fresh(Ids.Mask));
                paths = masks.Length;
                var surface = new Surface(surfaceId, name, asset.Id, Transform3.Identity, [.. faces], SurfaceContent.Empty, 1, masks, true, false,
                    PlaneColour(proposal?.Plane is null ? "curved" : tags.Contains("floor") ? "floor" : tags.Contains("wall") ? "wall" : "top"), [.. tags]);
                return p.WithStage(st => st with
                {
                    Meshes = st.Meshes.Find(asset.Id) is null ? st.Meshes.Add(asset) : st.Meshes,
                    Surfaces = st.Surfaces.Add(surface),
                    Scans = st.Scans.Update(scan.Id, sc => sc.MeshId == asset.Id ? sc : sc with { MeshId = asset.Id }),
                });
            });
            return r.Changed
                ? CommandReply.Success(surfaceId, string.Create(CultureInfo.InvariantCulture,
                    $"made surface {name} ({surfaceId}): {faces.Length:N0} triangles of {scan.Name}, outlined by {paths} mask path{(paths == 1 ? "" : "s")}"))
                : FromEdit(r);
        }).ConfigureAwait(false);
    }

    /// <summary>SCAN APPLY's results in the calibration ledger, inside its edit: each projector the scan placed gets a result with the
    /// scan's score (its dense fit, its decoded share, one view), applied as current — its fallback too when it has none.</summary>
    private Project RecordScanResults(Project project, ScanRecord scan, Scanning.ScanOutcome outcome, List<Scanning.ScannedProjector> applied,
        Dictionary<string, (string Id, QualityScore Score)> snapshots)
    {
        snapshots.Clear();
        foreach (var x in applied)
        {
            if (project.FindProjector(x.ProjectorId) is not { } pj || outcome.ScoreInputs(x.ProjectorId) is not { } inputs)
                continue;
            var score = Core.Calibration.Scoring.CalibrationScoring.ForScan(inputs);
            var id = project.NewId(Ids.Calibration);
            var snap = Wall.CalibrationLedger.Of(pj, id, Wall.CalibrationLedger.NameFor(project, pj.Id, CalibrationSource.Scan), score, CalibrationSource.Scan,
                "SCAN APPLY", Services.Wall.UnixMs, scanId: scan.Id);
            (project, _) = Wall.CalibrationLedger.Record(project, snap, apply: true);
            snapshots[pj.Id] = (id, score);
        }
        return project;
    }

    private static string PlaneColour(string kind) => kind switch
    {
        "floor" => "#8A9AA8",
        "wall" => "#C8A060",
        "top" => "#60C080",
        "ceiling" => "#A080C0",
        _ => "#C07070",
    };
}
