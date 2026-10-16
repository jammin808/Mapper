using PlayMap.Core.Bus;
using PlayMap.Core.Sync;
using PlayMap.Net.Sync;

namespace PlayMap.Net.Tests.Sync;

/// <summary>The leader's board against closed form on synthetic outputs: the phase of each output against the reference (within
/// 1 µs, extrapolated along its own period from an earlier vblank), the next slip (within 0.1 %, and the arithmetic of the design:
/// 50 ppm at 60 Hz slips every 5 min 33 s, 59.94 against 60 every 16.65 s), mixed rates amber, a locked group "same vblank", the
/// members' clock uncertainty carried into every number, and a report older than the last refused.</summary>
public sealed class SyncBoardTests
{
    private const long Second = 1_000_000_000;
    private const long T = 1_000_000 * Second;          // the common leader instant
    private const long P60Ps = 16_666_666_667;           // 60 Hz
    private const uint Leader = 0x0badc0de;
    private const uint Member = 0x9f8e7d6c;

    private static VblankReport Report(uint node, string output, long periodPs, double vblankNs, uint seq = 1, LockState state = LockState.FreeMeasured,
        uint uncertaintyUs = 0) =>
        new(state, VblankReport.FlagSettled, VblankReport.PathGl, node, VblankReport.HashId(output), seq, 0, periodPs, (long)Math.Round(vblankNs), 600, 0, 0,
            16_000, uncertaintyUs);

    private static SyncBoard Board(params (VblankReport Report, bool Local)[] reports)
    {
        var board = new SyncBoard();
        board.Name("0badc0de", "op_left");
        board.Name("9f8e7d6c", "op_right");
        foreach (var (r, local) in reports)
            Assert.True(board.Offer(r, 0, local));
        return board;
    }

    [Theory]
    [InlineData(1234.567, 50)]
    [InlineData(-4321.5, 50)]
    [InlineData(8000.25, -35)]
    [InlineData(-8300, 12.5)]
    public void PhaseAndNextSlipMatchClosedForm(double phaseUs, double ppm)
    {
        var periodPs = (long)Math.Round(P60Ps * (1 + ppm * 1e-6));
        var pr = P60Ps / 1000.0;
        var po = periodPs / 1000.0;
        // The member's last report came 600 refreshes before the instant: the board extrapolates along its own period.
        var vo = T + phaseUs * 1000 - 600 * po;
        var board = Board((Report(Leader, "op_left", P60Ps, T - 300 * pr), true), (Report(Member, "op_right", periodPs, vo, uncertaintyUs: 210), false));

        var p = board.Picture(T, 0);
        var right = p.Outputs.Single(o => o.Output == "op_right");
        Assert.Equal(phaseUs, right.PhaseUs, 0.001);   // within 1 ns, far inside the 1 µs the design asks
        Assert.Equal(210, right.UncertaintyUs, 1e-9);
        var delta = (po - pr) / pr;
        var expected = delta > 0 ? (pr / 2 - phaseUs * 1000) / delta / 1e9 : (phaseUs * 1000 + pr / 2) / -delta / 1e9;
        Assert.NotNull(right.NextSlipS);
        Assert.Equal(expected, right.NextSlipS!.Value, expected * 0.001);
        Assert.Equal(ppm, right.Ppm, 0.01);
        var pair = Assert.Single(p.Pairs);
        Assert.Equal(("op_left", "op_right"), (pair.A, pair.B));
        Assert.Equal(phaseUs, pair.PhaseUs, 0.001);
        Assert.False(pair.MixedRate);
        Assert.Equal("free-running", p.Verdict);
        Assert.Equal(2, p.Nodes);
        Assert.Equal(16, p.GroupLatencyMs, 1e-9);
        var left = p.Outputs.Single(o => o.Output == "op_left");
        Assert.Equal(0, left.PhaseUs);
        Assert.True(left.Local);
    }

    [Fact]
    public void FiftyPpmAtSixtyHertzSlipsEveryFiveMinutesThirtyThreeSeconds()
    {
        var periodPs = (long)Math.Round(P60Ps * 1.00005);
        var pr = P60Ps / 1000.0;
        // Just past a slip: the phase at −P/2, so the next one is a whole slip period away.
        var board = Board((Report(Leader, "op_left", P60Ps, T), true), (Report(Member, "op_right", periodPs, T - pr / 2 + 1), false));
        var p = board.Picture(T, 0, playing: true);
        Assert.Equal(pr / 50e-6 / 1e9, p.SoonestSlipS!.Value, 0.001 * 333.3);
        Assert.Equal("5 min 33 s", SyncBoard.Duration(p.SoonestSlipS.Value));
        Assert.Contains("next slip in 5 min 33 s", p.Words, StringComparison.Ordinal);
        Assert.Equal(Light.Amber, p.Light);   // under ten minutes while the show plays
        Assert.Equal(Light.Green, board.Picture(T, 0, playing: false).Light);
    }

