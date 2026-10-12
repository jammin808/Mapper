using System.Collections.Immutable;
using System.Diagnostics;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using Xunit.Abstractions;

namespace PlayMap.Core.Tests.Mapping;

/// <summary>The automatic 3D blend on a simulated stage: two projectors on a wall sum to one in light where
/// they overlap, a point only one reaches gets all of it, a box's shadow is filled by the other projector with
/// a smooth hand-over, the black floor is levelled, intensity compensation flattens the predicted light, and
/// a three-projector stage solves in well under a second.</summary>
public class BlendSolver3DTests(ITestOutputHelper output)
{
    internal static Surface SurfaceOf(string id, string meshId, Transform3? transform = null) =>
        new(id, id, meshId, transform ?? Transform3.Identity, [], SurfaceContent.Empty, 1, [], true, false, "#ffffff", []);

    internal static Projector ProjectorAt(string id, Vec3d eye, Vec3d target, double lumens = 0, double throwRatio = 1.0) =>
        new(id, id.ToUpperInvariant(), Intrinsics.FromThrowRatio(1920, 1080, throwRatio), LensDistortion.None,
            Pose.LookAt(eye, target, Vec3d.UnitY), lumens, "#ffffff", false, [], SolveLocks.None, 0);

    internal static Output OutputFor(string id, string projectorId, BlendSettings? blend = null) =>
        new(id, id.ToUpperInvariant(), OutputKind.Virtual, "", 1920, 1080, 60, OutputMode.Projector, projectorId, SourceRef.None,
            new RectD(0, 0, 1920, 1080), OutputWarp.None, blend ?? BlendSettings.Default, ColorTrim.Neutral, [], new RectD(0, 0, 1920, 1080), 0, true);

    /// <summary>A 6 × 3 m wall facing +Z at z = 0, two projectors 4 m back, 2.4 m apart, aimed 0.9 m either
    /// side of the middle: each throws a 4 m wide picture, so they share the middle 2.2 m. An optional box
    /// stands 1.2 m in front of the wall, a little right of the middle: the right projector's shadow of it
    /// falls on the wall inside the shared part, where the left projector sees the wall.</summary>
    internal static (Stage Stage, Dictionary<string, MeshData> Meshes, Output[] Outputs) Room(bool box, BlendSettings? blend = null, int wallSegments = 12, bool third = false)
    {
        var meshes = new Dictionary<string, MeshData> { ["wall"] = Primitives.Plane(6, 3, wallSegments, Math.Max(1, wallSegments / 2)) };
        var surfaces = ImmutableArray.CreateBuilder<Surface>();
        surfaces.Add(SurfaceOf("wall", "wall"));
        if (box)
        {
            meshes["box"] = Primitives.Box(0.5, 0.5, 0.5);
            surfaces.Add(SurfaceOf("box", "box", Transform3.Identity with { Position = new Vec3d(0.3, 0, 1.2) }));
        }
        var projectors = ImmutableArray.CreateBuilder<Projector>();
        projectors.Add(ProjectorAt("pa", new Vec3d(-1.2, 0, 4), new Vec3d(-0.9, 0, 0)));
        projectors.Add(ProjectorAt("pb", new Vec3d(1.2, 0, 4), new Vec3d(0.9, 0, 0)));
        var outputs = new List<Output> { OutputFor("a", "pa", blend), OutputFor("b", "pb", blend) };
        if (third)
        {
            projectors.Add(ProjectorAt("pc", new Vec3d(0, 1.0, 3.5), new Vec3d(0, 0.3, 0), throwRatio: 1.2));
            outputs.Add(OutputFor("c", "pc", blend));
        }
        var stage = Stage.Empty with { Surfaces = surfaces.ToImmutable(), Meshes = [], Projectors = projectors.ToImmutable() };
        return (stage, meshes, [.. outputs]);
    }

    private static Vec2d MapPixelToProjector(OutputBlend o, Projector p, int x, int y) =>
        new((x + 0.5) * p.Intrinsics.Width / o.Map.Width, (y + 0.5) * p.Intrinsics.Height / o.Map.Height);

