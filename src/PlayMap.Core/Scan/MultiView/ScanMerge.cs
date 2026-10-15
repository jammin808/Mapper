using System.Collections.Immutable;
using PlayMap.Core.Geometry;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Scan.MultiView;

/// <summary>The stage mesh of a multi-view scan: the projectors' meshes joined with their overlaps trimmed. Each
/// vertex carries the projector (an index into <see cref="ProjectorIds"/>) and the depth-map cell it came from, for
/// click-to-surface.</summary>
public sealed record MergedMesh(MeshData Mesh, ImmutableArray<string> ProjectorIds, int[] VertexProjector, int[] VertexCells, int DroppedTriangles)
{
    public int Triangles => Mesh.Indices.Length / 3;
}

/// <summary>
/// Joins per-projector meshes into one, each surface once. Projectors are taken in order of their median depth σ (the
/// steadiest first). A triangle of a later projector whose centroid lies within <c>spacing</c> local grid steps of
/// kept triangles is dropped when any of them measured that place as well or better (σ no larger); when every one of
/// them measured it worse, they are dropped instead and it is kept. So where two projectors light the same surface it
/// appears once, from the projector that measured it better there, and nothing is left uncovered.
/// </summary>
public static class ScanMerge
{
    public static MergedMesh Merge(IReadOnlyList<ProjectorDepthMap> maps, double spacing = 1.5, GridMeshOptions? mesh = null)
    {
        ArgumentNullException.ThrowIfNull(maps);
        var order = maps.Select((m, i) => (Map: m, Index: i)).OrderBy(x => x.Map.MedianSigma).ThenBy(x => x.Index).ToList();
        var kept = new List<Candidate>();
        var total = 0;
        foreach (var (map, projector) in order)
        {
            var grid = map.Mesh(mesh);
            var m = grid.Mesh;
            total += m.TriangleCount;
            var steps = PointCloudOps.GridSpacing(map.Cloud);
            var live = kept.Where(c => !c.Dropped).ToList();
            var tree = live.Count > 0 ? new KdTree3([.. live.Select(c => c.Centroid)]) : null;
            var mine = new List<Candidate>();
            for (var t = 0; t < m.Indices.Length; t += 3)
            {
                var (a, b, c) = (m.Indices[t], m.Indices[t + 1], m.Indices[t + 2]);
                var centroid = (m.Position((int)a) + m.Position((int)b) + m.Position((int)c)) / 3;
                var (ca, cb, cc) = (grid.VertexCells[a], grid.VertexCells[b], grid.VertexCells[c]);
                var sigma = (map.Sigma[ca] + map.Sigma[cb] + map.Sigma[cc]) / 3.0;
                var step = (steps[ca] + steps[cb] + steps[cc]) / 3.0;
                var candidate = new Candidate(projector, grid, t, centroid, sigma);
                if (tree is not null && double.IsFinite(step))
                {
                    var near = tree.Radius(centroid, spacing * step).Select(i => live[i]).Where(k => !k.Dropped).ToList();
                    if (near.Any(k => k.Sigma <= sigma))
                    {
                        candidate.Dropped = true;
                        continue;
                    }
                    foreach (var k in near)
                        k.Dropped = true;
                }
                mine.Add(candidate);
            }
            kept.AddRange(mine);
        }

        var positions = new List<float>();
        var normals = new List<float>();
        var uvs = new List<float>();
        var indices = new List<uint>();
        var vertexProjector = new List<int>();
        var vertexCells = new List<int>();
        var remap = new Dictionary<(GridMesh, uint), uint>();
        var count = 0;
        foreach (var k in kept.Where(k => !k.Dropped))
        {
            count++;
            var m = k.Grid.Mesh;
            for (var j = 0; j < 3; j++)
            {
                var v = m.Indices[k.First + j];
                if (!remap.TryGetValue((k.Grid, v), out var to))
                {
                    to = (uint)vertexCells.Count;
                    remap[(k.Grid, v)] = to;
                    var p = m.Position((int)v);
                    positions.Add((float)p.X);
                    positions.Add((float)p.Y);
                    positions.Add((float)p.Z);
                    if (m.HasNormals)
                    {
                        normals.Add(m.Normals[v * 3]);
                        normals.Add(m.Normals[v * 3 + 1]);
                        normals.Add(m.Normals[v * 3 + 2]);
                    }
                    uvs.Add(m.HasUvs ? m.Uvs[v * 2] : 0);
                    uvs.Add(m.HasUvs ? m.Uvs[v * 2 + 1] : 0);
                    vertexProjector.Add(k.Projector);
                    vertexCells.Add(k.Grid.VertexCells[v]);
                }
                indices.Add(to);
            }
        }
        var normalsOk = normals.Count == positions.Count;
        var merged = new MeshData([.. positions], normalsOk ? [.. normals] : [], [.. uvs], [.. indices]);
        return new MergedMesh(normalsOk ? merged : merged.WithComputedNormals(), [.. maps.Select(m => m.ProjectorId)],
            [.. vertexProjector], [.. vertexCells], total - count);
    }

    /// <summary>A triangle offered to the merge: whose it is, where, how well measured, and whether it was dropped.</summary>
    private sealed class Candidate(int projector, GridMesh grid, int first, Vec3d centroid, double sigma)
    {
        public int Projector { get; } = projector;
        public GridMesh Grid { get; } = grid;
        public int First { get; } = first;
        public Vec3d Centroid { get; } = centroid;
        public double Sigma { get; } = sigma;
        public bool Dropped { get; set; }
    }
}
