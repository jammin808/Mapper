using PlayMap.Core.Gpu;
using PlayMap.Render.Engine;

namespace PlayMap.Render.Tests.Rhi;

/// <summary>GPU time spans over a device's timestamp slots: a frame that times more spans than the device has slots asks for more, so
/// every span lands a time; a span whose owner draws rarely lands all the same, read as later frames start; a device that measures
/// nothing lands none and reads 0.</summary>
[Collection("gpu")]
public sealed class GpuSpanTests(BackendFixture backends)
{
    /// <summary>One frame in which every timer measures one draw of its own.</summary>
    private static FenceValue Frame(IGpuDevice d, RenderTarget target, ProgramId program, IEnumerable<GpuSpanTimer> timers)
    {
        var frame = BackendFrames.Next;
        return BackendFrames.Pass(d, target, PassDesc.ClearBlack, list =>
        {
            foreach (var timer in timers)
            {
                timer.Begin(list, frame);
                list.Draw(program, DrawState.Opaque).Set("uCell", 3f).Fullscreen();
                timer.End(list);
            }
        });
    }

    [Fact]
    public void FortySpansAFrameForTenFramesEachLandATime()
    {
        backends.Run(leg =>
        {
            var d = leg.Device;
            using var target = d.CreateTarget(new TargetDesc(16, 16, TextureFormat.Rgba8));
            var checker = d.Programs.Id("checker");
            var timers = Enumerable.Range(0, 40).Select(_ => new GpuSpanTimer(d)).ToArray();
            var last = FenceValue.None;
            for (var f = 0; f < 10; f++)
                last = Frame(d, target, checker, timers);
            d.WaitFor(last);
            // Two more frames start: every span in flight is read as they do.
            Frame(d, target, checker, timers.Take(1));
            Frame(d, target, checker, timers.Take(1));
            if (!d.Features.Timers)
            {
                Assert.All(timers, t => Assert.Equal((0L, 0L), (t.Landed, t.LastNs)));
                return;
            }
            Assert.All(timers, t => Assert.True(t.Landed >= 1, "a span never landed"));
            Assert.True(d.Timers.Slots >= 80, $"{d.Timers.Slots} slots for 40 spans");
            Assert.Empty(leg.TakeErrors());
        });
    }

    [Fact]
    public void ASpanWhoseOwnerDrawsRarelyLandsAsLaterFramesStart()
    {
        backends.Run(leg =>
        {
            var d = leg.Device;
            using var target = d.CreateTarget(new TargetDesc(16, 16, TextureFormat.Rgba8));
            var checker = d.Programs.Id("checker");
            // A generator only a preview draws, once, beside an output timed every frame.
            var rare = new GpuSpanTimer(d);
            var output = new GpuSpanTimer(d);
            var done = Frame(d, target, checker, [rare, output]);
            d.WaitFor(done);
            for (var f = 0; f < 10; f++)
                Frame(d, target, checker, [output]);
            if (!d.Features.Timers)
            {
                Assert.Equal(0, rare.Landed);
                return;
            }
            Assert.Equal(1, rare.Landed);
            Assert.Equal(0, rare.Pending);
            Assert.True(output.Landed >= 1);
            Assert.Empty(leg.TakeErrors());
        });
    }
}
