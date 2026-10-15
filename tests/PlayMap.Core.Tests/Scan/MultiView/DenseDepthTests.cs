using System.Globalization;
using PlayMap.Core.Scan.MultiView;
using Xunit.Abstractions;

namespace PlayMap.Core.Tests.Scan.MultiView;

/// <summary>Dense geometry per projector from every view: the surface it measures against the scene's own mesh, how
/// much of the lit raster three views cover against one, and the merged stage mesh.</summary>
public sealed class DenseDepthTests(ITestOutputHelper output)
{
    /// <summary>The dense surface's RMS distance to the truth mesh. The plan expected ≤ 3 mm; the first run measured
    /// 0.27 and 0.28 mm (2 projectors, 512 × 320, three 480 × 360 views), so the bar is tightened to 1 mm.</summary>
    private const double DenseRmsToleranceM = 0.001;

    /// <summary>Three views must see this share of the lit raster…</summary>
    private const double ThreeViewCoverage = 0.95;

    /// <summary>…which no single view reaches (the cylinder hides part, the phone's frame leaves part out).</summary>
    private const double SingleViewCoverage = 0.80;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [Fact]
    public void TheDenseSurfaceFromAllViewsLiesOnTheTruthMesh()
    {
        var toWorld = MultiViewKit.ToWorld;
        output.WriteLine(string.Create(Inv, $"dense depth, 2 projectors at stride 2 from 3 views: {MultiViewKit.DenseTook.TotalMilliseconds:0} ms"));
        foreach (var map in MultiViewKit.Dense)
        {
            var errors = new List<double>();
            for (var c = 0; c < map.Depth.Length; c++)
            {
                if (float.IsFinite(map.Depth[c]))
                    errors.Add(MeshDistance.To(MultiViewKit.Scene.Mesh, toWorld.Apply(map.Cloud.Points[c])));
            }
            errors.Sort();
            var rms = Math.Sqrt(errors.Sum(e => e * e) / errors.Count);
            var lit = MultiViewKit.Sim.LitCells(map.ProjectorId, map.Stride).Count(x => x);
            output.WriteLine(string.Create(Inv,
                $"{map.ProjectorId}: {map.Valid} of {lit} lit cells, RMS {rms * 1000:0.000} mm, median {errors[errors.Count / 2] * 1000:0.000} mm, 99 % {errors[(int)(errors.Count * 0.99)] * 1000:0.00} mm, worst {errors[^1] * 1000:0.00} mm, median σ {map.MedianSigma * toWorld.Scale * 1000:0.00} mm"));
            Assert.Equal(2, map.Stride);
            Assert.Equal(256, map.Width);
            Assert.Equal(160, map.Height);
            Assert.True(rms < DenseRmsToleranceM, $"{map.ProjectorId}: RMS {rms * 1000:0.000} mm");
            Assert.True(map.Valid >= 0.95 * lit, $"{map.ProjectorId}: {map.Valid} of {lit}");
            // Every valid cell has a finite σ and at least one view behind it; a cell two views agree on is common.
            for (var c = 0; c < map.Depth.Length; c++)
            {
                Assert.Equal(float.IsFinite(map.Depth[c]), float.IsFinite(map.Sigma[c]));
                Assert.Equal(float.IsFinite(map.Depth[c]), map.Views[c] > 0);
            }
            Assert.True(map.Views.Count(v => v >= 2) > map.Valid / 2);
            Assert.True(map.Cloud.HasNormals);
        }
    }

