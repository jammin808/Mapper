using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Geometry;

/// <summary>A point cloud read in chunks, so a survey of tens of millions of points never sits in memory at once: points in the
/// file's own units and frame (doubles, as stored), colours 0xRRGGBB when the file has them.</summary>
public interface IPointChunkSource : IDisposable
{
    /// <summary>The points the file declares (−1 when it does not say).</summary>
    long DeclaredCount { get; }

    /// <summary>"EPSG:…" when the file names its coordinate system, else "".</summary>
    string Crs { get; }

    /// <summary>The file's own bounds in its units, when its header gives them.</summary>
    (Vec3d Min, Vec3d Max)? DeclaredBounds { get; }

    bool HasColour { get; }

    /// <summary>Fills up to <c>points.Length</c> points (and colours, when the file has them); 0 at the end.</summary>
    int Read(Span<Vec3d> points, Span<int> colours, CancellationToken ct);
}

/// <summary>LAS 1.0–1.4 (point formats 0–10, uncompressed) read in chunks: coordinates through the header's scale and offset, RGB
/// when the format has it (16-bit brought to 8), and the coordinate system from the projection records — the OGC WKT record (2112)
/// or GeoTIFF's key directory (34735: ProjectedCSTypeGeoKey 3072, else GeographicTypeGeoKey 2048), in the VLRs or a 1.4 file's
/// extended VLRs. Written from the ASPRS LAS 1.4 specification.</summary>
public sealed partial class LasChunkSource : IPointChunkSource
{
    private readonly Stream _stream;
    private readonly bool _own;
    private readonly int _recordLength;
    private readonly int _rgbAt;
    private readonly Vec3d _scale;
    private readonly Vec3d _offset;
    private readonly byte[] _buffer;
    private long _left;
    private bool _wideKnown;
    private bool _wide;

    public long DeclaredCount { get; }

    public string Crs { get; } = "";

    public (Vec3d Min, Vec3d Max)? DeclaredBounds { get; }

    public bool HasColour => _rgbAt >= 0;

    public static LasChunkSource Open(string path) => new(File.OpenRead(path), own: true);

