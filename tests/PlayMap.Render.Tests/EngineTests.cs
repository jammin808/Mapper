using PlayMap.Core.Geometry;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Render.Engine;
using PlayMap.Render.Projection;
using PlayMap.Tests;
using Xunit.Abstractions;

namespace PlayMap.Render.Tests;

/// <summary>The engine facade: previews reach the desk's ring without a stall, the editor's view shows the
/// stage and the projectors' frusta, a warm frame allocates nothing, freeze holds the last picture, and the
/// default project renders at show size (its frame time is reported).</summary>
[Collection("gpu")]
public class EngineTests(BackendFixture backends, ITestOutputHelper log)
{
    private static string RingPath() => PreviewRing.PathFor("test", Guid.NewGuid().ToString("N"));

    [Fact]
    public void AnOutputPreviewReachesTheDesksRingWithTheRightPixels()
    {
        var path = RingPath();
        using var writer = new PreviewRingWriter(path, 64, 64);
        using var reader = new PreviewRingReader(path);
        var project = Scene.Project([], [Scene.SourceOutput("op1", SourceRef.Solid("#FF8000"), 64, 36)]);
        var got = backends.Run(leg =>
        {
            using var engine = new RenderEngine(leg.Device, new RenderEngineOptions(GpuTiming: false));
            var dest = new byte[64 * 64 * 4];
            for (var f = 0; f < 30; f++)
            {
                engine.Render(Scene.Inputs(project, 1 + f / 60.0));
                engine.Preview(new PreviewRequest("v1", writer, 32, 18, OutputId: "op1"));
                if (reader.TryRead(dest, 0, out var info))
                    return (info, dest[..(32 * 18 * 4)]);
            }
            return (default(PreviewFrameInfo), Array.Empty<byte>());
        });
        Assert.Equal(32, got.Item1.Width);
        Assert.Equal(18, got.Item1.Height);
        Assert.Equal((255, 128, 0), Pixels.At(got.Item2, 32, 16, 9));
    }

    [Fact]
    public void TheEditorViewShowsTheStageAndTheProjectorsFrusta()
    {
        var path = RingPath();
        using var writer = new PreviewRingWriter(path, 160, 90);
        using var reader = new PreviewRingReader(path);
        var mesh = new MeshData([-0.5f, -0.5f, 0, 0.5f, -0.5f, 0, 0.5f, 0.5f, 0, -0.5f, 0.5f, 0], [], [0, 1, 1, 1, 1, 0, 0, 0], [0, 1, 2, 0, 2, 3]);
        var stage = new TestStage();
        stage.Meshes["ms1"] = mesh;
        var surface = new Surface("sf1", "Wall", "ms1", Transform3.Identity, [], SurfaceContent.Empty, 1, [], true, false, "#20C060", []);
        var lampPose = Pose.LookAt(new Vec3d(0, 0.4, 2), Vec3d.Zero, new Vec3d(0, 1, 0));
        var lamp = new Projector("pj1", "Lamp", Intrinsics.FromThrowRatio(1920, 1080, 1.5), LensDistortion.None, lampPose, 0, "#FF2020", false, [], SolveLocks.None, 0);
        var project = Scene.Project([], [], stage: Stage.Empty with { Surfaces = [surface], Projectors = [lamp] });
        var eye = Pose.LookAt(new Vec3d(3, 2, 4), new Vec3d(0, 0.3, 0), new Vec3d(0, 1, 0));
        var camera = new EditorCamera(Intrinsics.FromFieldOfView(160, 90, 60), eye, Selected: new HashSet<string> { "sf1" });
        var dest = backends.Run(leg =>
        {
            using var engine = new RenderEngine(leg.Device, new RenderEngineOptions(GpuTiming: false));
            var buffer = new byte[160 * 90 * 4];
            for (var f = 0; f < 30; f++)
            {
                engine.Render(Scene.Inputs(project, 1, stage: stage));
                engine.Preview(new PreviewRequest("editor", writer, 160, 90, Editor: camera));
                if (reader.TryRead(buffer, 0, out _))
                    return buffer;
            }
            Assert.Empty(leg.TakeErrors());
            return Array.Empty<byte>();
        });
        Assert.NotEmpty(dest);
        int green = 0, red = 0;
        for (var i = 0; i < 160 * 90; i++)
        {
            var (r, g, b) = (dest[i * 4 + 2], dest[i * 4 + 1], dest[i * 4]);
            if (g > 80 && g > r + 30 && g > b + 10)
                green++;
            if (r > 180 && g < 120 && b < 120)
                red++;
        }
        Assert.True(green > 100, $"the surface's tint covers {green} pixels");
        Assert.True(red > 10, $"the frustum's lines cover {red} pixels");
    }

    [Fact]
    public void FreezeHoldsTheLastPictureAndBlackoutStillWins()
    {
        var project = Scene.Project([Scene.Gen("pattern.colorcycle", ParamSet.Empty.With("fade", false).With("interval", 2.0))],
            [Scene.SourceOutput("op1", SourceRef.Generator("gn_test"), 32, 18)]);
        backends.Run(leg =>
        {
            using var engine = new RenderEngine(leg.Device, new RenderEngineOptions(GpuTiming: false));
            var px = new byte[32 * 18 * 4];
            engine.Render(Scene.Inputs(project, 0.5));
            engine.Render(Scene.Inputs(project, 2.5) with { Freeze = true });
            Assert.True(engine.ReadOutput("op1", px));
            Assert.Equal((255, 0, 0), Pixels.At(px, 32, 5, 5));
            engine.Render(Scene.Inputs(project, 2.5));
            Assert.True(engine.ReadOutput("op1", px));
            Assert.Equal((0, 255, 0), Pixels.At(px, 32, 5, 5));
            engine.Render(Scene.Inputs(project, 2.6) with { Freeze = true, Blackout = true });
            Assert.True(engine.ReadOutput("op1", px));
            Assert.Equal((0, 0, 0), Pixels.At(px, 32, 5, 5));
        });
    }

