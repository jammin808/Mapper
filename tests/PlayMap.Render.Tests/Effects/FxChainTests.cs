using System.Collections.Immutable;
using System.Diagnostics;
using PlayMap.Core.Effects;
using PlayMap.Core.Gpu;
using PlayMap.Core.Model;
using PlayMap.Render.Effects;
using PlayMap.Render.Engine;
using PlayMap.Render.Generators;
using PlayMap.Render.Gpu;
using PlayMap.Tests;

namespace PlayMap.Render.Tests.Effects;

/// <summary>Chains in the frame: bypass and mix 0 hand the picture on bit for bit; a test pattern over an output with a chain
/// shows the pattern's own codes; a history effect is the same on two renderers fed the same instants from the same start, and a
/// renderer that starts late says it is history rather than claiming to match; a compile held open never holds a frame (counted
/// in frames, not seconds); ISF's bottom-left origin lands the right way up; the layer call point runs the same chain; and once
/// warm a frame with a chain allocates nothing.</summary>
[Collection("gl")]
public sealed class FxChainTests(GlFixture gl)
{
    private const int W = 64, H = 36;

    private static Project Card(FxChain chain) =>
        Scene.Project([Scene.Gen("pattern.testcard"), Scene.Gen("pattern.grid", id: "gn_pattern")],
            [Scene.SourceOutput("op1", SourceRef.Generator("gn_test"), W, H) with { Fx = chain }]);

    private static byte[] Draw(RenderEngine engine, FxPass fx, FrameInputs inputs)
    {
        engine.Render(inputs);
        var px = new byte[W * H * 4];
        Assert.True(engine.ReadOutput("op1", px));
        return px;
    }

    private static FrameInputs Inputs(Project project, FxPass fx, double seconds = 1) => Scene.Inputs(project, seconds) with { Passes = [fx] };