    public LasChunkSource(Stream stream, bool own = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _own = own;
        var head = new byte[375];
        Exactly(head.AsSpan(0, 227));
        if (Encoding.ASCII.GetString(head, 0, 4) != "LASF")
            throw new FormatException("Not a LAS file.");
        var minor = head[25];
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(94));
        var pointsAt = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(96));
        var vlrs = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(100));
        var formatByte = head[104];
        if ((formatByte & 0xC0) != 0)
            throw new NotSupportedException("Compressed LAS (LAZ) is not read here; decompress it to LAS first.");
        var format = formatByte & 0x3F;
        _recordLength = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(105));
        long count = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(107));
        _scale = new Vec3d(D(head, 131), D(head, 139), D(head, 147));
        _offset = new Vec3d(D(head, 155), D(head, 163), D(head, 171));
        DeclaredBounds = (new Vec3d(D(head, 187), D(head, 203), D(head, 219)), new Vec3d(D(head, 179), D(head, 195), D(head, 211)));
        var read = 227;
        ulong evlrAt = 0;
        uint evlrs = 0;
        if (minor >= 4 && headerSize >= 375)
        {
            Exactly(head.AsSpan(227, 375 - 227));
            read = 375;
            evlrAt = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(235));
            evlrs = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(243));
            var count64 = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(247));
            if (count == 0 || count64 > (ulong)count)
                count = checked((long)count64);
        }
        (var minLength, _rgbAt) = format switch
        {
            0 => (20, -1),
            1 => (28, -1),
            2 => (26, 20),
            3 => (34, 28),
            4 => (57, -1),
            5 => (63, 28),
            6 => (30, -1),
            7 => (36, 30),
            8 => (38, 30),
            9 => (59, -1),
            10 => (67, 30),
            _ => throw new NotSupportedException($"LAS point format {format} is not known."),
        };
        if (_recordLength < minLength)
            throw new FormatException($"LAS records of {_recordLength} bytes are too short for format {format}.");
        if (pointsAt < read)
            throw new FormatException("The LAS point data starts inside the header.");
        DeclaredCount = count;
        _left = count;

        // The VLRs sit between the header and the points.
        Skip(headerSize - read);
        var at = (long)headerSize;
        var crs = "";
        for (var i = 0; i < vlrs && at + 54 <= pointsAt; i++)
        {
            var vh = new byte[54];
            Exactly(vh);
            var user = Encoding.ASCII.GetString(vh, 2, 16).TrimEnd('\0', ' ');
            var record = BinaryPrimitives.ReadUInt16LittleEndian(vh.AsSpan(18));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(vh.AsSpan(20));
            var body = new byte[length];
            Exactly(body);
            at += 54 + length;
            if (user == "LASF_Projection")
                crs = Better(crs, CrsOf(record, body));
        }
        if (crs.Length == 0 && evlrs > 0 && evlrAt > 0 && _stream.CanSeek)
        {
            var back = _stream.Position;
            _stream.Seek((long)evlrAt, SeekOrigin.Begin);
            for (var i = 0; i < evlrs; i++)
            {
                var vh = new byte[60];
                if (_stream.Read(vh) < 60)
                    break;
                var user = Encoding.ASCII.GetString(vh, 2, 16).TrimEnd('\0', ' ');
                var record = BinaryPrimitives.ReadUInt16LittleEndian(vh.AsSpan(18));
                var length = (long)BinaryPrimitives.ReadUInt64LittleEndian(vh.AsSpan(20));
                if (length is < 0 or > 1 << 24)
                    break;
                var body = new byte[length];
                Exactly(body);
                if (user == "LASF_Projection")
                    crs = Better(crs, CrsOf(record, body));
            }
            _stream.Seek(back, SeekOrigin.Begin);
        }
        Crs = crs;
        Skip(pointsAt - at);
        _buffer = new byte[Math.Max(1, (1 << 20) / _recordLength) * _recordLength];
    }

    private static string Better(string have, string found) => have.Length > 0 ? have : found;

    /// <summary>The EPSG code a projection record names ("" when none): WKT's outermost authority (the last AUTHORITY or ID in
    /// the text), or the GeoKey directory's projected, else geographic, system.</summary>
    internal static string CrsOf(ushort record, ReadOnlySpan<byte> body)
    {
        if (record == 2112)
            return CrsOfWkt(Encoding.ASCII.GetString(body).TrimEnd('\0'));
        if (record != 34735 || body.Length < 8)
            return "";
        var keys = BinaryPrimitives.ReadUInt16LittleEndian(body[6..]);
        ushort projected = 0, geographic = 0;
        for (var k = 0; k < keys && 8 + k * 8 + 8 <= body.Length; k++)
        {
            var e = body[(8 + k * 8)..];
            var id = BinaryPrimitives.ReadUInt16LittleEndian(e);
            var location = BinaryPrimitives.ReadUInt16LittleEndian(e[2..]);
            var value = BinaryPrimitives.ReadUInt16LittleEndian(e[6..]);
            if (location != 0)
                continue;
            if (id == 3072)
                projected = value;
            else if (id == 2048)
                geographic = value;
        }
        var code = projected is > 0 and < 32767 ? projected : geographic is > 0 and < 32767 ? geographic : 0;
        return code > 0 ? "EPSG:" + code.ToString(CultureInfo.InvariantCulture) : "";
    }

    [GeneratedRegex(@"(?:AUTHORITY|ID)\[\s*""EPSG""\s*,\s*""?(\d+)""?", RegexOptions.IgnoreCase)]
    private static partial Regex WktAuthority();

    /// <summary>The outermost EPSG authority of a WKT text (WKT1 and WKT2 both put it last), or "".</summary>
    public static string CrsOfWkt(string wkt)
    {
        ArgumentNullException.ThrowIfNull(wkt);
        var matches = WktAuthority().Matches(wkt);
        return matches.Count == 0 ? "" : "EPSG:" + matches[^1].Groups[1].Value;
    }

    public int Read(Span<Vec3d> points, Span<int> colours, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var take = (int)Math.Min(Math.Min(points.Length, _buffer.Length / _recordLength), _left);
        if (take <= 0)
            return 0;
        Exactly(_buffer.AsSpan(0, take * _recordLength));
        var colour = HasColour && colours.Length >= take;
        if (colour && !_wideKnown)
        {
            // 16-bit colour unless every value of the first chunk fits a byte (some writers store 8-bit values).
            for (var k = 0; k < take && !_wide; k++)
                for (var c = 0; c < 3; c++)
                    _wide |= BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(k * _recordLength + _rgbAt + 2 * c)) > 255;
            _wideKnown = true;
        }
        var shift = _wide ? 8 : 0;
        for (var k = 0; k < take; k++)
        {
            var r = _buffer.AsSpan(k * _recordLength, _recordLength);
            points[k] = new Vec3d(
                BinaryPrimitives.ReadInt32LittleEndian(r) * _scale.X + _offset.X,
                BinaryPrimitives.ReadInt32LittleEndian(r[4..]) * _scale.Y + _offset.Y,
                BinaryPrimitives.ReadInt32LittleEndian(r[8..]) * _scale.Z + _offset.Z);
            if (colour)
            {
                var red = BinaryPrimitives.ReadUInt16LittleEndian(r[_rgbAt..]) >> shift;
                var green = BinaryPrimitives.ReadUInt16LittleEndian(r[(_rgbAt + 2)..]) >> shift;
                var blue = BinaryPrimitives.ReadUInt16LittleEndian(r[(_rgbAt + 4)..]) >> shift;
                colours[k] = (Math.Min(red, 255) << 16) | (Math.Min(green, 255) << 8) | Math.Min(blue, 255);
            }
        }
        _left -= take;
        return take;
    }

    private static double D(byte[] b, int at) => BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(at));

    private void Exactly(Span<byte> into)
    {
        try
        {
            _stream.ReadExactly(into);
        }
        catch (EndOfStreamException e)
        {
            throw new FormatException("The LAS file ends early.", e);
        }
    }

    private void Skip(long bytes)
    {
        if (bytes <= 0)
            return;
        if (_stream.CanSeek)
        {
            _stream.Seek(bytes, SeekOrigin.Current);
            return;
        }
        var scratch = new byte[(int)Math.Min(bytes, 1 << 16)];
        while (bytes > 0)
        {
            var n = (int)Math.Min(bytes, scratch.Length);
            Exactly(scratch.AsSpan(0, n));
            bytes -= n;
        }
    }

    public void Dispose()
    {
        if (_own)
            _stream.Dispose();
    }
}

