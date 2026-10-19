using PlayMap.Core.Gpu;
using PlayMap.Core.Model;
using PlayMap.Render.Engine;
using PlayMap.Render.Generators;

namespace PlayMap.Render.Tests.Content;

/// <summary>Every generator is a pure function of its own time (leader time − its origin, or its item's clip time): two engines
/// started three seconds apart draw the same pixels at the same leader instant; RETIME moves the picture by exactly the shift; a
/// leader time seven days on draws as the same offset from zero does (no float32 drift); a routed generator reads its item's time;
/// the sync marks keep the leader's grid.</summary>
[Collection("gpu")]
public class ContentTimeTests(BackendFixture backends)
{
    private const int W = 96;
    private const int H = 54;

    private static Generator[] Kinds(long originNs) =>
    [
        Scene.Gen("particles.flow", ParamSet.Empty.With("count", 4096).With("lifeMin", 0.5).With("lifeMax", 1.5), "gn_flow") with { OriginNs = originNs },
        Scene.Gen("fractal.mandelbulb", ParamSet.Empty.With("steps", 48), "gn_bulb") with { OriginNs = originNs },
        Scene.Gen("fractal.kifs", ParamSet.Empty.With("steps", 48), "gn_kifs") with { OriginNs = originNs },
        Scene.Gen("reactive.scene", ParamSet.Empty.With("scene", "tunnel"), "gn_tunnel") with { OriginNs = originNs },
        Scene.Gen("fractal.julia", null, "gn_julia") with { OriginNs = originNs },
        Scene.Gen("pattern.colorcycle", null, "gn_cycle") with { OriginNs = originNs },
    ];

    [Fact]
    public void AGeneratorsFrameAtALeaderInstantIsTheSameOnTwoEnginesStartedThreeSecondsApart()
    {
        var generators = Kinds(1_000_000_000);
        var outputs = generators.Select(g => Scene.SourceOutput("op_" + g.Id[3..], SourceRef.Generator(g.Id), W, H)).ToArray();
        var project = Scene.Project([.. generators], [.. outputs]);
        var (a, b) = backends.Run(leg =>
        {
            using var first = new RenderEngine(leg.Device, new RenderEngineOptions(Dither: false, GpuTiming: false));
            using var late = new RenderEngine(leg.Device, new RenderEngineOptions(Dither: false, GpuTiming: false));
            // The first engine plays from 1 s; the second starts at 4 s. Both draw 60 frames a second to 8 s.
            for (var f = 0; f <= 420; f++)
            {
                var t = 1 + f / 60.0;
                first.Render(Scene.Inputs(project, t));
                if (t >= 4)
                    late.Render(Scene.Inputs(project, t));
            }
            return (outputs.Select(o => Read(first, o.Id)).ToArray(), outputs.Select(o => Read(late, o.Id)).ToArray());
        });
        for (var i = 0; i < outputs.Length; i++)
            Assert.True(a[i].AsSpan().SequenceEqual(b[i]), $"{generators[i].Kind} differs between the two engines at 8 s");
    }

    private static byte[] Read(RenderEngine engine, string outputId)
    {
        var px = new byte[W * H * 4];
        Assert.True(engine.ReadOutput(outputId, px));
        return px;
    }

    [Fact]
    public void RetimeShiftsThePictureByExactlyItsShift()
    {
        backends.Run(leg =>
        {
            using var services = new RenderServices(leg.Device);
            using var retimed = new GeneratorHost(services);
            using var fresh = new GeneratorHost(services);
            var shifted = Kinds(5_000_000_000);
            var zero = Kinds(0);
            for (var i = 0; i < shifted.Length; i++)
            {
                // Leader 12 s from an origin at 5 s is 7 s of its own time: the picture a generator from the epoch draws at 7 s.
                var f1 = ContentDraws.Settle(retimed, shifted[i], 12, 1, W, H);
                var f2 = ContentDraws.Settle(fresh, zero[i], 7, 1, W, H);
                var p1 = Scene.ReadTexture(leg.Device, ContentDraws.Draw(retimed, shifted[i], 12, f1, W, H));
                var p2 = Scene.ReadTexture(leg.Device, ContentDraws.Draw(fresh, zero[i], 7, f2, W, H));
                Assert.True(p1.AsSpan().SequenceEqual(p2), $"{shifted[i].Kind}: a retimed generator is not its picture shifted");
                // And it is not the picture an un-retimed generator draws at 12 s.
                var f3 = ContentDraws.Settle(fresh, zero[i], 12, f2 + 1, W, H);
                var p3 = Scene.ReadTexture(leg.Device, ContentDraws.Draw(fresh, zero[i], 12, f3, W, H));
                Assert.False(p1.AsSpan().SequenceEqual(p3), $"{shifted[i].Kind}: RETIME changed nothing");
            }
        });
    }

