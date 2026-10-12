using System.Text;
using System.Text.Json;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Scan;

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
    /// <summary>The worst solved projector's reprojection RMS (pixels), 0 when none solved.</summary>
    public double WorstResidualPx => Solution.Projectors.Where(p => p.Solved).Select(p => p.RmsPx).DefaultIfEmpty(0).Max();
}

/// <summary>A scan's folder beside the project ("scans/&lt;id&gt;"): scan.json (the solve without its geometry),
/// mesh.ply and points.ply (binary PLY; UVs top-left with v down inside PlayMap, flipped at the file as MeshIO
/// does), and one smart-&lt;projector&gt;.pgm per projector. Reading it back gives everything SCAN APPLY needs
/// after a restart.</summary>
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
            var solution = new ScanSolution(file.Camera, file.Projectors ?? [], null, mesh, file.Points, file.Planes ?? [], file.Scale, file.Metric, file.Method, file.Words);
            return new ScanOutcome(file.Id, file.Name, file.CameraId, file.Simulated, file.Scene, solution, [], file.Truth ?? []);
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

    /// <summary>A binary greymap (P5): the smart scan opens in any image viewer.</summary>
    private static void WritePgm(Stream stream, int width, int height, byte[] pixels)
    {
        stream.Write(Encoding.ASCII.GetBytes(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"P5\n{width} {height}\n255\n")));
        stream.Write(pixels.AsSpan(0, Math.Min(pixels.Length, width * height)));
    }
}
