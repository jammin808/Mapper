using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Render.Engine;
using PlayMap.Render.Generators;
using PlayMap.Tests;

namespace PlayMap.Render.Tests.Content;

/// <summary>A surface-aware effect the projector pass draws on a stage surface runs on the generator's own time, as it does anywhere
/// else: on a plane the projector frames exactly, the stage's sweep is the picture the generator host draws flat at the same own time
/// (within a code), days into a show; RETIME moves it with its origin; the frame's content stats record the stage's draw (its own
/// time, ok, on the air) and keep it while a frame draws it; and a warm frame of it allocates nothing.</summary>
[Collection("gl")]
public class StageEffectTimeTests(GlFixture gl)
{
    private const int PW = 160;
    private const int PH = 90;
    private const long Days = 3L * 86_400 * 1_000_000_000;

    private static readonly float Aspect = PW / (float)PH;

    /// <summary>The flat effect's own plane in the world: x 0..aspect, y 0..1 at z 0, facing +z, UV v down.</summary>
    private static MeshData Plane() => new(
        [0, 1, 0, Aspect, 1, 0, Aspect, 0, 0, 0, 0, 0],
        [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1],
        [0, 0, 1, 0, 1, 1, 0, 1],
        [0, 1, 2, 0, 2, 3]);

    /// <summary>A lamp square on to the plane at the distance its throw fills the raster with it edge to edge.</summary>
    private static Projector Lamp() =>
        new("pj1", "Lamp", Intrinsics.FromThrowRatio(PW, PH, 1.0), LensDistortion.None,
            Pose.LookAt(new Vec3d(Aspect / 2.0, 0.5, Aspect), new Vec3d(Aspect / 2.0, 0.5, 0), new Vec3d(0, 1, 0)), 0, "#FF4000", false, [],
            SolveLocks.None, 0);

    private static Generator Sweep(long originNs) =>
        Scene.Gen("effect.sweep", ParamSet.Empty.With("speed", 0.5).With("width", 0.2), "gn_sweep") with { OriginNs = originNs };

    /// <summary>The sweep on the plane through the projector (op_pj) and, when asked, the same generator flat on an output of the
    /// same raster (op_flat, drawn by the generator host).</summary>
    private static Project Show(Generator sweep, bool flat = true, bool visible = true)
    {
        var surface = new Surface("sf1", "Plane", "ms_plane", Transform3.Identity, [], SurfaceContent.Empty with { Source = SourceRef.Generator(sweep.Id) },
            1, [], visible, false, "#3EC1F3", []);
        var lamp = Lamp();
        var projector = Scene.SourceOutput("op_pj", SourceRef.None, PW, PH) with { Mode = OutputMode.Projector, ProjectorId = lamp.Id };
        Output[] outputs = flat ? [projector, Scene.SourceOutput("op_flat", SourceRef.Generator(sweep.Id), PW, PH)] : [projector];
        return Scene.Project([sweep], [.. outputs], stage: Stage.Empty with { Surfaces = [surface], Projectors = [lamp] });
    }

    private static TestStage Resources()
    {
        var stage = new TestStage();
        stage.Meshes["ms_plane"] = Plane();
        return stage;
    }

    /// <summary>The leader instant of frame <paramref name="n"/> at 60 Hz, five seconds into the sweep's own time.</summary>
    private static long Tick(int n) => Days + 5_000_000_000 + n * 16_666_667L;

    private static FrameInputs At(Project project, long leaderNs, TestStage stage, ContentFrame content) => new()
    {
        Project = project,
        LeaderNs = leaderNs,
        Sources = NoSources.Instance,
        Stage = stage,
        Modulation = content,
    };

    private static byte[] Read(RenderEngine engine, string outputId)
    {
        var px = new byte[PW * PH * 4];
        Assert.True(engine.ReadOutput(outputId, px));
        return px;
    }

    private static int MaxDifference(byte[] a, byte[] b)
    {
        var worst = 0;
        for (var i = 0; i < a.Length; i++)
            worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
        return worst;
    }

    private static int Lit(byte[] bgra)
    {
        var n = 0;
        for (var i = 0; i < bgra.Length; i += 4)
            if (bgra[i] + bgra[i + 1] + bgra[i + 2] > 300)
                n++;
        return n;
    }

