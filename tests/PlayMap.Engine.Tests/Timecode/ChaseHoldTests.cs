using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Core.Time;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.Tests.Control;

namespace PlayMap.Engine.Tests.Tc;

/// <summary>A chase with GAP HOLD while rolling code runs between two items: the last frame of the item before is held on one
/// anchor, from the instant the code left that item — the same on every node, however late a node begins holding. The lane does
/// not move while the code rolls through the gap (no new revision, no new sequence for the decoder), the item on the air keeps the
/// instant it started (its effects' time runs on from where it played), a jump inside the gap moves that instant with the code, and
/// the next item still takes over on its exact frame.</summary>
public sealed class ChaseHoldTests
{
    private const long S = 1_000_000_000;

    /// <summary>Song 1 runs 01:00:00:00–01:00:10:00, Song 2 starts at 01:00:12:00: two seconds of gap between them.</summary>
    private static Project Gapped() => Project.CreateDefault("Show") with
    {
        Playlists =
        [
            new Playlist("pl_show", "Show", [PlaylistItem.For("it_a", SourceRef.Solid("#FF0000"), "Song 1") with { DurationNs = 10 * S, TcIn = "01:00:00:00" },
                PlaylistItem.For("it_b", SourceRef.Solid("#00FF00"), "Song 2") with { DurationNs = 8 * S, TcIn = "01:00:12:00" }], false, null),
        ],
    };

    /// <summary>An internal source rolling from 01:00:09:00 at the node's 5 s, chased with GAP HOLD, stepped half a second into
    /// the gap (01:00:10:12, the node's 6.5 s).</summary>
    private static async Task<ControlRig> HeldInTheGap(ManualClock clock)
    {
        var rig = await ControlRig.StartAsync(Gapped(), clock: clock);
        await rig.Ok("TIMECODE SOURCE ADD Rehearsal INTERNAL FORMAT 25");
        await rig.Ok("TIMECODE INTERNAL Rehearsal LOCATE 01:00:09:00");
        await rig.Ok("TIMECODE CHASE Show Rehearsal");
        await rig.Ok("TIMECODE SET Show GAP HOLD");
        await rig.Ok("TIMECODE INTERNAL Rehearsal PLAY");
        clock.AdvanceMs(1500);
        await rig.Engine.StepChasesAsync();
        return rig;
    }

    [Fact]
    public async Task AHeldChaseKeepsItsLaneWhileTheCodeRollsThroughTheGap()
    {
        var clock = new ManualClock(5 * S);
        await using var rig = await HeldInTheGap(clock);
        var held = rig.Engine.Playback.Lane("pl_show")!;
        Assert.Equal(("it_a", TransportState.Paused, "it_b"), (held.ItemId, held.Clip.State, held.NextItemId));
        Assert.Equal(10 * S - 1, held.Clip.Anchor.MediaAt(rig.Engine.LeaderNowNs));
        // 01:00:12:00 shows three seconds after 01:00:09:00 rolled at 5 s.
        Assert.Equal(8 * S, held.SwitchAtLeaderNs);
        var revision = rig.Engine.Playback.Snapshot.Revision;

        // A second of lane ticks inside the gap: the hold stands exactly as it was made.
        for (var i = 0; i < 100; i++)
        {
            clock.AdvanceMs(10);
            await rig.Engine.StepChasesAsync();
            var lane = rig.Engine.Playback.Lane("pl_show")!;
            Assert.Equal((held.Clip.Seq, held.Clip.Anchor, held.NextClip.Seq, held.SwitchAtLeaderNs),
                (lane.Clip.Seq, lane.Clip.Anchor, lane.NextClip.Seq, lane.SwitchAtLeaderNs));
        }
        Assert.Equal(revision, rig.Engine.Playback.Snapshot.Revision);

        // Past 01:00:12:00 Song 2 has taken over on its exact instant: half a second in at 01:00:12:12.
        clock.AdvanceMs(1000);
        await rig.Engine.StepChasesAsync();
        var next = rig.Engine.Playback.Lane("pl_show")!;
        Assert.Equal(("it_b", TransportState.Playing), (next.ItemId, next.Clip.State));
        Assert.Equal(S / 2, next.Clip.Anchor.MediaAt(rig.Engine.LeaderNowNs));
        Assert.Equal(8 * S, next.Clip.Anchor.LeaderAt(next.Clip.InNs));
    }

