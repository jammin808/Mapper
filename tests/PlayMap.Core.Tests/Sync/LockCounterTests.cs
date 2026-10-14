using System.Diagnostics;
using PlayMap.Core.Sync;
using Xunit.Abstractions;

namespace PlayMap.Core.Tests.Sync;

/// <summary>Content time under the lock: the 24-bit board counter unwrapped over days, anchors switched on a named count, and the
/// same show time on every node for every vblank.</summary>
public sealed class LockCounterTests(ITestOutputHelper log)
{
    private const uint Mask = 0xFFFFFF;

    /// <summary>120 Hz: the true period in ps (8 333 333 333.3…) and the leader's fitted one, 0.05 ppm long.</summary>
    private const double TruePeriodPs = 1e12 / 120;
    private static readonly long FittedPeriodPs = (long)Math.Round(TruePeriodPs * (1 + 0.05e-6));
    private const long T0 = 1_000_000_000_000;

    private static long Truth(long n) => T0 + (long)Math.Round(n * TruePeriodPs / 1000);

    [Fact]
    public void FourDaysAt120HzUnwrapExactlyAndShowTimeStaysOnTheRefreshes()
    {
        var watch = Stopwatch.StartNew();
        var counter = new LockCounter();
        const long days = 4L * 86_400 * 120;
        const uint start = 0xFFFF00;
        long seq = 0, wraps = 0, worstErrorNs = 0, worstStepNs = 0, switches = 0;
        LockAnchor? last = null;
        long previous = 0;
        for (long n = 0; n <= days; n++)
        {
            var c = (uint)((start + n) & Mask);
            if (n > 0 && c == 0)
                wraps++;
            counter.Read(c);
            if (counter.DueForReanchor(c, 120))
                counter.Offer(LockCounter.Make(c, Truth(n), FittedPeriodPs, ++seq));
            Assert.True(counter.TryLeaderNsAt(c, out var t), "an anchor is always in effect");
            if (last is not null && counter.Active != last)
                switches++;
            last = counter.Active;
            worstErrorNs = Math.Max(worstErrorNs, Math.Abs(t - Truth(n)));
            if (n > 0)
                worstStepNs = Math.Max(worstStepNs, Math.Abs(t - previous - (long)Math.Round(TruePeriodPs / 1000)));
            previous = t;
        }
        log.WriteLine($"{days} vblanks, {wraps} wraps, {seq} anchors, {switches} switches, worst error {worstErrorNs} ns, worst step error {worstStepNs} ns, {watch.ElapsedMilliseconds} ms");
        Assert.Equal((ulong)days, counter.Count64);
        Assert.Equal(3, wraps); // into the first a few refreshes in, then every 38.8 h
        Assert.Equal(0, counter.Faults);
        Assert.False(counter.Wide);
        // A 60 s anchor at 0.05 ppm drifts at most 3 µs before the next one takes over; each refresh steps one period.
        Assert.InRange(worstErrorNs, 0, 3_500);
        Assert.InRange(worstStepNs, 0, 3_500);
        Assert.True(switches >= seq - 2, $"{switches} of {seq} anchors took effect");
    }

    [Fact]
    public void AnAnchorSwitchAtANamedCountGivesTheSameShowTimeOnThreeNodesForEveryVblankAcrossTwoWraps()
    {
        var watch = Stopwatch.StartNew();
        var nodes = new[] { new LockCounter(), new LockCounter(), new LockCounter() };
        var delays = new[] { 0, 37, 100 };
        var inbox = new Queue<(long At, LockAnchor Anchor)>[] { new(), new(), new() };
        const uint start = 0xFFF000;
        const uint named = 0xFFFF88; // made here, it takes effect at count 0: the switch lands on the wrap itself
        var total = 2L * (Mask + 1) + 5_000;
        var leader = new LockCounter();
        long seq = 0, checkedVblanks = 0, switchesAtNamed = 0;
        for (long n = 0; n <= total; n++)
        {
            var c = (uint)((start + n) & Mask);
            leader.Read(c);
            if (leader.DueForReanchor(c, 120) || c == named)
            {
                // The leader's fitted time of this vblank: its own estimate, a little off the truth.
                var anchor = LockCounter.Make(c, Truth(n) + (seq % 3) - 1, FittedPeriodPs, ++seq);
                leader.Offer(anchor);
                // The group's first anchor is every node's before it presents under the lock; later ones arrive late, by up to 100
                // refreshes (each still before its effective count).
                for (var i = 0; i < nodes.Length; i++)
                    inbox[i].Enqueue((seq == 1 ? n : n + delays[i], anchor));
            }
            long first = 0;
            for (var i = 0; i < nodes.Length; i++)
            {
                while (inbox[i].Count > 0 && inbox[i].Peek().At <= n)
                    nodes[i].Offer(inbox[i].Dequeue().Anchor);
                // Node 2 reads its board only every third vblank: content time must not depend on when a node read.
                if (i != 2 || n % 3 == 0)
                    nodes[i].Read(c);
                Assert.True(nodes[i].TryLeaderNsAt(c, out var t));
                if (i == 0)
                    first = t;
                else if (t != first)
                    Assert.Fail($"node {i} shows {t} at count 0x{c:X6} (vblank {n}); node 0 shows {first}");
            }
            if (c == 0 && nodes[0].Active?.EffectiveAt24 == 0)
                switchesAtNamed++;
            checkedVblanks++;
        }
        log.WriteLine($"{checkedVblanks} vblanks on 3 nodes, {seq} anchors, {watch.ElapsedMilliseconds} ms");
        Assert.Equal(3, switchesAtNamed); // the run starts 4096 refreshes before a wrap and spans two more
        Assert.Equal(nodes[0].Count64, nodes[1].Count64);
    }

