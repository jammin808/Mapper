using System.Diagnostics;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Render.Effects;
using PlayMap.Render.Engine;
using PlayMap.Tests;

namespace PlayMap.Render.Tests.Effects;

/// <summary>Chains where the pictures are made: a layer's chain draws at its rectangle and nothing else of the canvas moves; a
/// chained layer that covers the canvas is drawn over the background, not instead of it; a surface's history effect steps once a
/// frame however many projectors draw it, and every projector shows the same picture; bypass and mix 0 leave layers and surfaces
/// bit for bit as they were; what the air shows instead of the show leaves layer and surface chains idle with the reason on the
/// outputs it covers (a hold keeps their state), and a chain out of the picture says why; a safe look is drawn through no chain,
/// even of a canvas or playlist the show shows with its chains; a playlist item's chain counts its time from the item's start,
/// each side of a transition from its own; and once warm a frame with every kind of chain, and a test pattern over one output,
/// allocates nothing.</summary>
[Collection("gl")]
public sealed class FxAttachTests(GlFixture gl)
{
    private const int W = 64, H = 36;
    private const int PW = 160, PH = 90;
    private const int SW = 96, SH = 54;

    private static readonly RenderEngineOptions Exact = new(Dither: false, GpuTiming: false);

    /// <summary>A counter: a persistent float buffer that gains 6/255 each frame the effect draws; the picture is its value.</summary>
    private const string Counter = """
        /*{ "ISFVSN": "2", "PASSES": [ { "TARGET": "count", "PERSISTENT": true, "FLOAT": true, "WIDTH": 1, "HEIGHT": 1 }, { } ] }*/
        void main() {
            vec4 last = IMG_NORM_PIXEL(count, vec2(0.5));
            if (PASSINDEX == 0)
                gl_FragColor = vec4(last.rgb + vec3(6.0 / 255.0), 1.0);
            else
                gl_FragColor = vec4(last.rgb, 1.0);
        }
        """;

    /// <summary>The picture as it is, at alpha one half.</summary>
    private const string HalfAlpha = """
        /*{ "ISFVSN": "2", "INPUTS": [ { "NAME": "inputImage", "TYPE": "image" } ] }*/
        void main() { gl_FragColor = vec4(IMG_THIS_PIXEL(inputImage).rgb, 0.5); }
        """;

    /// <summary>TIME as grey, a tenth a second.</summary>
    private const string Clock = """
        /*{ "ISFVSN": "2" }*/
        void main() { gl_FragColor = vec4(vec3(TIME * 0.1), 1.0); }
        """;

    private sealed class Levels(SourceRef safe) : IAirLevels
    {
        public float Gain(string outputId, long leaderNs) => 1f;

        public SourceRef? Override(string outputId, long leaderNs) => safe;

        public bool Hold(string outputId, long leaderNs) => false;
    }

    /// <summary>The air on some outputs only: a safe look per output, and one output held.</summary>
    private sealed class AirOn(IReadOnlyDictionary<string, SourceRef>? safe = null, string? held = null) : IAirLevels
    {
        public float Gain(string outputId, long leaderNs) => 1f;

        public SourceRef? Override(string outputId, long leaderNs) => safe?.GetValueOrDefault(outputId);

        public bool Hold(string outputId, long leaderNs) => outputId == held;
    }

    /// <summary>A playlist that shows one picture as one item (or a mix of two), started at a known instant.</summary>
    private sealed class OneItem(ItemOnAir item, SourceRef picture, SourceMix? mix = null) : ISourceProvider
    {
        public VideoFrameLease? Acquire(SourceRef source, long leaderNs) => null;

        public SourceRef? Route(SourceRef source, long leaderNs) => source.Kind == SourceKind.Playlist ? picture : null;

        public SourceMix? Mix(SourceRef source, long leaderNs) => source.Kind == SourceKind.Playlist ? mix : null;

        public ItemOnAir? Item(SourceRef source, long leaderNs) => source.Kind == SourceKind.Playlist ? item : null;
    }

    private static FxAsset Asset(string id, string source) =>
        new(id, id, FxFormat.Isf, "", FxCatalog.ContentHash(source, null)) { Source = source };

