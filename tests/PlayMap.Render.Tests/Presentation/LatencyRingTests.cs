using PlayMap.Core.Model;
using PlayMap.Render.Engine;
using PlayMap.Render.Timing;

namespace PlayMap.Render.Tests.Presentation;

/// <summary>Per-output latency: two outputs of 17 and 50 ms at 60 Hz plan a 2-frame ring for the faster one and an air latency of
/// 50 ms (raised to a group's, never lowered); a difference past six frames is refused with words and capped; the renderer presents
/// the picture drawn two frames earlier on the delayed output and today's on the other, its clock says so, and a ring let go presents
/// at once again.</summary>
[Collection("gl")]
public sealed class LatencyRingTests(GlFixture gl)
{
    [Fact]
    public void SeventeenAndFiftyMillisecondsAtSixtyHertzMakeATwoFrameRingAndFiftyMillisecondsOfAir()
    {
        var plan = LatencyPlan.Make([("op_fast", 17, 60), ("op_slow", 50, 60)]);
        Assert.Equal(50, plan.AirLatencyMs);
        Assert.Equal(50_000_000, plan.AirLatencyNs);
        var fast = plan.For("op_fast");
        Assert.Equal(2, fast.DelayFrames);
        Assert.InRange(Math.Abs(fast.ResidualMs), 0, 1000 / 60.0 / 2);
        Assert.Equal(-0.333, fast.ResidualMs);
        Assert.Equal(0, plan.For("op_slow").DelayFrames);
        Assert.Equal(0, plan.For("op_slow").ResidualMs);
        Assert.Equal("", plan.Words);

        // A group's slower display raises the air; a faster one never lowers it.
        var raised = LatencyPlan.Make([("op_fast", 17, 60), ("op_slow", 50, 60)], groupAirMs: 80);
        Assert.Equal(80, raised.AirLatencyMs);
        Assert.Equal(2, raised.For("op_slow").DelayFrames);
        Assert.Equal(4, raised.For("op_fast").DelayFrames);
        Assert.Equal(50, LatencyPlan.Make([("op_fast", 17, 60), ("op_slow", 50, 60)], groupAirMs: 20).AirLatencyMs);

        // Each output against its own refresh; an unknown refresh is 60 Hz; no outputs, no air.
        Assert.Equal(4, LatencyPlan.Make([("op_a", 0, 120), ("op_b", 33.3, 120)]).For("op_a").DelayFrames);
        Assert.Equal(2, LatencyPlan.Make([("op_a", 0, 0), ("op_b", 33.3, 0)]).For("op_a").DelayFrames);
        Assert.Equal(0, LatencyPlan.Make([]).AirLatencyMs);

        // More than six frames apart: refused when set, capped with words here.
        Assert.Null(LatencyPlan.Refuse([("op_a", 0, 60), ("op_b", 100, 60)]));
        Assert.Equal("op_a would wait 9 frames (150 ms at 60 Hz) for the slowest display: the delay ring holds at most 6 — check the displays' input lag",
            LatencyPlan.Refuse([("op_a", 0, 60), ("op_b", 150, 60)]));
        var capped = LatencyPlan.Make([("op_a", 0, 60), ("op_b", 150, 60)]);
        Assert.Equal(6, capped.For("op_a").DelayFrames);
        Assert.StartsWith("op_a would wait 9 frames", capped.Words, StringComparison.Ordinal);
    }

    private sealed class Picture(int id) : IDisposable
    {
        public int Id { get; } = id;
        public int Written { get; set; } = -1;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void TheRingPresentsWhatWasWrittenItsDelayAgoAndTheOldestWhileItFills()
    {
        using var ring = new DelayRing<Picture>();
        var made = 0;
        ring.Configure(2, () => new Picture(made++));
        Assert.Equal(3, ring.Count);
        var shown = new List<int>();
        for (var f = 0; f < 6; f++)
        {
            ring.Next().Written = f;
            shown.Add(ring.Presented().Written);
        }
        Assert.Equal([0, 0, 0, 1, 2, 3], shown);
        // The same delay again changes nothing; none lets every picture go.
        var first = ring.First!;
        ring.Configure(2, () => new Picture(made++));
        Assert.Equal(3, made);
        ring.Configure(0, () => throw new InvalidOperationException());
        Assert.True(first.Disposed);
        Assert.Equal(0, ring.Count);
        Assert.Throws<InvalidOperationException>(() => ring.Next());
    }

    [Fact]
    public void TheRendererPresentsTheDelayedOutputTwoFramesLateAndTheOtherAtOnce()
    {
        static byte Level(int frame) => (byte)(40 + frame * 30);
        static Project Frame(int f) => Scene.Project([],
        [
            Scene.SourceOutput("op_fast", SourceRef.Solid($"#{Level(f):X2}{Level(f):X2}{Level(f):X2}"), 16, 8),
            Scene.SourceOutput("op_slow", SourceRef.Solid($"#{Level(f):X2}{Level(f):X2}{Level(f):X2}"), 16, 8),
        ]);
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: false, Dither: false));
            var plan = LatencyPlan.Make([("op_fast", 17, 60), ("op_slow", 50, 60)]);
            foreach (var d in plan.Outputs)
                engine.SetDelay(d.OutputId, d.DelayFrames, d.ResidualMs);
            var bgra = new byte[16 * 8 * 4];
            for (var f = 0; f < 6; f++)
            {
                engine.Render(Scene.Inputs(Frame(f), 1 + f / 60.0));
                Assert.True(engine.ReadPresented("op_fast", bgra));
                Assert.InRange(bgra[1], Level(Math.Max(0, f - 2)) - 1, Level(Math.Max(0, f - 2)) + 1);
                Assert.True(engine.ReadPresented("op_slow", bgra));
                Assert.InRange(bgra[1], Level(f) - 1, Level(f) + 1);
                // The finished picture is this frame's on both (sends and snapshots read it).
                Assert.True(engine.ReadOutput("op_fast", bgra));
                Assert.InRange(bgra[1], Level(f) - 1, Level(f) + 1);
            }
            var present = engine.Present("op_fast")!.Value;
            Assert.Equal(2, present.DelayFrames);
            Assert.Equal(-0.333, present.LatencyResidualMs);
            Assert.Equal(0, engine.Present("op_slow")!.Value.DelayFrames);

            // The delay let go: the next frame presents at once.
            engine.SetDelay("op_fast", 0, 0);
            engine.Render(Scene.Inputs(Frame(6), 2));
            Assert.True(engine.ReadPresented("op_fast", bgra));
            Assert.InRange(bgra[1], Level(6) - 1, Level(6) + 1);
            Assert.Empty(c.Device.TakeErrors());
        });
    }
}
