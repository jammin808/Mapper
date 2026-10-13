using System.Collections.Immutable;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Numerics;
using PlayMap.Render.Engine;
using PlayMap.Render.Gpu;

namespace PlayMap.Render.Projection;

/// <summary>A mesh (or a face subset of one) on the GPU: interleaved position, normal and UV, its indices,
/// and its object-space bounds.</summary>
internal sealed class GpuMesh : IDisposable
{
    public static readonly VertexAttribute[] Layout =
    [
        new(0, 3, 32, 0),
        new(1, 3, 32, 12),
        new(2, 2, 32, 24),
    ];

    public required VertexArray Vao { get; init; }
    public required GpuBuffer Vertices { get; init; }
    public required GpuBuffer Indices { get; init; }
    public required int IndexCount { get; init; }
    public required Vec3d Min { get; init; }
    public required Vec3d Max { get; init; }

    public void Dispose()
    {
        Vao.Dispose();
        Vertices.Dispose();
        Indices.Dispose();
    }
}

/// <summary>A warp mesh on the GPU.</summary>
internal sealed class GpuWarp : IDisposable
{
    public static readonly VertexAttribute[] Layout =
    [
        new(0, 2, 16, 0),
        new(1, 2, 16, 8),
    ];

    public required VertexArray Vao { get; init; }
    public required GpuBuffer Vertices { get; init; }
    public required GpuBuffer Indices { get; init; }
    public required int IndexCount { get; init; }

    public void Dispose()
    {
        Vao.Dispose();
        Vertices.Dispose();
        Indices.Dispose();
    }
}

/// <summary>GPU copies of the objects the stage hands over, keyed by the object itself: an instance is
/// uploaded once and kept while it keeps coming back; one no frame has used for <see cref="IdleFrames"/>
/// frames is deleted. Identity is the change test — the stage's objects are immutable once handed over.</summary>
internal sealed class GpuCaches : IDisposable
{
    public const int IdleFrames = 120;

    private readonly record struct MeshKey(MeshData Mesh, ImmutableArray<int> Faces);

    private sealed class Entry<T> where T : IDisposable
    {
        public required T Value;
        public long LastUsed;
    }

    private readonly GlDevice _device;
    private readonly Dictionary<MeshKey, Entry<GpuMesh>> _meshes = [];
    private readonly Dictionary<object, Entry<GpuTexture>> _textures = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<WarpMeshGeometry, Entry<GpuWarp>> _warps = new(ReferenceEqualityComparer.Instance);
    private readonly List<object> _stale = [];
    private GpuWarp? _identity;
    private long _frame;

    public GpuCaches(GlDevice device)
    {
        _device = device;
    }

    public void BeginFrame(long frame) => _frame = frame;

    /// <summary>Meshes uploaded since the caches were made (a mesh instance that keeps coming back is not).</summary>
    public int MeshUploads { get; private set; }

    /// <summary>Warp meshes uploaded since the caches were made (the identity quad is not counted).</summary>
    public int WarpUploads { get; private set; }

    /// <summary>The GPU mesh of a surface: the whole mesh, or the listed triangles of it.</summary>
    public GpuMesh Mesh(MeshData mesh, ImmutableArray<int> faces)
    {
        var key = new MeshKey(mesh, faces.IsDefault ? [] : faces);
        if (!_meshes.TryGetValue(key, out var e))
        {
            e = new Entry<GpuMesh> { Value = UploadMesh(mesh, key.Faces) };
            _meshes[key] = e;
        }
        e.LastUsed = _frame;
        return e.Value;
    }

    private GpuMesh UploadMesh(MeshData mesh, ImmutableArray<int> faces)
    {
        var source = mesh.HasNormals ? mesh : mesh.WithComputedNormals();
        var n = source.VertexCount;
        var data = new float[n * 8];
        for (var i = 0; i < n; i++)
        {
            data[i * 8] = source.Positions[i * 3];
            data[i * 8 + 1] = source.Positions[i * 3 + 1];
            data[i * 8 + 2] = source.Positions[i * 3 + 2];
            data[i * 8 + 3] = source.Normals[i * 3];
            data[i * 8 + 4] = source.Normals[i * 3 + 1];
            data[i * 8 + 5] = source.Normals[i * 3 + 2];
            if (source.HasUvs)
            {
                data[i * 8 + 6] = source.Uvs[i * 2];
                data[i * 8 + 7] = source.Uvs[i * 2 + 1];
            }
        }
        uint[] indices;
        if (faces.IsDefaultOrEmpty)
        {
            indices = source.Indices;
        }
        else
        {
            var list = new List<uint>(faces.Length * 3);
            foreach (var t in faces)
            {
                if (t < 0 || t >= source.TriangleCount)
                    continue;
                list.Add(source.Indices[t * 3]);
                list.Add(source.Indices[t * 3 + 1]);
                list.Add(source.Indices[t * 3 + 2]);
            }
            indices = [.. list];
        }
        var (min, max) = source.Bounds();
        var vb = _device.CreateBuffer(BufferKind.Vertex);
        _device.SetBufferData<float>(vb, data, dynamic: false);
        var ib = _device.CreateBuffer(BufferKind.Index);
        _device.SetBufferData<uint>(ib, indices, dynamic: false);
        var vao = _device.CreateVertexArray(GpuMesh.Layout, vb, ib);
        MeshUploads++;
        return new GpuMesh { Vao = vao, Vertices = vb, Indices = ib, IndexCount = indices.Length, Min = min, Max = max };
    }