    [Fact]
    public void TwoProjectorsOnAWallSumToOneInLightWhereverTheyOverlap()
    {
        var (stage, meshes, outputs) = Room(box: false);
        var solution = BlendSolver3D.Solve(stage, outputs, id => meshes.GetValueOrDefault(id));
        output.WriteLine(solution.Words);
        var a = solution.For("a")!;
        var b = solution.For("b")!;
        var pa = stage.Projectors[0];
        var pb = stage.Projectors[1];
        var scene = StageScene.Build(stage, id => meshes.GetValueOrDefault(id));
        var checkedPixels = 0;
        var worst = 0.0;
        var total = 0.0;
        // Every shared point but those within two map pixels of either raster's border, where both
        // projectors fade toward edges that nearly meet and the share changes faster than a
        // quarter-resolution map can follow (it strays by up to 2 % there, in the outermost 8 pixels).
        for (var y = 2; y < a.Map.Height - 2; y += 2)
            for (var x = 2; x < a.Map.Width - 2; x += 2)
            {
                if (a.Coverage[y * a.Map.Width + x] != 2)
                    continue;
                var ray = CameraModel.PixelRay(MapPixelToProjector(a, pa, x, y), pa.Intrinsics, pa.Lens, pa.Pose);
                var hit = scene.Raycaster.Cast(ray)!.Value;
                var inB = CameraModel.Project(hit.Point, pb.Intrinsics, pb.Lens, pb.Pose)!.Value;
                var bx = inB.X * b.Map.Width / pb.Intrinsics.Width - 0.5;
                var by = inB.Y * b.Map.Height / pb.Intrinsics.Height - 0.5;
                if (bx < 2 || by < 2 || bx > b.Map.Width - 3 || by > b.Map.Height - 3)
                    continue;
                var lightB = BilinearLight(b, bx, by);
                var sum = BlendMath.ToLight(a.Map.WeightAt(x, y), 2.2) + lightB;
                worst = Math.Max(worst, Math.Abs(sum - 1));
                total += Math.Abs(sum - 1);
                checkedPixels++;
            }
        output.WriteLine($"checked {checkedPixels} shared points, worst |sum − 1| = {worst:0.0000}, mean {total / checkedPixels:0.00000}");
        Assert.True(checkedPixels > 1000);
        Assert.True(worst < 0.02, $"the two projectors' light strays {worst} from one");
        Assert.True(total / checkedPixels < 0.001, $"on average the light strays {total / checkedPixels} from one");
        Assert.Equal(2, a.MaxOverlap);
        Assert.Equal(new[] { "B" }, a.OverlapsWith);
        Assert.InRange(a.OverlapFraction, 0.4, 0.7);
        Assert.Equal(1, a.CoveredFraction, 6);
    }

    private static double BilinearLight(OutputBlend o, double x, double y)
    {
        var x0 = (int)x;
        var y0 = (int)y;
        var fx = x - x0;
        var fy = y - y0;
        double L(int px, int py) => BlendMath.ToLight(o.Map.WeightAt(px, py), 2.2);
        return (L(x0, y0) * (1 - fx) + L(x0 + 1, y0) * fx) * (1 - fy) + (L(x0, y0 + 1) * (1 - fx) + L(x0 + 1, y0 + 1) * fx) * fy;
    }

    [Fact]
    public void APointOnlyOneProjectorReachesGetsAllOfIt()
    {
        var (stage, meshes, outputs) = Room(box: false);
        var a = BlendSolver3D.Solve(stage, outputs, id => meshes.GetValueOrDefault(id)).For("a")!;
        var alone = 0;
        for (var i = 0; i < a.Coverage.Length; i++)
        {
            if (a.Coverage[i] != 1)
                continue;
            Assert.Equal(1f, a.Map.Weights[i]);
            Assert.Equal(0f, a.Map.Pedestal[i]);
            alone++;
        }
        Assert.True(alone > a.Coverage.Length / 3);
        // The far-left column only A reaches; the far-right column of A's picture is shared and fades out.
        Assert.Equal(1f, a.Map.WeightAt(0, 135));
        Assert.True(a.Map.WeightAt(a.Map.Width - 1, 135) < 0.2f);
    }

