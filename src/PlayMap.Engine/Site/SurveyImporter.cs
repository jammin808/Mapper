using System.Globalization;
using System.Security.Cryptography;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using PlayMap.Core.Storage;

namespace PlayMap.Engine.Site;

/// <summary>What a SURVEY IMPORT was asked: the file, its format, how to read it (units, axis), the coordinate system, the voxel for
/// clouds, the triangle budget (0: no decimation) and deviation limit for meshes, the site frame as it stood when accepted (unset:
/// the import picks one), the project it was asked for and the folder beside it the files are written in.</summary>
internal sealed record SurveyRequest(string Id, string Name, string Path, SurveyFormat Format, double UnitScale, bool ZUp, string Crs,
    double VoxelM, int Decimate, double MaxErrorM, SiteFrame SiteAtStart, string ProjectFolder, string ProjectId);

/// <summary>What an import made: the mesh (stage coordinates) and its file, the thinned cloud's file (grid coordinates), the counts
/// read, the deviation decimation measured, the frame the stage coordinates are in, and the words.</summary>
internal sealed record SurveyProduct(MeshAsset? Mesh, string CloudPath, long SourcePoints, long SourceTriangles, double DecimationErrorM,
    SiteFrame Frame, string Crs, string SourceHash, string Words)
{
    /// <summary>The files written beside the project (relative), removed when the import is not applied.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
}

/// <summary>The import's work, run on a worker, never on the control lane: read (chunked for LAS; E57 scans one at a time), place in
/// the site frame in double, thin clouds by voxel and mesh structured E57 stations, decimate meshes to the budget, and write the
/// mesh (binary PLY, stage coordinates, beside the project in meshes/) and the thinned cloud (binary PLY, grid coordinates in
/// double, in clouds/), each written beside and moved over. Progress runs 0 → 1; the token stops it between steps.</summary>
internal static class SurveyImporter
{
    private const int Chunk = 1 << 20;

    public static SurveyProduct Run(SurveyRequest r, IProgress<double> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(progress);
        var hash = Hash(r.Path, ct);
        progress.Report(0.05);
        return r.Format switch
        {
            SurveyFormat.Las => FromChunks(r, LasChunkSource.Open(r.Path), hash, progress, ct),
            SurveyFormat.E57 => FromE57(r, hash, progress, ct),
            SurveyFormat.Xyz or SurveyFormat.Pts or SurveyFormat.Csv => FromLoaded(r, hash, progress, ct),
            _ => FromLoaded(r, hash, progress, ct),
        };
    }

