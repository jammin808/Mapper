using System.Buffers.Binary;
using System.Text;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Geometry;

public static partial class MeshIO
{
    private enum PlyType
    {
        Int8,
        UInt8,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Float32,
        Float64,
    }

    private sealed record PlyProperty(string Name, PlyType Type, bool IsList, PlyType CountType);

    private sealed record PlyElement(string Name, long Count, List<PlyProperty> Properties);

    private enum PlyFormat
    {
        Ascii,
        BinaryLittleEndian,
        BinaryBigEndian,
    }

    /// <summary>Reads PLY, ASCII or binary (either byte order): vertex x y z, nx ny nz, red green blue (any
    /// type; 8-bit kept, 16-bit and 0..1 floats scaled), s t / u v / texture_u texture_v, and the face list
    /// vertex_indices (or vertex_index), fan-triangulated. Other elements and properties are read past. A file
    /// with no faces is a point cloud.</summary>
    public static GeometryFile ReadPly(Stream stream) => ReadPly(stream, GeometryReadOptions.AsStored);

    /// <summary>Reads PLY placed by <paramref name="options"/>: positions are kept in double as read (a double property keeps its
    /// precision) and rebased before they are narrowed to float.</summary>
    public static GeometryFile ReadPly(Stream stream, GeometryReadOptions options) => ReadPly(stream, options, CancellationToken.None);

    /// <summary>Reads PLY placed by <paramref name="options"/>; <paramref name="ct"/> stops a long file between records.</summary>
    public static GeometryFile ReadPly(Stream stream, GeometryReadOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        var (format, elements) = ReadPlyHeader(stream);
        CheckPlyCounts(stream, format, elements);
        IPlyValues values = format == PlyFormat.Ascii ? new PlyAscii(stream) : new PlyBinary(stream, format == PlyFormat.BinaryBigEndian);
        try
        {
            return ReadPlyBody(elements, values, options, ct);
        }
        finally
        {
            values.Dispose();
        }
    }

    /// <summary>The most vertices a PLY file is read with (three doubles each: 6 GB of positions).</summary>
    private const long MaxPlyVertices = 1L << 28;

    private static int PlySize(PlyType t) => t switch
    {
        PlyType.Int8 or PlyType.UInt8 => 1,
        PlyType.Int16 or PlyType.UInt16 => 2,
        PlyType.Int32 or PlyType.UInt32 or PlyType.Float32 => 4,
        _ => 8,
    };

    /// <summary>A header's counts held to what the file can carry, before anything is allocated for them: every record takes at least
    /// its fixed properties' bytes and each list's count (binary), or two characters a value (ASCII: a digit and a separator). A
    /// stream of unknown length is held to the vertex ceiling alone.</summary>
    private static void CheckPlyCounts(Stream stream, PlyFormat format, List<PlyElement> elements)
    {
        foreach (var e in elements)
        {
            if (e.Count < 0)
                throw new FormatException($"the PLY header declares {e.Count} {e.Name} records");
            if (e.Name == "vertex" && e.Count > MaxPlyVertices)
                throw new FormatException($"the PLY header declares {e.Count} vertices — more than {MaxPlyVertices} are not read");
        }
        if (!stream.CanSeek)
            return;
        var remaining = Math.Max(0, stream.Length - stream.Position);
        var least = 0.0;
        foreach (var e in elements)
        {
            var record = format == PlyFormat.Ascii
                ? 2.0 * e.Properties.Count
                : e.Properties.Sum(p => (double)PlySize(p.IsList ? p.CountType : p.Type));
            least += record * e.Count;
        }
        // The last ASCII value needs no separator after it.
        if (format == PlyFormat.Ascii && least > 0)
            least -= 1;
        if (least > remaining)
        {
            var named = string.Join(", ", elements.Select(e => $"{e.Count} {e.Name}"));
            throw new FormatException($"the PLY header declares {named}, more than its {remaining} bytes can hold — the file is damaged or cut short");
        }
    }

