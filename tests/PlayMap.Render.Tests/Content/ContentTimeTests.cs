using PlayMap.Core.Model;
using PlayMap.Render.Engine;
using PlayMap.Render.Generators;

namespace PlayMap.Render.Tests.Content;

/// <summary>Every generator is a pure function of its own time (leader time − its origin, or its item's clip time): two engines
/// started three seconds apart draw the same pixels at the same leader instant; RETIME moves the picture by exactly the shift; a
/// leader time seven days on draws as the same offset from zero does (no float32 drift); a routed generator reads its item's time;
/// the sync marks keep the leader's grid.</summary>
[Collection("gl")]
public class ContentTimeTests(GlFixture gl)
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
        var (a, b) = gl.Run(c =>
        {
            using var first = new RenderEngine(c, new RenderEngineOptions(Dither: false, GpuTiming: false));
            using var late = new RenderEngine(c, new RenderEngineOptions(Dither: false, GpuTiming: false));
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
        gl.Run(c =>
        {
            using var services = new RenderServices(c.Device);
            using var retimed = new GeneratorHost(services);
            using var fresh = new GeneratorHost(services);
            var shifted = Kinds(5_000_000_000);
            var zero = Kinds(0);
            for (var i = 0; i < shifted.Length; i++)
            {
                // Leader 12 s from an origin at 5 s is 7 s of its own time: the picture a generator from the epoch draws at 7 s.
                var f1 = ContentGl.Settle(retimed, shifted[i], 12, 1, W, H);
                var f2 = ContentGl.Settle(fresh, zero[i], 7, 1, W, H);
                var p1 = Scene.ReadTexture(c.Device, ContentGl.Draw(retimed, shifted[i], 12, f1, W, H));
                var p2 = Scene.ReadTexture(c.Device, ContentGl.Draw(fresh, zero[i], 7, f2, W, H));
                Assert.True(p1.AsSpan().SequenceEqual(p2), $"{shifted[i].Kind}: a retimed generator is not its picture shifted");
                // And it is not the picture an un-retimed generator draws at 12 s.
                var f3 = ContentGl.Settle(fresh, zero[i], 12, f2 + 1, W, H);
                var p3 = Scene.ReadTexture(c.Device, ContentGl.Draw(fresh, zero[i], 12, f3, W, H));
                Assert.False(p1.AsSpan().SequenceEqual(p3), $"{shifted[i].Kind}: RETIME changed nothing");
            }
        });
    }

    [Fact]
    public void ALeaderTimeSevenDaysOnDrawsAsTheSameOffsetFromZero()
    {
        const long week = 7L * 86_400 * 1_000_000_000;
        gl.Run(c =>
        {
            using var services = new RenderServices(c.Device);
            using var late = new GeneratorHost(services);
            using var early = new GeneratorHost(services);
            var shifted = Kinds(week);
            var zero = Kinds(0);
            for (var i = 0; i < shifted.Length; i++)
            {
                var f1 = ContentGl.Settle(late, shifted[i], week / 1e9 + 10.25, 1, W, H);
                var f2 = ContentGl.Settle(early, zero[i], 10.25, 1, W, H);
                var p1 = Scene.ReadTexture(c.Device, ContentGl.Draw(late, shifted[i], week / 1e9 + 10.25, f1, W, H));
                var p2 = Scene.ReadTexture(c.Device, ContentGl.Draw(early, zero[i], 10.25, f2, W, H));
                Assert.True(p1.AsSpan().SequenceEqual(p2), $"{shifted[i].Kind} drifts seven days on");
            }
        });
    }

    [Fact]
    public void AGeneratorAPlaylistRoutesReadsItsItemsClipTime()
    {
        gl.Run(c =>
        {
            using var services = new RenderServices(c.Device);
            using var routed = new GeneratorHost(services);
            using var own = new GeneratorHost(services);
            var content = new ContentFrame();
            foreach (var g in Kinds(0))
            {
                // Leader 100 s, the item 10 s in: the picture the generator draws 10 s into its own time.
                content.BeginItems();
                content.SetItemTime(g.Id, 10_000_000_000);
                var f1 = ContentGl.Settle(routed, g, 100, 1, W, H, content);
                var f2 = ContentGl.Settle(own, g, 10, 1, W, H);
                var p1 = Scene.ReadTexture(c.Device, ContentGl.Draw(routed, g, 100, f1, W, H, content));
                var p2 = Scene.ReadTexture(c.Device, ContentGl.Draw(own, g, 10, f2, W, H));
                Assert.True(p1.AsSpan().SequenceEqual(p2), $"{g.Kind} does not read its item's time");
                Assert.True(content.Stats.TryGet(g.Id, out var live));
                Assert.Equal(10_000_000_000, live.TimeNs);
            }
        });
    }

    [Fact]
    public void TheSyncMarksKeepTheLeadersGridWhateverTheirOrigin()
    {
        gl.Run(c =>
        {
            using var services = new RenderServices(c.Device);
            using var host = new GeneratorHost(services);
            var marks = Scene.Gen("pattern.syncmarks", null, "gn_marks") with { OriginNs = 1_000_000_000 };
            Assert.Equal(4.01, GeneratorHost.OwnSeconds(marks, GeneratorFrame.At(4.01), null, out var origin), 9);
            Assert.Equal(0, origin);
            var other = Scene.Gen("fractal.julia", null, "gn_j") with { OriginNs = 1_000_000_000 };
            Assert.Equal(3.01, GeneratorHost.OwnSeconds(other, GeneratorFrame.At(4.01), null, out origin), 9);
            Assert.Equal(1_000_000_000, origin);
        });
    }
}