    [Fact]
    public void AViewDecodedOnAnotherRasterIsRefused()
    {
        var pj1 = MultiViewKit.Solved.Projector("pj1");
        var wide = pj1 with { Intrinsics = pj1.Intrinsics with { Width = 2 * pj1.Intrinsics.Width, Cx = 2 * pj1.Intrinsics.Cx } };
        var e = Assert.Throws<ArgumentException>(() => ProjectorDepth.Build(wide, MultiViewKit.DepthViews(MultiViewKit.Solved, "pj1")));
        Assert.StartsWith("View v1 decoded a 512 × 320 raster; projector pj1 is 1024 × 320.", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThreeViewsCoverTheLitRasterThatNoSingleViewDoes()
    {
        var lit = MultiViewKit.Scene.Projectors.ToDictionary(p => p.Id, p => MultiViewKit.Sim.LitCells(p.Id, 4));
        CoverageMap Of(string projector, params string[] views) => CoverageMap.Build(projector,
            [.. views.Select(v => MultiViewKit.Capture(v, projector).Map)], 4, lit[projector]);

        var all = MultiViewKit.Scene.Projectors.Select(p => Of(p.Id, "v1", "v2", "v3")).ToList();
        var pooled = CoverageMap.SeenOf(all);
        output.WriteLine(string.Create(Inv, $"three views: {pooled:P1} ({string.Join(", ", all.Select(m => $"{m.ProjectorId} {m.Seen:P1}, twice {m.Twice:P1}"))})"));
        Assert.True(pooled >= ThreeViewCoverage, $"{pooled:P1}");
        Assert.All(all, m =>
        {
            Assert.Equal(128, m.Width);
            Assert.Equal(80, m.Height);
            Assert.True(m.Seen >= ThreeViewCoverage, $"{m.ProjectorId}: {m.Seen:P1}");
            Assert.True(m.Twice > 0.5, $"{m.ProjectorId}: twice {m.Twice:P1}");
            Assert.True(m.Twice <= m.Seen);
        });

        var best = 0.0;
        foreach (var v in MultiViewKit.Scene.Views)
        {
            var one = MultiViewKit.Scene.Projectors.Select(p => Of(p.Id, v.Id)).ToList();
            var seen = CoverageMap.SeenOf(one);
            best = Math.Max(best, seen);
            output.WriteLine(string.Create(Inv, $"{v.Id} alone: {seen:P1} ({string.Join(", ", one.Select(m => $"{m.ProjectorId} {m.Seen:P1}"))})"));
            Assert.All(one, m => Assert.Equal(0, m.TwiceCells));
            Assert.All(one, m => Assert.True(m.Seen <= SingleViewCoverage, $"{v.Id}/{m.ProjectorId}: {m.Seen:P1}"));
        }
        Assert.True(best <= SingleViewCoverage, $"best single view {best:P1}");
    }

    [Fact]
    public void TheMergedMeshCarriesEachSurfaceOnceFromTheSteadierProjector()
    {
        var maps = MultiViewKit.Dense;
        var meshes = maps.Select(m => m.Mesh()).ToList();
        var merged = ScanMerge.Merge(maps);
        output.WriteLine(string.Create(Inv,
            $"{merged.Triangles} triangles kept, {merged.DroppedTriangles} dropped, of {meshes.Sum(m => m.Mesh.TriangleCount)} ({string.Join(", ", meshes.Select(m => m.Mesh.TriangleCount))})"));
        Assert.Equal(meshes.Sum(m => m.Mesh.TriangleCount), merged.Triangles + merged.DroppedTriangles);
        Assert.True(merged.DroppedTriangles > 0);
        Assert.Equal(["pj1", "pj2"], merged.ProjectorIds.AsEnumerable());
        Assert.Equal(merged.Mesh.VertexCount, merged.VertexProjector.Length);
        Assert.Equal(merged.Mesh.VertexCount, merged.VertexCells.Length);
        Assert.Equal([0, 1], merged.VertexProjector.Distinct().Order());

        // Nothing is left uncovered: every triangle either projector offered has merged surface within two of its edges.
        var centroids = new List<Numerics.Vec3d>();
        for (var t = 0; t < merged.Mesh.Indices.Length; t += 3)
            centroids.Add((merged.Mesh.Position((int)merged.Mesh.Indices[t]) + merged.Mesh.Position((int)merged.Mesh.Indices[t + 1]) + merged.Mesh.Position((int)merged.Mesh.Indices[t + 2])) / 3);
        var tree = new Geometry.KdTree3(centroids);
        var offered = 0;
        var uncovered = 0;
        foreach (var g in meshes)
        {
            var m = g.Mesh;
            for (var t = 0; t < m.Indices.Length; t += 3)
            {
                var (a, b, c) = (m.Position((int)m.Indices[t]), m.Position((int)m.Indices[t + 1]), m.Position((int)m.Indices[t + 2]));
                var edge = Math.Max((a - b).Length, Math.Max((b - c).Length, (c - a).Length));
                tree.Nearest((a + b + c) / 3, out var d2);
                offered++;
                if (Math.Sqrt(d2) > 2 * edge)
                    uncovered++;
            }
        }
        output.WriteLine(string.Create(Inv, $"{uncovered} of {offered} offered triangles without merged surface near"));
        Assert.Equal(0, uncovered);

        // Where both projectors light the scene (the cylinder's front, the wall above it), one projector's triangles remain.
        var toWorld = MultiViewKit.ToWorld;
        foreach (var probe in new[] { new Numerics.Vec3d(0.1, 0.6, 0.75), new Numerics.Vec3d(0.3, 1.4, 0) })
        {
            var near = new HashSet<int>();
            for (var t = 0; t < merged.Mesh.Indices.Length; t += 3)
            {
                var a = (int)merged.Mesh.Indices[t];
                var centroid = toWorld.Apply((merged.Mesh.Position(a) + merged.Mesh.Position((int)merged.Mesh.Indices[t + 1]) + merged.Mesh.Position((int)merged.Mesh.Indices[t + 2])) / 3);
                if ((centroid - probe).Length < 0.03)
                    near.Add(merged.VertexProjector[a]);
            }
            output.WriteLine(string.Create(Inv, $"near {probe}: projectors {string.Join(", ", near)}"));
            Assert.Single(near);
            foreach (var p in maps.Select((m, i) => (m, i)))
            {
                // Both projectors' own meshes did reach the probe: the merge chose, nothing was missing.
                Assert.Contains(Enumerable.Range(0, meshes[p.i].Mesh.VertexCount), v => (toWorld.Apply(meshes[p.i].Mesh.Position(v)) - probe).Length < 0.03);
            }
        }
    }
}