    [Fact]
    public void ALeaderTimeSevenDaysOnDrawsAsTheSameOffsetFromZero()
    {
        const long week = 7L * 86_400 * 1_000_000_000;
        backends.Run(leg =>
        {
            using var services = new RenderServices(leg.Device);
            using var late = new GeneratorHost(services);
            using var early = new GeneratorHost(services);
            var shifted = Kinds(week);
            var zero = Kinds(0);
            for (var i = 0; i < shifted.Length; i++)
            {
                var f1 = ContentDraws.Settle(late, shifted[i], week / 1e9 + 10.25, 1, W, H);
                var f2 = ContentDraws.Settle(early, zero[i], 10.25, 1, W, H);
                var p1 = Scene.ReadTexture(leg.Device, ContentDraws.Draw(late, shifted[i], week / 1e9 + 10.25, f1, W, H));
                var p2 = Scene.ReadTexture(leg.Device, ContentDraws.Draw(early, zero[i], 10.25, f2, W, H));
                Assert.True(p1.AsSpan().SequenceEqual(p2), $"{shifted[i].Kind} drifts seven days on");
            }
        });
    }

    [Fact]
    public void OwnTimeIsWholeNanosecondsSoAnOddOriginDaysOnStepsAsZeroDoes()
    {
        // An origin and a leader that are no whole number of anything: the leader minus the origin is still exactly 10.25 s.
        const long origin = 3L * 86_400 * 1_000_000_000 + 333_333_337;
        backends.Run(leg =>
        {
            using var services = new RenderServices(leg.Device);
            using var odd = new GeneratorHost(services);
            using var zero = new GeneratorHost(services);
            var a = Scene.Gen("particles.flow", ParamSet.Empty.With("count", 4096).With("lifeMin", 0.5).With("lifeMax", 1), "gn_odd") with { OriginNs = origin };
            var b = a with { OriginNs = 0 };
            const long own = 10_250_000_000;
            var fa = Settle(odd, a, origin + own);
            var fb = Settle(zero, b, own);
            Assert.Equal(1230, ((FlowGenerator)odd.RendererOf(a.Id, W, H)!).SimStep);
            var pa = Scene.ReadTexture(leg.Device, DrawAt(odd, a, origin + own, fa));
            var pb = Scene.ReadTexture(leg.Device, DrawAt(zero, b, own, fb));
            Assert.True(pa.AsSpan().SequenceEqual(pb), "an odd origin days on draws another field");
        });
    }

    private static GpuTexture DrawAt(GeneratorHost host, Generator g, long leaderNs, long frame)
    {
        host.BeginFrame(frame);
        var tex = host.Render(g, W, H, ContentDraws.Context, GeneratorFrame.At(leaderNs / 1e9) with { LeaderNs = leaderNs });
        host.EndFrame();
        return tex;
    }

    private static long Settle(GeneratorHost host, Generator g, long leaderNs)
    {
        for (var f = 1; f < 40; f++)
        {
            DrawAt(host, g, leaderNs, f);
            if (host.RendererOf(g.Id, W, H)!.Progress >= 1)
                return f + 1;
        }
        throw new InvalidOperationException("did not settle");
    }

    [Fact]
    public void AGeneratorAPlaylistRoutesReadsItsItemsClipTime()
    {
        backends.Run(leg =>
        {
            using var services = new RenderServices(leg.Device);
            using var routed = new GeneratorHost(services);
            using var own = new GeneratorHost(services);
            var content = new ContentFrame();
            foreach (var g in Kinds(0))
            {
                // Leader 100 s, the item 10 s in: the picture the generator draws 10 s into its own time.
                content.BeginItems();
                content.SetItemTime(g.Id, 10_000_000_000);
                var f1 = ContentDraws.Settle(routed, g, 100, 1, W, H, content);
                var f2 = ContentDraws.Settle(own, g, 10, 1, W, H);
                var p1 = Scene.ReadTexture(leg.Device, ContentDraws.Draw(routed, g, 100, f1, W, H, content));
                var p2 = Scene.ReadTexture(leg.Device, ContentDraws.Draw(own, g, 10, f2, W, H));
                Assert.True(p1.AsSpan().SequenceEqual(p2), $"{g.Kind} does not read its item's time");
                Assert.True(content.Stats.TryGet(g.Id, out var live));
                Assert.Equal(10_000_000_000, live.TimeNs);
            }
        });
    }

    [Fact]
    public void TheSyncMarksKeepTheLeadersGridWhateverTheirOrigin()
    {
        backends.Run(leg =>
        {
            using var services = new RenderServices(leg.Device);
            using var host = new GeneratorHost(services);
            var marks = Scene.Gen("pattern.syncmarks", null, "gn_marks") with { OriginNs = 1_000_000_000 };
            Assert.Equal(4_010_000_000, GeneratorHost.OwnNs(marks, GeneratorFrame.At(4.01), null, out var origin));
            Assert.Equal(0, origin);
            var other = Scene.Gen("fractal.julia", null, "gn_j") with { OriginNs = 1_000_000_000 };
            Assert.Equal(3_010_000_000, GeneratorHost.OwnNs(other, GeneratorFrame.At(4.01), null, out origin));
            Assert.Equal(1_000_000_000, origin);
        });
    }
}
