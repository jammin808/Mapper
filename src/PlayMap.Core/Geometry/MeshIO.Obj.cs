using PlayMap.Core.Numerics;

namespace PlayMap.Core.Geometry;

public static partial class MeshIO
{
    /// <summary>Reads Wavefront OBJ: v (with optional r g b), vt, vn, f with any of the v, v/vt, v//vn,
    /// v/vt/vn forms and negative (relative) indices; polygons are fan-triangulated; groups, objects,
    /// smoothing and materials are ignored. Each distinct (v, vt, vn) corner becomes one vertex. A file with
    /// no faces is a point cloud of its vertices.</summary>
    public static GeometryFile ReadObj(TextReader reader) => ReadObj(reader, GeometryReadOptions.AsStored);

    /// <summary>Reads Wavefront OBJ placed by <paramref name="options"/>: vertices are parsed in double and rebased into the
    /// options' frame before they are narrowed to float; normals are turned into the stage's axes.</summary>
    public static GeometryFile ReadObj(TextReader reader, GeometryReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);
        var pos = new List<Vec3d>();
        var col = new List<int>();
        var tex = new List<Vec2d>();
        var nor = new List<Vec3d>();
        var corners = new List<(int V, int T, int N)>();
        var polygon = new List<(int V, int T, int N)>();
        Span<Range> tok = stackalloc Range[256];
        ReadOnlySpan<char> blanks = [' ', '\t'];
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var span = line.AsSpan().Trim();
            if (span.IsEmpty || span[0] == '#')
                continue;
            var n = Split(span, tok, blanks);
            var head = span[tok[0]];
            if (head is "v" && n >= 4)
            {
                pos.Add(new Vec3d(Number(span[tok[1]]), Number(span[tok[2]]), Number(span[tok[3]])));
                // "v x y z r g b": colours in 0..1.
                if (n >= 7)
                    col.Add(Rgb(Number(span[tok[4]]) * 255, Number(span[tok[5]]) * 255, Number(span[tok[6]]) * 255));
            }
            else if (head is "vt" && n >= 3)
            {
                tex.Add(new Vec2d(Number(span[tok[1]]), Number(span[tok[2]])));
            }
            else if (head is "vn" && n >= 4)
            {
                nor.Add(new Vec3d(Number(span[tok[1]]), Number(span[tok[2]]), Number(span[tok[3]])));
            }
            else if (head is "f" && n >= 4)
            {
                polygon.Clear();
                for (var k = 1; k < n; k++)
                    polygon.Add(Corner(span[tok[k]], pos.Count, tex.Count, nor.Count));
                for (var k = 1; k + 1 < polygon.Count; k++)
                {
                    corners.Add(polygon[0]);
                    corners.Add(polygon[k]);
                    corners.Add(polygon[k + 1]);
                }
            }
        }
        var colours = col.Count == pos.Count && pos.Count > 0 ? col : null;
        var frame = options.Resolve(pos);
        if (corners.Count == 0)
            return new GeometryFile(null, new PointCloud([.. pos.Select(x => options.Apply(x, frame))], null, colours?.ToArray()), []) { Site = frame };

        var anyUv = corners.Exists(c => c.T >= 0);
        var anyNormal = corners.Exists(c => c.N >= 0);
        var map = new Dictionary<(int, int, int), uint>();
        var outPos = new List<float>();
        var outUv = new List<float>();
        var outNor = new List<float>();
        var outCol = new List<int>();
        var idx = new uint[corners.Count];
        for (var i = 0; i < corners.Count; i++)
        {
            var c = corners[i];
            if (!map.TryGetValue(c, out var v))
            {
                v = (uint)map.Count;
                map[c] = v;
                var p = options.Apply(pos[c.V], frame);
                outPos.AddRange([(float)p.X, (float)p.Y, (float)p.Z]);
                if (anyUv)
                {
                    var t = c.T >= 0 ? tex[c.T] : Vec2d.Zero;
                    outUv.AddRange([(float)t.X, (float)(1 - t.Y)]);
                }
                if (anyNormal)
                {
                    var q = c.N >= 0 ? options.ApplyDirection(nor[c.N], frame) : Vec3d.Zero;
                    outNor.AddRange([(float)q.X, (float)q.Y, (float)q.Z]);
                }
                if (colours is not null)
                    outCol.Add(colours[c.V]);
            }
            idx[i] = v;
        }
        return new GeometryFile(new MeshData([.. outPos], [.. outNor], [.. outUv], idx), null, [.. outCol]) { Site = frame };
    }

    /// <summary>One face corner "v", "v/vt", "v//vn" or "v/vt/vn" as zero-based indices (−1 for absent).</summary>
    private static (int V, int T, int N) Corner(ReadOnlySpan<char> s, int nv, int nt, int nn)
    {
        var a = s.IndexOf('/');
        if (a < 0)
            return (Resolve(s, nv), -1, -1);
        var v = Resolve(s[..a], nv);
        var rest = s[(a + 1)..];
        var b = rest.IndexOf('/');
        if (b < 0)
            return (v, rest.IsEmpty ? -1 : Resolve(rest, nt), -1);
        var t = b == 0 ? -1 : Resolve(rest[..b], nt);
        var nPart = rest[(b + 1)..];
        return (v, t, nPart.IsEmpty ? -1 : Resolve(nPart, nn));
    }

    /// <summary>A one-based index, or a negative one counted back from the end, to zero-based.</summary>
    private static int Resolve(ReadOnlySpan<char> s, int count)
    {
        var i = (int)Number(s);
        var r = i > 0 ? i - 1 : count + i;
        if (i == 0 || r < 0 || r >= count)
            throw new FormatException($"A face index {i} points outside the {count} elements read so far.");
        return r;
    }

    /// <summary>Writes Wavefront OBJ: v, vt and vn when present, and f with matching index forms.</summary>
    public static void WriteObj(TextWriter writer, MeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(mesh);
        var p = mesh.Positions;
        for (var i = 0; i < mesh.VertexCount; i++)
            writer.WriteLine($"v {F(p[i * 3])} {F(p[i * 3 + 1])} {F(p[i * 3 + 2])}");
        if (mesh.HasUvs)
            for (var i = 0; i < mesh.VertexCount; i++)
                writer.WriteLine($"vt {F(mesh.Uvs[i * 2])} {F(1 - mesh.Uvs[i * 2 + 1])}");
        if (mesh.HasNormals)
            for (var i = 0; i < mesh.VertexCount; i++)
                writer.WriteLine($"vn {F(mesh.Normals[i * 3])} {F(mesh.Normals[i * 3 + 1])} {F(mesh.Normals[i * 3 + 2])}");
        string Ref(uint v)
        {
            var k = (v + 1).ToString(Inv);
            return (mesh.HasUvs, mesh.HasNormals) switch
            {
                (true, true) => $"{k}/{k}/{k}",
                (true, false) => $"{k}/{k}",
                (false, true) => $"{k}//{k}",
                _ => k,
            };
        }
        for (var t = 0; t < mesh.TriangleCount; t++)
            writer.WriteLine($"f {Ref(mesh.Indices[t * 3])} {Ref(mesh.Indices[t * 3 + 1])} {Ref(mesh.Indices[t * 3 + 2])}");
    }
}
