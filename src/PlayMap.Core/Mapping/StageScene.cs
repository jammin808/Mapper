using System.Globalization;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Mapping;

/// <summary>The stage's surfaces merged into one world-space mesh for ray casting: each surface's faces
/// (all, or its selection) through its transform, one bounding-volume hierarchy over the lot, and for every
/// merged triangle the surface it came from. Hidden surfaces are left out — they neither take light nor
/// cast shadows — unless asked for. Immutable and thread-safe after construction.</summary>
public sealed class StageScene
{
    private readonly int[] _surfaceOfTriangle;
    private readonly Vec3d[] _faceNormals;

    /// <summary>The merged mesh in world coordinates (metres).</summary>
    public MeshData World { get; }

    public MeshRaycaster Raycaster { get; }

    /// <summary>The surfaces that made it in, in stage order.</summary>
    public IReadOnlyList<Surface> Surfaces { get; }

    /// <summary>What could not be included, in words (a mesh not loaded, a face index out of range).</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>The length of the scene's bounding box diagonal (1 when empty).</summary>
    public double Diagonal { get; }

    private StageScene(MeshData world, int[] surfaceOfTriangle, IReadOnlyList<Surface> surfaces, IReadOnlyList<string> problems)
    {
        World = world;
        _surfaceOfTriangle = surfaceOfTriangle;
        Surfaces = surfaces;
        Problems = problems;
        Raycaster = new MeshRaycaster(world);
        _faceNormals = new Vec3d[world.TriangleCount];
        for (var t = 0; t < world.TriangleCount; t++)
        {
            var (a, b, c) = world.Triangle(t);
            _faceNormals[t] = Vec3d.Cross(world.Position(b) - world.Position(a), world.Position(c) - world.Position(a)).Normalized();
        }
        var (min, max) = world.Bounds();
        var diag = (max - min).Length;
        Diagonal = diag > 0 ? diag : 1;
    }

    /// <summary>Merges the stage's surfaces; <paramref name="meshes"/> gives a mesh by its id (null when it
    /// is not loaded — the surface is then left out and named in <see cref="Problems"/>).</summary>
    public static StageScene Build(Stage stage, Func<string, MeshData?> meshes, bool includeHidden = false)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(meshes);
        var pos = new List<float>();
        var nor = new List<float>();
        var uv = new List<float>();
        var idx = new List<uint>();
        var owner = new List<int>();
        var included = new List<Surface>();
        var problems = new List<string>();
        var allNormals = true;
        var allUvs = true;
        foreach (var surface in stage.Surfaces)
        {
            if (!surface.Visible && !includeHidden)
                continue;
            var mesh = meshes(surface.MeshId);
            if (mesh is null)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{surface.Name}: its mesh is not loaded."));
                continue;
            }
            allNormals &= mesh.HasNormals;
            allUvs &= mesh.HasUvs;
        }
        foreach (var surface in stage.Surfaces)
        {
            if (!surface.Visible && !includeHidden)
                continue;
            var mesh = meshes(surface.MeshId);
            if (mesh is null)
                continue;
            var surfaceIndex = included.Count;
            included.Add(surface);
            var tr = surface.Transform;
            var rot = Mat3d.FromRotationVector(tr.Rotation);
            var map = new Dictionary<int, uint>();
            var faces = surface.Faces.IsDefaultOrEmpty ? Enumerable.Range(0, mesh.TriangleCount) : surface.Faces;
            var skipped = 0;
            foreach (var t in faces)
            {
                if (t < 0 || t >= mesh.TriangleCount)
                {
                    skipped++;
                    continue;
                }
                var (a, b, c) = mesh.Triangle(t);
                foreach (var v in (ReadOnlySpan<int>)[a, b, c])
                {
                    if (!map.TryGetValue(v, out var nv))
                    {
                        nv = (uint)(pos.Count / 3);
                        map[v] = nv;
                        var p = tr.Apply(mesh.Position(v));
                        pos.AddRange([(float)p.X, (float)p.Y, (float)p.Z]);
                        if (allNormals)
                        {
                            // Normals transform by the inverse transpose: rotate the normal over the scale.
                            var n = mesh.Normal(v);
                            var ns = new Vec3d(n.X / NonZero(tr.Scale.X), n.Y / NonZero(tr.Scale.Y), n.Z / NonZero(tr.Scale.Z));
                            var wn = (rot * ns).Normalized();
                            nor.AddRange([(float)wn.X, (float)wn.Y, (float)wn.Z]);
                        }
                        if (allUvs)
                        {
                            var u = mesh.Uv(v);
                            uv.AddRange([(float)u.X, (float)u.Y]);
                        }
                    }
                    idx.Add(nv);
                }
                // A mirrored transform (negative determinant of the scale) turns the winding inside out.
                if (tr.Scale.X * tr.Scale.Y * tr.Scale.Z < 0)
                    (idx[^1], idx[^2]) = (idx[^2], idx[^1]);
                owner.Add(surfaceIndex);
            }
            if (skipped > 0)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{surface.Name}: {skipped} of its faces are not in its mesh."));
        }
        var world = new MeshData([.. pos], allNormals ? [.. nor] : [], allUvs ? [.. uv] : [], [.. idx]);
        return new StageScene(world, [.. owner], included, problems);
    }

    private static double NonZero(double s) => Math.Abs(s) < 1e-12 ? 1e-12 : s;

    /// <summary>Which surface (index into <see cref="Surfaces"/>) a merged triangle belongs to.</summary>
    public int SurfaceOfTriangle(int triangle) => _surfaceOfTriangle[triangle];

    /// <summary>The unit geometric normal of a merged triangle, as wound.</summary>
    public Vec3d FaceNormal(int triangle) => _faceNormals[triangle];

    /// <summary>The shading normal at a hit: the interpolated vertex normal when the meshes carry normals,
    /// else the face's, turned to the same side as the face's.</summary>
    public Vec3d NormalAt(in RayHit hit)
    {
        var face = _faceNormals[hit.Triangle];
        if (!World.HasNormals)
            return face;
        var (a, b, c) = World.Triangle(hit.Triangle);
        var n = (World.Normal(a) * (1 - hit.U - hit.V) + World.Normal(b) * hit.U + World.Normal(c) * hit.V).Normalized();
        if (n == Vec3d.Zero)
            return face;
        return Vec3d.Dot(n, face) < 0 ? -n : n;
    }

    /// <summary>The surface UV at a hit (zero when the meshes carry none).</summary>
    public Vec2d UvAt(in RayHit hit)
    {
        if (!World.HasUvs)
            return Vec2d.Zero;
        var (a, b, c) = World.Triangle(hit.Triangle);
        return World.Uv(a) * (1 - hit.U - hit.V) + World.Uv(b) * hit.U + World.Uv(c) * hit.V;
    }

    /// <summary>True when the straight path from <paramref name="point"/> (on a surface) to
    /// <paramref name="eye"/> is clear: the shadow test, started a hair off the surface.</summary>
    public bool Sees(Vec3d point, Vec3d eye)
    {
        var to = eye - point;
        var d = to.Length;
        if (d <= 0)
            return true;
        var dir = to / d;
        var eps = Diagonal * 1e-6 + 1e-6;
        return !Raycaster.Occluded(new Ray(point + dir * eps, dir), d - 2 * eps);
    }
}