    private static (PlyFormat Format, List<PlyElement> Elements) ReadPlyHeader(Stream stream)
    {
        if (HeaderLine(stream) != "ply")
            throw new FormatException("Not a PLY file.");
        PlyFormat? format = null;
        var elements = new List<PlyElement>();
        while (true)
        {
            var line = HeaderLine(stream) ?? throw new FormatException("The PLY header never ends.");
            var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;
            switch (parts[0])
            {
                case "end_header":
                    return (format ?? throw new FormatException("The PLY header names no format."), elements);
                case "format" when parts.Length >= 2:
                    format = parts[1] switch
                    {
                        "ascii" => PlyFormat.Ascii,
                        "binary_little_endian" => PlyFormat.BinaryLittleEndian,
                        "binary_big_endian" => PlyFormat.BinaryBigEndian,
                        _ => throw new FormatException($"Unknown PLY format '{parts[1]}'."),
                    };
                    break;
                case "element" when parts.Length >= 3:
                    elements.Add(new PlyElement(parts[1], long.Parse(parts[2], Inv), []));
                    break;
                case "property" when elements.Count > 0 && parts.Length >= 5 && parts[1] == "list":
                    elements[^1].Properties.Add(new PlyProperty(parts[4], PlyTypeOf(parts[3]), true, PlyTypeOf(parts[2])));
                    break;
                case "property" when elements.Count > 0 && parts.Length >= 3:
                    elements[^1].Properties.Add(new PlyProperty(parts[2], PlyTypeOf(parts[1]), false, PlyType.UInt8));
                    break;
                default:
                    // comment, obj_info and anything unknown carry nothing we read.
                    break;
            }
        }
    }