    [Fact]
    public async Task AHeldItemKeepsTheInstantItStartedWhileTheCodeRollsThroughTheGap()
    {
        var clock = new ManualClock(5 * S);
        await using var rig = await HeldInTheGap(clock);
        using var sources = new EngineSources(null);
        var playlist = SourceRef.Playlist("pl_show");
        sources.BeginFrame(rig.Engine.Snapshot, 1);
        var first = sources.Item(playlist, rig.Engine.LeaderNowNs);
        Assert.Equal("it_a", first?.Item.Id);
        // Held on its last frame from 6 s, the instant its code ended (01:00:10:00, a second after 01:00:09:00 rolled at 5 s): it
        // counts as started its length before — where it started while it played, to the last frame's nanosecond.
        Assert.Equal(6 * S - (10 * S - 1), first?.StartNs);

        for (var i = 0; i < 100; i++)
        {
            clock.AdvanceMs(10);
            await rig.Engine.StepChasesAsync();
            sources.BeginFrame(rig.Engine.Snapshot, i + 2);
            var onAir = sources.Item(playlist, rig.Engine.LeaderNowNs);
            Assert.Equal(("it_a", first?.StartNs), (onAir?.Item.Id, onAir?.StartNs));
        }
    }

    /// <summary>Rehearsal at 01:00:09:00, rolling from the clock's instant.</summary>
    private static async Task Roll(ControlRig rig)
    {
        await rig.Ok("TIMECODE SOURCE ADD Rehearsal INTERNAL FORMAT 25");
        await rig.Ok("TIMECODE INTERNAL Rehearsal LOCATE 01:00:09:00");
        await rig.Ok("TIMECODE INTERNAL Rehearsal PLAY");
    }

    /// <summary>The show starts chasing Rehearsal with GAP HOLD now.</summary>
    private static async Task Chase(ControlRig rig)
    {
        await rig.Ok("TIMECODE CHASE Show Rehearsal");
        await rig.Ok("TIMECODE SET Show GAP HOLD");
        await rig.Engine.StepChasesAsync();
    }

    /// <summary>The instant the show's item on the air counts as started, as a frame reads it.</summary>
    private static long? StartedOnAir(ControlRig rig, EngineSources sources, long frame)
    {
        sources.BeginFrame(rig.Engine.Snapshot, frame);
        return sources.Item(SourceRef.Playlist("pl_show"), rig.Engine.LeaderNowNs)?.StartNs;
    }

    [Fact]
    public async Task ANodeThatBeginsHoldingLateHoldsFromTheInstantTheCodeLeftTheItem()
    {
        // One node chases from before Song 1's end and first ticks in the gap 7 ms after its code ended (6 s); the other begins
        // chasing a second into the gap (a node restarted, a chase engaged late).
        var throughClock = new ManualClock(5 * S);
        await using var through = await ControlRig.StartAsync(Gapped(), clock: throughClock);
        using var throughSources = new EngineSources(null);
        await Roll(through);
        throughClock.AdvanceMs(997);
        await Chase(through);
        // Song 1 plays from 01:00:00:00, nine seconds before 01:00:09:00 rolled at 5 s.
        Assert.Equal(-4 * S, StartedOnAir(through, throughSources, 1));
        throughClock.AdvanceMs(10);
        await through.Engine.StepChasesAsync();
        throughClock.AdvanceMs(993);
        await through.Engine.StepChasesAsync();

        var lateClock = new ManualClock(5 * S);
        await using var late = await ControlRig.StartAsync(Gapped(), clock: lateClock);
        using var lateSources = new EngineSources(null);
        await Roll(late);
        lateClock.AdvanceMs(2000);
        await Chase(late);

        // Both hold Song 1's last frame from 6 s, so both count it as started where it did while it played.
        foreach (var rig in new[] { through, late })
        {
            var lane = rig.Engine.Playback.Lane("pl_show")!;
            Assert.Equal((7 * S, "it_a", TransportState.Paused, 10 * S - 1, 6 * S, "it_b", 8 * S),
                (rig.Engine.LeaderNowNs, lane.ItemId, lane.Clip.State, lane.Clip.Anchor.MediaNs, lane.Clip.Anchor.LeaderNs, lane.NextItemId, lane.SwitchAtLeaderNs));
        }
        Assert.Equal(((long?)(-4 * S + 1), (long?)(-4 * S + 1)), (StartedOnAir(through, throughSources, 2), StartedOnAir(late, lateSources, 1)));

        // A jump inside the gap moves that instant with the code: back to 01:00:10:10 at 7 s, Song 1's code ended 0.4 s before.
        await through.Ok("TIMECODE INTERNAL Rehearsal LOCATE 01:00:10:10");
        await through.Engine.StepChasesAsync();
        var jumped = through.Engine.Playback.Lane("pl_show")!;
        Assert.Equal(("it_a", 10 * S - 1, 6_600_000_000, "it_b", 8_600_000_000),
            (jumped.ItemId, jumped.Clip.Anchor.MediaNs, jumped.Clip.Anchor.LeaderNs, jumped.NextItemId, jumped.SwitchAtLeaderNs));
        Assert.Equal(6_600_000_000 - (10 * S - 1), StartedOnAir(through, throughSources, 3));
    }
}