    private static Layer L(string id, string hex, RectD rect, FxChain? fx = null, bool visible = true) =>
        new(id, id, SourceRef.Solid(hex), rect, 1, LayerBlend.Normal, visible) { Fx = fx ?? FxChain.Empty };

    private static Output CanvasOutput(string id, string canvasId) =>
        Scene.SourceOutput(id, SourceRef.Canvas(canvasId), W, H) with { Mode = OutputMode.Canvas, SourceRect = new RectD(0, 0, W, H) };

    private static Output ProjectorOutput(string id, string projectorId, int w, int h) =>
        Scene.SourceOutput(id, SourceRef.None, w, h) with { Mode = OutputMode.Projector, ProjectorId = projectorId };

    private static Projector Lamp(string id) =>
        new(id, id, Intrinsics.FromThrowRatio(PW, PH, 1.2), LensDistortion.None, Pose.LookAt(new Vec3d(0, 0, 2.5), Vec3d.Zero, Vec3d.UnitY),
            0, "#FF4000", false, [], SolveLocks.None, 0);

    private static MeshData Quad() => new(
        [-0.5f, -0.5f, 0, 0.5f, -0.5f, 0, 0.5f, 0.5f, 0, -0.5f, 0.5f, 0],
        [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1],
        [0, 1, 1, 1, 1, 0, 0, 0],
        [0, 1, 2, 0, 2, 3]);

    private static Surface Surf(SourceRef content, FxChain fx, bool visible = true) =>
        new("sf1", "Surface", "ms1", Transform3.Identity, [], SurfaceContent.Empty with { Source = content }, 1, [], visible, false, "#3EC1F3", [])
        {
            Fx = fx,
        };

    private static byte[] Read(RenderEngine engine, string outputId, int w, int h)
    {
        var px = new byte[w * h * 4];
        Assert.True(engine.ReadOutput(outputId, px));
        return px;
    }

    private static byte[] Draw(RenderEngine engine, FrameInputs inputs, string outputId = "op1", int w = W, int h = H)
    {
        engine.Render(inputs);
        return Read(engine, outputId, w, h);
    }

