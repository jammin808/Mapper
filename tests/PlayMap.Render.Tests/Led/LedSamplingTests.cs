using System.Runtime.InteropServices;
using PlayMap.Core.Led;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Render.Engine;
using PlayMap.Render.Generators;
using PlayMap.Render.Gpu;
using PlayMap.Render.Led;
using PlayMap.Render.Platform;
using PlayMap.Tests;

namespace PlayMap.Render.Tests.Led;

/// <summary>The LED pass on the GPU (llvmpipe here) against its CPU reference: the bytes read back for every row equal the
/// reference exactly for point sampling in every space (canvas, output, surface UV, source) and colour order (RGB, GRB, BRG, RGBW,
/// 16-bit), within one code for area sampling; blackout zeroes colour and dimmer slots and keeps constants; a frozen frame keeps
/// its colours while a test still applies; every test pattern is the reference's.</summary>
[Collection("gl")]
public sealed class LedSamplingTests(GlFixture gl)
{
    private const string Canvas = "cv00000001";
    private const string Output = "op00000001";
    private const string Media = "md00000001";
    private const string Surface = "sf00000001";
    private const string Node = "ln00000001";

    /// <summary>Pictures as a texture each and as the reference reads them: a float canvas, an 8-bit output, an 8-bit media frame.</summary>
    private sealed class Pictures : ILedPictures, IDisposable
    {
        public readonly Dictionary<string, (GpuTexture Tex, LedReference.Picture Ref)> ByKey = new(StringComparer.Ordinal);

        public GpuTexture? Picture(LedGroup group) => ByKey.TryGetValue(group.Key, out var p) ? p.Tex : null;

        public LedReference.Picture? Reference(LedGroup group) => ByKey.TryGetValue(group.Key, out var p) ? p.Ref : null;

