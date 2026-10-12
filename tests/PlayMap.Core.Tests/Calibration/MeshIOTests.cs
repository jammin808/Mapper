using System.Buffers.Binary;
using System.Text;
using PlayMap.Core.Geometry;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Calibration;

/// <summary>Geometry files: OBJ and PLY round trips (ASCII and binary) keep every corner's position, UV and
/// normal; the OBJ reader takes polygons, negative indices and the four corner forms; the PLY reader skips
/// what it does not use; XYZ/PTS columns, LAS 1.2 and 1.4 records and glTF/GLB scenes read back as written.</summary>
public class MeshIOTests
{
    /// <summary>A unit cube with per-face normals and UVs: 24 vertices, 12 triangles.</summary>
    private static MeshData Cube()
    {
        var pos = new List<float>();
        var nor = new List<float>();
        var uv = new List<float>();
        var idx = new List<uint>();
        Vec3d[] normals = [Vec3d.UnitX, -Vec3d.UnitX, Vec3d.UnitY, -Vec3d.UnitY, Vec3d.UnitZ, -Vec3d.UnitZ];
        foreach (var n in normals)
        {
            var u = Math.Abs(n.Y) > 0.5 ? Vec3d.UnitX : Vec3d.Cross(Vec3d.UnitY, n);
            var v = Vec3d.Cross(n, u);
            var o = (uint)(pos.Count / 3);
            foreach (var (a, b) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
            {
                var p = n * 0.5 + u * (0.5 * a) + v * (0.5 * b);
                pos.AddRange([(float)p.X, (float)p.Y, (float)p.Z]);
                nor.AddRange([(float)n.X, (float)n.Y, (float)n.Z]);
                uv.AddRange([(a + 1) / 2f, (b + 1) / 2f]);
            }
            idx.AddRange([o, o + 1, o + 2, o, o + 2, o + 3]);
        }
        return new MeshData([.. pos], [.. nor], [.. uv], [.. idx]);
    }

    /// <summary>Every triangle corner of b carries the same attributes as a's (vertex order may differ).</summary>
    private static void SameCorners(MeshData a, MeshData b, double tolerance = 1e-6)
    {
        Assert.Equal(a.TriangleCount, b.TriangleCount);
        Assert.Equal(a.HasNormals, b.HasNormals);
        Assert.Equal(a.HasUvs, b.HasUvs);
        for (var k = 0; k < a.Indices.Length; k++)
        {
            var i = (int)a.Indices[k];
            var j = (int)b.Indices[k];
            Assert.True(Vec3d.Distance(a.Position(i), b.Position(j)) <= tolerance, $"corner {k} position");
            Assert.True(Vec3d.Distance(a.Normal(i), b.Normal(j)) <= tolerance, $"corner {k} normal");
            Assert.True(Vec2d.Distance(a.Uv(i), b.Uv(j)) <= tolerance, $"corner {k} uv");
        }
    }

    [Fact]
    public void AnObjRoundTripKeepsEveryCorner()
    {
        var cube = Cube();
        var text = new StringWriter();
        MeshIO.WriteObj(text, cube);
        var read = MeshIO.ReadObj(new StringReader(text.ToString()));
        Assert.True(read.IsMesh);
        SameCorners(cube, read.Mesh!);
        Assert.Equal(24, read.Mesh!.VertexCount);
    }

    [Fact]
    public void TheObjReaderTakesPolygonsNegativeIndicesAndEveryCornerForm()
    {
        const string obj = """
            # a quad, a pentagon and a triangle in mixed forms
            mtllib scene.mtl
            o thing
            g walls
            v 0 0 0
            v 1 0 0
            v 1 1 0
            v 0 1 0
            vt 0 0
            vt 1 0
            vt 1 1
            vt 0 1
            vn 0 0 1
            usemtl paint
            s off
            f 1/1/1 2/2/1 3/3/1 4/4/1
            v 2 0 0
            v 3 0 1
            v 3 1 1
            v 2.5 1.5 0.5
            v 2 1 0
            f -5//1 -4//1 -3//1 -2//1 -1//1
            f 1 2 9
            """;
        var read = MeshIO.ReadObj(new StringReader(obj));
        var m = read.Mesh!;
        Assert.Equal(2 + 3 + 1, m.TriangleCount);
        Assert.True(m.HasUvs && m.HasNormals);
        // The quad fans from its first corner: (1,2,3) and (1,3,4).
        Assert.Equal(new Vec3d(1, 1, 0), m.Position((int)m.Indices[2]));
        // OBJ's v runs up from the bottom; PlayMap's runs down from the top.
        Assert.Equal(new Vec2d(1, 0), m.Uv((int)m.Indices[2]));
        // The pentagon's negative indices resolve against the nine vertices read by then.
        Assert.Equal(new Vec3d(2, 0, 0), m.Position((int)m.Indices[6]));
        Assert.Equal(new Vec3d(2, 1, 0), m.Position((int)m.Indices[14]));
        // A bare corner gets a zero UV and normal when others in the file have them.
        Assert.Equal(Vec3d.Zero, m.Normal((int)m.Indices[17]));

        var cloud = MeshIO.ReadObj(new StringReader("v 1 2 3 1 0 0\nv 4 5 6 0 1 0\n"));
        Assert.False(cloud.IsMesh);
        Assert.Equal(new Vec3d(4, 5, 6), cloud.Cloud!.Points[1]);
        Assert.Equal(0x00FF00, cloud.Cloud.Colors[1]);
        Assert.Throws<FormatException>(() => MeshIO.ReadObj(new StringReader("v 0 0 0\nf 1 2 3\n")));
    }

    [Fact]
    public void BinaryAndAsciiPlyRoundTripsKeepCornersAndColours()
    {
        var cube = Cube();
        var colours = Enumerable.Range(0, cube.VertexCount).Select(i => i * 0x0A0B0C).ToArray();
        foreach (var binary in new[] { true, false })
        {
            using var stream = new MemoryStream();
            MeshIO.WritePly(stream, cube, colours, binary);
            stream.Position = 0;
            var read = MeshIO.ReadPly(stream);
            SameCorners(cube, read.Mesh!);
            Assert.Equal(colours.Select(c => c & 0xFFFFFF), read.MeshColors);
        }
    }

    [Fact]
    public void ThePlyReaderSkipsWhatItDoesNotUseAndReadsPointClouds()
    {
        const string ply = """
            ply
            format ascii 1.0
            comment made by hand
            element vertex 4
            property float x
            property float y
            property float z
            property uchar red
            property uchar green
            property uchar blue
            property float confidence
            element face 1
            property uchar flags
            property list uchar int vertex_indices
            element edge 1
            property int vertex1
            property int vertex2
            end_header
            0 0 0 255 0 0 0.5
            1 0 0 0 255 0 0.5
            1 1 0 0 0 255 0.5
            0 1 0 9 9 9 0.5
            7 4 0 1 2 3
            0 1
            """;
        var read = MeshIO.ReadPly(new MemoryStream(Encoding.ASCII.GetBytes(ply)));
        Assert.Equal(2, read.Mesh!.TriangleCount);
        Assert.Equal([0u, 1, 2, 0, 2, 3], read.Mesh.Indices);
        Assert.Equal(0x0000FF, read.MeshColors[2]);
        Assert.Equal(new Vec3d(1, 1, 0), read.ToPointCloud().Points[2]);

        // A cloud with normals written and read back; invalid points are not written.
        var cloud = new PointCloud([new(1, 2, 3), PointCloud.Invalid, new(-4, 5.5, 6)], [Vec3d.UnitX, Vec3d.UnitY, Vec3d.UnitZ], [0x112233, 0, 0x445566]);
        using var stream = new MemoryStream();
        MeshIO.WritePly(stream, cloud);
        stream.Position = 0;
        var back = MeshIO.ReadPly(stream);
        Assert.False(back.IsMesh);
        Assert.Equal(2, back.Cloud!.Count);
        Assert.Equal(new Vec3d(-4, 5.5, 6), back.Cloud.Points[1]);
        Assert.Equal(Vec3d.UnitZ, back.Cloud.Normals[1]);
        Assert.Equal(0x445566, back.Cloud.Colors[1]);

        // Big-endian binary: one vertex.
        var header = Encoding.ASCII.GetBytes("ply\nformat binary_big_endian 1.0\nelement vertex 1\nproperty float x\nproperty float y\nproperty double z\nend_header\n");
        var body = new byte[16];
        BinaryPrimitives.WriteSingleBigEndian(body, 1.5f);
        BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(4), -2f);
        BinaryPrimitives.WriteDoubleBigEndian(body.AsSpan(8), 3.25);
        var big = MeshIO.ReadPly(new MemoryStream([.. header, .. body]));
        Assert.Equal(new Vec3d(1.5, -2, 3.25), big.Cloud!.Points[0]);
    }