    [Fact]
    public void AnImpossibleJumpIsAFaultAndAWideValueSwitchesTo32Bits()
    {
        var counter = new LockCounter();
        counter.Read(100);
        counter.Read(101);
        counter.Read(101 + (1u << 23) + 5);
        Assert.Equal(1, counter.Faults);
        Assert.Equal(1UL, counter.Count64);
        counter.Read(counter.Last + 1);
        Assert.Equal(2UL, counter.Count64);

        var wide = new LockCounter();
        wide.Read(0xFFFFFE);
        wide.Read(0x1000005);
        Assert.True(wide.Wide);
        Assert.Equal(7UL, wide.Count64);
    }

    [Fact]
    public void AWideCounterMapsFarCountsExactlyAndItsAnchorsWaitForTheirOwnCount()
    {
        const long period60Ps = 16_666_666_667;
        var counter = new LockCounter();
        counter.Read(0x0FFFFFFE);
        Assert.True(counter.Wide);
        var anchor = LockCounter.Make(0x0FFFFFFE, T0, period60Ps, seq: 1, wide: counter.Wide);
        Assert.Equal(0x0FFFFFFEu + LockCounter.DefaultLead, anchor.EffectiveAt24);
        counter.Offer(anchor);

        // 2³¹ refreshes on (414 days at 60 Hz) the count times the period passes 2⁶⁴ ps: still exact.
        Assert.True(counter.TryLeaderNsAt(0x0FFFFFFEu + (1u << 31), out var far));
        Assert.Equal(T0 + (long)((UInt128)(1UL << 31) * period60Ps / 1000), far);
        Assert.Equal(0x0FFFFFFFu, counter.Next(0x0FFFFFFE));
        Assert.Equal(0u, counter.Next(uint.MaxValue));
        Assert.Equal(0u, new LockCounter().Next(Mask));

        // A newer anchor made on the wide counter waits for its own 32-bit count, not a 24-bit one already passed.
        counter.Read(0x0FFFFFFF);
        var next = LockCounter.Make(0x0FFFFFFF, T0 + 16_666_667, period60Ps, seq: 2, wide: true);
        counter.Offer(next);
        Assert.Equal(next, counter.Pending);
        counter.Read(0x10000000);
        Assert.Equal(1, counter.Active!.Value.Seq);
        Assert.True(counter.TryLeaderNsAt(0x10000000, out var before));
        Assert.Equal(T0 + 33_333_333, before); // two periods on, from the active anchor
        for (var c = 0x10000001u; c <= next.EffectiveAt24; c++)
            counter.Read(c);
        Assert.Equal(2, counter.Active!.Value.Seq);
    }

    [Fact]
    public void AStaleAnchorAnswersNothingAndAnOlderOneIsDropped()
    {
        var counter = new LockCounter();
        Assert.False(counter.TryLeaderNsAt(5, out _));
        var anchor = LockCounter.Make(1000, T0, FittedPeriodPs, seq: 5);
        counter.Offer(anchor);
        Assert.True(counter.TryLeaderNsAt(1000 + (1u << 23), out var far));
        Assert.Equal(T0 + (long)((1UL << 23) * (ulong)FittedPeriodPs / 1000), far);
        Assert.False(counter.TryLeaderNsAt(1000 + (1u << 23) + 1, out _));
        Assert.False(counter.TryLeaderNsAt(999, out _));

        counter.Offer(anchor with { Seq = 4, LeaderNs = 0 });
        Assert.Equal(5, counter.Active!.Value.Seq);
        Assert.Null(counter.Pending);
        Assert.Contains("\"count24\":1000", LockCounter.Json(anchor), StringComparison.Ordinal);
        Assert.Contains("\"effectiveAt24\":1120", LockCounter.Json(anchor), StringComparison.Ordinal);
    }

    [Fact]
    public void BoardsAreNamedByTheirIds()
    {
        Assert.Equal("Quadro G-Sync (P358)", SyncBoards.Name(856));
        Assert.Equal("Quadro Sync (P2060)", SyncBoards.Name(8288));
        Assert.Equal("Quadro Sync II (P2061)", SyncBoards.Name(8289));
        Assert.Equal("sync board 0x2abc", SyncBoards.Name(0x2ABC));
        Assert.True(SyncBoards.Known(8289));
        Assert.False(SyncBoards.Known(0x2ABC));
    }
}
