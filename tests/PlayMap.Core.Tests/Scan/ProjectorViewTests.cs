using PlayMap.Core.Geometry;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using Xunit.Abstractions;

namespace PlayMap.Core.Tests.Scan;

/// <summary>The projector-space view: every projector pixel's camera point and the smart scan — the camera's
/// picture as the projector sees it, with scene features at the projector pixels that light them.</summary>
public class ProjectorViewTests(ITestOutputHelper output)
{
    private static Vec2d ProjectorPixelOf(SimScene scene, Vec3d world) =>
        CameraModel.Project(world, scene.Projector.Intrinsics, scene.Projector.Lens, scene.Projector.Pose) ?? throw new InvalidOperationException();

    [Fact]
    public void TheSmartScanPlacesTheFloorMarkerAtTheProjectorPixelThatLightsIt()
    {
        var scan = ScanTestKit.Robust;
        var view = ProjectorView.Build(scan.Map);
        var (mx, mz, side) = SimScenes.FloorMarker;
        var expected = ProjectorPixelOf(scan.Simulator.Scene, new Vec3d(mx, 0, mz));
        var corner = ProjectorPixelOf(scan.Simulator.Scene, new Vec3d(mx + side / 2, 0, mz + side / 2));
        // The marker's dark pixels around where it should be, against the floor's level there.
        int x0 = (int)expected.X - 25, x1 = (int)expected.X + 25, y0 = (int)expected.Y - 25, y1 = (int)expected.Y + 25;
        var levels = new List<int>();
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                if (view.Coverage[y * view.Width + x] != ProjectorCoverage.None)
                    levels.Add(view.SmartScan[y * view.Width + x]);
        levels.Sort();
        var floor = levels[levels.Count * 3 / 4];
        double sx = 0, sy = 0;
        var n = 0;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var t = y * view.Width + x;
                if (view.Coverage[t] == ProjectorCoverage.None || view.SmartScan[t] > floor / 2)
                    continue;
                sx += x + 0.5;
                sy += y + 0.5;
                n++;
            }
        }
        var found = new Vec2d(sx / n, sy / n);
        output.WriteLine($"marker expected at {expected}, found at {found} from {n} pixels; floor level {floor}");
        Assert.True(n > 50, $"{n} dark pixels");
        Assert.True(Vec2d.Distance(found, expected) < 1.0, $"marker at {found}, expected {expected}");
        // The marker's extent in projector pixels matches its size seen from the projector.
        var halfDiagonal = Vec2d.Distance(corner, expected);
        Assert.InRange(Math.Sqrt(n / 2.0), halfDiagonal * 0.75, halfDiagonal * 1.25);
    }

    [Fact]
    public void EveryMeasuredProjectorPixelsCameraPointSeesThatPixelsLight()
    {
        var scan = ScanTestKit.Robust;
        var view = ProjectorView.Build(scan.Map);
        var truth = scan.Truth;
        var errors = new List<double>();
        for (var py = 0; py < view.Height; py++)
        {
            for (var px = 0; px < view.Width; px++)
            {
                if (view.Coverage[py * view.Width + px] != ProjectorCoverage.Measured || !view.TryGetCamera(px, py, out var camera))
                    continue;
                // The truth's projector point at that camera point, between the four camera pixels around it.
                var fx = camera.X - 0.5;
                var fy = camera.Y - 0.5;
                var x0 = (int)Math.Floor(fx);
                var y0 = (int)Math.Floor(fy);
                if (x0 < 0 || y0 < 0 || x0 + 1 >= truth.Width || y0 + 1 >= truth.Height)
                    continue;
                int k00 = y0 * truth.Width + x0, k10 = k00 + 1, k01 = k00 + truth.Width, k11 = k01 + 1;
                if (!truth.Lit[k00] || !truth.Lit[k10] || !truth.Lit[k01] || !truth.Lit[k11])
                    continue;
                var tx = fx - x0;
                var ty = fy - y0;
                double Lerp(float[] a) => (a[k00] * (1 - tx) + a[k10] * tx) * (1 - ty) + (a[k01] * (1 - tx) + a[k11] * tx) * ty;
                var u = Lerp(truth.ProjectorX);
                var v = Lerp(truth.ProjectorY);
                if (Math.Abs(u - truth.ProjectorX[k00]) > 3 || Math.Abs(v - truth.ProjectorY[k00]) > 3)
                    continue; // a depth edge between the four
                errors.Add(Math.Max(Math.Abs(u - (px + 0.5)), Math.Abs(v - (py + 0.5))));
            }
        }
        errors.Sort();
        var p95 = errors[(int)(errors.Count * 0.95)];
        output.WriteLine($"{errors.Count} projector pixels; median {errors[errors.Count / 2]:0.000} px, 95th percentile {p95:0.000} px; covered {view.CoveredFraction:P1}");
        Assert.True(errors.Count > 15000);
        Assert.True(errors[errors.Count / 2] < 0.15, $"median {errors[errors.Count / 2]:0.000}");
        Assert.True(p95 < 0.5, $"95th percentile {p95:0.000}");
    }

    [Fact]
    public void ACoarseCameraIsInterpolatedAndSmallHolesAreFilled()
    {
        var scene = SimScenes.FloorAndBox(256, 160, 160, 120);
        var scan = ScanTestKit.Run(new SimGeometry(scene), new ScanPlanOptions { Mode = ScanMode.Robust });
        var view = ProjectorView.Build(scan.Map);
        var counts = view.Coverage.GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        output.WriteLine(string.Join(", ", counts.Select(kv => $"{kv.Key} {kv.Value}")));
        Assert.True(counts.GetValueOrDefault(ProjectorCoverage.Interpolated) > 1000);
        // Inside the box's top face (seen by both), every projector pixel is covered.
        var centre = ProjectorPixelOf(scene, new Vec3d(0, SimScenes.BoxHeight, 0));
        for (var y = (int)centre.Y - 6; y <= (int)centre.Y + 6; y++)
            for (var x = (int)centre.X - 10; x <= (int)centre.X + 10; x++)
                Assert.NotEqual(ProjectorCoverage.None, view.Coverage[y * view.Width + x]);
        // A hole punched into the decode is filled from its neighbours.
        var map = scan.Map;
        var hole = 0;
        for (var y = 60; y < 62; y++)
        {
            for (var x = 60; x < 62; x++)
            {
                var k = y * map.Width + x;
                if ((map.Flags[k] & DecodeStatus.Valid) != 0)
                {
                    map.Flags[k] &= ~DecodeStatus.Valid;
                    hole++;
                }
            }
        }
        Assert.True(hole > 0);
        var patched = ProjectorView.Build(map);
        Assert.True(patched.CoveredFraction >= view.CoveredFraction - 0.001);
    }

    [Fact]
    public void TheColourSmartScanShowsTheBoxsColourOnItsTop()
    {
        var scan = ScanTestKit.Robust;
        var view = ProjectorView.Build(scan.Map);
        Assert.NotNull(view.SmartScanColour);
        var top = ProjectorPixelOf(scan.Simulator.Scene, new Vec3d(0, SimScenes.BoxHeight, 0));
        var t = (int)top.Y * view.Width + (int)top.X;
        var c = view.SmartScanColour![t];
        int r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
        // The box is 0xE0B080: red over green over blue.
        Assert.True(r > g && g > b && r > 60, $"colour {c:X6}");
        Assert.InRange(view.SmartScan[t], 60, 110);
    }

    [Fact]
    public void AProjectorViewNeedsBothAxes()
    {
        var scan = ScanTestKit.Run(ScanTestKit.Floor, new ScanPlanOptions { Mode = ScanMode.Fast, Axes = ScanAxes.Columns });
        Assert.True(scan.Map.Stats.Valid > 1000);
        Assert.True(scan.Map.ProjectorY.All(float.IsNaN));
        Assert.Throws<ArgumentException>(() => ProjectorView.Build(scan.Map));
    }
}