/// <summary>A cloud already in memory, served in chunks (XYZ text, a PLY or OBJ without faces).</summary>
public sealed class MemoryChunkSource(PointCloud cloud, string crs = "") : IPointChunkSource
{
    private int _at;

    public long DeclaredCount => cloud.Count;

    public string Crs => crs;

    public (Vec3d Min, Vec3d Max)? DeclaredBounds => null;

    public bool HasColour => cloud.HasColors;

    public int Read(Span<Vec3d> points, Span<int> colours, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var take = Math.Min(points.Length, cloud.Count - _at);
        cloud.Points.AsSpan(_at, take).CopyTo(points);
        if (HasColour && colours.Length >= take)
            cloud.Colors.AsSpan(_at, take).CopyTo(colours);
        _at += take;
        return take;
    }

    public void Dispose()
    {
        // Nothing is held but the cloud, which the caller owns.
    }
}

/// <summary>Voxel thinning while streaming: every point falls in a cube of the voxel's size, and each occupied cube keeps the
/// centroid of its points (in double) and their mean colour. Memory grows with the occupied cubes, never with the points read.</summary>
public sealed class VoxelThinner
{
    private readonly double _size;
    private readonly Dictionary<(long, long, long), int> _cells = [];
    private readonly List<Vec3d> _sum = [];
    private readonly List<(long R, long G, long B)> _colour = [];
    private readonly List<int> _count = [];

