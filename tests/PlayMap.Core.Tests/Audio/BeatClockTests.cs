using PlayMap.Core.Audio;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Audio;

/// <summary>The beat clock as anchors: a pure function of leader time, changes scheduled ahead, continuous through a tempo change,
/// jumping at a resync's scheduled instant only, slewed (never backwards) through a nudge.</summary>
public sealed class BeatClockTests
{
    private const long Ms = 1_000_000;

    [Fact]
    public void TheClockStartsAtLeaderZeroOnEveryNode()
    {
        var a = TempoTimeline.Start(128);
        var b = TempoTimeline.Start(128);
        var rng = new Pcg32(11);
        for (var i = 0; i < 10_000; i++)
        {
            var t = (long)(rng.NextDouble() * 3600e9);
            Assert.Equal(a.BeatAt(t), b.BeatAt(t));
            Assert.Equal(a.PhasesAt(t), b.PhasesAt(t));
        }
        Assert.Equal(128.0, a.BeatAt(60_000 * Ms), 9);
        Assert.Equal((128L, 1, 32L, 1, 4L), a.CountsAt(60_000 * Ms));
    }

    [Fact]
    public void ATempoChangeIsContinuousAndTwoNodesAgreeAtEveryInstant()
    {
        var now = 10_000 * Ms;
        var at = now + TempoTimeline.LeadNs;
        var a = TempoTimeline.Start(120).WithTempo(at, 140, "set");
        var b = TempoTimeline.Start(120).WithTempo(at, 140, "set");
        Assert.Equal(at, a.Anchors[^1].LeaderNs);
        Assert.Equal(TempoTimeline.LeadNs, a.Anchors[^1].LeaderNs - now);
        // Continuous at the change, 120 before it and 140 after.
        Assert.Equal(a.At(at - 1).BeatAt(at), a.BeatAt(at), 9);
        Assert.Equal(120, a.BpmAt(at - 1));
        Assert.Equal(140, a.BpmAt(at));
        var rng = new Pcg32(5);
        for (var i = 0; i < 10_000; i++)
        {
            var t = (long)(rng.NextDouble() * 60e9);
            Assert.Equal(a.BeatAt(t), b.BeatAt(t));
        }
    }

    [Fact]
    public void AResyncLandsTheBarLineAtThePressSeenOneLeadLater()
    {
        var clock = TempoTimeline.Start(120);
        var press = 10_250 * Ms; // beat 20.5: halfway through bar 5
        var beatAtPress = clock.BeatAt(press);
        var bar = Math.Ceiling(beatAtPress / clock.BeatsPerBar) * clock.BeatsPerBar;
        var effective = press + TempoTimeline.LeadNs;
        var after = clock.WithBeat(effective, bar + (effective - press) * 120 / 60e9, "resync");
        // Before the effective instant nothing moved; from it on, the bar line stands at the press.
        Assert.Equal(clock.BeatAt(effective - 1), after.BeatAt(effective - 1));
        Assert.Equal(bar, after.At(effective).BeatAt(press), 9);
        Assert.Equal(0, after.PhasesAt(press + 2_000 * Ms).Bar, 9);
    }

    [Fact]
    public void ANudgeSlewsWithinTwoPercentAndNeverRunsBackwards()
    {
        var clock = TempoTimeline.Start(120);
        var at = 5_000 * Ms;
        var nudged = clock.WithNudge(at, 100 * Ms, "nudge");
        Assert.True(nudged.Anchors.Skip(1).All(x => Math.Abs(x.Bpm / 120 - 1) <= TempoTimeline.MaxSlew + 1e-12));
        var end = nudged.Anchors[^1].LeaderNs;
        // After the slew the tempo is back and the beat stands 100 ms ahead.
        Assert.Equal(120, nudged.BpmAt(end));
        Assert.Equal(clock.BeatAt(end + 100 * Ms), nudged.BeatAt(end), 6);
        var last = double.MinValue;
        for (var t = at - 10 * Ms; t < end + 100 * Ms; t += Ms)
        {
            var b = nudged.BeatAt(t);
            Assert.True(b > last, $"beat ran backwards at {t}");
            last = b;
        }
        var back = clock.WithNudge(at, -50 * Ms, "nudge");
        Assert.Equal(clock.BeatAt(back.Anchors[^1].LeaderNs - 50 * Ms), back.BeatAt(back.Anchors[^1].LeaderNs), 6);
    }

    [Fact]
    public void BoundariesAndPruningKeepWhatIsRead()
    {
        var clock = TempoTimeline.Start(120);
        // 120 BPM: a beat every 500 ms, a bar every 2 s.
        Assert.Equal(2_000 * Ms, clock.NextBoundary(1_200 * Ms, 4));
        Assert.Equal(1_500 * Ms, clock.NextBoundary(1_200 * Ms, 1));
        Assert.Equal(1_000 * Ms, clock.NextBoundary(1_000 * Ms, 1));
        var changed = clock.WithTempo(1_000 * Ms, 60, "set").WithTempo(3_000 * Ms, 90, "set");
        var pruned = changed.Pruned(2_000 * Ms);
        Assert.Equal(2, pruned.Anchors.Length);
        for (var t = 2_000 * Ms; t < 5_000 * Ms; t += 7 * Ms)
            Assert.Equal(changed.BeatAt(t), pruned.BeatAt(t));
        // A change scheduled before a pending one replaces it.
        var replaced = changed.WithTempo(2_500 * Ms, 100, "set");
        Assert.Equal(100, replaced.BpmAt(4_000 * Ms));
    }
}