        public void Float(GlDevice d, string key, int w, int h, Func<int, int, (float R, float G, float B)> at)
        {
            // A half-float picture, as a canvas's composite is: the reference reads the values the texture holds.
            var data = new float[w * h * 4];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var (r, g, b) = at(x, y);
                    var i = (y * w + x) * 4;
                    (data[i], data[i + 1], data[i + 2], data[i + 3]) = ((float)(Half)r, (float)(Half)g, (float)(Half)b, 1);
                }
            }
            if (ByKey.TryGetValue(key, out var old))
                old.Tex.Dispose();
            var tex = d.CreateTexture(w, h, TextureFormat.Rgba16F);
            d.Upload(tex, MemoryMarshal.AsBytes(data.AsSpan()), UploadFormat.Rgba32F);
            ByKey[key] = (tex, new LedReference.Picture(data, w, h));
        }

        public void Bytes(GlDevice d, string key, int w, int h, Func<int, int, (int R, int G, int B)> at)
        {
            var rgba = new byte[w * h * 4];
            var data = new float[w * h * 4];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var (r, g, b) = at(x, y);
                    var i = (y * w + x) * 4;
                    (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = ((byte)r, (byte)g, (byte)b, 255);
                    (data[i], data[i + 1], data[i + 2], data[i + 3]) = (r / 255f, g / 255f, b / 255f, 1);
                }
            }
            if (ByKey.TryGetValue(key, out var old))
                old.Tex.Dispose();
            var tex = d.CreateTexture(w, h, TextureFormat.Rgba8);
            d.Upload(tex, rgba, UploadFormat.Rgba8);
            ByKey[key] = (tex, new LedReference.Picture(data, w, h));
        }

        public void Dispose()
        {
            foreach (var (tex, _) in ByKey.Values)
                tex.Dispose();
        }
    }

    private static LedFixture Fixture(string id, string type, LedSpace space, string spaceId, LedLayout layout, Vec3d at, LedSampling sampling = LedSampling.Point) =>
        new(id, id, type, space, spaceId, Transform3.Identity with { Position = at }, layout, null, sampling, 0, LedColor.Neutral, true, false, false, "#FF8800", []);

    private static LedLayout Line(int n, double length) => new(LedShape.Line, n, 1, new Vec3d(length, 0, 0), LedStart.TopLeft, false, false, []);

    /// <summary>A rig on every space and colour order, auto-patched onto one Art-Net node.</summary>
    private static Project Rig(LedSampling sampling)
    {
        var p = Project.CreateDefault("LED");
        var surface = new Surface(Surface, "Wall", "ms00000001", Transform3.Identity, [],
            new SurfaceContent(SurfaceMapping.Uv, SourceRef.Media(Media), new RectD(0.25, 0, 0.5, 1), null), 1, [], true, false, "#20C060", []);
        var mode = new LedFixtureType("lt00000001", "Dimmer, mode, RGB", [new LedChannelSlot(LedChannel.Dimmer, 8, 255), new LedChannelSlot(LedChannel.Constant, 8, 42)],
            [new LedChannelSlot(LedChannel.R, 8, 0), new LedChannelSlot(LedChannel.G, 8, 0), new LedChannelSlot(LedChannel.B, 8, 0)], [], 0, "", "");
        var fixtures = new List<LedFixture>
        {
            Fixture("lf00000001", "builtin:grb", LedSpace.Canvas, Canvas, Line(60, 59), new Vec3d(2, 5, 0), sampling),
            Fixture("lf00000002", "builtin:rgb", LedSpace.Canvas, Canvas, new LedLayout(LedShape.Grid, 8, 4, new Vec3d(56, 21, 0), LedStart.BottomRight, false, true, []), new Vec3d(3, 3, 0), sampling),
            Fixture("lf00000003", "builtin:brg", LedSpace.Output, Output, Line(30, 58), new Vec3d(1, 10, 0), sampling),
            Fixture("lf00000004", "builtin:rgbw", LedSpace.Surface, Surface, Line(5, 1), new Vec3d(0, 0.5, 0), sampling),
            Fixture("lf00000005", "builtin:rgb16", LedSpace.Source, "media:" + Media, Line(9, 1), new Vec3d(0, 0.25, 0), sampling),
            Fixture("lf00000006", mode.Id, LedSpace.Canvas, Canvas, Line(3, 2), new Vec3d(10, 20, 0), sampling),
            Fixture("lf00000007", "builtin:l", LedSpace.Canvas, Canvas, Line(4, 30), new Vec3d(0, 31, 0), sampling),
        };
        var node = new LedNode(Node, "PixLite", LedProtocol.ArtNet, "10.0.0.21", 0, true, 0, 0, LedSync.Latch, 510, "#FFFFFF");
        Assert.True(LedPatcher.TryAutoPatch(p.Leds with { Types = [mode] }, fixtures, [node], 0, 1, false, out var patches, out var why), why);
        fixtures = [.. fixtures.Select(f => f with { Patch = patches.First(x => x.FixtureId == f.Id).Patch })];
        return p with
        {
            Canvases = [new Canvas(Canvas, "Wall", 64, 32, "#000000", [])],
            Outputs = [p.Outputs[0] with { Id = Output, Width = 60, Height = 20 }],
            Stage = p.Stage with { Surfaces = [surface] },
            Leds = new LedRig { Nodes = [node], Types = [mode], Fixtures = [.. fixtures] },
        };
    }

    private static void Fill(GlDevice d, Pictures pics, int seed)
    {
        var rng = new Pcg32((ulong)seed);
        var noise = Enumerable.Range(0, 64 * 32 * 3).Select(_ => rng.NextInt(256)).ToArray();
        // The canvas: a half-float gradient with noise; the output and the media frame: 8-bit noise.
        pics.Float(d, Canvas, 64, 32, (x, y) => (x / 63f, y / 31f, noise[(y * 64 + x) * 3] / 255f));
        pics.Bytes(d, Output, 60, 20, (x, y) => (noise[(y * 64 + x) * 3], noise[(y * 64 + x) * 3 + 1], noise[(y * 64 + x) * 3 + 2]));
        pics.Bytes(d, "media:" + Media, 64, 16, (x, y) => (noise[(y * 64 + x) * 3 + 2], x * 4, y * 16));
    }

    /// <summary>Gradients only (the design's flat and gradient sources): a bilinear tap between 8-bit texels is filtered with the
    /// driver's own weight precision, which noise would turn into whole codes; between neighbours a few codes apart it is a fraction
    /// of one.</summary>
    private static void FillSmooth(GlDevice d, Pictures pics)
    {
        pics.Float(d, Canvas, 64, 32, (x, y) => (x / 63f, y / 31f, (x + y) / 94f));
        pics.Bytes(d, Output, 60, 20, (x, y) => (x * 4, y * 12, 255 - x * 4));
        pics.Bytes(d, "media:" + Media, 64, 16, (x, y) => (255 - x * 3, x * 4, y * 16));
    }

    /// <summary>Runs the pass for a frame, then one more so the first's read-back is collected; answers the first frame's rows.</summary>
    private static byte[] Run(RenderContext ctx, LedPass pass, LedFramePool pool, Project p, long leaderNs, bool blackout = false, bool freeze = false)
    {
        var d = ctx.Device;
        pass.FrameEnd(new PassFrame(1, leaderNs, p, GeneratorFrame.At(leaderNs / 1e9), d, blackout, freeze));
        d.Finish();
        pass.FrameEnd(new PassFrame(2, leaderNs, p, GeneratorFrame.At(leaderNs / 1e9), d, blackout, freeze));
        var first = pool.Take();
        Assert.NotNull(first);
        Assert.Equal(leaderNs, first.LeaderNs);
        var bytes = first.Slots[..(first.Rows * LedPlan.SlotsPerRow)];
        pool.Return(first);
        while (pool.Take() is { } more)
            pool.Return(more);
        return bytes;
    }

    private static byte[] Reference(LedPlan plan, Pictures pics, LedLive live, long leaderNs, bool blackout, float[]? colors = null)
    {
        colors ??= LedReference.Sample(plan, pics.Reference);
        var air = new float[plan.Fixtures.Length * 4];
        LedAir.Fill(plan, live, leaderNs, blackout, _ => 1f, 1f, air);
        return LedReference.Patch(plan, colors, air, (float)live.LevelAt(leaderNs), live.Test, LedTestPatterns.StepAt(leaderNs));
    }

    [Fact]
    public void PointSamplingEqualsTheReferenceInEverySpaceAndColourOrder() => gl.Run(ctx =>
    {
        var d = ctx.Device;
        using var pics = new Pictures();
        Fill(d, pics, 7);
        var show = Rig(LedSampling.Point);
        var plan = LedPlanBuilder.Build(show, 200, 1);
        Assert.Empty(plan.Warnings);
        Assert.Empty(plan.Conflicts);
        Assert.Equal(3, plan.Groups.Length);
        var pool = new LedFramePool();
        using var pass = new LedPass(d, pics) { Sink = pool };
        pass.Offer(plan);
        var gpu = Run(ctx, pass, pool, show, 1_000_000_000L);
        var cpu = Reference(plan, pics, LedLive.Initial, 1_000_000_000L, false);
        Assert.Equal(cpu.Length, gpu.Length);
        for (var i = 0; i < cpu.Length; i++)
            Assert.True(cpu[i] == gpu[i], $"row {i / 512} slot {i % 512 + 1}: GPU {gpu[i]}, reference {cpu[i]}");
        // Not a trivially black frame: most patched colour slots carry light, and the constants are there.
        Assert.True(gpu.Count(b => b != 0) > 300);
        Assert.Contains((byte)42, gpu);
    });

    [Fact]
    public void AreaSamplingIsWithinOneCodeOfTheReference() => gl.Run(ctx =>
    {
        var d = ctx.Device;
        using var pics = new Pictures();
        FillSmooth(d, pics);
        var show = Rig(LedSampling.Area);
        var plan = LedPlanBuilder.Build(show, 200, 1);
        Assert.All(plan.Groups, g => Assert.Equal(9, g.Taps));
        var pool = new LedFramePool();
        using var pass = new LedPass(d, pics) { Sink = pool };
        pass.Offer(plan);
        var gpu = Run(ctx, pass, pool, show, 2_000_000_000L);
        var cpu = Reference(plan, pics, LedLive.Initial, 2_000_000_000L, false);
        for (var i = 0; i < cpu.Length; i++)
        {
            // An 8-bit slot within one code; a 16-bit pair (high, low) within one 8-bit code of its value.
            var sel = plan.Patch[i * 4 + 1] < 0 ? 0 : (int)plan.Patch[i * 4 + 1] & 3;
            if (sel == 1)
            {
                var (g16, c16) = ((gpu[i] << 8) | gpu[i + 1], (cpu[i] << 8) | cpu[i + 1]);
                Assert.True(Math.Abs(g16 - c16) <= 257, $"row {i / 512} slots {i % 512 + 1}–{i % 512 + 2}: GPU {g16}, reference {c16}");
                i++;
                continue;
            }
            Assert.True(Math.Abs(cpu[i] - gpu[i]) <= 1, $"row {i / 512} slot {i % 512 + 1}: GPU {gpu[i]}, reference {cpu[i]}");
        }
        // Area sampling averages: it differs from point sampling of the same noise.
        var point = Reference(LedPlanBuilder.Build(Rig(LedSampling.Point), 200, 1), pics, LedLive.Initial, 2_000_000_000L, false);
        Assert.NotEqual(point, cpu);
    });

    [Fact]
    public void BlackoutZeroesColourAndDimmerAndKeepsConstantsOnTheNextFrame() => gl.Run(ctx =>
    {
        var d = ctx.Device;
        using var pics = new Pictures();
        Fill(d, pics, 3);
        var show = Rig(LedSampling.Point);
        var plan = LedPlanBuilder.Build(show, 200, 1);
        var pool = new LedFramePool();
        using var pass = new LedPass(d, pics) { Sink = pool, Live = LedLive.Initial with { Test = LedTestKind.Full } };
        pass.Offer(plan);
        var gpu = Run(ctx, pass, pool, show, 3_000_000_000L, blackout: true);
        Assert.Equal(LedReference.Black(plan), gpu);
        Assert.Equal(gpu.Length - 1, gpu.Count(b => b == 0));
        Assert.Contains((byte)42, gpu);
    });

    [Fact]
    public void AFrozenFrameKeepsItsColoursAndTheTestStillApplies() => gl.Run(ctx =>
    {
        var d = ctx.Device;
        using var pics = new Pictures();
        Fill(d, pics, 5);
        var show = Rig(LedSampling.Point);
        var plan = LedPlanBuilder.Build(show, 200, 1);
        var pool = new LedFramePool();
        using var pass = new LedPass(d, pics) { Sink = pool };
        pass.Offer(plan);
        var before = Run(ctx, pass, pool, show, 4_000_000_000L);
        var held = LedReference.Sample(plan, pics.Reference);
        Fill(d, pics, 99);
        Assert.Equal(before, Run(ctx, pass, pool, show, 4_100_000_000L, freeze: true));
        // The picture moved and the frame is not frozen: the LEDs follow it.
        Assert.NotEqual(before, Run(ctx, pass, pool, show, 4_200_000_000L));
        // Frozen again with a test on part of the rig: the rest holds the colours the last sampled frame had.
        var live = LedLive.Initial with { Test = LedTestKind.Chase, TestScope = ["lf00000001"], TestWords = "lf00000001" };
        pass.Live = live;
        var now = LedReference.Sample(plan, pics.Reference);
        Fill(d, pics, 4);
        Assert.Equal(Reference(plan, pics, live, 4_300_000_000L, false, now), Run(ctx, pass, pool, show, 4_300_000_000L, freeze: true));
        Assert.NotEqual(held, now);
    });

    [Theory]
    [InlineData(LedTestKind.Chase)]
    [InlineData(LedTestKind.Order)]
    [InlineData(LedTestKind.Universe)]
    [InlineData(LedTestKind.Fixture)]
    [InlineData(LedTestKind.Gradient)]
    [InlineData(LedTestKind.Walk)]
    [InlineData(LedTestKind.White)]
    [InlineData(LedTestKind.Full)]
    [InlineData(LedTestKind.Black)]
    public void EveryTestPatternIsTheReferences(LedTestKind kind) => gl.Run(ctx =>
    {
        var d = ctx.Device;
        using var pics = new Pictures();
        Fill(d, pics, 1);
        var show = Rig(LedSampling.Point);
        var plan = LedPlanBuilder.Build(show, 200, 1);
        var pool = new LedFramePool();
        var live = LedLive.Initial with { Test = kind, LevelFrom = 1, LevelTo = 1 };
        using var pass = new LedPass(d, pics) { Sink = pool, Live = live };
        pass.Offer(plan);
        foreach (var leader in new[] { 1_760_000_000_000_000_000L, 1_760_000_000_730_000_000L, 1_760_000_003_150_000_000L })
            Assert.Equal(Reference(plan, pics, live, leader, false), Run(ctx, pass, pool, show, leader));
    });

    [Fact]
    public void TheLevelAndTheLocateBlinkAreTheReferences() => gl.Run(ctx =>
    {
        var d = ctx.Device;
        using var pics = new Pictures();
        Fill(d, pics, 2);
        var show = Rig(LedSampling.Point);
        var plan = LedPlanBuilder.Build(show, 200, 1);
        var pool = new LedFramePool();
        var live = LedLive.Initial with { LevelFrom = 0.5, LevelTo = 0.5, Locate = "lf00000003", LocateUntilNs = 10_000_000_000 };
        using var pass = new LedPass(d, pics) { Sink = pool, Live = live };
        pass.Offer(plan);
        foreach (var leader in new[] { 5_000_000_000L, 5_300_000_000L })
        {
            var gpu = Run(ctx, pass, pool, show, leader);
            var cpu = Reference(plan, pics, live, leader, false);
            var worst = 0;
            for (var i = 0; i < cpu.Length; i++)
                worst = Math.Max(worst, Math.Abs(cpu[i] - gpu[i]));
            // Half the level lands between codes on some pixels: the GPU's pow may round the other way there.
            Assert.InRange(worst, 0, 1);
        }
        Assert.Empty(plan.Warnings);
    });

    /// <summary>Air levels for a test: a gain, a held output and a safe look per output.</summary>
    private sealed class Air : IAirLevels
    {
        public float GainAll = 1f;
        public string? HeldOutput;
        public string? SafeOutput;

        public float Gain(string outputId, long leaderNs) => GainAll;

        public SourceRef? Override(string outputId, long leaderNs) => outputId == SafeOutput ? SourceRef.Solid("#00FF00") : null;

        public bool Hold(string outputId, long leaderNs) => outputId == HeldOutput;
    }

    private static byte[] RunAir(RenderContext ctx, LedPass pass, LedFramePool pool, Project p, long leaderNs, IAirLevels air)
    {
        var d = ctx.Device;
        pass.FrameEnd(new PassFrame(1, leaderNs, p, GeneratorFrame.At(leaderNs / 1e9), d, false, false) { Levels = air });
        d.Finish();
        pass.FrameEnd(new PassFrame(2, leaderNs, p, GeneratorFrame.At(leaderNs / 1e9), d, false, false) { Levels = air });
        var first = pool.Take()!;
        var bytes = first.Slots[..(first.Rows * LedPlan.SlotsPerRow)];
        pool.Return(first);
        while (pool.Take() is { } more)
            pool.Return(more);
        return bytes;
    }

    [Fact]
    public void AnOutputHeldOrShowingASafeLookHoldsOrDarkensTheRigAsItsOutputsAre() => gl.Run(ctx =>
    {
        var d = ctx.Device;
        using var pics = new Pictures();
        Fill(d, pics, 21);
        var show = Rig(LedSampling.Point);
        var plan = LedPlanBuilder.Build(show, 200, 1);
        var pool = new LedFramePool();
        using var pass = new LedPass(d, pics) { Sink = pool };
        pass.Offer(plan);
        var air = new Air();
        var live = RunAir(ctx, pass, pool, show, 1_000_000_000, air);
        // An output holds its picture (a FREEZE outcome): the rig's canvas and source fixtures hold too.
        // (The renderer holds the output's own picture; here only the canvas and the media move.)
        air.HeldOutput = Output;
        pics.Float(d, Canvas, 64, 32, (x, y) => (y / 31f, x / 63f, 0.5f));
        pics.Bytes(d, "media:" + Media, 64, 16, (x, y) => (x * 4, 255 - y * 16, 77));
        Assert.Equal(live, RunAir(ctx, pass, pool, show, 1_100_000_000, air));
        air.HeldOutput = null;
        var moved = RunAir(ctx, pass, pool, show, 1_200_000_000, air);
        Assert.NotEqual(live, moved);
        // A panic shows an output its safe look: the rig's fixtures go dark (no safe look of their own), constants kept; the output
        // fixture samples its output's final picture, which the renderer replaces itself.
        air.SafeOutput = Output;
        var safe = RunAir(ctx, pass, pool, show, 1_300_000_000, air);
        var lf3 = plan.Fixture("lf00000003")!;
        var rows = new float[plan.Fixtures.Length * 4];
        LedAir.Fill(plan, LedLive.Initial, 1_300_000_000, false, _ => 1f, 0f, rows);
        var colors = LedReference.Sample(plan, pics.Reference);
        Assert.Equal(LedReference.Patch(plan, colors, rows, 1, LedTestKind.None, 0), safe);
        Assert.Contains((byte)42, safe);
        Assert.Equal(Output, lf3.AirOutput);
    });

    [Fact]
    public void AWarmFrameAllocatesNothing() => gl.Run(ctx =>
    {
        var d = ctx.Device;
        using var pics = new Pictures();
        Fill(d, pics, 8);
        var show = Rig(LedSampling.Area);
        var plan = LedPlanBuilder.Build(show, 200, 1);
        var pool = new LedFramePool();
        var live = LedLive.Initial with { Test = LedTestKind.Chase, TestScope = ["lf00000002"], Locate = "lf00000001", LocateUntilNs = long.MaxValue };
        using var pass = new LedPass(d, pics) { Sink = pool, Live = live };
        pass.Offer(plan);
        var air = new Air { GainAll = 0.8f };
        void Frame(long k)
        {
            pass.FrameEnd(new PassFrame(k, k * 16_666_667L, show, GeneratorFrame.At(k / 60.0), d, k % 7 == 0, k % 11 == 0) { Levels = air });
            while (pool.Take() is { } f)
                pool.Return(f);
        }
        for (var k = 1; k <= 30; k++)
            Frame(k);
        var before = Allocations.Start();
        for (var k = 31; k <= 330; k++)
            Frame(k);
        Assert.Equal(0, Allocations.Since(before));
        Assert.True(pass.Frames >= 330);
    });

    /// <summary>Pictures that count how often a group's picture is asked for.</summary>
    private sealed class Counting(ILedPictures inner) : ILedPictures
    {
        public int Asked;

        public GpuTexture? Picture(LedGroup group)
        {
            Asked++;
            return inner.Picture(group);
        }
    }

    private sealed class RawOutput(string outputId) : IRawOutputs
    {
        public SourceRef? For(string id) => id == outputId ? SourceRef.Solid("#FFFFFF") : null;
    }

    [Fact]
    public void BlackoutResolvesNoPictureAndARawOutputsFixturesAreDark() => gl.Run(ctx =>
    {
        var d = ctx.Device;
        using var pics = new Pictures();
        Fill(d, pics, 31);
        var show = Rig(LedSampling.Point);
        var plan = LedPlanBuilder.Build(show, 200, 1);
        var counting = new Counting(pics);
        var pool = new LedFramePool();
        using var pass = new LedPass(d, counting) { Sink = pool };
        pass.Offer(plan);
        Run(ctx, pass, pool, show, 1_000_000_000);
        Assert.True(counting.Asked > 0);
        // Blackout on every fixture: nothing can light, so no picture is resolved; the frame sent is black with its constants.
        counting.Asked = 0;
        Assert.Equal(LedReference.Black(plan), Run(ctx, pass, pool, show, 1_100_000_000, blackout: true));
        Assert.Equal(0, counting.Asked);
        // An output on a raw frame (a scan or an align run): its fixture is dark, the rest of the rig plays.
        pass.Raw = new RawOutput(Output);
        var gpu = Run(ctx, pass, pool, show, 1_200_000_000);
        var air = new float[plan.Fixtures.Length * 4];
        LedAir.Fill(plan, LedLive.Initial, 1_200_000_000, false, id => id == Output ? 0f : 1f, 1f, air);
        var cpu = LedReference.Patch(plan, LedReference.Sample(plan, pics.Reference), air, 1, LedTestKind.None, 0);
        Assert.Equal(cpu, gpu);
        var lf3 = plan.Fixture("lf00000003")!;
        var row = plan.RowOf(Node, 0);
        Assert.Contains(gpu, b => b != 0);
        Assert.True(lf3.Pixels > 0 && row >= 0);
    });
}