    /// <summary>One header line, read byte by byte so the stream stops exactly where the body begins.</summary>
    private static string? HeaderLine(Stream stream)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0)
                return sb.Length > 0 ? sb.ToString() : null;
            if (b == '\n')
                return sb.ToString().TrimEnd('\r');
            if (sb.Length > 4096)
                throw new FormatException("A PLY header line is too long.");
            sb.Append((char)b);
        }
    }

    private static PlyType PlyTypeOf(string s) => s switch
    {
        "char" or "int8" => PlyType.Int8,
        "uchar" or "uint8" => PlyType.UInt8,
        "short" or "int16" => PlyType.Int16,
        "ushort" or "uint16" => PlyType.UInt16,
        "int" or "int32" => PlyType.Int32,
        "uint" or "uint32" => PlyType.UInt32,
        "float" or "float32" => PlyType.Float32,
        "double" or "float64" => PlyType.Float64,
        _ => throw new FormatException($"Unknown PLY type '{s}'."),
    };

    private static GeometryFile ReadPlyBody(List<PlyElement> elements, IPlyValues values, GeometryReadOptions options, CancellationToken ct)
    {
        double[] raw = [];
        float[] nor = [];
        float[] uv = [];
        int[] col = [];
        var idx = new List<uint>();
        var poly = new List<uint>();
        var vertexCount = 0;
        foreach (var e in elements)
        {
            if (e.Name == "vertex")
            {
                vertexCount = checked((int)e.Count);
                var roles = e.Properties.Select(p => VertexRole(p.Name)).ToArray();
                bool Has(params int[] r) => r.All(x => roles.Contains(x));
                raw = new double[vertexCount * 3];
                nor = Has(3, 4, 5) ? new float[vertexCount * 3] : [];
                uv = Has(9, 10) ? new float[vertexCount * 2] : [];
                col = Has(6, 7, 8) ? new int[vertexCount] : [];
                var rgb = new double[3];
                for (var i = 0; i < vertexCount; i++)
                {
                    if ((i & 0xFFFF) == 0)
                        ct.ThrowIfCancellationRequested();
                    Array.Clear(rgb);
                    for (var k = 0; k < e.Properties.Count; k++)
                    {
                        var p = e.Properties[k];
                        if (p.IsList)
                        {
                            SkipList(values, p);
                            continue;
                        }
                        var v = values.Read(p.Type);
                        switch (roles[k])
                        {
                            case >= 0 and <= 2:
                                raw[i * 3 + roles[k]] = v;
                                break;
                            case >= 3 and <= 5 when nor.Length > 0:
                                nor[i * 3 + roles[k] - 3] = (float)v;
                                break;
                            case >= 6 and <= 8:
                                rgb[roles[k] - 6] = ColourByte(v, p.Type);
                                break;
                            case 9 or 10 when uv.Length > 0:
                                uv[i * 2 + roles[k] - 9] = roles[k] == 10 ? (float)(1 - v) : (float)v;
                                break;
                            default:
                                break;
                        }
                    }
                    if (col.Length > 0)
                        col[i] = Rgb(rgb[0], rgb[1], rgb[2]);
                }
            }
            else if (e.Name == "face")
            {
                for (long f = 0; f < e.Count; f++)
                {
                    if ((f & 0xFFFF) == 0)
                        ct.ThrowIfCancellationRequested();
                    foreach (var p in e.Properties)
                    {
                        if (p.IsList && p.Name is "vertex_indices" or "vertex_index")
                        {
                            poly.Clear();
                            var n = (int)values.Read(p.CountType);
                            for (var k = 0; k < n; k++)
                                poly.Add(checked((uint)values.Read(p.Type)));
                            for (var k = 1; k + 1 < poly.Count; k++)
                                idx.AddRange([poly[0], poly[k], poly[k + 1]]);
                        }
                        else if (p.IsList)
                        {
                            SkipList(values, p);
                        }
                        else
                        {
                            values.Read(p.Type);
                        }
                    }
                }
            }
            else
            {
                for (long r = 0; r < e.Count; r++)
                    foreach (var p in e.Properties)
                        if (p.IsList)
                            SkipList(values, p);
                        else
                            values.Read(p.Type);
            }
        }
        var frame = options.Resolve(RawPoints(raw, vertexCount));
        var pos = new float[vertexCount * 3];
        for (var i = 0; i < vertexCount; i++)
        {
            var p = options.Apply(new Vec3d(raw[i * 3], raw[i * 3 + 1], raw[i * 3 + 2]), frame);
            pos[i * 3] = (float)p.X;
            pos[i * 3 + 1] = (float)p.Y;
            pos[i * 3 + 2] = (float)p.Z;
            if (frame is not null && nor.Length > 0)
            {
                var n = options.ApplyDirection(new Vec3d(nor[i * 3], nor[i * 3 + 1], nor[i * 3 + 2]), frame);
                nor[i * 3] = (float)n.X;
                nor[i * 3 + 1] = (float)n.Y;
                nor[i * 3 + 2] = (float)n.Z;
            }
        }
        if (idx.Count > 0)
            return new GeometryFile(new MeshData(pos, nor, uv, [.. idx]), null, col) { Site = frame };
        var pts = new Vec3d[vertexCount];
        var nrm = nor.Length > 0 ? new Vec3d[vertexCount] : null;
        for (var i = 0; i < vertexCount; i++)
        {
            // A cloud keeps the double value: rebased as read, or the float the file was narrowed to as before.
            pts[i] = frame is null
                ? new Vec3d(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2])
                : options.Apply(new Vec3d(raw[i * 3], raw[i * 3 + 1], raw[i * 3 + 2]), frame);
            if (nrm is not null)
                nrm[i] = new Vec3d(nor[i * 3], nor[i * 3 + 1], nor[i * 3 + 2]);
        }
        return new GeometryFile(null, new PointCloud(pts, nrm, col.Length > 0 ? col : null), []) { Site = frame };
    }

    private static IEnumerable<Vec3d> RawPoints(double[] raw, int count)
    {
        for (var i = 0; i < count; i++)
            yield return new Vec3d(raw[i * 3], raw[i * 3 + 1], raw[i * 3 + 2]);
    }

    private static void SkipList(IPlyValues values, PlyProperty p)
    {
        var n = (long)values.Read(p.CountType);
        for (long k = 0; k < n; k++)
            values.Read(p.Type);
    }

    /// <summary>0–2 position, 3–5 normal, 6–8 colour, 9–10 texture coordinate, −1 anything else.</summary>
    private static int VertexRole(string name) => name switch
    {
        "x" => 0,
        "y" => 1,
        "z" => 2,
        "nx" => 3,
        "ny" => 4,
        "nz" => 5,
        "red" or "r" or "diffuse_red" => 6,
        "green" or "g" or "diffuse_green" => 7,
        "blue" or "b" or "diffuse_blue" => 8,
        "s" or "u" or "texture_u" or "texture_s" => 9,
        "t" or "v" or "texture_v" or "texture_t" => 10,
        _ => -1,
    };

    private static double ColourByte(double v, PlyType t) => t switch
    {
        PlyType.Float32 or PlyType.Float64 => v * 255,
        PlyType.UInt16 => v / 257,
        _ => v,
    };

    private interface IPlyValues : IDisposable
    {
        double Read(PlyType type);
    }

    /// <summary>ASCII body: whitespace-separated tokens, line breaks not significant.</summary>
    private sealed class PlyAscii(Stream stream) : IPlyValues
    {
        private readonly StreamReader _reader = new(stream, Encoding.ASCII, false, 1 << 16, leaveOpen: true);
        private string _line = "";
        private int _pos;

        public double Read(PlyType type)
        {
            while (true)
            {
                while (_pos < _line.Length && char.IsWhiteSpace(_line[_pos]))
                    _pos++;
                if (_pos < _line.Length)
                    break;
                _line = _reader.ReadLine() ?? throw new EndOfStreamException("The PLY body ends early.");
                _pos = 0;
            }
            var start = _pos;
            while (_pos < _line.Length && !char.IsWhiteSpace(_line[_pos]))
                _pos++;
            return Number(_line.AsSpan(start, _pos - start));
        }

        public void Dispose() => _reader.Dispose();
    }

    /// <summary>Binary body in either byte order.</summary>
    private sealed class PlyBinary(Stream stream, bool bigEndian) : IPlyValues
    {
        private readonly BinaryReader _reader = new(stream, Encoding.ASCII, leaveOpen: true);

        public double Read(PlyType type) => type switch
        {
            PlyType.Int8 => _reader.ReadSByte(),
            PlyType.UInt8 => _reader.ReadByte(),
            PlyType.Int16 => bigEndian ? BinaryPrimitives.ReverseEndianness(_reader.ReadInt16()) : _reader.ReadInt16(),
            PlyType.UInt16 => bigEndian ? BinaryPrimitives.ReverseEndianness(_reader.ReadUInt16()) : _reader.ReadUInt16(),
            PlyType.Int32 => bigEndian ? BinaryPrimitives.ReverseEndianness(_reader.ReadInt32()) : _reader.ReadInt32(),
            PlyType.UInt32 => bigEndian ? BinaryPrimitives.ReverseEndianness(_reader.ReadUInt32()) : _reader.ReadUInt32(),
            PlyType.Float32 => bigEndian ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReverseEndianness(_reader.ReadInt32())) : _reader.ReadSingle(),
            PlyType.Float64 => bigEndian ? BitConverter.Int64BitsToDouble(BinaryPrimitives.ReverseEndianness(_reader.ReadInt64())) : _reader.ReadDouble(),
            _ => throw new FormatException("Unknown PLY type."),
        };

        public void Dispose() => _reader.Dispose();
    }

    /// <summary>Writes a mesh as PLY (binary little-endian by default): float x y z, nx ny nz and s t when the
    /// mesh has them, uchar red green blue when colours are given, and a uchar/int face list.</summary>
    public static void WritePly(Stream stream, MeshData mesh, int[]? colors = null, bool binary = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(mesh);
        if (colors is { Length: > 0 } && colors.Length != mesh.VertexCount)
            throw new ArgumentException("One colour per vertex (or none).", nameof(colors));
        var n = mesh.VertexCount;
        WritePlyCore(stream, n, mesh.TriangleCount, binary, mesh.HasNormals, mesh.HasUvs, colors is { Length: > 0 },
            i => (mesh.Positions[i * 3], mesh.Positions[i * 3 + 1], mesh.Positions[i * 3 + 2]),
            i => (mesh.Normals[i * 3], mesh.Normals[i * 3 + 1], mesh.Normals[i * 3 + 2]),
            i => (mesh.Uvs[i * 2], 1 - mesh.Uvs[i * 2 + 1]),
            i => colors![i],
            t => (mesh.Indices[t * 3], mesh.Indices[t * 3 + 1], mesh.Indices[t * 3 + 2]));
    }

    /// <summary>Writes a point cloud as PLY (invalid points skipped; organisation is not kept).</summary>
    public static void WritePly(Stream stream, PointCloud cloud, bool binary = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(cloud);
        var keep = Enumerable.Range(0, cloud.Count).Where(i => PointCloud.IsValid(cloud.Points[i])).ToArray();
        WritePlyCore(stream, keep.Length, 0, binary, cloud.HasNormals, false, cloud.HasColors,
            i => ((float)cloud.Points[keep[i]].X, (float)cloud.Points[keep[i]].Y, (float)cloud.Points[keep[i]].Z),
            i => ((float)cloud.Normals[keep[i]].X, (float)cloud.Normals[keep[i]].Y, (float)cloud.Normals[keep[i]].Z),
            _ => (0f, 0f),
            i => cloud.Colors[keep[i]],
            _ => (0u, 0u, 0u));
    }

    private static void WritePlyCore(Stream stream, int vertices, int triangles, bool binary, bool normals, bool uvs, bool colours,
        Func<int, (float X, float Y, float Z)> position, Func<int, (float X, float Y, float Z)> normal, Func<int, (float U, float V)> uv,
        Func<int, int> colour, Func<int, (uint A, uint B, uint C)> triangle)
    {
        var header = new StringBuilder();
        header.Append("ply\n").Append(binary ? "format binary_little_endian 1.0\n" : "format ascii 1.0\n");
        header.Append("element vertex ").Append(vertices.ToString(Inv)).Append('\n');
        header.Append("property float x\nproperty float y\nproperty float z\n");
        if (normals)
            header.Append("property float nx\nproperty float ny\nproperty float nz\n");
        if (uvs)
            header.Append("property float s\nproperty float t\n");
        if (colours)
            header.Append("property uchar red\nproperty uchar green\nproperty uchar blue\n");
        if (triangles > 0)
            header.Append("element face ").Append(triangles.ToString(Inv)).Append("\nproperty list uchar int vertex_indices\n");
        header.Append("end_header\n");
        var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        stream.Write(headerBytes);
        if (binary)
        {
            using var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            for (var i = 0; i < vertices; i++)
            {
                var (x, y, z) = position(i);
                w.Write(x);
                w.Write(y);
                w.Write(z);
                if (normals)
                {
                    var (a, b, c) = normal(i);
                    w.Write(a);
                    w.Write(b);
                    w.Write(c);
                }
                if (uvs)
                {
                    var (u, v) = uv(i);
                    w.Write(u);
                    w.Write(v);
                }
                if (colours)
                {
                    var rgb = colour(i);
                    w.Write((byte)(rgb >> 16));
                    w.Write((byte)(rgb >> 8));
                    w.Write((byte)rgb);
                }
            }
            for (var t = 0; t < triangles; t++)
            {
                var (a, b, c) = triangle(t);
                w.Write((byte)3);
                w.Write((int)a);
                w.Write((int)b);
                w.Write((int)c);
            }
            return;
        }
        using var tw = new StreamWriter(stream, Encoding.ASCII, 1 << 16, leaveOpen: true) { NewLine = "\n" };
        for (var i = 0; i < vertices; i++)
        {
            var (x, y, z) = position(i);
            var sb = new StringBuilder();
            sb.Append(F(x)).Append(' ').Append(F(y)).Append(' ').Append(F(z));
            if (normals)
            {
                var (a, b, c) = normal(i);
                sb.Append(' ').Append(F(a)).Append(' ').Append(F(b)).Append(' ').Append(F(c));
            }
            if (uvs)
            {
                var (u, v) = uv(i);
                sb.Append(' ').Append(F(u)).Append(' ').Append(F(v));
            }
            if (colours)
            {
                var rgb = colour(i);
                sb.Append(' ').Append(((rgb >> 16) & 255).ToString(Inv)).Append(' ').Append(((rgb >> 8) & 255).ToString(Inv))
                    .Append(' ').Append((rgb & 255).ToString(Inv));
            }
            tw.WriteLine(sb.ToString());
        }
        for (var t = 0; t < triangles; t++)
        {
            var (a, b, c) = triangle(t);
            tw.WriteLine(string.Create(Inv, $"3 {a} {b} {c}"));
        }
    }
}
