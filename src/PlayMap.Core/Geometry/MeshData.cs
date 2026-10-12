using PlayMap.Core.Numerics;

namespace PlayMap.Core.Geometry;

/// <summary>Triangle mesh data, flat arrays ready for a GPU buffer: positions and normals as xyz floats,
/// UVs as uv floats, indices as uint triples (counter-clockwise front faces). Immutable by convention
/// once built — the mesh cache shares one instance between the render thread and the solvers.</summary>
public sealed class MeshData
{
    public float[] Positions { get; }
    public float[] Normals { get; }
    public float[] Uvs { get; }
    public uint[] Indices { get; }

    public MeshData(float[] positions, float[] normals, float[] uvs, uint[] indices)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(normals);
        ArgumentNullException.ThrowIfNull(uvs);
        ArgumentNullException.ThrowIfNull(indices);
        if (positions.Length % 3 != 0)
            throw new ArgumentException("Positions are xyz triples.", nameof(positions));
        var n = positions.Length / 3;
        if (normals.Length != 0 && normals.Length != n * 3)
            throw new ArgumentException("One normal per vertex (or none).", nameof(normals));
        if (uvs.Length != 0 && uvs.Length != n * 2)
            throw new ArgumentException("One UV per vertex (or none).", nameof(uvs));
        if (indices.Length % 3 != 0)
            throw new ArgumentException("Indices are triangles.", nameof(indices));
        foreach (var i in indices)
            if (i >= n)
                throw new ArgumentException("An index points past the vertices.", nameof(indices));
        Positions = positions;
        Normals = normals;
        Uvs = uvs;
        Indices = indices;
    }

    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Indices.Length / 3;
    public bool HasNormals => Normals.Length > 0;
    public bool HasUvs => Uvs.Length > 0;

    public Vec3d Position(int i) => new(Positions[i * 3], Positions[i * 3 + 1], Positions[i * 3 + 2]);
    public Vec3d Normal(int i) => HasNormals ? new Vec3d(Normals[i * 3], Normals[i * 3 + 1], Normals[i * 3 + 2]) : Vec3d.Zero;
    public Vec2d Uv(int i) => HasUvs ? new Vec2d(Uvs[i * 2], Uvs[i * 2 + 1]) : Vec2d.Zero;

    public (int A, int B, int C) Triangle(int t) => ((int)Indices[t * 3], (int)Indices[t * 3 + 1], (int)Indices[t * 3 + 2]);

    public (Vec3d Min, Vec3d Max) Bounds()
    {
        if (VertexCount == 0)
            return (Vec3d.Zero, Vec3d.Zero);
        var min = Position(0);
        var max = min;
        for (var i = 1; i < VertexCount; i++)
        {
            var p = Position(i);
            min = Vec3d.Min(min, p);
            max = Vec3d.Max(max, p);
        }
        return (min, max);
    }

    /// <summary>Area-weighted vertex normals from the faces.</summary>
    public MeshData WithComputedNormals()
    {
        var acc = new double[Positions.Length];
        for (var t = 0; t < TriangleCount; t++)
        {
            var (a, b, c) = Triangle(t);
            var n = Vec3d.Cross(Position(b) - Position(a), Position(c) - Position(a));
            foreach (var v in (ReadOnlySpan<int>)[a, b, c])
            {
                acc[v * 3] += n.X;
                acc[v * 3 + 1] += n.Y;
                acc[v * 3 + 2] += n.Z;
            }
        }
        var normals = new float[Positions.Length];
        for (var v = 0; v < VertexCount; v++)
        {
            var n = new Vec3d(acc[v * 3], acc[v * 3 + 1], acc[v * 3 + 2]).Normalized();
            normals[v * 3] = (float)n.X;
            normals[v * 3 + 1] = (float)n.Y;
            normals[v * 3 + 2] = (float)n.Z;
        }
        return new MeshData(Positions, normals, Uvs, Indices);
    }

    /// <summary>The sub-mesh of the given triangles (vertices re-indexed, unused ones dropped).</summary>
    public MeshData Subset(IReadOnlyCollection<int> triangles)
    {
        ArgumentNullException.ThrowIfNull(triangles);
        var map = new Dictionary<uint, uint>();
        var pos = new List<float>();
        var nor = new List<float>();
        var uv = new List<float>();
        var idx = new List<uint>(triangles.Count * 3);
        foreach (var t in triangles)
        {
            if (t < 0 || t >= TriangleCount)
                continue;
            for (var k = 0; k < 3; k++)
            {
                var old = Indices[t * 3 + k];
                if (!map.TryGetValue(old, out var neu))
                {
                    neu = (uint)map.Count;
                    map[old] = neu;
                    pos.AddRange(Positions.AsSpan((int)old * 3, 3));
                    if (HasNormals)
                        nor.AddRange(Normals.AsSpan((int)old * 3, 3));
                    if (HasUvs)
                        uv.AddRange(Uvs.AsSpan((int)old * 2, 2));
                }
                idx.Add(neu);
            }
        }
        return new MeshData([.. pos], [.. nor], [.. uv], [.. idx]);
    }
}