    /// <summary>A coverage map as a one-channel float texture.</summary>
    public GpuTexture Coverage(CoverageMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return Texture(map, map.Width, map.Height, TextureFormat.R32F, map.Values, UploadFormat.R32F);
    }

    /// <summary>A blend map as a two-channel float texture: weight in red, pedestal in green.</summary>
    public GpuTexture Blend(BlendMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (_textures.TryGetValue(map, out var e))
        {
            e.LastUsed = _frame;
            return e.Value;
        }
        var n = map.Width * map.Height;
        var rg = new float[n * 2];
        for (var i = 0; i < n; i++)
        {
            rg[i * 2] = i < map.Weights.Length ? map.Weights[i] : 1f;
            rg[i * 2 + 1] = i < map.Pedestal.Length ? map.Pedestal[i] : 0f;
        }
        return Texture(map, map.Width, map.Height, TextureFormat.Rg32F, rg, UploadFormat.Rg32F);
    }

    /// <summary>A lens lookup as a two-channel float texture of source UVs.</summary>
    public GpuTexture Lens(LensLookup lens)
    {
        ArgumentNullException.ThrowIfNull(lens);
        return Texture(lens, lens.Width, lens.Height, TextureFormat.Rg32F, lens.SourceUv, UploadFormat.Rg32F, TextureFilter.Nearest);
    }

    private GpuTexture Texture(object key, int width, int height, TextureFormat format, float[] values, UploadFormat upload, TextureFilter filter = TextureFilter.Linear)
    {
        if (!_textures.TryGetValue(key, out var e))
        {
            var tex = _device.CreateTexture(Math.Max(1, width), Math.Max(1, height), format, filter);
            _device.Upload(tex, GlDevice.Bytes<float>(values), upload);
            e = new Entry<GpuTexture> { Value = tex };
            _textures[key] = e;
        }
        e.LastUsed = _frame;
        return e.Value;
    }

    /// <summary>A warp mesh on the GPU; the identity warp (a single quad) for null.</summary>
    public GpuWarp Warp(WarpMeshGeometry? geometry)
    {
        if (geometry is null)
            return _identity ??= UploadWarp([0, 0, 1, 0, 1, 1, 0, 1], [0, 0, 1, 0, 1, 1, 0, 1], [0, 1, 2, 0, 2, 3]);
        if (!_warps.TryGetValue(geometry, out var e))
        {
            e = new Entry<GpuWarp> { Value = UploadWarp(geometry.Positions, geometry.TexCoords, geometry.Indices) };
            _warps[geometry] = e;
            WarpUploads++;
        }
        e.LastUsed = _frame;
        return e.Value;
    }

    private GpuWarp UploadWarp(float[] positions, float[] texCoords, uint[] indices)
    {
        var n = positions.Length / 2;
        var data = new float[n * 4];
        for (var i = 0; i < n; i++)
        {
            data[i * 4] = positions[i * 2];
            data[i * 4 + 1] = positions[i * 2 + 1];
            data[i * 4 + 2] = i * 2 + 1 < texCoords.Length ? texCoords[i * 2] : 0;
            data[i * 4 + 3] = i * 2 + 1 < texCoords.Length ? texCoords[i * 2 + 1] : 0;
        }
        var vb = _device.CreateBuffer(BufferKind.Vertex);
        _device.SetBufferData<float>(vb, data, dynamic: false);
        var ib = _device.CreateBuffer(BufferKind.Index);
        _device.SetBufferData<uint>(ib, indices, dynamic: false);
        var vao = _device.CreateVertexArray(GpuWarp.Layout, vb, ib);
        return new GpuWarp { Vao = vao, Vertices = vb, Indices = ib, IndexCount = indices.Length };
    }

    /// <summary>Deletes what no frame has used for a while.</summary>
    public void EndFrame()
    {
        foreach (var (k, e) in _meshes)
            if (_frame - e.LastUsed > IdleFrames)
                _stale.Add(k);
        foreach (var k in _stale)
        {
            var key = (MeshKey)k;
            _meshes[key].Value.Dispose();
            _meshes.Remove(key);
        }
        _stale.Clear();
        foreach (var (k, e) in _textures)
            if (_frame - e.LastUsed > IdleFrames)
                _stale.Add(k);
        foreach (var k in _stale)
        {
            _textures[k].Value.Dispose();
            _textures.Remove(k);
        }
        _stale.Clear();
        foreach (var (k, e) in _warps)
            if (_frame - e.LastUsed > IdleFrames)
                _stale.Add(k);
        foreach (var k in _stale)
        {
            var key = (WarpMeshGeometry)k;
            _warps[key].Value.Dispose();
            _warps.Remove(key);
        }
        _stale.Clear();
    }

    public void Dispose()
    {
        foreach (var e in _meshes.Values)
            e.Value.Dispose();
        foreach (var e in _textures.Values)
            e.Value.Dispose();
        foreach (var e in _warps.Values)
            e.Value.Dispose();
        _identity?.Dispose();
        _meshes.Clear();
        _textures.Clear();
        _warps.Clear();
    }
}
