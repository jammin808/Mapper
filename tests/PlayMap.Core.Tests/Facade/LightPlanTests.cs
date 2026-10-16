using System.Diagnostics;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Tests.Mapping;
using Xunit.Abstractions;

namespace PlayMap.Core.Tests.Facade;

/// <summary>The light plan (facade M11) against the analytic case: one 30 000 lm 4K projector at throw ratio 3, 100 m from a wall,
/// head-on, derate 1 and even light: its picture is 33.3 × 18.75 m, so 48 lux (± 2 %) at p50 and a pixel pitch of 8.7 mm (± 1 %); a
/// double stack lights 96; with 15 lux ambient the contrast is (48 + 15)/15 = 4.2. Two projectors side by side share their overlap
/// evenly. Twelve projectors over a wall plan in well under the budget.</summary>
public sealed class LightPlanTests(ITestOutputHelper log)
{
    private static Projector Projector(string id, Vec3d eye, Vec3d at, double lumens = 30_000) =>
        new(id, id.ToUpperInvariant(), Intrinsics.FromThrowRatio(3840, 2160, 3), LensDistortion.None, Pose.LookAt(eye, at, Vec3d.UnitY), lumens,
            "#FFFFFF", false, [], SolveLocks.None, 0);

    private static (StageScene Scene, Stage Stage) Wall(params Projector[] projectors)
    {
        var meshes = new Dictionary<string, MeshData> { ["wall"] = Primitives.Plane(60, 40, 60, 40) };
        var stage = Stage.Empty with { Surfaces = [BlendSolver3DTests.SurfaceOf("wall", "wall")], Projectors = [.. projectors] };
        return (StageScene.Build(stage, id => meshes.GetValueOrDefault(id)), stage);
    }

    private static readonly LightPlanOptions Even = LightPlanOptions.Default with { Derate = 1, Uniformity = 1, SampleSpacing = 0.25 };

    [Fact]
    public void OneProjectorAtAHundredMetresGivesFortyEightLuxAndAnEightPointSevenMillimetrePitch()
    {
        var (scene, stage) = Wall(Projector("pj1", new Vec3d(0, 0, 100), Vec3d.Zero));
        var s = Assert.Single(LightPlan.Analyse(scene, stage, null, Even with { AmbientLux = 15 }).Surfaces);
        log.WriteLine(s.Words);
        Assert.InRange(s.LuxP50, 48 * 0.98, 48 * 1.02);
        Assert.InRange(s.PitchMmP50, 8.68 * 0.99, 8.68 * 1.01);
        Assert.InRange(s.ContrastP5, 4.0, 4.25);
        Assert.InRange(s.Coverage, 0.25, 0.27); // 625 m² of 2400.
        Assert.Equal(1, s.StackDepth);
        Assert.Equal(s.LuxP50 * 0.5 / Math.PI, s.NitsP50, 6);
        Assert.True(s.DeficitLm > 0, "48 lux is short of the 100 a town wants");
    }

    [Fact]
    public void ADoubleStackLightsTwiceAndTwoSideBySideShareTheirOverlap()
    {
        var a = Projector("pj1", new Vec3d(0, 0, 100), Vec3d.Zero);
        var b = Projector("pj2", new Vec3d(0, 0, 100), Vec3d.Zero);
        var (scene, stage) = Wall(a, b);
        stage = stage with { Stacks = [new StackGroup("sk1", "S1", ["pj1", "pj2"], "pj1", StackFailover.Hold)] };
        var stacked = Assert.Single(LightPlan.Analyse(scene, stage, null, Even).Surfaces);
        log.WriteLine(stacked.Words);
        Assert.InRange(stacked.LuxP50, 96 * 0.98, 96 * 1.02);
        Assert.Equal(2, stacked.StackDepth);

        // Not stacked, the same two share every point evenly: one projector's light.
        var shared = Assert.Single(LightPlan.Analyse(scene, stage with { Stacks = [] }, null, Even).Surfaces);
        Assert.InRange(shared.LuxP50, 48 * 0.98, 48 * 1.02);
    }

    [Fact]
    public void TwelveProjectorsPlanWithinTheBudget()
    {
        var projectors = Enumerable.Range(0, 12).Select(i => Projector($"pj{i}", new Vec3d(-22 + 4 * i, 0, 100), new Vec3d(-22 + 4 * i, 0, 0))).ToArray();
        var (scene, stage) = Wall(projectors);
        var watch = Stopwatch.StartNew();
        var plan = LightPlan.Analyse(scene, stage, null, LightPlanOptions.Default with { SampleSpacing = 0.12 });
        watch.Stop();
        log.WriteLine($"{plan.Words} in {watch.ElapsedMilliseconds} ms");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(12), "4 × the 3 s budget");
        Assert.True(Assert.Single(plan.Surfaces).Coverage > 0.4);
    }
}
