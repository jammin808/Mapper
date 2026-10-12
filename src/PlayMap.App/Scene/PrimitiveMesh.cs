using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.App.Scene;

/// <summary>The desk's own geometry for primitive meshes (plane, box, cylinder), enough to draw outlines and
/// pick surfaces under the cursor without the engine's mesh cache. Units are metres; a plane faces +Z with
/// its centre at the origin, a box and a cylinder sit centred on the origin. A scanned or imported mesh has
/// no desk geometry (null): the engine's picture shows it, the desk draws its gizmo only.</summary>
public static class PrimitiveMesh
{
    public static MeshData? Build(MeshAsset mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.Origin != MeshOrigin.Primitive)
            return null;
        var p = mesh.PrimitiveParams;
        return mesh.Primitive switch
        {
            "plane" => Plane(p.GetDouble("width", 1), p.GetDouble("height", 1)),
            "box" => Box(p.GetDouble("width", 1), p.GetDouble("height", 1), p.GetDouble("depth", 1)),
            "cylinder" => Cylinder(p.GetDouble("radius", 0.5), p.GetDouble("height", 1), Math.Clamp(p.GetInt("segments", 24), 3, 128)),
            _ => null,
        };
    }

    /// <summary>The mesh moved into the world by a surface's transform.</summary>
    public static MeshData ToWorld(MeshData local, Transform3 t)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(t);
        var pos = new float[local.Positions.Length];
        for (var i = 0; i < local.VertexCount; i++)
        {
            var w = t.Apply(local.Position(i));
            pos[i * 3] = (float)w.X;
            pos[i * 3 + 1] = (float)w.Y;
            pos[i * 3 + 2] = (float)w.Z;
        }
        return new MeshData(pos, [], local.Uvs, local.Indices);
    }

    /// <summary>The outline edges worth drawing: every edge that borders one triangle, or two triangles that
    /// are not coplanar (a box draws its 12 edges, not its 18 triangle sides).</summary>
    public static IReadOnlyList<(int A, int B)> FeatureEdges(MeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var faces = new Dictionary<(int, int), List<int>>();
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.Triangle(t);
            Add(faces, a, b, t);
            Add(faces, b, c, t);
            Add(faces, c, a, t);
        }
        var edges = new List<(int, int)>();
        foreach (var (key, tris) in faces)
        {
            if (tris.Count != 2)
            {
                edges.Add(key);
                continue;
            }
            var n0 = Normal(mesh, tris[0]);
            var n1 = Normal(mesh, tris[1]);
            if (Vec3d.Dot(n0, n1) < 0.999)
                edges.Add(key);
        }
        return edges;
    }

    private static void Add(Dictionary<(int, int), List<int>> faces, int a, int b, int t)
    {
        var key = a < b ? (a, b) : (b, a);
        if (!faces.TryGetValue(key, out var list))
            faces[key] = list = [];
        list.Add(t);
    }

    private static Vec3d Normal(MeshData m, int t)
    {
        var (a, b, c) = m.Triangle(t);
        return Vec3d.Cross(m.Position(b) - m.Position(a), m.Position(c) - m.Position(a)).Normalized();
    }

    public static MeshData Plane(double width, double height)
    {
        float w = (float)(width / 2), h = (float)(height / 2);
        return new MeshData([-w, -h, 0, w, -h, 0, w, h, 0, -w, h, 0], [], [0, 1, 1, 1, 1, 0, 0, 0], [0, 1, 2, 0, 2, 3]);
    }

    public static MeshData Box(double width, double height, double depth)
    {
        float x = (float)(width / 2), y = (float)(height / 2), z = (float)(depth / 2);
        var pos = new List<float>();
        var uv = new List<float>();
        var idx = new List<uint>();
        // Six faces, four corners each (own UVs per face), counter-clockwise seen from outside.
        float[][] faces =
        [
            [-x, -y, z, x, -y, z, x, y, z, -x, y, z],
            [x, -y, -z, -x, -y, -z, -x, y, -z, x, y, -z],
            [-x, -y, -z, -x, -y, z, -x, y, z, -x, y, -z],
            [x, -y, z, x, -y, -z, x, y, -z, x, y, z],
            [-x, y, z, x, y, z, x, y, -z, -x, y, -z],
            [-x, -y, -z, x, -y, -z, x, -y, z, -x, -y, z],
        ];
        foreach (var f in faces)
        {
            var b = (uint)(pos.Count / 3);
            pos.AddRange(f);
            uv.AddRange([0, 1, 1, 1, 1, 0, 0, 0]);
            idx.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
        }
        return new MeshData([.. pos], [], [.. uv], [.. idx]);
    }

    public static MeshData Cylinder(double radius, double height, int segments)
    {
        var pos = new List<float>();
        var uv = new List<float>();
        var idx = new List<uint>();
        var h = (float)(height / 2);
        for (var i = 0; i <= segments; i++)
        {
            var a = 2 * Math.PI * i / segments;
            var x = (float)(Math.Cos(a) * radius);
            var z = (float)(Math.Sin(a) * radius);
            pos.AddRange([x, -h, z, x, h, z]);
            var u = (float)i / segments;
            uv.AddRange([u, 1, u, 0]);
        }
        for (var i = 0; i < segments; i++)
        {
            var b = (uint)(i * 2);
            idx.AddRange([b, b + 1, b + 3, b, b + 3, b + 2]);
        }
        return new MeshData([.. pos], [], [.. uv], [.. idx]);
    }
}