    [Fact]
    public void APatternOverrideReplacesEveryOutputsContentAndIdentifyLaysTheCardOver()
    {
        var project = Scene.Project([Scene.Gen("pattern.flat", ParamSet.Empty.With("level", 50.0))],
            [Scene.SourceOutput("op1", SourceRef.Solid("#0000FF"), 64, 36)]);
        backends.Run(leg =>
        {
            using var engine = new RenderEngine(leg.Device, new RenderEngineOptions(GpuTiming: false));
            var px = new byte[64 * 36 * 4];
            engine.Render(Scene.Inputs(project) with { PatternOverride = SourceRef.Generator("gn_test") });
            Assert.True(engine.ReadOutput("op1", px));
            Assert.Equal((128, 128, 128), Pixels.At(px, 64, 3, 3));
            engine.Render(Scene.Inputs(project) with { Identify = true });
            Assert.True(engine.ReadOutput("op1", px));
            Assert.NotEqual((0, 0, 255), Pixels.At(px, 64, 0, 0));
        });
    }

    [Fact]
    public void AWarmFrameAllocatesNothing()
    {
        var generators = new[]
        {
            Scene.Gen("pattern.testcard", null, "gn_card"),
            Scene.Gen("fractal.julia", null, "gn_julia"),
            Scene.Gen("particles.field", ParamSet.Empty.With("count", 300), "gn_snow"),
            Scene.Gen("reactive.scene", null, "gn_plasma"),
            Scene.Gen("pattern.motion", ParamSet.Empty.With("fps", false), "gn_bar"),
        };
        var canvas = new Canvas("cv1", "Canvas", 160, 90, "#000000",
        [
            new Layer("l1", "card", SourceRef.Generator("gn_card"), new RectD(0, 0, 80, 45), 1, LayerBlend.Normal, true),
            new Layer("l2", "julia", SourceRef.Generator("gn_julia"), new RectD(80, 0, 80, 45), 1, LayerBlend.Normal, true),
            new Layer("l3", "snow", SourceRef.Generator("gn_snow"), new RectD(0, 45, 80, 45), 1, LayerBlend.Add, true),
            new Layer("l4", "plasma", SourceRef.Generator("gn_plasma"), new RectD(80, 45, 80, 45), 0.8, LayerBlend.Screen, true),
        ]);
        var output = Scene.SourceOutput("op1", SourceRef.Canvas("cv1"), 160, 90) with { Mode = OutputMode.Canvas, SourceRect = new RectD(0, 0, 160, 90) };
        var project = Scene.Project([.. generators], [output, Scene.SourceOutput("op2", SourceRef.Generator("gn_bar"), 160, 90)], [canvas]);
        var allocated = backends.Run(leg =>
        {
            using var engine = new RenderEngine(leg.Device, new RenderEngineOptions(GpuTiming: true));
            for (var f = 0; f < 20; f++)
                engine.Render(Scene.Inputs(project, 10 + f / 60.0));
            var before = Allocations.Start();
            for (var f = 20; f < 80; f++)
                engine.Render(Scene.Inputs(project, 10 + f / 60.0));
            var bytes = Allocations.Since(before);
            Assert.Empty(leg.TakeErrors());
            return bytes;
        });
        Assert.True(allocated == 0, $"sixty warm frames allocated {allocated} bytes");
    }

    [Fact]
    public void TheDefaultProjectRendersAtShowSizeAndReportsItsFrameTime()
    {
        var project = Project.CreateDefault();
        var ms = backends.Run(leg =>
        {
            using var engine = new RenderEngine(leg.Device);
            engine.WarmUp();
            engine.Render(Scene.Inputs(project, 1));
            leg.Finish();
            const int frames = 10;
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var f = 0; f < frames; f++)
                engine.Render(Scene.Inputs(project, 1 + f / 60.0));
            leg.Finish();
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds / frames;
            var px = new byte[1920 * 1080 * 4];
            Assert.True(engine.ReadOutput("op_1", px));
            Assert.Equal((255, 255, 255), Pixels.At(px, 1920, 0, 540));
            Assert.Empty(leg.TakeErrors());
            log.WriteLine($"{leg.Device.Adapter.Name}: 1920×1080 test card canvas → output, {elapsed:0.0} ms a frame");
            return elapsed;
        });
        Assert.True(ms > 0);
    }

    [Fact]
    public void AnEngineDeletesEverythingItMadeWhenDisposed()
    {
        var project = Scene.Project([Scene.Gen("particles.field")], [Scene.SourceOutput("op1", SourceRef.Generator("gn_test"), 64, 36)]);
        backends.Run(leg =>
        {
            var before = leg.Census();
            using (var engine = new RenderEngine(leg.Device))
            {
                engine.Render(Scene.Inputs(project, 3));
                Assert.True(leg.Census().Textures > before.Textures);
            }
            Assert.Equal(before, leg.Census());
        });
    }
}