    /// <summary>The file's SHA-256, read a megabyte at a time so a stop is heard between reads.</summary>
    private static string Hash(string path, CancellationToken ct)
    {
        using var stream = File.OpenRead(path);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        int n;
        while ((n = stream.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, n);
        }
        return "sha256:" + Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>A file naming another EPSG system than the site's is refused: it would be placed as if it were in the site's.</summary>
    private static void SameSystem(SurveyRequest r, string fileCrs)
    {
        var site = r.SiteAtStart;
        if (r.Crs.Length == 0 && site.IsSet && fileCrs.StartsWith("EPSG:", StringComparison.Ordinal) && site.Crs.StartsWith("EPSG:", StringComparison.Ordinal)
            && !string.Equals(fileCrs, site.Crs, StringComparison.Ordinal))
            throw new FormatException($"the file says it is in {fileCrs} but the site is in {site.Crs} — reproject it in your survey software, or import it in the site's system");
    }

    private static GeometryReadOptions Options(SurveyRequest r) => r.SiteAtStart.IsSet
        ? new GeometryReadOptions(r.UnitScale, r.ZUp, r.SiteAtStart)
        : new GeometryReadOptions(r.UnitScale, r.ZUp, null, AutoSite: true, Crs: r.Crs);

    /// <summary>A mesh file (OBJ, PLY, glTF) — or one of those, or XYZ text, holding only points.</summary>
    private static SurveyProduct FromLoaded(SurveyRequest r, string hash, IProgress<double> progress, CancellationToken ct)
    {
        var options = Options(r);
        if (r.Format is SurveyFormat.Xyz or SurveyFormat.Pts or SurveyFormat.Csv)
        {
            var cloud = MeshIO.Load(r.Path).Cloud!;
            progress.Report(0.3);
            return FromChunks(r, new MemoryChunkSource(cloud, r.Crs), hash, progress, ct);
        }
        var file = MeshIO.Load(r.Path, options, ct);
        ct.ThrowIfCancellationRequested();
        progress.Report(0.3);
        if (file.Mesh is not { } mesh)
        {
            // Points only: back to the file's own values, thinned as any cloud.
            var raw = MeshIO.Load(r.Path, GeometryReadOptions.AsStored, ct).Cloud!;
            return FromChunks(r, new MemoryChunkSource(raw, r.Crs), hash, progress, ct);
        }
        var frame = file.Site ?? r.SiteAtStart;
        return Finish(r, mesh, file.MeshColors, mesh.TriangleCount, mesh.VertexCount, frame, hash, null, null, progress, ct);
    }

    /// <summary>A cloud streamed in chunks: each chunk placed on the grid in double and voxel-thinned, so memory follows the
    /// occupied voxels, not the points.</summary>
    private static SurveyProduct FromChunks(SurveyRequest r, IPointChunkSource source, string hash, IProgress<double> progress, CancellationToken ct)
    {
        using (source)
        {
            SameSystem(r, source.Crs);
            var options = Options(r) with { Crs = r.Crs.Length > 0 ? r.Crs : source.Crs };
            var thinner = new VoxelThinner(r.VoxelM);
            var points = new Vec3d[Chunk];
            var colours = new int[Chunk];
            var total = Math.Max(1, source.DeclaredCount);
            int n;
            while ((n = source.Read(points, colours, ct)) > 0)
            {
                for (var i = 0; i < n; i++)
                    points[i] = options.ToGrid(points[i]);
                thinner.Add(points.AsSpan(0, n), source.HasColour ? colours.AsSpan(0, n) : []);
                progress.Report(0.05 + 0.75 * Math.Min(1, thinner.PointsSeen / (double)total));
            }
            var thinned = thinner.ToCloud(source.HasColour);
            var frame = FrameOf(options, thinned.Points);
            return Finish(r, null, [], 0, thinner.PointsSeen, frame, hash, thinned, options.Crs, progress, ct);
        }
    }

    /// <summary>The frame of grid points: the site as set (refused when they are far from its origin), or one picked from them (the
    /// options' auto site; refused when they span too far).</summary>
    private static SiteFrame FrameOf(GeometryReadOptions options, IReadOnlyList<Vec3d> grid) =>
        (options with { UnitScale = 1, ZUp = true }).Resolve(grid) ?? SiteFrame.None;

    /// <summary>E57: each scan read in turn; a structured scan meshed on its own grid (GridMesher), every scan's points thinned into
    /// the cloud. The stations' meshes join into one, decimated as any mesh.</summary>
    private static SurveyProduct FromE57(SurveyRequest r, string hash, IProgress<double> progress, CancellationToken ct)
    {
        E57File file;
        using (var stream = File.OpenRead(r.Path))
            file = MeshIO.ReadE57(stream, ct);
        progress.Report(0.4);
        SameSystem(r, LasChunkSource.CrsOfWkt(file.CoordinateMetadata));
        var crs = r.Crs.Length > 0 ? r.Crs : LasChunkSource.CrsOfWkt(file.CoordinateMetadata);
        var options = Options(r) with { Crs = crs };
        var thinner = new VoxelThinner(r.VoxelM);
        var colour = file.Scans.Count > 0 && file.Scans.All(s => s.Points.HasColors);
        foreach (var scan in file.Scans)
        {
            ct.ThrowIfCancellationRequested();
            var grid = scan.Points.Points.Select(p => PointCloud.IsValid(p) ? options.ToGrid(p) : p).ToArray();
            thinner.Add(grid, colour ? scan.Points.Colors : []);
        }
        var thinned = thinner.ToCloud(colour);
        var frame = FrameOf(options, thinned.Points);
        var meshes = new List<MeshData>();
        foreach (var scan in file.Scans.Where(s => s.Structured))
        {
            ct.ThrowIfCancellationRequested();
            var stage = scan.Points.Points.Select(p => PointCloud.IsValid(p) ? frame.ToStage(options.ToGrid(p)) : p).ToArray();
            var organised = new PointCloud(stage, null, null, scan.Points.GridWidth, scan.Points.GridHeight);
            var mesh = GridMesher.Build(organised).Mesh;
            if (mesh.TriangleCount > 0)
                meshes.Add(mesh);
        }
        progress.Report(0.5);
        var merged = Merge(meshes);
        return Finish(r, merged, [], merged?.TriangleCount ?? 0, thinner.PointsSeen, frame, hash, thinned, crs, progress, ct);
    }

    private static MeshData? Merge(List<MeshData> meshes)
    {
        if (meshes.Count == 0)
            return null;
        if (meshes.Count == 1)
            return meshes[0];
        var uvs = meshes.All(m => m.HasUvs);
        var normals = meshes.All(m => m.HasNormals);
        var pos = new List<float>();
        var nor = new List<float>();
        var uv = new List<float>();
        var idx = new List<uint>();
        foreach (var m in meshes)
        {
            var at = (uint)(pos.Count / 3);
            pos.AddRange(m.Positions);
            if (normals)
                nor.AddRange(m.Normals);
            if (uvs)
                uv.AddRange(m.Uvs);
            idx.AddRange(m.Indices.Select(i => i + at));
        }
        return new MeshData([.. pos], [.. nor], [.. uv], [.. idx]);
    }

    private static SurveyProduct Finish(SurveyRequest r, MeshData? mesh, int[] meshColours, long sourceTriangles, long sourcePoints, SiteFrame frame,
        string hash, PointCloud? gridCloud, string? crs, IProgress<double> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var words = new List<string>();
        var written = new List<string>();
        double deviation = 0;
        MeshAsset? asset = null;
        if (mesh is not null)
        {
            if (r.Decimate > 0 && mesh.TriangleCount > r.Decimate)
            {
                var d = MeshDecimator.Decimate(mesh, r.Decimate, r.MaxErrorM, meshColours, new Relay(progress, 0.5, 0.35), ct);
                mesh = d.Mesh;
                meshColours = d.Colours;
                deviation = d.DeviationM;
                words.Add(d.Words);
            }
            else
            {
                words.Add(string.Create(CultureInfo.InvariantCulture, $"{mesh.TriangleCount:N0} triangles kept as read"));
            }
            ct.ThrowIfCancellationRequested();
            var relative = Fresh(r.ProjectFolder, $"meshes/{r.Id}-{Slug(r.Name)}", ".ply");
            written.Add(relative);
            var meshHash = Write(r.ProjectFolder, relative, s => MeshIO.WritePly(s, mesh, meshColours.Length == mesh.VertexCount ? meshColours : null));
            asset = new MeshAsset("", r.Name, MeshOrigin.Imported, relative, meshHash, "", ParamSet.Empty, mesh.VertexCount, mesh.TriangleCount);
            gridCloud ??= new PointCloud([.. Enumerable.Range(0, mesh.VertexCount).Select(i => frame.ToGrid(mesh.Position(i)))]);
        }
        progress.Report(0.9);
        var cloudPath = "";
        if (gridCloud is { Count: > 0 })
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                cloudPath = Fresh(r.ProjectFolder, $"clouds/{r.Id}", ".ply");
                written.Add(cloudPath);
                Write(r.ProjectFolder, cloudPath, s => GridCloudFile.Write(s, gridCloud.Points, gridCloud.HasColors ? gridCloud.Colors : null));
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
            {
                // The mesh written before is referenced by nothing: it goes with the failure.
                foreach (var f in written.Where(f => File.Exists(System.IO.Path.Combine(r.ProjectFolder, f))))
                    File.Delete(System.IO.Path.Combine(r.ProjectFolder, f));
                throw;
            }
            words.Add(string.Create(CultureInfo.InvariantCulture, $"{sourcePoints:N0} points thinned to {gridCloud.Count:N0}")
                + (mesh is null ? string.Create(CultureInfo.InvariantCulture, $" at {r.VoxelM * 100:0.#} cm") : ""));
        }
        progress.Report(1);
        return new SurveyProduct(asset, cloudPath, sourcePoints, sourceTriangles, deviation, frame, crs ?? r.Crs, hash, string.Join("; ", words))
        {
            Files = written,
        };
    }

