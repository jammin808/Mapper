using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using PlayMap.Core.Scan.MultiView;
using PlayMap.Core.Storage;

namespace PlayMap.Engine.Scanning;

/// <summary>
/// A scan's views and its multi-view solve in its folder, beside scan.json: views/&lt;view&gt;/view.json (the view without its
/// decodes, with its place, the site frame that place is in and the camera latencies its flashes measured) and one &lt;projector&gt;.pmcm per decode (<see cref="CorrespondenceArchive"/>), multiview.json (the solve, the projectors
/// held, each view's influence) and depth/&lt;projector&gt;.bin (each projector's dense depth with its cloud). A decode is written
/// once — it never changes after its view — so adding a view writes only that view's files. Read back, the coverage, the merged
/// mesh and the regions are made again from the depth maps and the decodes (each deterministic), so INTO, SOLVE, DROP, SURFACE,
/// SUGGEST and APPLY work after a restart.
/// </summary>
public static class ScanViewArchive
{
    private const uint DepthMagic = 0x44504D50; // "PMPD" read little-endian

    private sealed record MultiViewFile(int Version, MultiViewResult Result, ImmutableArray<string> Held, ImmutableArray<ViewInfluence> Influence);

    /// <summary>Writes the outcome's views and its multi-view solve under <paramref name="scanFolder"/> (the scan's own folder).</summary>
    public static void Save(string scanFolder, ScanOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(scanFolder);
        ArgumentNullException.ThrowIfNull(outcome);
        foreach (var view in outcome.Captured)
        {
            // A view whose files did not read is never written over: what is on disk is all there is of it.
            if (view.State == "unreadable")
                continue;
            var folder = Path.Combine(scanFolder, "views", view.Id);
            Directory.CreateDirectory(folder);
            var json = Path.Combine(folder, "view.json");
            // A view written before keeps its decodes (they never change); a new one writes all of its own, over anything there.
            var fresh = !File.Exists(json);
            foreach (var (projector, map) in view.Maps)
            {
                var path = Path.Combine(folder, projector + ".pmcm");
                if (fresh || !File.Exists(path))
                    WriteAtomically(path, s => CorrespondenceArchive.Write(s, map));
            }
            WriteAtomically(json, s => JsonSerializer.Serialize(s, view, ProjectJson.Options));
        }
        var multiPath = Path.Combine(scanFolder, "multiview.json");
        var depthFolder = Path.Combine(scanFolder, "depth");
        if (outcome.Multi is not { } multi)
        {
            File.Delete(multiPath);
            if (Directory.Exists(depthFolder))
                Directory.Delete(depthFolder, recursive: true);
            return;
        }
        WriteAtomically(multiPath, s => JsonSerializer.Serialize(s, new MultiViewFile(1, multi.Result, multi.Held, multi.Influence), ProjectJson.Compact));
        Directory.CreateDirectory(depthFolder);
        foreach (var stale in Directory.EnumerateFiles(depthFolder, "*.bin").Where(f => multi.DepthOf(Path.GetFileNameWithoutExtension(f)) is null).ToList())
            File.Delete(stale);
        foreach (var d in multi.Depth)
            WriteAtomically(Path.Combine(depthFolder, d.ProjectorId + ".bin"), s => WriteDepth(s, d));
    }

