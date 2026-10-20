using PlayMap.Core.Effects;
using PlayMap.Core.Model;
using PlayMap.Render.Effects;
using PlayMap.Render.Tests.Effects;

namespace PlayMap.Render.Tests.Shaders;

/// <summary>A quarantined kind on the reference backend (GL): its slot draws its input unchanged (± 0 codes: the chain hands on the
/// input itself) and says so in its observed state (quarantined, at no GPU time), while the rest of the chain draws what it draws
/// after a slot bypassed by hand; once released the slot draws its effect again. A slot that drew before its kind was quarantined
/// reads quarantined after every frame past the runner's idle sweep, which lets go of its renderer and never of its observed state.</summary>
[Collection("gpu")]
public sealed class QuarantineBypassTests(BackendFixture backends)
{
    private const int W = 32, H = 18;
    private static readonly Project Show = Project.CreateDefault("Quarantine");

    private static (int, int, int) Ramp(int x, int y) => (x * 8, y * 14, 200 - x * 6);

    [Fact]
    public void AQuarantinedSlotPassesItsInputUnchangedAndDrawsAgainOnceReleased()
    {
        backends.Run(leg =>
        {
            var d = leg.Device;
            using var lane = new FxLane(d);
            using var runner = new FxChainRunner(d, lane);
            using var picture = FxRig.Picture(d, W, H, Ramp);
            var input = Scene.ReadTexture(d, picture);
            var quarantine = new ShaderQuarantine();
            quarantine.Add(new QuarantineEntry(FxCatalog.Builtin.Source("fx.invert")!.Hash, "Invert", QuarantineReason.DeviceRemoved,
                "the GPU stopped inside its pass", 0, "test"));
            runner.Quarantine = quarantine;
            var chain = FxRig.Chain(FxRig.Slot("fx.invert"));
            long frame = 0;

            var held = FxRig.Run(d, lane, runner, Show, chain, picture, ref frame);
            Assert.Equal(input, held);
            Assert.Same(picture, FxRig.Step(d, lane, runner, Show, chain, picture, ref frame, 1_000_000_000));
            var live = FxRig.Live(runner, "fx00000001");
            Assert.Equal((FxSlotState.Quarantined, 0.0), (live.State, live.GpuMs));
            Assert.StartsWith("quarantined on this node", live.Words, StringComparison.Ordinal);

            // The rest of the chain draws: the levels slot after it draws on the input itself, exactly as the same chain does on a
            // node with no quarantine whose first slot is bypassed by hand.
            var levels = FxRig.Slot("fx.levels", ParamSet.Empty.With("outWhite", 0.5), id: "fx00000002");
            var two = FxRig.Chain(FxRig.Slot("fx.invert"), levels);
            var after = FxRig.Run(d, lane, runner, Show, two, picture, ref frame, owner: "op_two");
            Assert.Equal(FxSlotState.Quarantined, FxRig.Live(runner, "fx00000001").State);
            Assert.Equal(FxSlotState.Ok, FxRig.Live(runner, "fx00000002").State);
            using var plain = new FxChainRunner(d, lane);
            var expected = FxRig.Run(d, lane, plain, Show, FxRig.Chain(FxRig.Slot("fx.invert", bypass: true), levels), picture,
                ref frame, owner: "op_two");
            Assert.Equal(FxSlotState.Bypassed, FxRig.Live(plain, "fx00000001").State);
            Assert.Equal(expected.Length, after.Length);
            for (var i = 0; i < after.Length; i++)
                Assert.InRange(after[i], expected[i] - 1, expected[i] + 1);

            quarantine.ReleaseAll();
            var drawn = FxRig.Run(d, lane, runner, Show, chain, picture, ref frame);
            Assert.Equal(FxSlotState.Ok, FxRig.Live(runner, "fx00000001").State);
            for (var i = 0; i < input.Length; i += 4)
                for (var k = 0; k < 3; k++)
                    Assert.InRange(drawn[i + k], 255 - input[i + k] - 1, 255 - input[i + k] + 1);
        });
    }

    [Fact]
    public void ASlotThatDrewBeforeItsKindWasQuarantinedReadsQuarantinedAfterEveryFramePastTheIdleSweep()
    {
        backends.Run(leg =>
        {
            var d = leg.Device;
            using var lane = new FxLane(d);
            using var runner = new FxChainRunner(d, lane);
            using var picture = FxRig.Picture(d, W, H, Ramp);
            var quarantine = new ShaderQuarantine();
            runner.Quarantine = quarantine;
            var chain = FxRig.Chain(FxRig.Slot("fx.invert"));
            long frame = 0;
            FxRig.Run(d, lane, runner, Show, chain, picture, ref frame);
            Assert.Equal(FxSlotState.Ok, FxRig.Live(runner, "fx00000001").State);

            quarantine.Add(new QuarantineEntry(FxCatalog.Builtin.Source("fx.invert")!.Hash, "Invert", QuarantineReason.DeviceRemoved,
                "the GPU stopped inside its pass", 0, "test"));
            for (var i = 0; i < FxChainRunner.IdleFrames + 60; i++)
            {
                Assert.Same(picture, FxRig.Step(d, lane, runner, Show, chain, picture, ref frame, 1_000_000_000));
                Assert.True(runner.Stats.TryGet("fx00000001", out var live), $"after frame {frame} the slot has no observed state");
                Assert.Equal(FxSlotState.Quarantined, live.State);
            }

            quarantine.ReleaseAll();
            FxRig.Run(d, lane, runner, Show, chain, picture, ref frame);
            Assert.Equal(FxSlotState.Ok, FxRig.Live(runner, "fx00000001").State);
        });
    }
}
