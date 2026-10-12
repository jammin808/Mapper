using System.Globalization;
using PlayMap.Core.Calibration;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Engine;

/// <summary>The mapping verbs that call the solvers. A solver that is not on this node yet refuses with the
/// reason rather than pretending.</summary>
public sealed partial class EngineCore
{
    /// <summary>PROJECTOR SOLVE: the pose (and the unlocked optics) from the projector's pins. "LOCK SHIFT|FOCAL|
    /// NONE" overrides the projector's own locks for this solve and is kept on it. A solve worse than the one the
    /// projector has is still applied — the operator asked — but the words say it got worse.</summary>
    private CommandReply SolveProjector(List<string> args)
    {
        var pj = Store.Project.FindProjector(args[0]) ?? Store.Project.Stage.Projectors.FirstOrDefault(x => x.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));
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
        var r = Store.Apply($"solve {pj.Name}", p => p.WithStage(st => st with
        {
            Projectors = st.Projectors.Update(pj.Id, x => x with
            {
                Intrinsics = solution.Intrinsics,
                Lens = solution.Lens,
                Pose = solution.Pose,
                Locks = locks,
                SolveResidualPx = Math.Max(solution.RmsPx, 1e-6),
            }),
        }));
        if (!r.Changed)
            return FromEdit(r);
        var worse = before > 0 && solution.RmsPx > before * 1.5 ? $" (worse than the {before:0.0} px it had — check the pins)" : "";
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
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
        }), $"{pj.Name}: {solution.Words}{worse}");
    }

    private CommandReply SolveBlend(List<string> args) =>
        CommandReply.Fail("the 3D blend solver is not on this node yet");

    /// <summary>SCAN APPLY: a finished scan onto the stage, as one undoable edit. The scan's mesh is written beside the
    /// project (meshes/, binary PLY) and becomes a mesh asset with one visible surface over all of it; every solved
    /// projector takes the scan's optics and pose (as a solve does — a locked projector is left as it is); the camera
    /// takes its solved optics and is marked calibrated (the simulator's camera is added as a rig); each detected
    /// plane is offered as a hidden surface tagged "suggested" (SURFACE SHOW makes it real, REMOVE drops it). A scan
    /// already applied is refused: UNDO takes it back.</summary>
    private CommandReply ApplyScan(ScanRecord scan)
    {
        if (scan.MeshId is { } applied && Store.Project.FindMesh(applied) is not null)
            return CommandReply.Fail($"{scan.Name} is applied already (mesh {applied}) — UNDO takes it back");
        if (ProjectFolderForFiles is not { } folder)
            return CommandReply.Fail("the project has no folder yet — PROJECT SAVE it first: the scan's mesh is written beside it");
        var outcome = (Services.Scan as Scanning.ScanRunner)?.Find(scan.Id) ?? Scanning.ScanArchive.Load(folder, scan.DataPath);
        if (outcome is null)
            return CommandReply.Fail($"{scan.Name}'s data is not on this node{(scan.DataPath.Length > 0 ? $" (no {scan.DataPath}/scan.json)" : " (it was kept in memory by a node that has stopped)")}");
        if (outcome.Solution.Mesh is not { } mesh)
            return CommandReply.Fail($"{scan.Name} has no mesh — nothing was triangulated; scan again with the camera seeing more of the projection");

        var p = Store.Project;
        var meshId = p.NewId(Ids.Mesh);
        var relative = $"meshes/{scan.Id}-{meshId}.ply";
        string hash;
        try
        {
            var full = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var temp = full + ".saving";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                MeshIO.WritePly(stream, mesh);
            using (var read = File.OpenRead(temp))
                hash = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(read));
            File.Move(temp, full, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CommandReply.Fail($"the scan's mesh could not be written beside the project: {ex.Message}");
        }

        var solution = outcome.Solution;
        var asset = new MeshAsset(meshId, "Scan mesh — " + scan.Name, MeshOrigin.Scanned, relative, hash, "", ParamSet.Empty, mesh.VertexCount, mesh.TriangleCount);
        var applyTo = solution.Projectors.Where(x => x.Solved && p.FindProjector(x.ProjectorId) is { Locked: false }).ToList();
        var locked = solution.Projectors.Where(x => x.Solved && p.FindProjector(x.ProjectorId) is { Locked: true }).Select(x => x.Name).ToList();
        var rig = p.Stage.Cameras.Find(outcome.CameraId);
        var cameraId = rig?.Id ?? p.NewId(Ids.Camera);
        string surfaceId = "";
        var suggestions = new List<(string Id, Scanning.PlaneSuggestion Plane)>();
        var r = Store.Apply($"apply {scan.Name}", project =>
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
            return project.WithStage(st =>
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
                    Meshes = st.Meshes.Add(asset),
                    Surfaces = surfaces,
                    Projectors = projectors,
                    Cameras = cameras,
                    Scans = st.Scans.Update(scan.Id, s => s with { MeshId = meshId, CameraId = cameraId }),
                };
            });
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

    private static string PlaneColour(string kind) => kind switch
    {
        "floor" => "#8A9AA8",
        "wall" => "#C8A060",
        "top" => "#60C080",
        "ceiling" => "#A080C0",
        _ => "#C07070",
    };
}