    /// <summary>Draws until every slot of the output's chain is past compiling; answers the picture.</summary>
    private static byte[] DrawCompiled(RenderEngine engine, FxPass fx, FrameInputs inputs)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var px = Draw(engine, fx, inputs);
            var chain = inputs.Project.Outputs[0].Fx;
            if (chain.Slots.All(s => fx.Stats.TryGet(s.Id, out var live) && live.State != FxSlotState.Compiling))
                return px;
            Assert.True(waited.Elapsed < FxRig.Patience, "the chain never compiled");
            Thread.Sleep(2);
        }
    }

    [Fact]
    public void BypassAndMixZeroAreBitExactPassThrough()
    {
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: false));
            using var fx = new FxPass(c);
            var plain = Draw(engine, fx, Inputs(Card(FxChain.Empty), fx));
            var bypassed = Card(FxRig.Chain(FxRig.Slot("fx.invert", bypass: true), FxRig.Slot("fx.blur", id: "fx00000002", mix: 0)));
            Assert.Equal(plain, Draw(engine, fx, Inputs(bypassed, fx)));
            Assert.Equal(FxSlotState.Bypassed, FxRig.Live(engine, fx, "fx00000001").State);
            Assert.Equal(FxSlotState.Bypassed, FxRig.Live(engine, fx, "fx00000002").State);
            Assert.Equal(0, fx.Lane.Pending + fx.Lane.Programs);

            var inverted = DrawCompiled(engine, fx, Inputs(Card(FxRig.Chain(FxRig.Slot("fx.invert"))), fx));
            Assert.Equal(FxSlotState.Ok, FxRig.Live(engine, fx, "fx00000001").State);
            Pixels.Near(Inverse(Pixels.At(plain, W, 20, 10)), Pixels.At(inverted, W, 20, 10), 1);
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    [Fact]
    public void APatternOverAnOutputWithAChainShowsThePatternsOwnCodes()
    {
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: false));
            using var fx = new FxPass(c);
            var chained = Card(FxRig.Chain(FxRig.Slot("fx.invert"), FxRig.Slot("fx.blur", id: "fx00000002")));
            DrawCompiled(engine, fx, Inputs(chained, fx));
            Assert.Equal(FxSlotState.Ok, FxRig.Live(engine, fx, "fx00000002").State);

            var pattern = SourceRef.Generator("gn_pattern");
            var expected = Draw(engine, fx, Inputs(Card(FxChain.Empty), fx) with { PatternOverride = pattern });
            var shown = Draw(engine, fx, Inputs(chained, fx) with { PatternOverride = pattern });
            Assert.Equal(expected, shown);
            // The slots say they are not drawing, and why, rather than keeping the word of a frame the pattern replaced.
            var covered = FxRig.Live(engine, fx, "fx00000002");
            Assert.Equal((FxSlotState.Idle, FxPass.CoveredWords), (covered.State, covered.Words));
            // The chain runs again the moment the pattern is down.
            Assert.NotEqual(expected, Draw(engine, fx, Inputs(chained, fx)));
        });
    }

    [Fact]
    public void AHistoryEffectMatchesFromTheSameStartAndALateRendererSaysItIsHistory()
    {
        gl.Run(c =>
        {
            var d = c.Device;
            using var lane = new FxCompileLane(c);
            using var a = new FxChainRunner(d, lane);
            using var b = new FxChainRunner(d, lane);
            using var late = new FxChainRunner(d, lane);
            var show = Project.CreateDefault("History");
            var chain = FxRig.Chain(FxRig.Slot("fx.feedback", ParamSet.Empty.With("decay", 0.9)));
            using (var first = FxRig.Picture(d, W, H, (_, _) => (0, 0, 0)))
            {
                long warm = 0;
                FxRig.Run(d, lane, a, show, chain, first, ref warm, owner: "warm");
            }
            // Everything is compiled: a, b and late now differ only in when they start.
            long fa = 0, fb = 0, fl = 0;
            for (var k = 0; k < 240; k++)
            {
                var leaderNs = 1_000_000_000L + k * 16_666_667L;
                using var bar = FxRig.Picture(d, W, H, (x, _) => Math.Abs(x - k * 2 % W) < 3 ? (255, 255, 255) : (0, 0, 0));
                var pa = Scene.ReadTexture(d, FxRig.Step(d, lane, a, show, chain, bar, ref fa, leaderNs));
                var pb = Scene.ReadTexture(d, FxRig.Step(d, lane, b, show, chain, bar, ref fb, leaderNs));
                Assert.Equal(pa, pb);
                if (k < 180)
                    continue;
                var pl = Scene.ReadTexture(d, FxRig.Step(d, lane, late, show, chain, bar, ref fl, leaderNs));
                if (k == 180)
                    Assert.NotEqual(pa, pl);
            }
            Assert.Equal(FxSlotState.Ok, FxRig.Live(late, "fx00000001").State);
            Assert.True(FxRig.Live(late, "fx00000001").History);
            Assert.True(FxRig.Live(a, "fx00000001").History);
        });
    }

    [Fact]
    public void ACompileHeldOpenNeverDelaysAFrame()
    {
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: false));
            using var fx = new FxPass(c);
            using var gate = new ManualResetEventSlim(false);
            var held = 0;
            fx.Lane.BeforeCompile = _ =>
            {
                Interlocked.Increment(ref held);
                gate.Wait();
            };
            Assert.Equal("shared-context", fx.Lane.Mode);
            var plain = Draw(engine, fx, Inputs(Card(FxChain.Empty), fx));
            var inputs = Inputs(Card(FxRig.Chain(FxRig.Slot("fx.invert"))), fx);
            try
            {
                var before = engine.Frame;
                for (var i = 0; i < 30; i++)
                    Assert.Equal(plain, Draw(engine, fx, inputs));
                Assert.Equal(before + 30, engine.Frame);
                Assert.Equal(FxSlotState.Compiling, FxRig.Live(engine, fx, "fx00000001").State);
                Assert.Equal(1, fx.Lane.Pending);
            }
            finally
            {
                gate.Set();
            }
            var inverted = DrawCompiled(engine, fx, inputs);
            Assert.Equal(1, Volatile.Read(ref held));
            Assert.Equal(FxSlotState.Ok, FxRig.Live(engine, fx, "fx00000001").State);
            Pixels.Near(Inverse(Pixels.At(plain, W, 20, 10)), Pixels.At(inverted, W, 20, 10), 1);
        });
    }

    [Fact]
    public void AnIsfFilesBottomLeftOriginLandsTheRightWayUp()
    {
        const string ramp = """
            /*{ "ISFVSN": "2", "DESCRIPTION": "height as red" }*/
            void main() { gl_FragColor = vec4(isf_FragNormCoord.y, 0.0, 0.0, 1.0); }
            """;
        var asset = new FxAsset("fa00000001", "Ramp", FxFormat.Isf, "", FxCatalog.ContentHash(ramp, null)) { Source = ramp };
        gl.Run(c =>
        {
            var d = c.Device;
            using var lane = new FxCompileLane(c);
            using var runner = new FxChainRunner(d, lane);
            using var input = FxRig.Picture(d, W, H, (_, _) => (0, 0, 255));
            var show = Project.CreateDefault("Up") with { FxAssets = [asset] };
            long frame = 0;
            var px = FxRig.Run(d, lane, runner, show, FxRig.Chain(FxRig.Slot("isf:fa00000001")), input, ref frame);
            Assert.Equal(FxSlotState.Ok, FxRig.Live(runner, "fx00000001").State);
            Assert.InRange(Pixels.At(px, W, 10, 0).R, 248, 255);
            Assert.InRange(Pixels.At(px, W, 10, H - 1).R, 0, 7);
        });
    }

    [Fact]
    public void TheLayerCallPointRunsTheSameChain()
    {
        var layer = new Layer("ly_test", "Test", SourceRef.Solid("#284664"), new RectD(0, 0, W, H), 1, LayerBlend.Normal, true)
        {
            Fx = FxRig.Chain(FxRig.Slot("fx.invert")),
        };
        gl.Run(c =>
        {
            var d = c.Device;
            using var fx = new FxPass(c);
            using var input = FxRig.Picture(d, W, H, (_, _) => (40, 70, 100));
            var show = Project.CreateDefault("Layer");
            var waited = Stopwatch.StartNew();
            for (var frame = 1L; ; frame++)
            {
                var pass = new PassFrame(frame, 1_000_000_000, show, GeneratorFrame.At(1), d, false, false);
                var result = fx.Layer(in pass, layer, input);
                if (fx.Stats.TryGet("fx00000001", out var live) && live.State == FxSlotState.Ok)
                {
                    Pixels.Near((215, 185, 155), Pixels.At(Scene.ReadTexture(d, result), W, 5, 5), 1);
                    break;
                }
                Assert.Same(input, result);
                Assert.True(waited.Elapsed < FxRig.Patience, "the layer's chain never compiled");
                Thread.Sleep(2);
            }
        });
    }

    [Fact]
    public void AWarmFrameWithAChainAllocatesNothing()
    {
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: true));
            using var fx = new FxPass(c);
            var chain = FxRig.Chain(FxRig.Slot("fx.blur"), FxRig.Slot("fx.feedback", id: "fx00000002"),
                FxRig.Slot("fx.invert", id: "fx00000003", mix: 0.5, blend: FxBlend.Add), FxRig.Slot("fx.vignette", id: "fx00000004", scale: 0.5));
            var inputs = Inputs(Card(chain), fx);
            DrawCompiled(engine, fx, inputs);
            for (var i = 0; i < 30; i++)
            {
                inputs = inputs with { LeaderNs = inputs.LeaderNs + 16_666_667 };
                engine.Render(inputs);
            }
            Assert.All(chain.Slots, s => Assert.Equal(FxSlotState.Ok, FxRig.Live(engine, fx, s.Id).State));
            var before = Allocations.Start();
            for (var i = 0; i < 60; i++)
            {
                inputs = inputs with { LeaderNs = inputs.LeaderNs + 16_666_667 };
                engine.Render(inputs);
            }
            Assert.Equal(0, Allocations.Since(before));
            // A slot's GPU time comes from timestamp queries, core in desktop GL 3.3: an ES context measures none and says 0.
            var gpuMs = FxRig.Live(engine, fx, "fx00000001").GpuMs;
            if (c.Device.Caps.IsCore)
                Assert.True(gpuMs > 0, "the slot's GPU time is measured");
            else
                Assert.Equal(0d, gpuMs);
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    [Fact]
    public void WithoutASecondContextTheLaneCompilesOnTheRenderThreadOneJobAFrame()
    {
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: false));
            using var fx = new FxPass(c, sharedContextLane: false);
            Assert.Equal("render-thread", fx.Lane.Mode);
            var plain = Draw(engine, fx, Inputs(Card(FxChain.Empty), fx));
            var inverted = DrawCompiled(engine, fx, Inputs(Card(FxRig.Chain(FxRig.Slot("fx.invert"))), fx));
            Assert.Equal(FxSlotState.Ok, FxRig.Live(engine, fx, "fx00000001").State);
            Pixels.Near(Inverse(Pixels.At(plain, W, 20, 10)), Pixels.At(inverted, W, 20, 10), 1);
        });
    }

    [Fact]
    public void AnImportedPictureIsDecodedOnTheLaneAndReadTheRightWayUp()
    {
        string png;
        using (var bitmap = new SkiaSharp.SKBitmap(2, 2))
        {
            bitmap.SetPixel(0, 0, new SkiaSharp.SKColor(255, 0, 0));
            bitmap.SetPixel(1, 0, new SkiaSharp.SKColor(255, 0, 0));
            bitmap.SetPixel(0, 1, new SkiaSharp.SKColor(0, 0, 255));
            bitmap.SetPixel(1, 1, new SkiaSharp.SKColor(0, 0, 255));
            using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            png = Convert.ToBase64String(data.ToArray());
        }
        const string src = """
            /*{ "ISFVSN": "2", "IMPORTED": { "card": { "PATH": "card.png" } } }*/
            void main() { gl_FragColor = IMG_NORM_PIXEL(card, isf_FragNormCoord); }
            """;
        var asset = new FxAsset("fa00000002", "Card", FxFormat.Isf, "", FxCatalog.ContentHash(src, null))
        {
            Source = src,
            Images = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, [KeyValuePair.Create("card", png)]),
        };
        gl.Run(c =>
        {
            var d = c.Device;
            using var lane = new FxCompileLane(c);
            using var runner = new FxChainRunner(d, lane);
            using var input = FxRig.Picture(d, W, H, (_, _) => (0, 255, 0));
            long frame = 0;
            var px = FxRig.Run(d, lane, runner, Project.CreateDefault("Import") with { FxAssets = [asset] },
                FxRig.Chain(FxRig.Slot("isf:fa00000002")), input, ref frame);
            Assert.Equal(FxSlotState.Ok, FxRig.Live(runner, "fx00000001").State);
            Pixels.Near((255, 0, 0), Pixels.At(px, W, 10, 2), 2);
            Pixels.Near((0, 0, 255), Pixels.At(px, W, 10, H - 3), 2);
        });
    }

    [Fact]
    public void AnOutputNotDrawnSaysWhyAndItsChainDrawsOnTheFirstFrameBack()
    {
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: false));
            using var fx = new FxPass(c);
            var plain = Draw(engine, fx, Inputs(Card(FxChain.Empty), fx));
            var inputs = Inputs(Card(FxRig.Chain(FxRig.Slot("fx.invert"))), fx);
            DrawCompiled(engine, fx, inputs);
            // Black for longer than the runner keeps an unused slot and the lane an unused program.
            for (var i = 0; i < FxCompileLane.UnusedFrames + 2 * FxChainRunner.IdleFrames; i++)
                Draw(engine, fx, inputs with { Blackout = true });
            var black = FxRig.Live(engine, fx, "fx00000001");
            Assert.Equal((FxSlotState.Idle, FxPass.BlackWords), (black.State, black.Words));
            Assert.Equal(1, fx.Lane.Programs);

            var back = Draw(engine, fx, inputs);
            Assert.Equal(FxSlotState.Ok, FxRig.Live(engine, fx, "fx00000001").State);
            Pixels.Near(Inverse(Pixels.At(plain, W, 20, 10)), Pixels.At(back, W, 20, 10), 1);

            // A held picture keeps the chain's last word: it still shows what the chain made.
            Draw(engine, fx, inputs with { Freeze = true });
            Assert.Equal((FxSlotState.Ok, ""), (FxRig.Live(engine, fx, "fx00000001").State, FxRig.Live(engine, fx, "fx00000001").Words));
        });
    }

    [Fact]
    public void AnotherEditOfTheShowNeverRestartsAnEffectOnTheAir()
    {
        const string white = """
            /*{ "ISFVSN": "2" }*/
            void main() { gl_FragColor = vec4(1.0); }
            """;
        var asset = new FxAsset("fa00000003", "White", FxFormat.Isf, "", FxCatalog.ContentHash(white, null)) { Source = white };
        gl.Run(c =>
        {
            var d = c.Device;
            using var lane = new FxCompileLane(c);
            using var a = new FxChainRunner(d, lane);
            using var b = new FxChainRunner(d, lane);
            var show = Project.CreateDefault("Edits");
            var edited = show with { FxAssets = [asset] };
            var chain = FxRig.Chain(FxRig.Slot("fx.feedback", ParamSet.Empty.With("decay", 0.9)));
            using (var warmup = new FxChainRunner(d, lane))
            using (var first = FxRig.Picture(d, W, H, (_, _) => (0, 0, 0)))
            {
                long warm = 0;
                FxRig.Run(d, lane, warmup, show, chain, first, ref warm, owner: "warm");
            }
            long fa = 0, fb = 0;
            for (var k = 0; k < 60; k++)
            {
                // Halfway, b's show gains a file no slot uses: another catalogue, the same feedback files, the same trail.
                var leaderNs = 1_000_000_000L + k * 16_666_667L;
                using var bar = FxRig.Picture(d, W, H, (x, _) => Math.Abs(x - k * 2 % W) < 3 ? (255, 255, 255) : (0, 0, 0));
                var pa = Scene.ReadTexture(d, FxRig.Step(d, lane, a, show, chain, bar, ref fa, leaderNs));
                var pb = Scene.ReadTexture(d, FxRig.Step(d, lane, b, k < 30 ? show : edited, chain, bar, ref fb, leaderNs));
                Assert.Equal(pa, pb);
            }
            Assert.Equal(FxSlotState.Ok, FxRig.Live(b, "fx00000001").State);
        });
    }

    [Fact]
    public void AShowsCatalogueIsMadeOnceWhileAnotherShowIsRead()
    {
        static FxAsset Asset(string id, string colour)
        {
            var fs = "/*{ \"ISFVSN\": \"2\" }*/\nvoid main() { gl_FragColor = vec4(" + colour + "); }\n";
            return new FxAsset(id, id, FxFormat.Isf, "", FxCatalog.ContentHash(fs, null)) { Source = fs };
        }
        ImmutableArray<FxAsset> one = [Asset("fa00000011", "1.0")];
        ImmutableArray<FxAsset> two = [Asset("fa00000012", "0.5")];
        var first = FxCatalog.For(one);
        var second = FxCatalog.For(two);
        Assert.NotSame(first, second);
        for (var i = 0; i < 3; i++)
        {
            Assert.Same(first, FxCatalog.For(one));
            Assert.Same(second, FxCatalog.For(two));
        }
        Assert.Equal(first.Source("isf:fa00000011")!.Hash, FxCatalog.ContentHash(one[0].Source, null));
    }

    [Fact]
    public void AnImportedPictureTooLargeToDecodeFaultsItsSlotAndTheLaneGoesOn()
    {
        const string src = """
            /*{ "ISFVSN": "2", "IMPORTED": { "card": { "PATH": "card.png" } } }*/
            void main() { gl_FragColor = IMG_NORM_PIXEL(card, isf_FragNormCoord); }
            """;
        var asset = new FxAsset("fa00000004", "Huge", FxFormat.Isf, "", FxCatalog.ContentHash(src, null))
        {
            Source = src,
            Images = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, [KeyValuePair.Create("card", Convert.ToBase64String(PngHeader(100_000, 100_000)))]),
        };
        gl.Run(c =>
        {
            var d = c.Device;
            using var lane = new FxCompileLane(c);
            using var runner = new FxChainRunner(d, lane);
            using var input = FxRig.Picture(d, W, H, (_, _) => (40, 70, 100));
            var show = Project.CreateDefault("Huge") with { FxAssets = [asset] };
            long frame = 0;
            var px = FxRig.Run(d, lane, runner, show, FxRig.Chain(FxRig.Slot("isf:fa00000004"), FxRig.Slot("fx.invert", id: "fx00000002")), input, ref frame);
            var huge = FxRig.Live(runner, "fx00000001");
            Assert.Equal(FxSlotState.Faulted, huge.State);
            Assert.Contains("100000 × 100000", huge.Words, StringComparison.Ordinal);
            Assert.Equal(FxSlotState.Ok, FxRig.Live(runner, "fx00000002").State);
            Pixels.Near((215, 185, 155), Pixels.At(px, W, 10, 10), 1);
        });
    }

    [Fact]
    public void AFaultMetDrawingIsNotTriedAgainUntilTheSlotChangesAndBuffersFitTheContext()
    {
        gl.Run(c =>
        {
            var d = c.Device;
            var caps = d.Caps;
            Assert.Equal(TextureFormat.Rgba32F, IsfRenderer.BufferFormat(caps with { ColorBufferFloat = true }, wantsFloat: true));
            Assert.Equal(TextureFormat.Rgba16F, IsfRenderer.BufferFormat(caps with { ColorBufferFloat = true }, wantsFloat: false));
            Assert.Equal(TextureFormat.Rgba8, IsfRenderer.BufferFormat(caps with { ColorBufferFloat = false }, wantsFloat: true));

            using var lane = new FxCompileLane(c);
            using var black = FxRig.Picture(d, 1, 1, (_, _) => (0, 0, 0));
            using var input = FxRig.Picture(d, W, H, (_, _) => (40, 70, 100));
            using var renderer = new IsfRenderer(d, lane, black, black);
            var catalog = FxCatalog.Builtin;
            var kind = catalog.Find("fx.invert")!;
            var source = catalog.Translation(kind.Id, FxChainRunner.DialectOf(d.Dialect))!;
            var slot = FxRig.Slot("fx.invert");
            renderer.Configure(slot, kind, source, catalog.Source(kind.Id)!);
            var clock = FxTime.At(1_000_000_000, 0, 60, 1_760_000_000_000);
            var waited = Stopwatch.StartNew();
            long frame = 0;
            while (renderer.Apply(++frame, in clock, input, W, H) is null)
            {
                lane.Collect(frame);
                Assert.True(waited.Elapsed < FxRig.Patience, "the effect never compiled");
                Thread.Sleep(2);
            }

            renderer.Fail("a target this context cannot make");
            Assert.Null(renderer.Apply(++frame, in clock, input, W, H));
            Assert.Equal((FxSlotState.Faulted, "a target this context cannot make"), (renderer.State, renderer.Words));
            renderer.Configure(slot with { Mix = 0.5 }, kind, source, catalog.Source(kind.Id)!);
            Assert.NotNull(renderer.Apply(frame + 1, in clock, input, W, H));
            Assert.Equal(FxSlotState.Ok, renderer.State);
        });
    }

    /// <summary>A PNG that says it is width × height (its header and an empty image stream): what a decoder reads before it
    /// decodes anything.</summary>
    private static byte[] PngHeader(int width, int height)
    {
        static uint Crc(ReadOnlySpan<byte> data)
        {
            var c = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                c ^= b;
                for (var k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            return ~c;
        }
        static void Chunk(List<byte> png, string type, byte[] data)
        {
            var body = new List<byte>(System.Text.Encoding.ASCII.GetBytes(type));
            body.AddRange(data);
            png.AddRange(BigEndian((uint)data.Length));
            png.AddRange(body);
            png.AddRange(BigEndian(Crc(body.ToArray())));
        }
        static byte[] BigEndian(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
        var png = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Chunk(png, "IHDR", [.. BigEndian((uint)width), .. BigEndian((uint)height), 8, 6, 0, 0, 0]);
        Chunk(png, "IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        Chunk(png, "IEND", []);
        return [.. png];
    }

    private static (int R, int G, int B) Inverse((int R, int G, int B) c) => (255 - c.R, 255 - c.G, 255 - c.B);
}