    [Fact]
    public void XyzAndPtsReadersFollowTheColumns()
    {
        var pts = MeshIO.ReadXyz(new StringReader("2\n1.5 2.5 3.5 -120 255 128 0\n4 5 6 -50 1 2 3\n"));
        Assert.Equal(2, pts.Count);
        Assert.Equal(new Vec3d(1.5, 2.5, 3.5), pts.Points[0]);
        Assert.Equal(0xFF8000, pts.Colors[0]);

        var csv = MeshIO.ReadXyz(new StringReader("X,Y,Z\n0.5,1.5,2.5\n// note\n-1;2;3\n"));
        Assert.Equal(2, csv.Count);
        Assert.False(csv.HasColors);
        Assert.Equal(new Vec3d(-1, 2, 3), csv.Points[1]);

        var normals = MeshIO.ReadXyz(new StringReader("0 0 0 0 0.6 0.8\n1 1 1 1 0 0\n"));
        Assert.True(normals.HasNormals && !normals.HasColors);
        Assert.Equal(new Vec3d(0, 0.6, 0.8), normals.Normals[0]);

        var rgb = MeshIO.ReadXyz(new StringReader("0 0 0 10 20 30\n"));
        Assert.True(rgb.HasColors && !rgb.HasNormals);
        Assert.Equal(0x0A141E, rgb.Colors[0]);

        var intensity = MeshIO.ReadXyz(new StringReader("1 2 3 0.7\n"));
        Assert.Equal(new Vec3d(1, 2, 3), intensity.Points[0]);
        Assert.False(intensity.HasColors);
    }