    /// <summary>The outcome with its views and multi-view solve read back from <paramref name="scanFolder"/>; unchanged when the
    /// folder keeps no views (a scan made before views were kept). Nothing that does not read is guessed: a view whose view.json or
    /// every decode does not read is listed "unreadable" with the words why (it takes no part, and its folder is never written over),
    /// a view missing one projector's decode keeps the others and names the one it lacks, a folder with no view.json (a view whose
    /// write never finished) is no view, and a solve that does not read is solved again on the next SCAN SOLVE.</summary>
    public static ScanOutcome Load(string scanFolder, ScanOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(scanFolder);
        ArgumentNullException.ThrowIfNull(outcome);
        var viewsFolder = Path.Combine(scanFolder, "views");
        if (!Directory.Exists(viewsFolder))
            return outcome;
        var views = new List<ScanViewData>();
        foreach (var folder in Directory.EnumerateDirectories(viewsFolder).OrderBy(ViewOrder).ThenBy(f => f, StringComparer.Ordinal))
        {
            if (ReadView(folder) is { } view)
                views.Add(view);
        }
        if (views.Count == 0)
            return outcome;
        // The camera latencies each view measured are kept in its view.json: the scan's are its newest views'.
        var loaded = outcome with { Captured = [.. views], Latencies = MultiViewScan.Latencies(views) };
        var multiPath = Path.Combine(scanFolder, "multiview.json");
        if (!File.Exists(multiPath))
            return loaded;
        try
        {
            MultiViewFile? file;
            using (var s = DurableFile.OpenRead(multiPath))
                file = JsonSerializer.Deserialize<MultiViewFile>(s, ProjectJson.Options);
            if (file?.Result is not { Solved: true } result)
                return loaded;
            var depth = new List<ProjectorDepthMap>();
            foreach (var p in result.Projectors)
            {
                // A projector no view decoded has no depth map: it was written without one.
                var path = Path.Combine(scanFolder, "depth", p.Id + ".bin");
                if (!File.Exists(path))
                    continue;
                using var s = DurableFile.OpenRead(path);
                depth.Add(ReadDepth(s));
            }
            var coverage = new List<CoverageMap>();
            foreach (var p in result.Projectors)
            {
                var maps = result.Views.Select(v => views.FirstOrDefault(x => x.Id == v.Id)?.Maps.GetValueOrDefault(p.Id)).OfType<CorrespondenceMap>().ToList();
                if (maps.Count > 0)
                    coverage.Add(CoverageMap.Build(p.Id, maps));
            }
            var multi = new MultiViewOutcome(result, file.Held.IsDefault ? [] : file.Held, depth, coverage, ScanMerge.Merge(depth), ProjectorRegions.Build(depth),
                file.Influence.IsDefault ? [] : file.Influence);
            return loaded with
            {
                Multi = multi,
                Regions = MultiViewScan.Regions(multi),
                // The merged mesh made again is the one written: the stage's faces index it.
                Solution = loaded.Solution with { Mesh = multi.Merged.Mesh, VertexCells = multi.Merged.VertexCells },
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
        {
            return loaded;
        }
    }

    /// <summary>v2 before v10: the number after the "v".</summary>
    private static int ViewOrder(string folder) =>
        int.TryParse(Path.GetFileName(folder).AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;

    private static ScanViewData? ReadView(string folder)
    {
        var json = Path.Combine(folder, "view.json");
        if (!File.Exists(json))
            return null;
        ScanViewData? view;
        try
        {
            using var s = DurableFile.OpenRead(json);
            view = JsonSerializer.Deserialize<ScanViewData>(s, ProjectJson.Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return Unreadable(Path.GetFileName(folder), $"its view.json did not read ({ex.Message})");
        }
        if (view is null || view.ProjectorIds.IsDefault)
            return Unreadable(Path.GetFileName(folder), "its view.json holds no view");
        var maps = ImmutableDictionary.CreateBuilder<string, CorrespondenceMap>(StringComparer.Ordinal);
        var unread = new List<string>();
        foreach (var projector in view.ProjectorIds)
        {
            var path = Path.Combine(folder, projector + ".pmcm");
            try
            {
                using var s = DurableFile.OpenRead(path);
                maps[projector] = CorrespondenceArchive.Read(s);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
            {
                unread.Add(ex is FileNotFoundException ? $"{projector}.pmcm is missing" : $"{projector}.pmcm did not read: {ex.Message}");
            }
        }
        if (maps.Count == 0)
        {
            return view with
            {
                State = "unreadable",
                Words = $"its decodes did not read from the scan's folder ({(unread.Count > 0 ? string.Join("; ", unread) : "it names no projector")}) — it takes no part",
            };
        }
        return unread.Count == 0
            ? view with { Maps = maps.ToImmutable() }
            : view with { Maps = maps.ToImmutable(), Words = $"{string.Join("; ", unread)}: the view takes part with the decodes that read" };
    }

    /// <summary>A view whose view.json did not read: its folder's name, the words why, nothing else known.</summary>
    private static ScanViewData Unreadable(string id, string why) =>
        new(id, "", "", new Intrinsics(1, 1, 0, 0, 1, 1), LensDistortion.None, false, [], "", "", 0) { State = "unreadable", Words = why + " — it takes no part" };

    private static void WriteAtomically(string path, Action<Stream> write)
    {
        var temp = path + ".saving";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            write(stream);
        DurableFile.MoveIntoPlace(temp, path);
    }

    private static void WriteDepth(Stream stream, ProjectorDepthMap d)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(DepthMagic);
        w.Write(1);
        w.Write(d.ProjectorId);
        w.Write(d.Width);
        w.Write(d.Height);
        w.Write(d.Stride);
        w.Write(d.Origin.X);
        w.Write(d.Origin.Y);
        w.Write(d.Origin.Z);
        w.Write(d.Curvature.Length == d.Depth.Length);
        w.Write(d.Cloud.HasNormals);
        w.Flush();
        using var z = new ZLibStream(stream, CompressionLevel.Fastest, leaveOpen: true);
        using var zw = new BinaryWriter(z, Encoding.UTF8, leaveOpen: true);
        foreach (var x in d.Depth)
            zw.Write(x);
        foreach (var x in d.Sigma)
            zw.Write(x);
        zw.Write(d.Views);
        if (d.Curvature.Length == d.Depth.Length)
        {
            foreach (var x in d.Curvature)
                zw.Write(x);
        }
        foreach (var p in d.Cloud.Points)
            WriteVec(zw, p);
        if (d.Cloud.HasNormals)
        {
            foreach (var n in d.Cloud.Normals)
                WriteVec(zw, n);
        }
    }

    private static ProjectorDepthMap ReadDepth(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != DepthMagic || r.ReadInt32() != 1)
            throw new InvalidDataException("not a depth file of this version");
        var id = r.ReadString();
        var w = r.ReadInt32();
        var h = r.ReadInt32();
        var stride = r.ReadInt32();
        var origin = new Vec3d(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
        var hasCurvature = r.ReadBoolean();
        var hasNormals = r.ReadBoolean();
        if (w < 1 || h < 1 || (long)w * h > CorrespondenceArchive.MaxPixels || stride < 1)
            throw new InvalidDataException("a depth file with an impossible grid");
        var n = w * h;
        try
        {
            using var z = new ZLibStream(stream, CompressionMode.Decompress, leaveOpen: true);
            using var zr = new BinaryReader(z, Encoding.UTF8, leaveOpen: true);
            var depth = ReadFloats(zr, n);
            var sigma = ReadFloats(zr, n);
            var views = zr.ReadBytes(n);
            if (views.Length != n)
                throw new InvalidDataException("the depth file is cut short");
            var curvature = hasCurvature ? ReadFloats(zr, n) : [];
            var points = new Vec3d[n];
            for (var i = 0; i < n; i++)
                points[i] = ReadVec(zr);
            Vec3d[]? normals = null;
            if (hasNormals)
            {
                normals = new Vec3d[n];
                for (var i = 0; i < n; i++)
                    normals[i] = ReadVec(zr);
            }
            return new ProjectorDepthMap(id, w, h, stride, depth, sigma, views, new PointCloud(points, normals, null, w, h))
            {
                Origin = origin,
                Curvature = curvature,
            };
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("the depth file is cut short");
        }
    }

    private static float[] ReadFloats(BinaryReader r, int n)
    {
        var values = new float[n];
        for (var i = 0; i < n; i++)
            values[i] = r.ReadSingle();
        return values;
    }

    private static void WriteVec(BinaryWriter w, Vec3d v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }

    private static Vec3d ReadVec(BinaryReader r) => new(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
}
