using PlayMap.Core.Led;
using PlayMap.Core.Model;
using PlayMap.Tests;

namespace PlayMap.Core.Tests.Led;

/// <summary>The patch pass, as its CPU reference computes it (the GL goldens hold the GPU's bytes against this): colour orders, white
/// extraction, 16-bit coarse and fine, gains, level and air, blackout with dimmer and constants, the locate blink, and the test
/// patterns, which never change a pixel faster than three flashes a second.</summary>
public sealed class LedReferenceTests
{
    private const string Node = "ln00000001";

    /// <summary>A linear value whose output (gamma 2.2) is the 8-bit code <paramref name="code"/>.</summary>
    private static float Lin(int code) => MathF.Pow(code / 255f, 2.2f);

    private static (LedPlan Plan, float[] Air) Plan(LedFixtureType? custom, string type, int pixels, Func<LedFixture, LedFixture>? edit = null)
    {
        var f = LedPatchTests.Line("lf00000001", type, pixels, new LedPatch(Node, 0, 1));
        f = edit?.Invoke(f) ?? f;
        var p = LedPatchTests.Show([LedPatchTests.Node(Node)], [f]);
        if (custom is not null)
            p = p with { Leds = p.Leds with { Types = [custom] } };
        var plan = LedPlanBuilder.Build(p, 200, 1);
        var air = new float[plan.Fixtures.Length * 4];
        LedAir.Fill(plan, LedLive.Initial, 0, false, _ => 1f, 1f, air);
        return (plan, air);
    }

    private static float[] Colors(params (float R, float G, float B)[] px)
    {
        var c = new float[px.Length * 4];
        for (var i = 0; i < px.Length; i++)
            (c[i * 4], c[i * 4 + 1], c[i * 4 + 2], c[i * 4 + 3]) = (px[i].R, px[i].G, px[i].B, 1);
        return c;
    }

    [Fact]
    public void ColourOrdersFollowTheType()
    {
        var colors = Colors((Lin(10), Lin(20), Lin(30)));
        Assert.Equal([10, 20, 30], Run("builtin:rgb", colors)[..3]);
        Assert.Equal([20, 10, 30], Run("builtin:grb", colors)[..3]);
        Assert.Equal([30, 10, 20], Run("builtin:brg", colors)[..3]);
        // L is the Rec. 709 luminance of the linear colour: a grey's is the grey.
        Assert.Equal(100, Run("builtin:l", Colors((Lin(100), Lin(100), Lin(100))))[0]);
    }

    private static byte[] Run(string type, float[] colors, float level = 1, LedTestKind test = LedTestKind.None, int step = 0)
    {
        var (plan, air) = Plan(null, type, colors.Length / 4);
        LedAir.Fill(plan, LedLive.Initial with { Test = test }, 0, false, _ => 1f, 1f, air);
        return LedReference.Patch(plan, colors, air, level, test, step);
    }

    [Fact]
    public void WhiteIsTakenOutOfTheColourForRgbwFixtures()
    {
        // Linear (r, g, b) = (a + w, w, w): white takes w out of each, so R carries a, G and B nothing, W carries w.
        var w = Lin(128);
        var bytes = Run("builtin:rgbw", Colors((Lin(200) + w, w, w), (1, 1, 1), (0, 0, 0), (Lin(50) + Lin(90), Lin(90), Lin(90) + Lin(120))));
        Assert.Equal([200, 0, 0, 128], bytes[..4]);
        Assert.Equal([0, 0, 0, 255], bytes[4..8]);
        Assert.Equal([0, 0, 0, 0], bytes[8..12]);
        Assert.Equal([50, 0, 120, 90], bytes[12..16]);
        Assert.Equal([0, 0, 255, 0], Run("builtin:grbw", Colors((0, 0, 1)))[..4]);
    }

    [Fact]
    public void SixteenBitChannelsCarryCoarseThenFine()
    {
        // 0.25 encoded is 16384 = 0x4000: high byte 64, low byte 0; 1.0 is 0xFFFF.
        var bytes = Run("builtin:rgb16", Colors((MathF.Pow(0.25f, 2.2f), 1, 0)));
        Assert.Equal([64, 0, 255, 255, 0, 0], bytes[..6]);
    }

    [Fact]
    public void GainsBrightnessLevelAndAirScale()
    {
        var colors = Colors((Lin(200), Lin(200), Lin(200)));
        var (plan, air) = Plan(null, "builtin:rgb", 1, f => f with { Color = f.Color with { GainR = 0, GainG = 1, GainB = 1 } });
        Assert.Equal([0, 200, 200], LedReference.Patch(plan, colors, air, 1, LedTestKind.None, 0)[..3]);
        Assert.Equal([0, 0, 0], LedReference.Patch(plan, colors, air, 0, LedTestKind.None, 0)[..3]);
        // The air is a code multiplier, as an output's: half the air halves the code.
        air[0] = 0.5f;
        Assert.Equal([0, 100, 100], LedReference.Patch(plan, colors, air, 1, LedTestKind.None, 0)[..3]);
        // Linear PWM: output gamma 1 sends linear light.
        (plan, air) = Plan(null, "builtin:rgb", 1, f => f with { Color = f.Color with { OutputGamma = 1 } });
        Assert.Equal(Math.Floor(Lin(200) * 255 + 0.5), LedReference.Patch(plan, colors, air, 1, LedTestKind.None, 0)[0]);
    }