    /// <summary>A LAS file with the given version, point format and points (coordinates in units of 1 mm with an
    /// offset), one variable-length record between header and points.</summary>
    private static byte[] Las(byte minor, byte format, (Vec3d P, ushort R, ushort G, ushort B)[] points)
    {
        var headerSize = minor >= 4 ? 375 : 227;
        var record = format switch { 3 => 34, 6 => 30, 7 => 36, _ => 20 };
        var vlr = 54 + 10;
        var offset = headerSize + vlr;
        var bytes = new byte[offset + points.Length * record];
        Encoding.ASCII.GetBytes("LASF").CopyTo(bytes, 0);
        bytes[24] = 1;
        bytes[25] = minor;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(94), (ushort)headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(96), (uint)offset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(100), 1);
        bytes[104] = format;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(105), (ushort)record);
        // LAS 1.4 with a new format leaves the legacy count at zero and fills the 64-bit one.
        if (minor < 4)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(107), (uint)points.Length);
        else
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(247), (ulong)points.Length);
        Vec3d scale = new(0.001, 0.001, 0.001), origin = new(500000, 4000000, 100);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(131), scale.X);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(139), scale.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(147), scale.Z);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(155), origin.X);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(163), origin.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(171), origin.Z);
        var rgbAt = format switch { 3 => 28, 7 => 30, _ => -1 };
        for (var i = 0; i < points.Length; i++)
        {
            var r = bytes.AsSpan(offset + i * record, record);
            var (p, red, green, blue) = points[i];
            BinaryPrimitives.WriteInt32LittleEndian(r, (int)Math.Round((p.X - origin.X) / scale.X));
            BinaryPrimitives.WriteInt32LittleEndian(r[4..], (int)Math.Round((p.Y - origin.Y) / scale.Y));
            BinaryPrimitives.WriteInt32LittleEndian(r[8..], (int)Math.Round((p.Z - origin.Z) / scale.Z));
            if (rgbAt < 0)
                continue;
            BinaryPrimitives.WriteUInt16LittleEndian(r[rgbAt..], red);
            BinaryPrimitives.WriteUInt16LittleEndian(r[(rgbAt + 2)..], green);
            BinaryPrimitives.WriteUInt16LittleEndian(r[(rgbAt + 4)..], blue);
        }
        return bytes;
    }

    [Fact]
    public void LasReadsVersionsOneTwoAndOneFourWithScaleOffsetAndColour()
    {
        (Vec3d, ushort, ushort, ushort)[] points =
        [
            (new Vec3d(500001.234, 4000002.5, 101.75), 65535, 32768, 0),
            (new Vec3d(499999.001, 3999998.25, 99.5), 256, 512, 65280),
        ];
        var v12 = MeshIO.ReadLas(new MemoryStream(Las(2, 3, points)));
        Assert.Equal(2, v12.Count);
        Assert.True(Vec3d.Distance(v12.Points[0], points[0].Item1) < 1e-6);
        Assert.True(Vec3d.Distance(v12.Points[1], points[1].Item1) < 1e-6);
        Assert.Equal(0xFF8000, v12.Colors[0]);
        Assert.Equal(0x0102FF, v12.Colors[1]);

        var v14 = MeshIO.ReadLas(new MemoryStream(Las(4, 6, points)));
        Assert.Equal(2, v14.Count);
        Assert.False(v14.HasColors);
        Assert.True(Vec3d.Distance(v14.Points[1], points[1].Item1) < 1e-6);

        // 8-bit values in the 16-bit fields are kept as they are.
        var narrow = MeshIO.ReadLas(new MemoryStream(Las(4, 7, [(new Vec3d(500000, 4000000, 100), 10, 20, 30)])));
        Assert.Equal(0x0A141E, narrow.Colors[0]);

        var compressed = Las(2, 3, points);
        compressed[104] |= 0x80;
        Assert.Throws<NotSupportedException>(() => MeshIO.ReadLas(new MemoryStream(compressed)));
        Assert.Throws<FormatException>(() => MeshIO.ReadLas(new MemoryStream(Encoding.ASCII.GetBytes(new string('x', 300)))));

        Assert.Equal(new Vec3d(1, 3, -2), MeshIO.ZUpToYUp(new Vec3d(1, 2, 3)));
    }

    [Fact]
    public void GltfAndGlbScenesReadWithTheirNodeTransforms()
    {
        var cube = Cube();
        var glb = MeshIO.ReadGltf(new MemoryStream(MeshIO.WriteGlb(cube)));
        SameCorners(cube, glb.Mesh!);

        // A .gltf with a data URI: one triangle, 16-bit indices, a translated and scaled node under a parent.
        var buffer = new byte[3 * 12 + 8];
        float[] tri = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        for (var i = 0; i < 9; i++)
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(i * 4), tri[i]);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(36), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(38), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(40), 2);
        var json = $$"""
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],
             "nodes":[{"children":[1],"translation":[10,0,0]},{"mesh":0,"scale":[2,2,2]}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1}]}],
             "buffers":[{"byteLength":{{buffer.Length}},"uri":"data:application/octet-stream;base64,{{Convert.ToBase64String(buffer)}}"}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36},{"buffer":0,"byteOffset":36,"byteLength":6}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},{"bufferView":1,"componentType":5123,"count":3,"type":"SCALAR"}]}
            """;
        var gltf = MeshIO.ReadGltf(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        var m = gltf.Mesh!;
        Assert.Equal(1, m.TriangleCount);
        Assert.False(m.HasNormals || m.HasUvs);
        Assert.Equal(new Vec3d(12, 0, 0), m.Position(1));
        Assert.Equal(new Vec3d(10, 2, 0), m.Position(2));

        // An external buffer needs a resolver.
        var external = json.Replace($"data:application/octet-stream;base64,{Convert.ToBase64String(buffer)}", "tri.bin", StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => MeshIO.ReadGltf(new MemoryStream(Encoding.UTF8.GetBytes(external))));
        var resolved = MeshIO.ReadGltf(new MemoryStream(Encoding.UTF8.GetBytes(external)), uri => uri == "tri.bin" ? buffer : []);
        Assert.Equal(new Vec3d(10, 2, 0), resolved.Mesh!.Position(2));
    }

    [Fact]
    public void LoadPicksTheReaderByExtension()
    {
        var dir = Path.Combine(Path.GetTempPath(), "playmap-meshio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cube = Cube();
            using (var w = new StreamWriter(Path.Combine(dir, "cube.obj")))
                MeshIO.WriteObj(w, cube);
            using (var s = File.Create(Path.Combine(dir, "cube.ply")))
                MeshIO.WritePly(s, cube);
            File.WriteAllBytes(Path.Combine(dir, "cube.glb"), MeshIO.WriteGlb(cube));
            File.WriteAllText(Path.Combine(dir, "points.pts"), "1\n1 2 3 0 4 5 6\n");
            File.WriteAllBytes(Path.Combine(dir, "points.las"), Las(2, 3, [(new Vec3d(500000, 4000000, 100), 1, 2, 3)]));
            SameCorners(cube, MeshIO.Load(Path.Combine(dir, "cube.obj")).Mesh!);
            SameCorners(cube, MeshIO.Load(Path.Combine(dir, "cube.ply")).Mesh!);
            SameCorners(cube, MeshIO.Load(Path.Combine(dir, "cube.glb")).Mesh!);
            Assert.Equal(0x040506, MeshIO.Load(Path.Combine(dir, "points.pts")).Cloud!.Colors[0]);
            Assert.Equal(1, MeshIO.Load(Path.Combine(dir, "points.las")).ToPointCloud().Count);
            Assert.Throws<NotSupportedException>(() => MeshIO.Load(Path.Combine(dir, "scan.laz")));
            Assert.Throws<NotSupportedException>(() => MeshIO.Load(Path.Combine(dir, "model.fbx")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
