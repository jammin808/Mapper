using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Core.Time;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.Tests.Control;

namespace PlayMap.Engine.Tests.Tc;

/// <summary>A chase with GAP HOLD while rolling code runs between two items: the last frame of the item before is held on one
/// anchor, made once when the hold began. The lane does not move while the code rolls through the gap (no new revision, no new
/// sequence for the decoder), the item on the air keeps the instant it started (its effects' time stands still), and the next item
/// still takes over on its exact frame.</summary>
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
        // Held on its last frame since 6.5 s: it counts as started that frame's length before.
        Assert.Equal(6 * S + S / 2 - (10 * S - 1), first?.StartNs);

        for (var i = 0; i < 100; i++)
        {
            clock.AdvanceMs(10);
            await rig.Engine.StepChasesAsync();
            sources.BeginFrame(rig.Engine.Snapshot, i + 2);
            var onAir = sources.Item(playlist, rig.Engine.LeaderNowNs);
            Assert.Equal(("it_a", first?.StartNs), (onAir?.Item.Id, onAir?.StartNs));
        }
    }
}