    /// <summary>Progress of a stage mapped into its share of the whole.</summary>
    private sealed class Relay(IProgress<double> to, double from, double span) : IProgress<double>
    {
        public void Report(double value) => to.Report(from + span * Math.Clamp(value, 0, 1));
    }

    private static string Slug(string name)
    {
        var s = new string([.. name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-');
        return s.Length == 0 ? "survey" : s.Length > 40 ? s[..40] : s;
    }

    /// <summary>A path no file has yet (an id handed out again after an UNDO never overwrites what the history still names).</summary>
    private static string Fresh(string folder, string stem, string extension)
    {
        var path = stem + extension;
        for (var n = 2; File.Exists(System.IO.Path.Combine(folder, path)); n++)
            path = stem + "-" + n.ToString(CultureInfo.InvariantCulture) + extension;
        return path;
    }

    /// <summary>Written beside, then moved over (the half-written file removed when writing fails); answers the content hash.</summary>
    private static string Write(string folder, string relative, Action<Stream> write)
    {
        var full = System.IO.Path.Combine(folder, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        var temp = full + ".saving";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                write(stream);
            string hash;
            using (var read = File.OpenRead(temp))
                hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(read));
            DurableFile.MoveIntoPlace(temp, full, overwrite: false);
            return hash;
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
