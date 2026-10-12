using PlayMap.Core.Geometry;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using Xunit.Abstractions;

namespace PlayMap.Core.Tests.Scan;

/// <summary>Point-cloud processing on the simulated floor and box (the camera's organised cloud, with a
/// millimetre of noise): normals face the sensor and match the faces, down-sampling and outlier removal do
/// what they say, RANSAC finds the floor and the box's faces, region growing separates them, a click on the
/// box's top selects that face only, and the grid mesher leaves the box's occluding edge open.</summary>
public class PointCloudTests(ITestOutputHelper output)
{
    private static readonly Lazy<(SimScene Scene, ScanSimulator Sim, PointCloud Cloud, float[] Curvature)> Floor = new(() =>
    {
        var geometry = ScanTestKit.Floor;
        var plan = ScanPlan.Create(ScanTestKit.ProjectorWidth, ScanTestKit.ProjectorHeight, new ScanPlanOptions { Mode = ScanMode.Fast });
        var sim = new ScanSimulator(geometry, plan);
        var cloud = PointCloudOps.EstimateNormals(sim.Truth.Cloud(0.001), geometry.Scene.Camera.Pose.Position, out var curvature);
        return (geometry.Scene, sim, cloud, curvature);
    });

    private static Vec3d Sensor => Floor.Value.Scene.Camera.Pose.Position;

    private static bool OnBoxTop(Vec3d p) => Math.Abs(p.Y - SimScenes.BoxHeight) < 1e-4 && Math.Abs(p.X) <= SimScenes.BoxHalf && Math.Abs(p.Z) <= SimScenes.BoxHalf;
    private static bool OnFloor(Vec3d p) => Math.Abs(p.Y) < 1e-4;
    private static bool OnBoxFront(Vec3d p) => Math.Abs(p.Z - SimScenes.BoxHalf) < 1e-4 && p.Y > 0;
    private static bool OnBoxRight(Vec3d p) => Math.Abs(p.X - SimScenes.BoxHalf) < 1e-4 && p.Y > 0;

    private static double AngleDeg(Vec3d a, Vec3d b) => Math.Acos(Math.Clamp(Math.Abs(Vec3d.Dot(a.Normalized(), b.Normalized())), 0, 1)) * 180 / Math.PI;

    [Fact]
    public void NormalsFaceTheSensorAndMatchTheSurfaces()
    {
        var (_, sim, cloud, curvature) = Floor.Value;
        var truth = sim.Truth;
        var checkedPoints = 0;
        var good = 0;
        for (var k = 0; k < cloud.Count; k++)
        {
            if (!PointCloud.IsValid(cloud.Points[k]))
                continue;
            Assert.True(Vec3d.Dot(cloud.Normals[k], Sensor - cloud.Points[k]) >= 0, "a normal faces away from the sensor");
            checkedPoints++;
            if (AngleDeg(cloud.Normals[k], truth.Normals[k]) < 10)
                good++;
        }
        output.WriteLine($"{good}/{checkedPoints} normals within 10° of the truth");
        Assert.True(good >= 0.95 * checkedPoints);
        // Flat interiors have little variation; a point on the box's front-top crease has more.
        var mid = CameraPixel(new Vec3d(-0.7, 0, -0.5));
        Assert.True(curvature[mid] < 0.02, $"floor variation {curvature[mid]}");

        var compact = PointCloudOps.VoxelDownsample(sim.Truth.Cloud(0.001), 0.012);
        var unorganised = PointCloudOps.EstimateNormals(compact, Sensor, new NormalOptions { K = 20 });
        var onTop = Enumerable.Range(0, compact.Count).Where(i => OnBoxTop(Truth(compact.Points[i]))).ToList();
        Assert.NotEmpty(onTop);
        Assert.True(onTop.Count(i => AngleDeg(unorganised.Normals[i], Vec3d.UnitY) < 10) >= 0.9 * onTop.Count);
    }

    /// <summary>The noiseless point a noisy one came from is within 5 mm; the face tests allow 0.1 mm, so
    /// classify by the nearest truth instead.</summary>
    private static Vec3d Truth(Vec3d noisy)
    {
        var points = Floor.Value.Sim.Truth.Points;
        var tree = TruthTree.Value;
        return points[tree.Nearest(noisy, out _)];
    }

    private static readonly Lazy<KdTree3> TruthTree = new(() => new KdTree3(Floor.Value.Sim.Truth.Points));