    public VoxelThinner(double voxelM)
    {
        if (!(voxelM > 0) || !double.IsFinite(voxelM))
            throw new ArgumentOutOfRangeException(nameof(voxelM), "a voxel has a size above 0");
        _size = voxelM;
    }

    public long PointsSeen { get; private set; }

    public int Cells => _count.Count;

    public void Add(ReadOnlySpan<Vec3d> points, ReadOnlySpan<int> colours)
    {
        var colour = colours.Length >= points.Length;
        for (var i = 0; i < points.Length; i++)
        {
            var p = points[i];
            if (!PointCloud.IsValid(p))
                continue;
            PointsSeen++;
            var key = ((long)Math.Floor(p.X / _size), (long)Math.Floor(p.Y / _size), (long)Math.Floor(p.Z / _size));
            if (!_cells.TryGetValue(key, out var at))
            {
                at = _count.Count;
                _cells[key] = at;
                _sum.Add(Vec3d.Zero);
                _colour.Add((0, 0, 0));
                _count.Add(0);
            }
            _sum[at] += p;
            _count[at]++;
            if (colour)
            {
                var c = colours[i];
                var (r, g, b) = _colour[at];
                _colour[at] = (r + ((c >> 16) & 255), g + ((c >> 8) & 255), b + (c & 255));
            }
        }
    }

    /// <summary>One point per occupied voxel, in the order the voxels were first met.</summary>
    public PointCloud ToCloud(bool withColour)
    {
        var pts = new Vec3d[_count.Count];
        var col = withColour ? new int[_count.Count] : null;
        for (var i = 0; i < pts.Length; i++)
        {
            var n = _count[i];
            pts[i] = _sum[i] / n;
            if (col is not null)
            {
                var (r, g, b) = _colour[i];
                col[i] = (int)((r / n) << 16 | (g / n) << 8 | (b / n));
            }
        }
        return new PointCloud(pts, null, col);
    }
}

/// <summary>A cloud kept in grid coordinates (E, N, H in double) as a binary PLY with double positions, so it stays exact whatever
/// the site frame becomes; read back with <see cref="MeshIO.ReadPly(Stream, GeometryReadOptions)"/> (Z-up, the site's frame).</summary>
public static class GridCloudFile
{
    public static void Write(Stream stream, IReadOnlyList<Vec3d> grid, IReadOnlyList<int>? colours)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(grid);
        var colour = colours is not null && colours.Count == grid.Count;
        var header = new StringBuilder();
        header.Append("ply\nformat binary_little_endian 1.0\ncomment grid coordinates: x east, y north, z up (metres)\n");
        header.Append("element vertex ").Append(grid.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        header.Append("property double x\nproperty double y\nproperty double z\n");
        if (colour)
            header.Append("property uchar red\nproperty uchar green\nproperty uchar blue\n");
        header.Append("end_header\n");
        stream.Write(Encoding.ASCII.GetBytes(header.ToString()));
        using var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        for (var i = 0; i < grid.Count; i++)
        {
            w.Write(grid[i].X);
            w.Write(grid[i].Y);
            w.Write(grid[i].Z);
            if (colour)
            {
                var c = colours![i];
                w.Write((byte)(c >> 16));
                w.Write((byte)(c >> 8));
                w.Write((byte)c);
            }
        }
    }
}