    /// <summary>Draws until every slot named is drawn and past compiling (counted in renderer frames); answers the last picture.</summary>
    private static byte[] DrawCompiled(RenderEngine engine, FxPass fx, FrameInputs inputs, string[] slots, string outputId = "op1", int w = W, int h = H)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var px = Draw(engine, inputs, outputId, w, h);
            if (slots.All(id => fx.Stats.TryGet(id, out var live) && live.State == FxSlotState.Ok))
                return px;
            Assert.True(waited.Elapsed < FxRig.Patience, "the chain never drew");
            Thread.Sleep(2);
        }
    }

    private static (FxSlotState State, string Words) Said(FxPass fx, string slotId) =>
        fx.Stats.TryGet(slotId, out var live) ? (live.State, live.Words) : throw new InvalidOperationException($"no stats for {slotId}");

    [Fact]
    public void ALayersChainDrawsAtItsRectangleAndTheOtherLayersAndTheBackgroundAreUntouched()
    {
        Project Show(FxChain fx) => Scene.Project([], [CanvasOutput("op1", "cv1")],
            [new Canvas("cv1", "Canvas", W, H, "#0000FF", [L("ly_left", "#284664", new RectD(0, 0, 32, 36)), L("ly_fx", "#284664", new RectD(32, 0, 32, 18), fx)])]);
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c.Backend, Exact);
            using var fx = new FxPass(c);
            var plain = Draw(engine, Scene.Inputs(Show(FxChain.Empty)) with { Passes = [fx] });
            Assert.Equal((0, 0, 255), Pixels.At(plain, W, 40, 30));

            // Bypass and mix 0 hand the layer on bit for bit.
            var still = Show(FxRig.Chain(FxRig.Slot("fx.invert", bypass: true), FxRig.Slot("fx.blur", id: "fx00000002", mix: 0)));
            Assert.Equal(plain, Draw(engine, Scene.Inputs(still) with { Passes = [fx] }));
            Assert.Equal(FxSlotState.Bypassed, Said(fx, "fx00000001").State);
            Assert.Equal(FxSlotState.Bypassed, Said(fx, "fx00000002").State);

            var inverted = DrawCompiled(engine, fx, Scene.Inputs(Show(FxRig.Chain(FxRig.Slot("fx.invert")))) with { Passes = [fx] }, ["fx00000001"]);
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    if (x >= 32 && y < 18)
                        Pixels.Near(Inverse(Pixels.At(plain, W, x, y)), Pixels.At(inverted, W, x, y), 1, $"({x}, {y}) in the layer");
                    else
                        Assert.True(Pixels.At(plain, W, x, y) == Pixels.At(inverted, W, x, y), $"({x}, {y}) outside the layer moved");
                }
            }
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    [Fact]
    public void AChainedLayerThatCoversTheCanvasIsDrawnOverTheBackgroundNotInsteadOfIt()
    {
        var asset = Asset("fa00000021", HalfAlpha);
        Project Show(FxChain fx) => Scene.Project([], [CanvasOutput("op1", "cv1")],
            [new Canvas("cv1", "Canvas", W, H, "#000000", [L("ly_full", "#FFFFFF", new RectD(0, 0, W, H), fx)])]) with { FxAssets = [asset] };
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c.Backend, Exact);
            using var fx = new FxPass(c);
            Assert.Equal((255, 255, 255), Pixels.At(Draw(engine, Scene.Inputs(Show(FxChain.Empty)) with { Passes = [fx] }), W, 10, 10));
            // White at alpha one half over the black background: the effect's alpha blends, it is not written as the canvas's.
            var half = DrawCompiled(engine, fx, Scene.Inputs(Show(FxRig.Chain(FxRig.Slot("isf:fa00000021")))) with { Passes = [fx] }, ["fx00000001"]);
            Pixels.Near((128, 128, 128), Pixels.At(half, W, 10, 10), 2);
        });
    }

    [Fact]
    public void ASurfacesHistoryEffectStepsOnceAFrameAndEveryProjectorShowsTheSamePicture()
    {
        var asset = Asset("fa00000022", Counter);
        var stage = new TestStage();
        stage.Meshes["ms1"] = Quad();
        Project Show(FxChain fx) => Scene.Project([Scene.Gen("pattern.testcard")], [ProjectorOutput("op1", "pj1", PW, PH), ProjectorOutput("op2", "pj2", SW, SH)],
            stage: Stage.Empty with { Surfaces = [Surf(SourceRef.Generator("gn_test"), fx)], Projectors = [Lamp("pj1"), Lamp("pj2")] }) with { FxAssets = [asset] };
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c.Backend, Exact);
            using var fx = new FxPass(c);
            var frame = 0;
            (byte[] A, byte[] B) Both(Project p)
            {
                engine.Render(Scene.Inputs(p, 1 + ++frame / 60.0, stage: stage) with { Passes = [fx] });
                return (Read(engine, "op1", PW, PH), Read(engine, "op2", SW, SH));
            }

            // A chain that changes nothing leaves the surface as it was on both projectors, bit for bit (a generator drawn at each
            // projector's own raster).
            var plain = Both(Show(FxChain.Empty));
            var still = Both(Show(FxRig.Chain(FxRig.Slot("isf:fa00000022", bypass: true), FxRig.Slot("fx.invert", id: "fx00000002", mix: 0))));
            Assert.Equal(plain.A, still.A);
            Assert.Equal(plain.B, still.B);

            var counting = Show(FxRig.Chain(FxRig.Slot("isf:fa00000022")));
            var waited = Stopwatch.StartNew();
            while (!(fx.Stats.TryGet("fx00000001", out var live) && live.State == FxSlotState.Ok))
            {
                Both(counting);
                Assert.True(waited.Elapsed < FxRig.Patience, "the counter never drew");
                Thread.Sleep(2);
            }
            var first = Both(counting);
            var (a0, b0) = (Pixels.At(first.A, PW, PW / 2, PH / 2).R, Pixels.At(first.B, SW, SW / 2, SH / 2).R);
            Assert.Equal(a0, b0);
            Assert.InRange(a0, 6, 60);
            for (var i = 0; i < 9; i++)
                Both(counting);
            var later = Both(counting);
            var (a1, b1) = (Pixels.At(later.A, PW, PW / 2, PH / 2).R, Pixels.At(later.B, SW, SW / 2, SH / 2).R);
            Assert.Equal(a1, b1);
            // Ten frames, ten steps of 6: two projectors drawing the surface never step it twice.
            Assert.InRange(a1 - a0, 58, 62);
            Assert.True(Said(fx, "fx00000001").State == FxSlotState.Ok);
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    [Fact]
    public void WhatTheAirShowsInsteadOfTheShowLeavesLayerAndSurfaceChainsIdleWithTheReason()
    {
        var stage = new TestStage();
        stage.Meshes["ms1"] = Quad();
        var pattern = SourceRef.Generator("gn_pattern");
        Project Show(bool chains, bool visible = true)
        {
            var fxLayer = chains ? FxRig.Chain(FxRig.Slot("fx.invert")) : FxChain.Empty;
            var fxSurface = chains ? FxRig.Chain(FxRig.Slot("fx.invert", id: "fx00000002")) : FxChain.Empty;
            // A canvas no output shows, with a chained layer: out of the picture whatever the air does.
            var fxAside = chains ? FxRig.Chain(FxRig.Slot("fx.invert", id: "fx00000003")) : FxChain.Empty;
            return Scene.Project([Scene.Gen("pattern.grid", id: "gn_pattern")], [CanvasOutput("op1", "cv1"), ProjectorOutput("op2", "pj1", PW, PH)],
                [new Canvas("cv1", "Canvas", W, H, "#000000", [L("ly_fx", "#284664", new RectD(0, 0, W, H), fxLayer, visible)]),
                 new Canvas("cv2", "Aside", W, H, "#000000", [L("ly_aside", "#284664", new RectD(0, 0, W, H), fxAside)])],
                Stage.Empty with { Surfaces = [Surf(SourceRef.Solid("#284664"), fxSurface, visible)], Projectors = [Lamp("pj1")] });
        }
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c.Backend, Exact);
            using var fx = new FxPass(c);
            (byte[] Canvas, byte[] Stage) Frame(FrameInputs inputs)
            {
                engine.Render(inputs with { Passes = [fx] });
                return (Read(engine, "op1", W, H), Read(engine, "op2", PW, PH));
            }
            FrameInputs In(Project p) => Scene.Inputs(p, stage: stage);

            DrawCompiled(engine, fx, In(Show(true)) with { Passes = [fx] }, ["fx00000001", "fx00000002"]);
            var drawn = Frame(In(Show(true)));
            Pixels.Near((215, 185, 155), Pixels.At(drawn.Canvas, W, 10, 10), 1, "the layer's chain");
            Pixels.Near((215, 185, 155), Pixels.At(drawn.Stage, PW, PW / 2, PH / 2), 1, "the surface's chain");
            Frame(In(Show(true)));
            Assert.Equal((FxSlotState.Idle, FxPass.LayerUnshownWords), Said(fx, "fx00000003"));

            // A hold on the canvas's output keeps the picture the chain made: the layer's chain keeps its state (it is not out of
            // the picture), while the projector draws on.
            Frame(In(Show(true)) with { Levels = new AirOn(held: "op1") });
            var holding = Frame(In(Show(true)) with { Levels = new AirOn(held: "op1") });
            Pixels.Near((215, 185, 155), Pixels.At(holding.Canvas, W, 10, 10), 1, "the held picture");
            Assert.Equal((FxSlotState.Ok, ""), Said(fx, "fx00000001"));
            Assert.Equal(FxSlotState.Ok, Said(fx, "fx00000002").State);

            // A test pattern over every output shows its own codes, and the chains say why they are not drawing (once the frame
            // is wholly drawn, so after the next one begins).
            Frame(In(Show(true)) with { PatternOverride = pattern });
            var covered = Frame(In(Show(true)) with { PatternOverride = pattern });
            Assert.Equal((FxSlotState.Idle, FxPass.CoveredWords), Said(fx, "fx00000001"));
            Assert.Equal((FxSlotState.Idle, FxPass.CoveredWords), Said(fx, "fx00000002"));
            // A chain no covered output would show is out of the picture, not covered.
            Assert.Equal((FxSlotState.Idle, FxPass.LayerUnshownWords), Said(fx, "fx00000003"));
            var expected = Frame(In(Show(false)) with { PatternOverride = pattern });
            Assert.Equal(expected.Canvas, covered.Canvas);
            Assert.Equal(expected.Stage, covered.Stage);

            // So does a panic's safe look.
            var safe = new Levels(SourceRef.Solid("#102030"));
            Frame(In(Show(true)) with { Levels = safe });
            var looks = Frame(In(Show(true)) with { Levels = safe });
            Assert.Equal((16, 32, 48), Pixels.At(looks.Canvas, W, 10, 10));
            Assert.Equal((16, 32, 48), Pixels.At(looks.Stage, PW, PW / 2, PH / 2));
            Assert.Equal((FxSlotState.Idle, FxPass.CoveredWords), Said(fx, "fx00000001"));
            Assert.Equal((FxSlotState.Idle, FxPass.CoveredWords), Said(fx, "fx00000002"));

            // The pattern over the canvas's output only: the projector still draws the surface's chain, and the layer's says the
            // pattern covers the output that would show it.
            Frame(In(Show(true)) with { PatternOverride = pattern, PatternOutputs = ["op1"] });
            var half = Frame(In(Show(true)) with { PatternOverride = pattern, PatternOutputs = ["op1"] });
            Pixels.Near((215, 185, 155), Pixels.At(half.Stage, PW, PW / 2, PH / 2), 1, "the surface's chain beside a covered output");
            Assert.Equal((FxSlotState.Idle, FxPass.CoveredWords), Said(fx, "fx00000001"));
            Assert.Equal(FxSlotState.Ok, Said(fx, "fx00000002").State);

            // Out of the picture: a layer and a surface that are not visible.
            Frame(In(Show(true, visible: false)));
            Frame(In(Show(true, visible: false)));
            Assert.Equal((FxSlotState.Idle, FxPass.LayerHiddenWords), Said(fx, "fx00000001"));
            Assert.Equal((FxSlotState.Idle, FxPass.SurfaceHiddenWords), Said(fx, "fx00000002"));

            // Both draw again on the first frame back.
            var back = Frame(In(Show(true)));
            Pixels.Near((215, 185, 155), Pixels.At(back.Canvas, W, 10, 10), 1, "the layer's chain back");
            Pixels.Near((215, 185, 155), Pixels.At(back.Stage, PW, PW / 2, PH / 2), 1, "the surface's chain back");
            Assert.Equal((FxSlotState.Ok, ""), Said(fx, "fx00000001"));
            Assert.Equal((FxSlotState.Ok, ""), Said(fx, "fx00000002"));
        });
    }

    [Fact]
    public void ASafeLookIsDrawnThroughNoChainEvenOfACanvasOrPlaylistTheShowShowsWithItsChains()
    {
        var white = SourceRef.Solid("#FFFFFF");
        var item = PlaylistItem.For("it_fx", white, "Fx") with { Fx = FxRig.Chain(FxRig.Slot("fx.invert", id: "fx00000002")) };
        var waiting = PlaylistItem.For("it_next", white, "Next") with { Fx = FxRig.Chain(FxRig.Slot("fx.invert", id: "fx00000003")) };
        var black = SourceRef.Solid("#000000");
        // The safe looks come first and last in output order: a show picture made before or after them keeps its chains.
        var project = Scene.Project([Scene.Gen("pattern.grid", id: "gn_pattern")],
            [Scene.SourceOutput("op1", black, W, H), CanvasOutput("op2", "cv1"), Scene.SourceOutput("op3", SourceRef.Playlist("pl_1"), W, H),
             Scene.SourceOutput("op4", black, W, H)],
            [new Canvas("cv1", "Canvas", W, H, "#000000", [L("ly_fx", "#284664", new RectD(0, 0, W, H), FxRig.Chain(FxRig.Slot("fx.invert")))])])
            with { Playlists = [new Playlist("pl_1", "List", [item, waiting], false, null)] };
        var air = new AirOn(new Dictionary<string, SourceRef> { ["op1"] = SourceRef.Canvas("cv1"), ["op4"] = SourceRef.Playlist("pl_1") });
        var sources = new OneItem(new ItemOnAir(item, 0), white);
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c.Backend, Exact);
            using var fx = new FxPass(c);
            var inputs = Scene.Inputs(project, 1, sources) with { Passes = [fx], Levels = air };
            DrawCompiled(engine, fx, inputs, ["fx00000001", "fx00000002"], "op2");
            engine.Render(inputs);
            Assert.Equal((40, 70, 100), Pixels.At(Read(engine, "op1", W, H), W, 10, 10));
            Pixels.Near((215, 185, 155), Pixels.At(Read(engine, "op2", W, H), W, 10, 10), 1, "the show's canvas with its layer's chain");
            Pixels.Near((0, 0, 0), Pixels.At(Read(engine, "op3", W, H), W, 10, 10), 1, "the show's playlist with its item's chain");
            Assert.Equal((255, 255, 255), Pixels.At(Read(engine, "op4", W, H), W, 10, 10));
            Assert.Equal(FxSlotState.Ok, Said(fx, "fx00000001").State);
            Assert.Equal(FxSlotState.Ok, Said(fx, "fx00000002").State);
            Assert.Equal((FxSlotState.Idle, FxPass.ItemUnshownWords), Said(fx, "fx00000003"));

            // A pattern over the playlist's output: the item it was showing keeps its chain with the pattern's words; the item
            // waiting its turn is still not shown.
            var covered = inputs with { PatternOverride = SourceRef.Generator("gn_pattern"), PatternOutputs = ["op3"] };
            engine.Render(covered);
            engine.Render(covered);
            Assert.Equal((FxSlotState.Idle, FxPass.CoveredWords), Said(fx, "fx00000002"));
            Assert.Equal((FxSlotState.Idle, FxPass.ItemUnshownWords), Said(fx, "fx00000003"));
            Assert.Equal(FxSlotState.Ok, Said(fx, "fx00000001").State);
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    [Fact]
    public void APlaylistItemsChainCountsItsTimeFromTheItemsStartAndEachSideOfATransitionFromItsOwn()
    {
        var asset = Asset("fa00000023", Clock);
        var white = SourceRef.Solid("#FFFFFF");
        var a = PlaylistItem.For("it_a", white, "A") with { Fx = FxRig.Chain(FxRig.Slot("isf:fa00000023")) };
        var b = PlaylistItem.For("it_b", white, "B") with { Fx = FxRig.Chain(FxRig.Slot("isf:fa00000023", id: "fx00000002")) };
        var project = Scene.Project([], [Scene.SourceOutput("op1", SourceRef.Playlist("pl_1"), W, H)]) with { FxAssets = [asset] };
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c.Backend, Exact);
            using var fx = new FxPass(c);
            // Item A started at 10 s: its TIME is 0 then, 2.5 s later 2.5.
            var playing = new OneItem(new ItemOnAir(a, 10_000_000_000), white);
            var start = DrawCompiled(engine, fx, Scene.Inputs(project, 10, playing) with { Passes = [fx] }, ["fx00000001"]);
            Pixels.Near((0, 0, 0), Pixels.At(start, W, 10, 10), 1, "TIME at the item's start");
            var later = Draw(engine, Scene.Inputs(project, 12.5, playing) with { Passes = [fx] });
            Pixels.Near((64, 64, 64), Pixels.At(later, W, 10, 10), 1, "TIME 2.5 s into the item");

            // A fade from A into B, which started at 12 s: as it opens the picture is A's, at A's own time, and B's chain ran too.
            var fading = new OneItem(new ItemOnAir(a, 10_000_000_000), white,
                new SourceMix(white, white, TransitionKind.Fade, 0) { FromItem = new ItemOnAir(a, 10_000_000_000), ToItem = new ItemOnAir(b, 12_000_000_000) });
            var mixed = DrawCompiled(engine, fx, Scene.Inputs(project, 12.5, fading) with { Passes = [fx] }, ["fx00000001", "fx00000002"]);
            Pixels.Near((64, 64, 64), Pixels.At(mixed, W, 10, 10), 1, "the outgoing side at its own time");
            // At the end of the window the picture is B's, 0.5 s into B.
            var into = Draw(engine, Scene.Inputs(project, 12.5, new OneItem(new ItemOnAir(a, 10_000_000_000), white,
                new SourceMix(white, white, TransitionKind.Fade, 1) { FromItem = new ItemOnAir(a, 10_000_000_000), ToItem = new ItemOnAir(b, 12_000_000_000) })) with { Passes = [fx] });
            Pixels.Near((13, 13, 13), Pixels.At(into, W, 10, 10), 1, "the incoming side at its own time");
        });
    }

    [Fact]
    public void AWarmFrameWithLayerSurfaceAndItemChainsAllocatesNothing()
    {
        var stage = new TestStage();
        stage.Meshes["ms1"] = Quad();
        var white = SourceRef.Solid("#FFFFFF");
        var item = PlaylistItem.For("it_on", white, "On") with { Fx = FxRig.Chain(FxRig.Slot("fx.vignette", id: "fx00000004")) };
        var waiting = PlaylistItem.For("it_off", white, "Off") with { Fx = FxRig.Chain(FxRig.Slot("fx.invert", id: "fx00000005")) };
        // A fourth output shows another canvas under a test pattern: its layer's chain is kept off the air, said so every frame.
        var project = Scene.Project([Scene.Gen("pattern.testcard")],
            [CanvasOutput("op1", "cv1"), ProjectorOutput("op2", "pj1", PW, PH), Scene.SourceOutput("op3", SourceRef.Playlist("pl_1"), W, H),
             CanvasOutput("op4", "cv2")],
            [new Canvas("cv1", "Canvas", W, H, "#000000",
                [L("ly_fx", "#284664", new RectD(0, 0, W, H), FxRig.Chain(FxRig.Slot("fx.blur"))),
                 L("ly_off", "#284664", new RectD(0, 0, W, H), FxRig.Chain(FxRig.Slot("fx.invert", id: "fx00000006")), visible: false)]),
             new Canvas("cv2", "Covered", W, H, "#000000", [L("ly_covered", "#284664", new RectD(0, 0, W, H), FxRig.Chain(FxRig.Slot("fx.invert", id: "fx00000007")))])],
            Stage.Empty with { Surfaces = [Surf(SourceRef.Generator("gn_test"), FxRig.Chain(FxRig.Slot("fx.feedback", id: "fx00000002"), FxRig.Slot("fx.vignette", id: "fx00000003", scale: 0.5)))], Projectors = [Lamp("pj1")] })
            with { Playlists = [new Playlist("pl_1", "List", [item, waiting], false, null)] };
        var sources = new OneItem(new ItemOnAir(item, 1_000_000_000), white);
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c.Backend, new RenderEngineOptions(GpuTiming: true));
            using var fx = new FxPass(c);
            var inputs = Scene.Inputs(project, 1, sources, stage) with
            {
                Passes = [fx],
                PatternOverride = SourceRef.Generator("gn_test"),
                PatternOutputs = ["op4"],
            };
            DrawCompiled(engine, fx, inputs, ["fx00000001", "fx00000002", "fx00000003", "fx00000004"]);
            for (var i = 0; i < 30; i++)
            {
                inputs = inputs with { LeaderNs = inputs.LeaderNs + 16_666_667 };
                engine.Render(inputs);
            }
            Assert.Equal((FxSlotState.Idle, FxPass.ItemUnshownWords), Said(fx, "fx00000005"));
            Assert.Equal((FxSlotState.Idle, FxPass.LayerHiddenWords), Said(fx, "fx00000006"));
            Assert.Equal((FxSlotState.Idle, FxPass.CoveredWords), Said(fx, "fx00000007"));
            var before = Allocations.Start();
            for (var i = 0; i < 60; i++)
            {
                inputs = inputs with { LeaderNs = inputs.LeaderNs + 16_666_667 };
                engine.Render(inputs);
            }
            Assert.Equal(0, Allocations.Since(before));
            foreach (var id in new[] { "fx00000001", "fx00000002", "fx00000003", "fx00000004" })
                Assert.Equal(FxSlotState.Ok, Said(fx, id).State);
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    private static (int R, int G, int B) Inverse((int R, int G, int B) c) => (255 - c.R, 255 - c.G, 255 - c.B);
}