    [Fact]
    public void FiftyNineNinetyFourAgainstSixtyIsAMixedRateAmberAndSlipsEverySixteenSeconds()
    {
        var p5994 = (long)Math.Round(1e12 / 59.94);   // 1001 ppm longer than 60 Hz
        var pr = P60Ps / 1000.0;
        var board = Board((Report(Leader, "op_left", P60Ps, T), true), (Report(Member, "op_right", p5994, T - pr / 2 + 1), false));
        var p = board.Picture(T, 0);
        Assert.True(p.MixedRate);
        Assert.Equal("mixed rate", p.Verdict);
        Assert.Equal(Light.Amber, p.Light);
        Assert.True(Assert.Single(p.Pairs).MixedRate);
        Assert.Equal(16.65, p.SoonestSlipS!.Value, 0.0166);
        Assert.Equal(59.94, p.Outputs.Single(o => o.Output == "op_right").RefreshHz, 0.001);
        Assert.Equal(1.0, p.Outputs.Single(o => o.Output == "op_right").Ppm, 0.01);   // against its own standard rate, 60000/1001: 59.94 is 1 ppm slow of it
    }

    [Fact]
    public void ALockedGroupIsOnOneVblankAndNeverSlips()
    {
        var board = Board((Report(Leader, "op_left", P60Ps, T, state: LockState.Framelocked), true),
            (Report(Member, "op_right", P60Ps + 3, T + 12_000, state: LockState.Framelocked), false));
        var p = board.Picture(T, 0);
        Assert.Equal("same vblank", p.Verdict);
        Assert.Equal(Light.Green, p.Light);
        Assert.Null(Assert.Single(p.Pairs).NextSlipS);
        Assert.Equal(12, p.Pairs[0].PhaseUs, 1e-6);
        Assert.Equal("2 nodes, 2 outputs on one vblank", p.Words);
    }

    /// <summary>A locked report with a board and its count at its last vblank.</summary>
    private static VblankReport Boarded(uint node, string output, double vblankNs, uint count) =>
        Report(node, output, P60Ps, vblankNs, state: LockState.Framelocked) with
        {
            Flags = VblankReport.FlagSettled | VblankReport.FlagBoard | VblankReport.FlagBoardSynced,
            BoardFrameCount = count,
        };

    [Fact]
    public void LockedOutputsOnTwoChainsAreNotOneVblankWhateverTheirDriversSay()
    {
        // 6 ms apart: two boards each locked to something, not to each other.
        var apart = Board((Report(Leader, "op_left", P60Ps, T, state: LockState.Framelocked), true),
            (Report(Member, "op_right", P60Ps, T + 6_000_000, state: LockState.Framelocked), false)).Picture(T, 0);
        Assert.Equal(("free-running", Light.Amber), (apart.Verdict, apart.Light));
        Assert.Contains("op_right on 9f8e7d6c reports locked but is not on op_left's vblank (6000 µs apart)", apart.Words, StringComparison.Ordinal);

        // Within a timestamp's jitter, but the boards count different vblanks (the member's last vblank is 30 periods later, so its
        // count must be 30 more): not one chain either.
        var later = T + 30 * P60Ps / 1000.0 + 20_000;
        var miscounted = Board((Boarded(Leader, "op_left", T, 0xFFFFF0), true), (Boarded(Member, "op_right", later, 0xFFFFF0 + 31 - 0x1000000), false)).Picture(T, 0);
        Assert.Equal("free-running", miscounted.Verdict);
        Assert.Contains("its frame count disagrees", miscounted.Words, StringComparison.Ordinal);

        // The same with counts that agree across the 24-bit wrap: one vblank.
        var counted = Board((Boarded(Leader, "op_left", T, 0xFFFFF0), true), (Boarded(Member, "op_right", later, 0xFFFFF0 + 30 - 0x1000000), false)).Picture(T, 0);
        Assert.Equal(("same vblank", Light.Green), (counted.Verdict, counted.Light));
        Assert.Null(Assert.Single(counted.Pairs).NextSlipS);
    }

    [Theory]
    [InlineData(60000.0 / 1001)]
    [InlineData(24000.0 / 1001)]
    [InlineData(120000.0 / 1001)]
    public void ADisplayExactlyOnARateOfTheThousandAndOneFamilyReadsNoPpm(double hz)
    {
        var p = Board((Report(Leader, "op_left", (long)Math.Round(1e12 / hz), T), true)).Picture(T, 0);
        Assert.Equal(0, Assert.Single(p.Outputs).Ppm, 0.005);
    }