    [Fact]
    public void TheStagesSweepIsTheGeneratorsPictureAtItsOwnTimeAndMovesWithItsOrigin()
    {
        // Three days into a show, the sweep counted from three days: 10.3 s of its own time.
        const long own = 10_300_000_000;
        const long later = 2_000_000_000;
        var stage = Resources();
        var content = new ContentFrame();
        var (first, retimed, unretimed) = gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(Dither: false, GpuTiming: false));
            (byte[] OnStage, byte[] Flat) Draw(Generator sweep, long leaderNs)
            {
                engine.Render(At(Show(sweep), leaderNs, stage, content));
                Assert.Empty(c.Device.TakeErrors());
                return (Read(engine, "op_pj"), Read(engine, "op_flat"));
            }
            var a = Draw(Sweep(Days), Days + own);
            // RETIME two seconds later moves the origin by those two seconds: the same 10.3 s of its own time.
            var b = Draw(Sweep(Days + later), Days + own + later);
            // Not retimed, it has run on to 12.3 s.
            var u = Draw(Sweep(Days), Days + own + later);
            return (a, b, u);
        });
        Assert.True(Lit(first.OnStage) > 100, $"the sweep's band lit only {Lit(first.OnStage)} pixels of the plane");
        // The stage's sweep is the picture the generator draws anywhere else at the same own time, days into a show.
        Assert.True(MaxDifference(first.OnStage, first.Flat) <= 1, $"the stage's sweep is {MaxDifference(first.OnStage, first.Flat)} codes off the flat one at 10.3 s");
        Assert.True(MaxDifference(retimed.OnStage, retimed.Flat) <= 1, $"the retimed stage sweep is {MaxDifference(retimed.OnStage, retimed.Flat)} codes off the flat one");
        Assert.True(MaxDifference(unretimed.OnStage, unretimed.Flat) <= 1, $"the stage's sweep is {MaxDifference(unretimed.OnStage, unretimed.Flat)} codes off the flat one at 12.3 s");
        // It moves with its origin: retimed by the time that passed, it draws the picture it drew before; not retimed, another.
        Assert.True(first.OnStage.AsSpan().SequenceEqual(retimed.OnStage), "RETIME did not move the stage's sweep with its origin");
        Assert.True(MaxDifference(first.OnStage, unretimed.OnStage) > 100, "the stage's sweep did not move with its own time");
        // The stage's draw is recorded at its own time.
        Assert.True(content.Stats.TryGet("gn_sweep", out var live));
        Assert.Equal(own + later, live.TimeNs);
    }

    [Fact]
    public void TheStagesDrawIsRecordedInTheFramesStatsAndKeptWhileAFrameDrawsIt()
    {
        var stage = Resources();
        var content = new ContentFrame();
        var sweep = Sweep(Days);
        var card = Scene.Gen("pattern.bars", null, "gn_card");
        var withCard = Show(sweep, flat: false) with
        {
            Generators = [sweep, card],
            Outputs = [.. Show(sweep, flat: false).Outputs, Scene.SourceOutput("op_card", SourceRef.Generator("gn_card"), PW, PH)],
        };
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(Dither: false, GpuTiming: false));
            var n = 0;
            for (; n < 3; n++)
                engine.Render(At(withCard, Tick(n), stage, content));
            // Drawn only by the projector pass, it is observed: ok, at its own time, this frame.
            Assert.True(content.Stats.TryGet("gn_sweep", out var live), "the stage's draw is not in the frame's stats");
            Assert.Equal(GeneratorState.Ok, live.State);
            Assert.Equal(Tick(n - 1) - Days, live.TimeNs);
            Assert.Equal(engine.Frame, live.LastFrame);
            Assert.Equal(engine.Frame, content.Stats.Frame);
            // The card goes and its renderer is let go: the sweep the stage still draws is kept in the stats every frame meanwhile.
            var show = Show(sweep, flat: false);
            for (var f = 0; f <= GeneratorHost.IdleFrames + 2; f++, n++)
            {
                engine.Render(At(show, Tick(n), stage, content));
                Assert.True(content.Stats.TryGet("gn_sweep", out live), $"the stage's sweep was forgotten {f} frames after the card went");
                Assert.Equal(engine.Frame, live.LastFrame);
            }
            Assert.False(content.Stats.TryGet("gn_card", out _), "the card's renderer was not let go");
            // Hidden, nothing draws it: it is forgotten once it has gone undrawn as long as a renderer is kept.
            var hidden = Show(sweep, flat: false, visible: false);
            for (var f = 0; f <= GeneratorHost.IdleFrames + 2; f++, n++)
                engine.Render(At(hidden, Tick(n), stage, content));
            Assert.False(content.Stats.TryGet("gn_sweep", out _), "a sweep nothing draws is still in the stats");
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    /// <summary>Drawn flat and on the stage, the sweep's GPU time is its flat renderer's; once that renderer is let go and only the
    /// stage draws it, no draw of its own is timed (its light is inside the projector pass), so it reports none rather than the last
    /// measurement of a renderer that no longer draws.</summary>
    [Fact]
    public void TheStagesSweepKeepsNoGpuTimeOfAFlatRendererLetGo()
    {
        var stage = Resources();
        var content = new ContentFrame();
        var sweep = Sweep(Days);
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(Dither: false, GpuTiming: false));
            var n = 0;
            for (; n < 5; n++)
                engine.Render(At(Show(sweep), Tick(n), stage, content));
            Assert.True(content.Stats.TryGet("gn_sweep", out var live));
            // A measurement of the flat draw (as a context that times it would have).
            live.Measured(3_000_000);
            Assert.True(live.GpuMsP95 > 0);
            var stageOnly = Show(sweep, flat: false);
            for (var f = 0; f <= GeneratorHost.IdleFrames + 2; f++, n++)
                engine.Render(At(stageOnly, Tick(n), stage, content));
            Assert.True(content.Stats.TryGet("gn_sweep", out live), "the stage's sweep was forgotten");
            Assert.Equal((GeneratorState.Ok, engine.Frame), (live.State, live.LastFrame));
            Assert.Equal((0.0, 0.0), (live.GpuMs, live.GpuMsP95));
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    [Fact]
    public void AWarmFrameOfTheStagesSweepAllocatesNothing()
    {
        var stage = Resources();
        var content = new ContentFrame();
        var project = Show(Sweep(Days), flat: false);
        var allocated = gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: false));
            for (var n = 0; n < 20; n++)
                engine.Render(At(project, Tick(n), stage, content));
            var before = Allocations.Start();
            for (var n = 20; n < 80; n++)
                engine.Render(At(project, Tick(n), stage, content));
            var bytes = Allocations.Since(before);
            Assert.Empty(c.Device.TakeErrors());
            return bytes;
        });
        Assert.True(allocated == 0, $"sixty warm frames of the stage's sweep allocated {allocated} bytes");
    }
}