    private static int CameraPixel(Vec3d world)
    {
        var cam = Floor.Value.Scene.Camera;
        var p = CameraModel.Project(world, cam.Intrinsics, cam.Lens, cam.Pose) ?? throw new InvalidOperationException();
        return (int)p.Y * cam.Intrinsics.Width + (int)p.X;
    }

    [Fact]
    public void VoxelDownsamplingKeepsOnePointPerOccupiedVoxel()
    {
        var cloud = Floor.Value.Cloud;
        const double voxel = 0.05;
        var down = PointCloudOps.VoxelDownsample(cloud, voxel);
        var valid = cloud.Points.Count(PointCloud.IsValid);
        var voxels = cloud.Points.Where(PointCloud.IsValid).Select(p => ((long)Math.Floor(p.X / voxel), (long)Math.Floor(p.Y / voxel), (long)Math.Floor(p.Z / voxel))).Distinct().Count();
        output.WriteLine($"{valid} points → {down.Count} ({voxels} voxels)");
        Assert.Equal(voxels, down.Count);
        Assert.True(down.Count < valid / 5);
        Assert.False(down.IsOrganised);
        Assert.True(down.HasNormals);
        Assert.All(down.Normals, n => Assert.Equal(1, n.Length, 6));
        var (min, max) = cloud.Bounds();
        Assert.All(down.Points, p => Assert.True(p.X >= min.X && p.X <= max.X && p.Y >= min.Y - 1e-9 && p.Y <= max.Y && p.Z >= min.Z && p.Z <= max.Z));
    }

    [Fact]
    public void StatisticalOutlierRemovalDropsStrayPointsAndKeepsTheSurface()
    {
        var surface = PointCloudOps.VoxelDownsample(Floor.Value.Cloud, 0.015);
        var rng = new Pcg32(21);
        var (min, max) = surface.Bounds();
        var strays = Enumerable.Range(0, 300).Select(_ => new Vec3d(rng.Range(min.X, max.X), rng.Range(0.5, 1.5), rng.Range(min.Z, max.Z))).ToArray();
        var all = new PointCloud([.. surface.Points, .. strays]);
        var (kept, indices) = PointCloudOps.RemoveStatisticalOutliers(all, 12, 2.0);
        var keptStrays = indices.Count(i => i >= surface.Count);
        var keptSurface = indices.Count(i => i < surface.Count);
        output.WriteLine($"kept {keptSurface}/{surface.Count} surface points and {keptStrays}/{strays.Length} strays");
        Assert.Equal(indices.Length, kept.Count);
        Assert.True(keptStrays <= strays.Length / 20);
        Assert.True(keptSurface >= 0.97 * surface.Count);

        // An organised cloud keeps its grid.
        var (organised, _) = PointCloudOps.RemoveStatisticalOutliers(Floor.Value.Cloud, 8, 3.0);
        Assert.True(organised.IsOrganised);
        Assert.Equal(Floor.Value.Cloud.Count, organised.Count);
    }

    [Fact]
    public void RansacFindsTheFloorAndTheBoxFaces()
    {
        var cloud = Floor.Value.Cloud;
        var planes = PlaneDetector.Detect(cloud, new PlaneDetectionOptions { DistanceThreshold = 0.006, Seed = 3 });
        foreach (var p in planes)
            output.WriteLine($"{p.Count} inliers: n={p.Plane.Normal} d={p.Plane.Offset:0.0000} rms={p.RmsDistance * 1000:0.00} mm");
        Assert.True(planes.Count >= 4);
        for (var i = 1; i < planes.Count; i++)
            Assert.True(planes[i - 1].Count >= planes[i].Count);
        var floor = planes[0];
        Assert.True(AngleDeg(floor.Plane.Normal, Vec3d.UnitY) < 1);
        Assert.True(Math.Abs(floor.Plane.SignedDistance(Vec3d.Zero)) < 0.002);
        bool Has(Vec3d normal, Vec3d point) => planes.Any(p => AngleDeg(p.Plane.Normal, normal) < 2 && Math.Abs(p.Plane.SignedDistance(point)) < 0.003 && p.RmsDistance < 0.002);
        Assert.True(Has(Vec3d.UnitY, new Vec3d(0, SimScenes.BoxHeight, 0)), "no box top");
        Assert.True(Has(Vec3d.UnitZ, new Vec3d(0, 0.1, SimScenes.BoxHalf)), "no box front");
        Assert.True(Has(Vec3d.UnitX, new Vec3d(SimScenes.BoxHalf, 0.1, 0)), "no box side");
        // The floor plane holds floor points only.
        Assert.True(floor.Inliers.Count(i => OnFloor(Floor.Value.Sim.Truth.Points[i])) >= 0.99 * floor.Count);
        // Seeded: the same answer again.
        var again = PlaneDetector.Detect(cloud, new PlaneDetectionOptions { DistanceThreshold = 0.006, Seed = 3 });
        Assert.Equal(planes.Select(p => p.Count), again.Select(p => p.Count));
    }