    [Fact]
    public void ABoxsShadowIsFilledByTheOtherProjectorWithASmoothHandOver()
    {
        var (stage, meshes, outputs) = Room(box: true);
        var solution = BlendSolver3D.Solve(stage, outputs, id => meshes.GetValueOrDefault(id));
        output.WriteLine(solution.Words);
        var a = solution.For("a")!;
        var pa = stage.Projectors[0];
        var pb = stage.Projectors[1];
        var scene = StageScene.Build(stage, id => meshes.GetValueOrDefault(id));
        var inShadow = 0;
        var wall = scene.Surfaces.ToList().FindIndex(s => s.Id == "wall");
        for (var y = 0; y < a.Map.Height; y++)
            for (var x = 0; x < a.Map.Width; x++)
            {
                var hit = scene.Raycaster.Cast(CameraModel.PixelRay(MapPixelToProjector(a, pa, x, y), pa.Intrinsics, pa.Lens, pa.Pose));
                if (hit is not { } h || scene.SurfaceOfTriangle(h.Triangle) != wall)
                    continue;
                var inB = CameraModel.Project(h.Point, pb.Intrinsics, pb.Lens, pb.Pose);
                if (inB is not { } p || !CameraModel.InRaster(p, pb.Intrinsics) || scene.Sees(h.Point, pb.Pose.Position))
                    continue;
                // B's frame covers this bit of wall but the box is in the way: A carries it alone.
                Assert.Equal(1, a.Coverage[y * a.Map.Width + x]);
                Assert.Equal(1f, a.Map.WeightAt(x, y));
                inShadow++;
            }
        output.WriteLine($"{inShadow} of A's map pixels fill B's shadow");
        Assert.True(inShadow > 300);
        Assert.True(a.ShadowFraction > 0);

        // Across the shadow's left edge (wall x from −1 to 0.2 m, clear of the box in A's own view) A's light
        // goes from shared to all of it without a jump: B fades away from its occlusion edge before its
        // shadow begins, so A takes the point over gradually. (Right of the box the strip between B's shadow
        // and A's own is 10 cm wide, and the hand-over has only that much room.)
        var row = a.Map.Height / 2;
        var maxStep = 0.0;
        var lowest = 1.0;
        var highest = 0.0;
        double? previous = null;
        for (var x = 0; x < a.Map.Width; x++)
        {
            var hit = scene.Raycaster.Cast(CameraModel.PixelRay(MapPixelToProjector(a, pa, x, row), pa.Intrinsics, pa.Lens, pa.Pose))!.Value;
            if (scene.SurfaceOfTriangle(hit.Triangle) != wall || hit.Point.X < -1.0 || hit.Point.X > 0.2)
                continue;
            var light = BlendMath.ToLight(a.Map.WeightAt(x, row), 2.2);
            if (previous is { } p)
                maxStep = Math.Max(maxStep, Math.Abs(light - p));
            previous = light;
            lowest = Math.Min(lowest, light);
            highest = Math.Max(highest, light);
        }
        output.WriteLine($"A's light across the shadow's edge: {lowest:0.000} to {highest:0.000}, largest step {maxStep:0.000}");
        Assert.True(highest > 0.999 && lowest < 0.85, "the row should run from shared into the shadow");
        Assert.True(maxStep < 0.05, $"A's light jumps by {maxStep} between neighbouring pixels");
    }

    [Fact]
    public void TheBlackPedestalLevelsEveryPointToTheDeepestFloor()
    {
        const double black = 0.02;
        var (stage, meshes, outputs) = Room(box: false, BlendSettings.Default with { BlackLevel = black });
        var solution = BlendSolver3D.Solve(stage, outputs, id => meshes.GetValueOrDefault(id));
        foreach (var o in solution.Outputs)
            for (var i = 0; i < o.Coverage.Length; i++)
            {
                if (o.Coverage[i] == 0)
                    continue;
                // The point's floor: each covering projector's black, plus this projector's pedestal share
                // times the number covering it (each adds the same share).
                var floor = o.Coverage[i] * (black + BlendMath.ToLight(o.Map.Pedestal[i], 2.2));
                Assert.Equal(2 * black, floor, 1e-6);
            }
    }

