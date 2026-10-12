using System.Globalization;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Geometry;

/// <summary>What a geometry file holds: a triangle mesh, or — a file with no faces — a point cloud. A mesh's
/// vertex colours ride in <see cref="MeshColors"/> (0xRRGGBB per vertex, empty when none) because
/// <see cref="MeshData"/> carries none.</summary>
public sealed record GeometryFile(MeshData? Mesh, PointCloud? Cloud, int[] MeshColors)
{
    public bool IsMesh => Mesh is not null;

    /// <summary>The points: the cloud as read, or the mesh's vertices with their normals and colours.</summary>
    public PointCloud ToPointCloud()
    {
        if (Cloud is not null)
            return Cloud;
        var m = Mesh!;
        var pts = new Vec3d[m.VertexCount];
        var nrm = m.HasNormals ? new Vec3d[m.VertexCount] : null;
        for (var i = 0; i < m.VertexCount; i++)
        {
            pts[i] = m.Position(i);
            if (nrm is not null)
                nrm[i] = m.Normal(i);
        }
        return new PointCloud(pts, nrm, MeshColors.Length == m.VertexCount ? MeshColors : null);
    }
}

/// <summary>Mesh and point-cloud files in and out: OBJ (read, write), PLY (ASCII and binary read; binary or
/// ASCII write), XYZ/PTS text and LAS 1.0–1.4 points (read), glTF 2.0 / GLB (read). Coordinates are kept as
/// stored (LAS is usually Z-up — <see cref="ZUpToYUp(PointCloud)"/> turns it to the stage's Y-up). UVs inside
/// PlayMap have their origin at the texture's top-left with v down (as glTF, images and the primitives do);
/// OBJ and PLY store v up from the bottom-left, so their v is flipped on the way in and out.
/// Every number is read and written culture-invariantly.</summary>
public static partial class MeshIO
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Reads a file by its extension: .obj, .ply, .xyz/.pts/.txt/.asc/.csv, .las, .glb/.gltf.</summary>
    public static GeometryFile Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".obj":
            {
                using var reader = new StreamReader(path);
                return ReadObj(reader);
            }
            case ".ply":
            {
                using var stream = File.OpenRead(path);
                return ReadPly(stream);
            }
            case ".xyz" or ".pts" or ".txt" or ".asc" or ".csv":
            {
                using var reader = new StreamReader(path);
                return new GeometryFile(null, ReadXyz(reader), []);
            }
            case ".las":
            {
                using var stream = File.OpenRead(path);
                return new GeometryFile(null, ReadLas(stream), []);
            }
            case ".laz":
                throw new NotSupportedException("Compressed LAS (LAZ) is not read; decompress it to LAS first.");
            case ".glb" or ".gltf":
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
                using var stream = File.OpenRead(path);
                return ReadGltf(stream, uri => File.ReadAllBytes(Path.Combine(dir, Uri.UnescapeDataString(uri))));
            }
            default:
                throw new NotSupportedException($"No reader for '{ext}' files.");
        }
    }

    /// <summary>A Z-up point (surveying, LAS) in the stage's Y-up frame: (x, y, z) ↦ (x, z, −y).</summary>
    public static Vec3d ZUpToYUp(Vec3d p)
    {
        var up = p.Z;
        var toward = -p.Y;
        return new Vec3d(p.X, up, toward);
    }

    /// <summary>A Z-up cloud in the stage's Y-up frame (normals turned too, organisation kept).</summary>
    public static PointCloud ZUpToYUp(PointCloud cloud)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        return new PointCloud(cloud.Points.Select(ZUpToYUp).ToArray(),
            cloud.HasNormals ? cloud.Normals.Select(ZUpToYUp).ToArray() : null,
            cloud.HasColors ? cloud.Colors : null, cloud.GridWidth, cloud.GridHeight);
    }

    // ---- shared helpers ----

    /// <summary>Splits a line into token ranges at any of the separators (empty tokens skipped); returns the
    /// count, at most the buffer's length.</summary>
    internal static int Split(ReadOnlySpan<char> line, Span<Range> into, ReadOnlySpan<char> separators)
    {
        var n = 0;
        var i = 0;
        while (i < line.Length && n < into.Length)
        {
            while (i < line.Length && separators.Contains(line[i]))
                i++;
            if (i >= line.Length)
                break;
            var start = i;
            while (i < line.Length && !separators.Contains(line[i]))
                i++;
            into[n++] = start..i;
        }
        return n;
    }

    internal static bool TryNumber(ReadOnlySpan<char> s, out double value) =>
        double.TryParse(s, NumberStyles.Float, Inv, out value);

    internal static double Number(ReadOnlySpan<char> s) =>
        TryNumber(s, out var v) ? v : throw new FormatException($"'{s.ToString()}' is not a number.");

    internal static int Rgb(double r, double g, double b) =>
        (ClampByte(r) << 16) | (ClampByte(g) << 8) | ClampByte(b);

    private static int ClampByte(double v) => (int)Math.Clamp(Math.Round(v), 0, 255);

    internal static string F(float v) => v.ToString(Inv);
}