    [Fact]
    public void RegionGrowingSeparatesTheFloorFromTheBoxFaces()
    {
        var (_, sim, cloud, curvature) = Floor.Value;
        var seg = RegionGrowing.Segment(cloud, curvature: curvature);
        var truth = sim.Truth.Points;
        output.WriteLine(string.Join(", ", seg.Regions.Take(6).Select(r => r.Length)));
        int Dominant(Func<Vec3d, bool> face)
        {
            var labels = Enumerable.Range(0, truth.Length).Where(k => PointCloud.IsValid(truth[k]) && face(truth[k]) && seg.Labels[k] >= 0).Select(k => seg.Labels[k]);
            return labels.GroupBy(l => l).OrderByDescending(g => g.Count()).First().Key;
        }
        var floor = Dominant(OnFloor);
        var top = Dominant(OnBoxTop);
        var front = Dominant(OnBoxFront);
        var right = Dominant(OnBoxRight);
        Assert.Equal(0, floor);
        Assert.Equal(4, new[] { floor, top, front, right }.Distinct().Count());
        // The box top's region is the box top.
        Assert.True(seg.Regions[top].Count(k => OnBoxTop(truth[k])) >= 0.98 * seg.Regions[top].Length);
    }

    [Fact]
    public void AClickOnTheBoxTopSelectsTheTopFaceOnly()
    {
        var (scene, sim, cloud, _) = Floor.Value;
        var truth = sim.Truth.Points;
        var pixel = CameraPixel(new Vec3d(0.05, SimScenes.BoxHeight, -0.05));
        var w = scene.Camera.Intrinsics.Width;
        var selection = ClickSelect.FromPixel(cloud, pixel % w, pixel / w);
        Assert.NotNull(selection);
        var onTop = Enumerable.Range(0, truth.Length).Count(k => PointCloud.IsValid(truth[k]) && OnBoxTop(truth[k]));
        var selectedTop = selection.Indices.Count(k => OnBoxTop(truth[k]));
        output.WriteLine($"selected {selection.Count}: {selectedTop}/{onTop} of the top face; {selection.Kind}, rms {selection.RmsDistance * 1000:0.00} mm");
        // Anything else selected sits on the top's very edge (the crease the front shares with it).
        Assert.All(selection.Indices.Where(k => !OnBoxTop(truth[k])), k => Assert.True(Math.Abs(truth[k].Y - SimScenes.BoxHeight) < 0.005));
        Assert.True(selectedTop >= 0.99 * selection.Count, $"{selectedTop} of {selection.Count} on the top");
        Assert.True(selectedTop >= 0.9 * onTop, $"{selectedTop}/{onTop}");
        Assert.Equal(SurfaceKind.Plane, selection.Kind);
        Assert.True(AngleDeg(selection.Plane!.Value.Normal, Vec3d.UnitY) < 1);
        Assert.True(selection.Plane.Value.Normal.Y > 0, "the plane's normal faces the camera's side");
        Assert.Equal(-SimScenes.BoxHeight, selection.Plane.Value.Offset, 2);

        // A click on the floor takes the floor and none of the box.
        var floorPixel = CameraPixel(new Vec3d(-0.6, 0, 0.6));
        var floor = ClickSelect.FromPixel(cloud, floorPixel % w, floorPixel / w)!;
        Assert.Equal(SurfaceKind.Plane, floor.Kind);
        Assert.All(floor.Indices, k => Assert.True(truth[k].Y < 0.005));
        Assert.True(floor.Indices.Count(k => OnFloor(truth[k])) >= 0.999 * floor.Count);
        Assert.True(floor.Count > 20000);

        // An unorganised cloud selects the same face from a seed point.
        var compact = cloud.Compact();
        var seed = Enumerable.Range(0, compact.Count).MinBy(i => (compact.Points[i] - truth[pixel]).LengthSquared);
        var fromPoint = ClickSelect.FromPoint(compact, seed);
        Assert.Equal(SurfaceKind.Plane, fromPoint.Kind);
        Assert.All(fromPoint.Indices, i => Assert.True(Math.Abs(compact.Points[i].Y - SimScenes.BoxHeight) < 0.01));
        Assert.True(fromPoint.Count >= 0.85 * onTop);
    }