    [Fact]
    public void BlackoutZeroesColourAndDimmerAndKeepsConstants()
    {
        var mode = new LedFixtureType("lt00000001", "Dimmer, mode, RGB", [new LedChannelSlot(LedChannel.Dimmer, 8, 255), new LedChannelSlot(LedChannel.Constant, 8, 42)],
            [new LedChannelSlot(LedChannel.R, 8, 0), new LedChannelSlot(LedChannel.G, 8, 0), new LedChannelSlot(LedChannel.B, 8, 0)], [new LedChannelSlot(LedChannel.Constant, 16, 0x1234)], 0, "", "");
        var (plan, air) = Plan(mode, mode.Id, 2);
        var colors = Colors((1, 1, 1), (Lin(5), Lin(6), Lin(7)));
        Assert.Equal([255, 42, 255, 255, 255, 5, 6, 7, 0x12, 0x34], LedReference.Patch(plan, colors, air, 1, LedTestKind.None, 0)[..10]);
        LedAir.Fill(plan, LedLive.Initial, 0, blackout: true, _ => 1f, 1f, air);
        Assert.Equal([0, 42, 0, 0, 0, 0, 0, 0, 0x12, 0x34], LedReference.Patch(plan, colors, air, 1, LedTestKind.Full, 0)[..10]);
        Assert.Equal([0, 42, 0, 0, 0, 0, 0, 0, 0x12, 0x34], LedReference.Black(plan)[..10]);
        // A node that does not follow blackout keeps its picture.
        var p = LedPatchTests.Show([LedPatchTests.Node(Node) with { FollowBlackout = false }], [LedPatchTests.Line("lf00000001", mode.Id, 2, new LedPatch(Node, 0, 1))]);
        var unfollowed = LedPlanBuilder.Build(p with { Leds = p.Leds with { Types = [mode] } }, 200, 1);
        LedAir.Fill(unfollowed, LedLive.Initial, 0, blackout: true, _ => 1f, 1f, air);
        Assert.Equal([255, 42, 255, 255, 255], LedReference.Patch(unfollowed, colors, air, 1, LedTestKind.None, 0)[..5]);
    }

    [Fact]
    public void TestPatternsAreCodeValuesAndCoverOnlyTheirScope()
    {
        var colors = Colors((0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0));
        Assert.Equal([255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0], Run("builtin:rgb", colors, test: LedTestKind.Order)[..12]);
        Assert.Equal([0, 0, 0, 85, 85, 85, 170, 170, 170, 255, 255, 255], Run("builtin:rgb", colors, test: LedTestKind.Gradient)[..12]);
        // FULL lights every colour channel of an RGBW fixture; WHITE goes through the white LED.
        Assert.Equal([255, 255, 255, 255], Run("builtin:rgbw", colors, test: LedTestKind.Full)[..4]);
        Assert.Equal([0, 0, 0, 255], Run("builtin:rgbw", colors, test: LedTestKind.White)[..4]);
        // A test on other fixtures leaves this one on the show.
        var (plan, air) = Plan(null, "builtin:rgb", 4);
        LedAir.Fill(plan, LedLive.Initial with { Test = LedTestKind.White, TestScope = ["lf0000000f"] }, 0, false, _ => 1f, 1f, air);
        Assert.Equal(new byte[12], LedReference.Patch(plan, colors, air, 1, LedTestKind.White, 0)[..12]);
    }