    [Fact]
    public void IntensityCompensationOnlyDimsAndFlattensThePredictedLight()
    {
        // One projector thrown obliquely across the wall: the near side is much brighter than the far.
        var (stage, meshes, _) = Room(box: false);
        stage = stage with { Projectors = [ProjectorAt("pa", new Vec3d(-2.5, 0.3, 2.2), new Vec3d(0.3, 0, 0), throwRatio: 0.8)] };
        var settings = BlendSettings.Default with { IntensityCompensation = true };
        var outputs = new[] { OutputFor("a", "pa", settings) };
        foreach (var uniformity in new[] { 1.0, 0.5 })
        {
            var a = BlendSolver3D.Solve(stage, outputs, id => meshes.GetValueOrDefault(id), new BlendSolveOptions { Uniformity = uniformity }).For("a")!;
            double lo = double.MaxValue, hi = 0, rawLo = double.MaxValue, rawHi = 0;
            for (var i = 0; i < a.Coverage.Length; i++)
            {
                if (a.Coverage[i] == 0)
                    continue;
                Assert.InRange(a.Gain[i], 0f, 1f);
                var lit = a.Irradiance[i] * a.Gain[i];
                lo = Math.Min(lo, lit);
                hi = Math.Max(hi, lit);
                rawLo = Math.Min(rawLo, a.Irradiance[i]);
                rawHi = Math.Max(rawHi, a.Irradiance[i]);
                // The map carries the gain: weight in light = share × gain (alone, the share is 1).
                Assert.Equal(a.Gain[i], BlendMath.ToLight(a.Map.Weights[i], 2.2), 1e-5);
            }
            output.WriteLine($"uniformity {uniformity}: raw light {rawLo:0.000}–{rawHi:0.000}, compensated {lo:0.000}–{hi:0.000}");
            Assert.True(rawHi / rawLo > 2, "the oblique throw should be uneven to begin with");
            if (uniformity >= 1)
                Assert.True(hi / lo < 1 + 1e-5, $"full uniformity leaves a spread of {hi / lo}");
            else
                Assert.True(hi / lo < rawHi / rawLo && hi / lo > 1.01);
        }
    }

    [Fact]
    public void ThreeProjectorsOnAFewThousandTrianglesSolveQuicklyAndRepeatExactly()
    {
        var (stage, meshes, outputs) = Room(box: true, wallSegments: 60, third: true);
        Assert.True(meshes["wall"].TriangleCount >= 3000);
        BlendSolver3D.Solve(stage, outputs, id => meshes.GetValueOrDefault(id)); // warm the JIT
        var sw = Stopwatch.StartNew();
        var first = BlendSolver3D.Solve(stage, outputs, id => meshes.GetValueOrDefault(id));
        var ms = sw.Elapsed.TotalMilliseconds;
        output.WriteLine($"three projectors, 480×270 each, {meshes["wall"].TriangleCount + 12} triangles: {ms:0} ms");
        output.WriteLine(first.Words);
        // A Debug build on a shared machine: the bound catches a pathological regression (an optimised build
        // solves this stage in about half a second with every core contended).
        Assert.True(ms < 20000, $"took {ms} ms");
        Assert.Equal(3, first.Outputs.Count);
        Assert.All(first.Outputs, o => Assert.Equal(480 * 270, o.Map.Weights.Length));
        Assert.Equal(3, first.Outputs.Max(o => o.MaxOverlap));
        var again = BlendSolver3D.Solve(stage, outputs, id => meshes.GetValueOrDefault(id));
        for (var k = 0; k < 3; k++)
        {
            Assert.Equal(first.Outputs[k].Map.Weights, again.Outputs[k].Map.Weights);
            Assert.Equal(first.Outputs[k].Map.Pedestal, again.Outputs[k].Map.Pedestal);
        }
    }

    [Fact]
    public void AnOutputThatDrivesNoProjectorIsNamedNotGuessed()
    {
        var (stage, meshes, outputs) = Room(box: false);
        var stray = OutputFor("x", "nobody");
        var solution = BlendSolver3D.Solve(stage, [.. outputs, stray], id => meshes.GetValueOrDefault(id));
        Assert.Equal(2, solution.Outputs.Count);
        Assert.Contains(solution.Problems, p => p.Contains("X is in Projector mode but drives no projector", StringComparison.Ordinal));
        var hidden = stage with { Surfaces = [stage.Surfaces[0] with { Visible = false }] };
        var dark = BlendSolver3D.Solve(hidden, outputs, id => meshes.GetValueOrDefault(id));
        Assert.All(dark.Outputs, o => Assert.Equal(0, o.CoveredFraction));
    }
}