    [Fact]
    public void AClickOnTheCylinderIsFreeformAndStopsAtTheWall()
    {
        var geometry = ScanTestKit.Cylinder;
        var scene = geometry.Scene;
        var plan = ScanPlan.Create(scene.Projector.Intrinsics.Width, scene.Projector.Intrinsics.Height, new ScanPlanOptions { Mode = ScanMode.Fast });
        var sim = new ScanSimulator(geometry, plan);
        var cloud = PointCloudOps.EstimateNormals(sim.Truth.Cloud(0.0005), scene.Camera.Pose.Position);
        var cam = scene.Camera;
        var p = CameraModel.Project(new Vec3d(0.1, 0.6, 0.75), cam.Intrinsics, cam.Lens, cam.Pose)!.Value;
        var selection = ClickSelect.FromPixel(cloud, (int)p.X, (int)p.Y, new ClickSelectOptions { MaxSeedAngleDeg = 80 })!;
        var truth = sim.Truth.Points;
        bool OnCylinder(Vec3d q) => Math.Abs(Math.Sqrt((q.X - 0.1) * (q.X - 0.1) + (q.Z - 0.5) * (q.Z - 0.5)) - 0.25) < 1e-3 && q.Y < 1.2 - 1e-6;
        output.WriteLine($"selected {selection.Count}, {selection.Kind}, rms {selection.RmsDistance * 1000:0.0} mm");
        Assert.Equal(SurfaceKind.Freeform, selection.Kind);
        Assert.Null(selection.Plane);
        Assert.True(selection.Indices.Count(k => OnCylinder(truth[k])) >= 0.98 * selection.Count);
        Assert.True(selection.Count > 1500);
    }

    [Fact]
    public void TheGridMesherDropsTheBoxsOccludingEdgeAndFacesTheSensor()
    {
        var (_, sim, cloud, _) = Floor.Value;
        var grid = GridMesher.Build(cloud);
        var mesh = grid.Mesh;
        var truth = sim.Truth.Points;
        output.WriteLine($"{mesh.TriangleCount} triangles, {grid.DroppedTriangles} dropped");
        Assert.True(mesh.TriangleCount > 50000);
        Assert.True(grid.DroppedTriangles > 100);
        var facing = 0;
        var boxToFloor = 0;
        var longest = 0.0;
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.Triangle(t);
            var pa = mesh.Position(a);
            var pb = mesh.Position(b);
            var pc = mesh.Position(c);
            if (Vec3d.Dot(Vec3d.Cross(pb - pa, pc - pa), Sensor - pa) > 0)
                facing++;
            longest = Math.Max(longest, Math.Max((pb - pa).Length, Math.Max((pc - pb).Length, (pa - pc).Length)));
            var cells = new[] { grid.VertexCells[a], grid.VertexCells[b], grid.VertexCells[c] };
            if (cells.Any(k => OnBoxTop(truth[k])) && cells.Any(k => OnFloor(truth[k])))
                boxToFloor++;
        }
        output.WriteLine($"{facing} of {mesh.TriangleCount} face the camera");
        // A few slivers seen edge-on can turn over in the noise; the rest face the camera.
        Assert.True(facing >= 0.999 * mesh.TriangleCount);
        Assert.Equal(0, boxToFloor);
        Assert.True(longest < 0.1, $"longest edge {longest:0.000} m");
        Assert.True(mesh.HasUvs && mesh.HasNormals);
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var uv = mesh.Uv(v);
            var k = grid.VertexCells[v];
            Assert.Equal((k % cloud.GridWidth + 0.5) / cloud.GridWidth, uv.X, 5);
            Assert.Equal((k / cloud.GridWidth + 0.5) / cloud.GridHeight, uv.Y, 5);
        }
        // Top-left origin, v down: the vertex highest in the camera's picture has the smallest v.
        var topVertex = Enumerable.Range(0, mesh.VertexCount).MinBy(v => grid.VertexCells[v] / cloud.GridWidth);
        Assert.True(mesh.Uv(topVertex).Y < 0.5);
        Assert.Equal(Enumerable.Range(0, mesh.VertexCount).Min(v => mesh.Uv(v).Y), mesh.Uv(topVertex).Y, 6);
    }
}