    [Fact]
    public void TheRowsAndTheReferenceFollowTheNamedOrdersNotTheReportsArrival()
    {
        const uint Other = 0x1a2b3c4d;
        var p50 = (long)Math.Round(1e12 / 50);
        var reports = new (VblankReport Report, bool Local)[]
        {
            (Report(Member, "op_b", P60Ps, T), false),
            (Report(Other, "op_x", P60Ps, T), false),
            (Report(Leader, "op_2", p50, T), true),
            (Report(Member, "op_a", P60Ps, T), false),
            (Report(Leader, "op_1", P60Ps, T), true),
        };
        SyncPicture Picture(IEnumerable<(VblankReport Report, bool Local)> arrival)
        {
            var board = new SyncBoard();
            board.Name("0badc0de", "op_1", "op_2");
            board.Name("9f8e7d6c", "op_a", "op_b");
            foreach (var (r, local) in arrival)
                Assert.True(board.Offer(r, 0, local));
            return board.Picture(T, 0);
        }

        // The leader's own outputs in its order, then each member's by node id in the order it named them (one it did not name, by
        // hash): the same whichever way round the reports came.
        var x = "#" + VblankReport.HashId("op_x").ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        string[] rows = ["0badc0de/op_1", "0badc0de/op_2", "1a2b3c4d/" + x, "9f8e7d6c/op_a", "9f8e7d6c/op_b"];
        foreach (var arrival in new[] { reports, Enumerable.Reverse(reports).ToArray() })
        {
            var p = Picture(arrival);
            Assert.Equal(rows, p.Outputs.Select(o => o.Node + "/" + o.Output));
            Assert.Equal("mixed rate", p.Verdict);
            Assert.All(p.Pairs, pair => Assert.Equal(("op_1", "0badc0de"), (pair.A, pair.ANode)));
            var odd = p.Pairs.First(pair => pair.MixedRate);
            Assert.Equal(("op_2", "0badc0de"), (odd.B, odd.BNode));
        }

        // Named the other way round, the leader's first is op_2: its rate is the reference's.
        var board = new SyncBoard();
        board.Name("0badc0de", "op_2", "op_1");
        foreach (var (r, local) in reports)
            Assert.True(board.Offer(r, 0, local));
        var other = board.Picture(T, 0);
        Assert.All(other.Pairs, pair => Assert.Equal("op_2", pair.A));
        Assert.All(other.Pairs, pair => Assert.True(pair.MixedRate));

        // With none of the leader's heard, the first member's by the same order is the reference.
        var members = new SyncBoard();
        members.Name("9f8e7d6c", "op_a", "op_b");
        foreach (var (r, local) in reports.Where(r => !r.Local).Reverse())
            Assert.True(members.Offer(r, 0, local));
        var heard = members.Picture(T, 0);
        Assert.Equal(["1a2b3c4d/" + x, "9f8e7d6c/op_a", "9f8e7d6c/op_b"], heard.Outputs.Select(o => o.Node + "/" + o.Output));
        Assert.All(heard.Pairs, pair => Assert.Equal((x, "1a2b3c4d"), (pair.A, pair.ANode)));
    }

    [Fact]
    public void AnOlderReportIsRefusedAnUnheardOneGreysAndARestartedMemberCountsAgain()
    {
        var board = new SyncBoard();
        Assert.True(board.Offer(Report(Member, "op_right", P60Ps, T, seq: 5), 0));
        Assert.False(board.Offer(Report(Member, "op_right", P60Ps, T, seq: 5), 1));
        Assert.False(board.Offer(Report(Member, "op_right", P60Ps, T, seq: 4), 2));
        Assert.Equal(2, board.Stale);
        Assert.Equal("#" + VblankReport.HashId("op_right").ToString("x8", System.Globalization.CultureInfo.InvariantCulture),
            board.Picture(T, 0).Outputs[0].Output);
        Assert.False(board.Offer(Report(Member, "op_right", P60Ps, T, state: (LockState)42), 3));

        // Six seconds of silence: not heard, the verdict unknown; the member restarted counts from one again.
        var silent = board.Picture(T, 6 * Second);
        Assert.False(silent.Outputs[0].Heard);
        Assert.Equal("unknown", silent.Verdict);
        Assert.Equal(Light.Grey, silent.Light);
        Assert.True(board.Offer(Report(Member, "op_right", P60Ps, T, seq: 1), 6 * Second));
        Assert.True(board.Picture(T, 6 * Second).Outputs[0].Heard);
        Assert.Equal(SyncPicture.Empty, new SyncBoard().Picture(T, 0));
    }
}
