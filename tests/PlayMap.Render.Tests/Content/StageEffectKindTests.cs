using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Render.Engine;
using PlayMap.Render.Generators;

namespace PlayMap.Render.Tests.Content;

/// <summary>A surface whose generator names an effect kind this node does not have (a show made on a newer node) is a generator that
/// faults like any other: the projector pass shows its error card on that surface, the frame's content stats say it faulted, and
/// every other surface the projector lights is drawn as it would be without it.</summary>
[Collection("gpu")]
public class StageEffectKindTests(BackendFixture backends)
{
    private const int PW = 160;
    private const int PH = 90;

    private static readonly float Aspect = PW / (float)PH;

    /// <summary>A plane from x0 to x1 (y 0..1, z 0), facing +z, UV v down.</summary>
    private static MeshData Plane(float x0, float x1) => new(
        [x0, 1, 0, x1, 1, 0, x1, 0, 0, x0, 0, 0],
        [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1],
        [0, 0, 1, 0, 1, 1, 0, 1],
        [0, 1, 2, 0, 2, 3]);

    /// <summary>A lamp square on to the planes at the distance its throw fills the raster with them edge to edge.</summary>
    private static Projector Lamp() =>
        new("pj1", "Lamp", Intrinsics.FromThrowRatio(PW, PH, 1.0), LensDistortion.None,
            Pose.LookAt(new Vec3d(Aspect / 2.0, 0.5, Aspect), new Vec3d(Aspect / 2.0, 0.5, 0), new Vec3d(0, 1, 0)), 0, "#FF4000", false, [],
            SolveLocks.None, 0);

    private static Surface On(string id, string meshId, SourceRef source) =>
        new(id, id, meshId, Transform3.Identity, [], SurfaceContent.Empty with { Source = source }, 1, [], true, false, "#3EC1F3", []);

    private static int Lit(byte[] bgra, int x0, int x1)
    {
        var n = 0;
        for (var y = 0; y < PH; y++)
            for (var x = x0; x < x1; x++)
            {
                var i = (y * PW + x) * 4;
                if (bgra[i] + bgra[i + 1] + bgra[i + 2] > 300)
                    n++;
            }
        return n;
    }

    [Fact]
    public void AnEffectKindThisNodeDoesNotHaveFaultsItsOwnSurfaceAndNoOther()
    {
        var stage = new TestStage();
        stage.Meshes["ms_left"] = Plane(0, Aspect / 2);
        stage.Meshes["ms_right"] = Plane(Aspect / 2, Aspect);
        var unknown = Scene.Gen("effect.ripple", null, "gn_ripple");
        var lamp = Lamp();
        var project = Scene.Project([unknown],
            [Scene.SourceOutput("op_pj", SourceRef.None, PW, PH) with { Mode = OutputMode.Projector, ProjectorId = lamp.Id }],
            stage: Stage.Empty with
            {
                Surfaces = [On("sf_left", "ms_left", SourceRef.Generator(unknown.Id)), On("sf_right", "ms_right", SourceRef.Solid("#FFFFFF"))],
                Projectors = [lamp],
            });
        var content = new ContentFrame();
        var (px, faults) = backends.Run(leg =>
        {
            using var engine = new RenderEngine(leg.Device, new RenderEngineOptions(Dither: false, GpuTiming: false));
            for (var n = 0; n < 3; n++)
                engine.Render(new FrameInputs { Project = project, LeaderNs = 1_000_000_000L + n * 16_666_667L, Sources = NoSources.Instance, Stage = stage, Modulation = content });
            var bgra = new byte[PW * PH * 4];
            Assert.True(engine.ReadOutput("op_pj", bgra));
            Assert.Empty(leg.TakeErrors());
            return (bgra, engine.GeneratorFaults());
        });
        // The white surface beside it is lit edge to edge (all but its border pixels).
        Assert.True(Lit(px, PW / 2 + 2, PW - 2) > (PW / 2 - 4) * (PH - 4), $"the white surface lit only {Lit(px, PW / 2 + 2, PW - 2)} pixels");
        // Its own surface shows its error card, and it is said faulted.
        Assert.True(Lit(px, 2, PW / 2 - 2) > 0, "the unknown effect's surface shows no card");
        Assert.Equal("gn_ripple", Assert.Single(faults).GeneratorId);
        Assert.True(content.Stats.TryGet("gn_ripple", out var live), "the unknown effect is not in the frame's stats");
        Assert.Equal(GeneratorState.Faulted, live.State);
    }
}
