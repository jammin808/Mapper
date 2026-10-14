using System.Text;
using System.Text.Json;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Scan;
using PlayMap.Core.Vision;

namespace PlayMap.Engine.Scanning;

/// <summary>Where a simulated device really was (the scan simulator's truth), so a demo scan can say how close its
/// solve came. "camera" for the camera.</summary>
public sealed record SimulatedDevice(string Id, Intrinsics Intrinsics, Pose Pose);

/// <summary>A finished scan as the node keeps it until it is applied: the solve (camera, projectors, cloud, mesh,
/// plane suggestions), each projector's smart scan, and — for a simulated scan — the truth it was made from.
/// The project keeps only the <see cref="ScanRecord"/>; the rest is in memory and in the scan's folder.</summary>
public sealed record ScanOutcome(
    string Id,
    string Name,
    string CameraId,
    bool Simulated,
    string Scene,
    ScanSolution Solution,
    IReadOnlyList<(string ProjectorId, ProjectorView View)> Views,
    IReadOnlyList<SimulatedDevice> Truth)
{
    /// <summary>What click-to-surface works from (null when nothing was triangulated).</summary>
    public ScanGeometry? Geometry { get; init; }

    /// <summary>The surfaces the scan suggests, placed on the smart scan.</summary>
    public IReadOnlyList<ScanRegion> Regions { get; init; } = [];

    /// <summary>The worst solved projector's reprojection RMS (pixels), 0 when none solved.</summary>
    public double WorstResidualPx => Solution.Projectors.Where(p => p.Solved).Select(p => p.RmsPx).DefaultIfEmpty(0).Max();

    /// <summary>What the calibration ledger scores a solved projector of this scan by (wp2-wall): its dense fit over its inlier
    /// pairs, the share of its picture the decode judged good (the quality report's 0–100), and one camera view; null when the
    /// scan did not solve that projector.</summary>
    public Core.Calibration.Scoring.ScanScoreInputs? ScoreInputs(string projectorId) =>
        Solution.Projectors.FirstOrDefault(p => p.Solved && string.Equals(p.ProjectorId, projectorId, StringComparison.Ordinal)) is { } x
            ? new Core.Calibration.Scoring.ScanScoreInputs(projectorId, x.RmsPx, x.Inliers, Math.Clamp(x.Quality / 100.0, 0, 1), 1) { CameraId = CameraId }
            : null;
}

/// <summary>A scan's folder beside the project ("scans/&lt;id&gt;"): scan.json (the solve without its geometry),
/// mesh.ply and points.ply (binary PLY; UVs top-left with v down inside PlayMap, flipped at the file as MeshIO
/// does), one smart-&lt;projector&gt;.pgm per projector, and what click-to-surface needs on the camera's grid —
/// grid.bin (the organised points), cells.bin (each mesh vertex's cell) and camera.ppm (the camera's picture).
/// Reading it back gives everything SCAN APPLY and SCAN SURFACE need after a restart.</summary>
public static class ScanArchive
{
    public const string Folder = "scans";

    private sealed record ArchiveFile(
        int Version,
        string Id,
        string Name,
        string CameraId,
        bool Simulated,
        string Scene,
        ScanCameraView Camera,
        IReadOnlyList<ScannedProjector> Projectors,
        IReadOnlyList<PlaneSuggestion> Planes,
        int Points,
        double Scale,
        bool Metric,
        string Method,
        string Words,
        IReadOnlyList<SimulatedDevice> Truth);

    /// <summary>The scan's folder relative to the project ("scans/sc1a2b3c4d").</summary>
    public static string DataPath(string scanId) => Folder + "/" + scanId;

    /// <summary>Writes the scan's folder under <paramref name="projectFolder"/>; answers its path relative to the
    /// project.</summary>
    public static string Save(string projectFolder, ScanOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(projectFolder);
        ArgumentNullException.ThrowIfNull(outcome);
        var relative = DataPath(outcome.Id);
        var folder = Path.Combine(projectFolder, Folder, outcome.Id);
        Directory.CreateDirectory(folder);
        var s = outcome.Solution;
        var file = new ArchiveFile(1, outcome.Id, outcome.Name, outcome.CameraId, outcome.Simulated, outcome.Scene, s.Camera, s.Projectors, s.Planes,
            s.Points, s.Scale, s.Metric, s.Method, s.Words, outcome.Truth);
        WriteAtomically(Path.Combine(folder, "scan.json"), stream => JsonSerializer.Serialize(stream, file, ProjectJson.Options));
        if (s.Mesh is { } mesh)
            WriteAtomically(Path.Combine(folder, "mesh.ply"), stream => MeshIO.WritePly(stream, mesh));
        if (s.Cloud is { } cloud)
            WriteAtomically(Path.Combine(folder, "points.ply"), stream => MeshIO.WritePly(stream, cloud));
        foreach (var (projectorId, view) in outcome.Views)
            WriteAtomically(Path.Combine(folder, "smart-" + projectorId + ".pgm"), stream => WritePgm(stream, view.Width, view.Height, view.SmartScan));
        if (outcome.Geometry is { } g)
        {
            WriteAtomically(Path.Combine(folder, "grid.bin"), stream => WriteGrid(stream, g.Cloud));
            WriteAtomically(Path.Combine(folder, "cells.bin"), stream => WriteInts(stream, g.VertexCells));
            WriteAtomically(Path.Combine(folder, "camera.ppm"), stream => WritePpm(stream, g.Image));
        }
        return relative;
    }

