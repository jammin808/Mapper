using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace PlayMap.Core.Geometry;

public static partial class MeshIO
{
    private const uint GlbMagic = 0x46546C67;
    private const uint GlbJson = 0x4E4F534A;
    private const uint GlbBin = 0x004E4942;

    /// <summary>Reads glTF 2.0 — a .gltf JSON or a binary .glb — into one mesh: every triangle primitive of
    /// every mesh node in the default scene, placed by its node's world transform (matrix or TRS), with
    /// POSITION, NORMAL, TEXCOORD_0 (top-left origin, as PlayMap keeps it) and indices. Buffers come from the
    /// GLB's binary chunk, base64 data URIs, or <paramref name="resolveUri"/> for external files.</summary>
    public static GeometryFile ReadGltf(Stream stream, Func<string, byte[]>? resolveUri = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        byte[]? bin = null;
        ReadOnlyMemory<byte> json = bytes;
        if (bytes.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == GlbMagic)
            (json, bin) = SplitGlb(bytes);
        using var doc = JsonDocument.Parse(json);
        return new GltfReader(doc.RootElement, bin, resolveUri).Read();
    }

    private static (ReadOnlyMemory<byte> Json, byte[]? Bin) SplitGlb(byte[] bytes)
    {
        var length = (int)Math.Min(bytes.Length, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
        var at = 12;
        ReadOnlyMemory<byte> json = default;
        byte[]? bin = null;
        while (at + 8 <= length)
        {
            var chunkLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
            var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
            if (at + 8 + chunkLength > length)
                throw new FormatException("A GLB chunk runs past the file.");
            if (type == GlbJson)
                json = bytes.AsMemory(at + 8, chunkLength);
            else if (type == GlbBin && bin is null)
                bin = bytes.AsSpan(at + 8, chunkLength).ToArray();
            at += 8 + ((chunkLength + 3) & ~3);
        }
        if (json.IsEmpty)
            throw new FormatException("The GLB has no JSON chunk.");
        return (json, bin);
    }

    private sealed class GltfReader(JsonElement root, byte[]? glbBin, Func<string, byte[]>? resolve)
    {
        private readonly Dictionary<int, byte[]> _buffers = [];
        private readonly List<float> _pos = [];
        private readonly List<float> _nor = [];
        private readonly List<float> _uv = [];
        private readonly List<uint> _idx = [];
        private bool _anyNormal;
        private bool _anyUv;

        public GeometryFile Read()
        {
            var nodes = Array(root, "nodes");
            if (nodes.Count == 0)
            {
                // No scene graph: every mesh as it stands.
                for (var m = 0; m < Array(root, "meshes").Count; m++)
                    AddMesh(m, Matrix4x4.Identity);
            }
            else
            {
                foreach (var n in SceneRoots(nodes.Count))
                    Walk(nodes, n, Matrix4x4.Identity, 0);
            }
            var vertexCount = _pos.Count / 3;
            var normals = _anyNormal ? [.. _nor] : System.Array.Empty<float>();
            var uvs = _anyUv ? [.. _uv] : System.Array.Empty<float>();
            if (normals.Length != 0 && normals.Length != vertexCount * 3)
                normals = [];
            if (uvs.Length != 0 && uvs.Length != vertexCount * 2)
                uvs = [];
            return new GeometryFile(new MeshData([.. _pos], normals, uvs, [.. _idx]), null, []);
        }

        private IEnumerable<int> SceneRoots(int nodeCount)
        {
            var scenes = Array(root, "scenes");
            if (scenes.Count > 0)
            {
                var s = root.TryGetProperty("scene", out var si) ? si.GetInt32() : 0;
                return Array(scenes[Math.Clamp(s, 0, scenes.Count - 1)], "nodes").Select(e => e.GetInt32()).ToArray();
            }
            // No scenes: the nodes nobody lists as a child.
            var children = new HashSet<int>();
            foreach (var n in Array(root, "nodes"))
                foreach (var c in Array(n, "children"))
                    children.Add(c.GetInt32());
            return Enumerable.Range(0, nodeCount).Where(i => !children.Contains(i)).ToArray();
        }

        private void Walk(IReadOnlyList<JsonElement> nodes, int index, Matrix4x4 parent, int depth)
        {
            if (depth > 64 || index < 0 || index >= nodes.Count)
                return;
            var node = nodes[index];
            var world = Local(node) * parent;
            if (node.TryGetProperty("mesh", out var mesh))
                AddMesh(mesh.GetInt32(), world);
            foreach (var c in Array(node, "children"))
                Walk(nodes, c.GetInt32(), world, depth + 1);
        }

        /// <summary>The node's local transform in System.Numerics' row-vector convention. glTF stores its
        /// column-vector matrices column-major, which is the same 16 numbers row-major for row vectors.</summary>
        private static Matrix4x4 Local(JsonElement node)
        {
            if (node.TryGetProperty("matrix", out var m))
            {
                var v = m.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                return new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
            }
            var s = node.TryGetProperty("scale", out var se) ? Vec(se) : [1, 1, 1];
            var r = node.TryGetProperty("rotation", out var re) ? Vec(re) : [0, 0, 0, 1];
            var t = node.TryGetProperty("translation", out var te) ? Vec(te) : [0, 0, 0];
            return Matrix4x4.CreateScale(s[0], s[1], s[2])
                * Matrix4x4.CreateFromQuaternion(new Quaternion(r[0], r[1], r[2], r[3]))
                * Matrix4x4.CreateTranslation(t[0], t[1], t[2]);
        }

        private static float[] Vec(JsonElement e) => e.EnumerateArray().Select(x => x.GetSingle()).ToArray();

        private void AddMesh(int meshIndex, Matrix4x4 world)
        {
            var meshes = Array(root, "meshes");
            if (meshIndex < 0 || meshIndex >= meshes.Count)
                return;
            Matrix4x4.Invert(world, out var inverse);
            var normalMatrix = Matrix4x4.Transpose(inverse);
            var mirrored = world.GetDeterminant() < 0;
            foreach (var prim in Array(meshes[meshIndex], "primitives"))
            {
                var mode = prim.TryGetProperty("mode", out var me) ? me.GetInt32() : 4;
                if (mode != 4 || !prim.TryGetProperty("attributes", out var attrs) || !attrs.TryGetProperty("POSITION", out var pa))
                    continue;
                var positions = Accessor(pa.GetInt32(), 3);
                var count = positions.Length / 3;
                var normals = attrs.TryGetProperty("NORMAL", out var na) ? Accessor(na.GetInt32(), 3) : null;
                var uvs = attrs.TryGetProperty("TEXCOORD_0", out var ta) ? Accessor(ta.GetInt32(), 2) : null;
                var baseIndex = (uint)(_pos.Count / 3);
                for (var i = 0; i < count; i++)
                {
                    var p = Vector3.Transform(new Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]), world);
                    _pos.AddRange([p.X, p.Y, p.Z]);
                    var n = normals is not null
                        ? Vector3.Normalize(Vector3.TransformNormal(new Vector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]), normalMatrix))
                        : Vector3.Zero;
                    _nor.AddRange([n.X, n.Y, n.Z]);
                    _uv.AddRange(uvs is not null ? [uvs[i * 2], uvs[i * 2 + 1]] : [0f, 0f]);
                }
                _anyNormal |= normals is not null;
                _anyUv |= uvs is not null;
                var indices = prim.TryGetProperty("indices", out var ia)
                    ? Accessor(ia.GetInt32(), 1).Select(x => (uint)x).ToArray()
                    : Enumerable.Range(0, count).Select(x => (uint)x).ToArray();
                for (var t = 0; t + 2 < indices.Length; t += 3)
                {
                    var (a, b, c) = (indices[t], indices[t + 1], indices[t + 2]);
                    if (a >= count || b >= count || c >= count)
                        throw new FormatException("A glTF index points past its primitive's vertices.");
                    _idx.AddRange(mirrored ? [baseIndex + a, baseIndex + c, baseIndex + b] : [baseIndex + a, baseIndex + b, baseIndex + c]);
                }
            }
        }

        /// <summary>An accessor's values as floats (integers as their values; normalised integers to 0..1),
        /// <paramref name="width"/> per element.</summary>
        private float[] Accessor(int index, int width)
        {
            var accessors = Array(root, "accessors");
            if (index < 0 || index >= accessors.Count)
                throw new FormatException("A glTF accessor index is out of range.");
            var a = accessors[index];
            if (a.TryGetProperty("sparse", out _))
                throw new NotSupportedException("Sparse glTF accessors are not read.");
            var count = a.GetProperty("count").GetInt32();
            var componentType = a.GetProperty("componentType").GetInt32();
            var normalised = a.TryGetProperty("normalized", out var ne) && ne.GetBoolean();
            var components = a.GetProperty("type").GetString() switch
            {
                "SCALAR" => 1,
                "VEC2" => 2,
                "VEC3" => 3,
                "VEC4" => 4,
                _ => throw new NotSupportedException("Matrix glTF accessors are not read."),
            };
            if (components < width)
                throw new FormatException("A glTF accessor has fewer components than its attribute needs.");
            var result = new float[count * width];
            if (!a.TryGetProperty("bufferView", out var bv))
                return result;
            var view = Array(root, "bufferViews")[bv.GetInt32()];
            var buffer = Buffer(view.GetProperty("buffer").GetInt32());
            var size = componentType switch
            {
                5120 or 5121 => 1,
                5122 or 5123 => 2,
                5125 or 5126 => 4,
                _ => throw new FormatException($"Unknown glTF component type {componentType}."),
            };
            var start = (view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0)
                + (a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
            var stride = view.TryGetProperty("byteStride", out var st) ? st.GetInt32() : size * components;
            for (var i = 0; i < count; i++)
            {
                for (var c = 0; c < width; c++)
                {
                    var at = start + i * stride + c * size;
                    if (at + size > buffer.Length)
                        throw new FormatException("A glTF accessor reads past its buffer.");
                    var s = buffer.AsSpan(at, size);
                    result[i * width + c] = componentType switch
                    {
                        5126 => BinaryPrimitives.ReadSingleLittleEndian(s),
                        5125 => BinaryPrimitives.ReadUInt32LittleEndian(s),
                        5123 => normalised ? BinaryPrimitives.ReadUInt16LittleEndian(s) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(s),
                        5122 => normalised ? Math.Max(-1, BinaryPrimitives.ReadInt16LittleEndian(s) / 32767f) : BinaryPrimitives.ReadInt16LittleEndian(s),
                        5121 => normalised ? s[0] / 255f : s[0],
                        _ => normalised ? Math.Max(-1, (sbyte)s[0] / 127f) : (sbyte)s[0],
                    };
                }
            }
            return result;
        }

        private byte[] Buffer(int index)
        {
            if (_buffers.TryGetValue(index, out var cached))
                return cached;
            var buffers = Array(root, "buffers");
            if (index < 0 || index >= buffers.Count)
                throw new FormatException("A glTF buffer index is out of range.");
            byte[] data;
            if (!buffers[index].TryGetProperty("uri", out var uri))
            {
                data = glbBin ?? throw new FormatException("A glTF buffer has no data.");
            }
            else
            {
                var u = uri.GetString() ?? "";
                if (u.StartsWith("data:", StringComparison.Ordinal))
                {
                    var comma = u.IndexOf(',', StringComparison.Ordinal);
                    data = Convert.FromBase64String(u[(comma + 1)..]);
                }
                else
                {
                    data = (resolve ?? throw new FormatException($"The glTF buffer '{u}' is an external file and no resolver was given."))(u);
                }
            }
            _buffers[index] = data;
            return data;
        }

        private static IReadOnlyList<JsonElement> Array(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().ToArray()
                : [];
    }

    /// <summary>A minimal GLB of one mesh (positions, normals, UVs in glTF's top-left convention, indices),
    /// for tests and exports.</summary>
    internal static byte[] WriteGlb(MeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        using var bin = new MemoryStream();
        using (var w = new BinaryWriter(bin, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var f in mesh.Positions)
                w.Write(f);
            foreach (var f in mesh.Normals)
                w.Write(f);
            for (var i = 0; i < mesh.Uvs.Length; i += 2)
            {
                w.Write(mesh.Uvs[i]);
                w.Write(mesh.Uvs[i + 1]);
            }
            foreach (var i in mesh.Indices)
                w.Write(i);
        }
        var n = mesh.VertexCount;
        var posBytes = n * 12;
        var norBytes = mesh.HasNormals ? n * 12 : 0;
        var uvBytes = mesh.HasUvs ? n * 8 : 0;
        var idxBytes = mesh.Indices.Length * 4;
        var (min, max) = mesh.Bounds();
        var views = new List<string> { Fmt($"{{\"buffer\":0,\"byteOffset\":0,\"byteLength\":{posBytes}}}") };
        var accessors = new List<string>
        {
            Fmt($"{{\"bufferView\":0,\"componentType\":5126,\"count\":{n},\"type\":\"VEC3\",\"min\":[{min.X},{min.Y},{min.Z}],\"max\":[{max.X},{max.Y},{max.Z}]}}"),
        };
        var attributes = new List<string> { "\"POSITION\":0" };
        var offset = posBytes;
        if (mesh.HasNormals)
        {
            views.Add(Fmt($"{{\"buffer\":0,\"byteOffset\":{offset},\"byteLength\":{norBytes}}}"));
            accessors.Add(Fmt($"{{\"bufferView\":{views.Count - 1},\"componentType\":5126,\"count\":{n},\"type\":\"VEC3\"}}"));
            attributes.Add(Fmt($"\"NORMAL\":{accessors.Count - 1}"));
            offset += norBytes;
        }
        if (mesh.HasUvs)
        {
            views.Add(Fmt($"{{\"buffer\":0,\"byteOffset\":{offset},\"byteLength\":{uvBytes}}}"));
            accessors.Add(Fmt($"{{\"bufferView\":{views.Count - 1},\"componentType\":5126,\"count\":{n},\"type\":\"VEC2\"}}"));
            attributes.Add(Fmt($"\"TEXCOORD_0\":{accessors.Count - 1}"));
            offset += uvBytes;
        }
        views.Add(Fmt($"{{\"buffer\":0,\"byteOffset\":{offset},\"byteLength\":{idxBytes}}}"));
        accessors.Add(Fmt($"{{\"bufferView\":{views.Count - 1},\"componentType\":5125,\"count\":{mesh.Indices.Length},\"type\":\"SCALAR\"}}"));
        var json = Fmt($"{{\"asset\":{{\"version\":\"2.0\"}},\"scene\":0,\"scenes\":[{{\"nodes\":[0]}}],\"nodes\":[{{\"mesh\":0}}],") +
            Fmt($"\"meshes\":[{{\"primitives\":[{{\"attributes\":{{{string.Join(",", attributes)}}},\"indices\":{accessors.Count - 1}}}]}}],") +
            Fmt($"\"buffers\":[{{\"byteLength\":{bin.Length}}}],\"bufferViews\":[{string.Join(",", views)}],\"accessors\":[{string.Join(",", accessors)}]}}");
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var jsonPadded = (jsonBytes.Length + 3) & ~3;
        var binPadded = (int)((bin.Length + 3) & ~3);
        using var glb = new MemoryStream();
        using (var w = new BinaryWriter(glb, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(GlbMagic);
            w.Write(2u);
            w.Write((uint)(12 + 8 + jsonPadded + 8 + binPadded));
            w.Write((uint)jsonPadded);
            w.Write(GlbJson);
            w.Write(jsonBytes);
            for (var i = jsonBytes.Length; i < jsonPadded; i++)
                w.Write((byte)' ');
            w.Write((uint)binPadded);
            w.Write(GlbBin);
            w.Write(bin.ToArray());
            for (var i = (int)bin.Length; i < binPadded; i++)
                w.Write((byte)0);
        }
        return glb.ToArray();
    }

    private static string Fmt(FormattableString s) => s.ToString(Inv);
}
