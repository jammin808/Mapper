using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.App.Scene;

/// <summary>A surface as the desk can draw and pick it: its world-space geometry (primitives only), the
/// outline edges and a ray caster. Built once per surface edit, not per frame.</summary>
public sealed record SceneSurface(Surface Surface, MeshData? World, MeshRaycaster? Raycaster, IReadOnlyList<(int A, int B)> Edges);

/// <summary>The desk's own picture of the stage for overlays and picking: rebuilt for the surfaces whose
/// mesh or transform changed on a push, shared by the 3D view and the projector view.</summary>
public sealed class SceneCache
{
    private readonly Dictionary<string, (Surface Surface, MeshAsset? Mesh, SceneSurface Built)> _surfaces = new(StringComparer.Ordinal);

    public IReadOnlyList<SceneSurface> Surfaces { get; private set; } = [];

    public void Update(Project p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var next = new List<SceneSurface>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in p.Stage.Surfaces)
        {
            seen.Add(s.Id);
            var mesh = p.FindMesh(s.MeshId);
            if (_surfaces.TryGetValue(s.Id, out var had) && ReferenceEquals(had.Mesh, mesh) && had.Surface.Transform == s.Transform && had.Surface.MeshId == s.MeshId)
            {
                var keep = had.Built with { Surface = s };
                _surfaces[s.Id] = (s, mesh, keep);
                next.Add(keep);
                continue;
            }
            var local = mesh is null ? null : PrimitiveMesh.Build(mesh);
            var world = local is null ? null : PrimitiveMesh.ToWorld(local, s.Transform);
            var built = new SceneSurface(s, world, world is null ? null : new MeshRaycaster(world), world is null ? [] : PrimitiveMesh.FeatureEdges(world));
            _surfaces[s.Id] = (s, mesh, built);
            next.Add(built);
        }
        foreach (var gone in _surfaces.Keys.Where(k => !seen.Contains(k)).ToList())
            _surfaces.Remove(gone);
        Surfaces = next;
    }

    /// <summary>The nearest visible surface the ray meets, and where.</summary>
    public (SceneSurface Surface, RayHit Hit)? Pick(Ray ray)
    {
        (SceneSurface, RayHit)? best = null;
        foreach (var s in Surfaces)
        {
            if (!s.Surface.Visible || s.Raycaster is null)
                continue;
            if (s.Raycaster.Cast(ray) is { } hit && (best is null || hit.Distance < best.Value.Item2.Distance))
                best = (s, hit);
        }
        return best;
    }

    /// <summary>The bounds of everything drawable (for framing the view); a unit box when empty.</summary>
    public (Vec3d Min, Vec3d Max) Bounds()
    {
        var min = new Vec3d(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3d(double.MinValue, double.MinValue, double.MinValue);
        var any = false;
        foreach (var s in Surfaces)
        {
            if (s.World is null)
                continue;
            var (a, b) = s.World.Bounds();
            min = Vec3d.Min(min, a);
            max = Vec3d.Max(max, b);
            any = true;
        }
        return any ? (min, max) : (new Vec3d(-1, 0, -1), new Vec3d(1, 2, 1));
    }
}