    /// <summary>A scan read back from its folder (no point cloud and no smart scans: the mesh is what applying
    /// needs); null when the folder or its scan.json is missing or unreadable.</summary>
    public static ScanOutcome? Load(string projectFolder, string dataPath)
    {
        if (string.IsNullOrEmpty(projectFolder) || string.IsNullOrEmpty(dataPath))
            return null;
        var folder = Path.Combine(projectFolder, dataPath);
        var json = Path.Combine(folder, "scan.json");
        if (!File.Exists(json))
            return null;
        try
        {
            ArchiveFile? file;
            using (var stream = File.OpenRead(json))
                file = JsonSerializer.Deserialize<ArchiveFile>(stream, ProjectJson.Options);
            if (file is null)
                return null;
            var meshPath = Path.Combine(folder, "mesh.ply");
            var mesh = File.Exists(meshPath) ? MeshIO.Load(meshPath).Mesh : null;
            var cloud = ReadGrid(Path.Combine(folder, "grid.bin"));
            var cells = ReadInts(Path.Combine(folder, "cells.bin"));
            var image = ReadPpm(Path.Combine(folder, "camera.ppm"));
            var solution = new ScanSolution(file.Camera, file.Projectors ?? [], cloud, mesh, file.Points, file.Planes ?? [], file.Scale, file.Metric, file.Method, file.Words)
            {
                VertexCells = cells,
            };
            var outcome = new ScanOutcome(file.Id, file.Name, file.CameraId, file.Simulated, file.Scene, solution, [], file.Truth ?? []);
            return outcome with { Geometry = GeometryOf(outcome, image) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Written beside, then moved over: a crash mid-write never leaves half a file.</summary>
    private static void WriteAtomically(string path, Action<Stream> write)
    {
        var temp = path + ".saving";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            write(stream);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Click-to-surface's inputs from a solved scan and the camera's picture; null without a cloud, a mesh,
    /// its cells, a picture on the cloud's grid or a solved first projector.</summary>
    public static ScanGeometry? GeometryOf(ScanOutcome outcome, ImageRgb8? image)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var s = outcome.Solution;
        if (s.Cloud is not { IsOrganised: true } cloud || s.Mesh is not { } mesh || s.VertexCells is not { } cells || cells.Length != mesh.VertexCount
            || image is null || image.Width != cloud.GridWidth || image.Height != cloud.GridHeight
            || s.Projectors.FirstOrDefault(p => p.Solved) is not { } first)
            return null;
        var withNormals = cloud.HasNormals ? cloud : PointCloudOps.EstimateNormals(cloud, s.Camera.Pose.Position);
        return new ScanGeometry(withNormals, image, mesh, cells, s.Camera.View, new Core.Calibration.DeviceView(first.Intrinsics, first.Lens, first.Pose));
    }

    private static void WriteGrid(Stream stream, PointCloud cloud)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(cloud.GridWidth);
        w.Write(cloud.GridHeight);
        foreach (var p in cloud.Points)
        {
            w.Write(p.X);
            w.Write(p.Y);
            w.Write(p.Z);
        }
    }

    private static PointCloud? ReadGrid(string path)
    {
        if (!File.Exists(path))
            return null;
        using var r = new BinaryReader(File.OpenRead(path));
        var w = r.ReadInt32();
        var h = r.ReadInt32();
        if (w < 1 || h < 1 || (long)w * h * 24 + 8 != r.BaseStream.Length)
            return null;
        var points = new Core.Numerics.Vec3d[w * h];
        for (var i = 0; i < points.Length; i++)
            points[i] = new Core.Numerics.Vec3d(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
        return new PointCloud(points, gridWidth: w, gridHeight: h);
    }

    private static void WriteInts(Stream stream, int[] values)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(values.Length);
        foreach (var v in values)
            w.Write(v);
    }

    private static int[]? ReadInts(string path)
    {
        if (!File.Exists(path))
            return null;
        using var r = new BinaryReader(File.OpenRead(path));
        var n = r.ReadInt32();
        if (n < 0 || (long)n * 4 + 4 != r.BaseStream.Length)
            return null;
        var values = new int[n];
        for (var i = 0; i < n; i++)
            values[i] = r.ReadInt32();
        return values;
    }

    /// <summary>A binary pixmap (P6): the camera's picture, as the segmenter reads it.</summary>
    private static void WritePpm(Stream stream, ImageRgb8 image)
    {
        stream.Write(Encoding.ASCII.GetBytes(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"P6\n{image.Width} {image.Height}\n255\n")));
        for (var y = 0; y < image.Height; y++)
            stream.Write(image.Row(y));
    }

    private static ImageRgb8? ReadPpm(string path)
    {
        if (!File.Exists(path))
            return null;
        var bytes = File.ReadAllBytes(path);
        var fields = new List<string>();
        var at = 0;
        while (fields.Count < 4 && at < bytes.Length)
        {
            while (at < bytes.Length && char.IsWhiteSpace((char)bytes[at]))
                at++;
            var start = at;
            while (at < bytes.Length && !char.IsWhiteSpace((char)bytes[at]))
                at++;
            fields.Add(Encoding.ASCII.GetString(bytes, start, at - start));
        }
        at++;
        if (fields.Count < 4 || fields[0] != "P6"
            || !int.TryParse(fields[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var w)
            || !int.TryParse(fields[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var h)
            || w < 1 || h < 1 || bytes.Length - at < w * h * 3)
            return null;
        var image = ImageRgb8.Create(w, h);
        bytes.AsSpan(at, w * h * 3).CopyTo(image.Data);
        return image;
    }

    /// <summary>A binary greymap (P5): the smart scan opens in any image viewer.</summary>
    private static void WritePgm(Stream stream, int width, int height, byte[] pixels)
    {
        stream.Write(Encoding.ASCII.GetBytes(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"P5\n{width} {height}\n255\n")));
        stream.Write(pixels.AsSpan(0, Math.Min(pixels.Length, width * height)));
    }
}