    [Fact]
    public void TheLocateBlinkAlternatesWhiteAndTheEditorColourTwiceASecond()
    {
        var (plan, air) = Plan(null, "builtin:rgb", 1);
        var live = LedLive.Initial with { Locate = "lf00000001", LocateUntilNs = 10_000_000_000 };
        LedAir.Fill(plan, live, 100_000_000, false, _ => 1f, 1f, air);
        Assert.Equal([255, 255, 255], LedReference.Patch(plan, Colors((0, 0, 0)), air, 1, LedTestKind.None, 0)[..3]);
        LedAir.Fill(plan, live, 300_000_000, false, _ => 1f, 1f, air);
        Assert.Equal([0xFF, 0x88, 0x00], LedReference.Patch(plan, Colors((0, 0, 0)), air, 1, LedTestKind.None, 0)[..3]);
        LedAir.Fill(plan, live, 10_000_000_000, false, _ => 1f, 1f, air);
        Assert.Equal(0, air[3]);
    }

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
    public void NoPatternFlashesMoreThanThreeTimesASecond(LedTestKind kind)
    {
        // Every pixel of fixtures of 1, 3, 10 and 50 pixels, at 60 frames a second for 5 s from an arbitrary instant: a flash is a
        // pair of opposite changes, so at most six changes in any second.
        foreach (var pixels in new[] { 1, 3, 10, 50 })
        {
            for (var fixture = 0; fixture < 4; fixture++)
            {
                for (var pixel = 0; pixel < pixels; pixel++)
                {
                    var changes = new List<int>();
                    var last = -1f;
                    for (var k = 0; k < 300; k++)
                    {
                        var leader = 1_760_000_000_123_456_789L + k * 1_000_000_000L / 60;
                        var (r, g, b) = LedTestPatterns.Code(kind, pixel, pixels, fixture, 4, 0, LedTestPatterns.StepAt(leader));
                        var lum = r + g + b;
                        if (last >= 0 && lum != last)
                            changes.Add(k);
                        last = lum;
                    }
                    for (var i = 0; i + 6 < changes.Count; i++)
                        Assert.True(changes[i + 6] - changes[i] >= 60, $"{kind}: pixel {pixel} of {pixels} changes 7 times within a second");
                }
            }
        }
    }

    [Fact]
    public void TheFramePoolDropsTheOldestWhenTheSenderFallsBehind()
    {
        var pool = new LedFramePool();
        for (var i = 0; i < 4; i++)
        {
            var f = pool.Rent(2)!;
            Assert.Equal(1024, f.Slots.Length);
            f.LeaderNs = i;
            pool.Submit(f);
        }
        // Every frame is queued: the next rent drops the oldest (leader 0) and reuses its buffer.
        var reused = pool.Rent(2)!;
        Assert.Equal(1, pool.Dropped);
        reused.LeaderNs = 9;
        pool.Submit(reused);
        var taken = new List<long>();
        while (pool.Take() is { } t)
        {
            taken.Add(t.LeaderNs);
            pool.Return(t);
        }
        Assert.Equal([1L, 2, 3, 9], taken);
        Assert.Equal(0, pool.Skipped);
    }

    [Fact]
    public void AFramePoolSizedByOneFrameAllocatesNothingForABurst()
    {
        var pool = new LedFramePool();
        var burst = new LedFrame?[LedFramePool.Size];
        // A steady frame: one read rents one frame, the sender takes and returns it.
        pool.Submit(pool.Rent(3)!);
        pool.Return(pool.Take()!);
        // A burst: a GPU a frame or two behind hands several reads over in one collect, each renting a frame no steady frame took.
        var before = Allocations.Start();
        for (var i = 0; i < burst.Length; i++)
            burst[i] = pool.Rent(3);
        foreach (var f in burst)
            pool.Submit(f!);
        while (pool.Take() is { } t)
            pool.Return(t);
        Assert.Equal(0, Allocations.Since(before));
        Assert.All(burst, f => Assert.Equal(3 * LedPlan.SlotsPerRow, f!.Slots.Length));
        Assert.Equal(0, pool.Dropped);
    }

    [Fact]
    public void AFrameTheSenderHeldWhileThePlanGrewIsGrownAsItComesBack()
    {
        var pool = new LedFramePool();
        pool.Submit(pool.Rent(2)!);
        var held = pool.Take()!;
        held.Slots[5] = 7;
        // The plan grows while the sender holds a frame: that rent is the plan's cold frame, and grows the frames the pool holds.
        var queued = pool.Rent(5)!;
        queued.Slots[9] = 3;
        pool.Submit(queued);
        var free = pool.Rent(5)!;
        Assert.Equal(5 * LedPlan.SlotsPerRow, free.Slots.Length);
        pool.Return(free);
        // A smaller plan keeps the high water.
        var small = pool.Rent(1)!;
        Assert.Equal(1, small.Rows);
        Assert.Equal(5 * LedPlan.SlotsPerRow, small.Slots.Length);
        pool.Return(small);
        // The held frame comes back grown, its slots kept; the queued one kept its slots.
        pool.Return(held);
        Assert.Equal(5 * LedPlan.SlotsPerRow, held.Slots.Length);
        Assert.Equal(7, held.Slots[5]);
        var first = pool.Take();
        Assert.Same(queued, first);
        Assert.Equal(3, queued.Slots[9]);
        pool.Return(first!);
        // Every frame is now at the high water: a burst of the grown plan rents and allocates nothing.
        var burst = new LedFrame?[LedFramePool.Size];
        var before = Allocations.Start();
        for (var i = 0; i < burst.Length; i++)
            burst[i] = pool.Rent(5);
        Assert.Equal(0, Allocations.Since(before));
        Assert.All(burst, f => Assert.True(f!.Slots.Length >= 5 * LedPlan.SlotsPerRow));
    }
}
