using PlayMap.Core.Model;
using PlayMap.Core.Playback;

namespace PlayMap.Engine.Tests.Show;

/// <summary>The lane side of a manual transition on a hand-moved clock: GO into a fading item keeps the item on the air as the
/// current one with the incoming one next and the window opening at the GO's instant; a GO inside the window steps from the
/// incoming item (it is on the air) and mixes from it; an item that ends inside the window holds until the switch; the switch
/// promotes the incoming item.</summary>
public sealed class ManualTransitionLaneTests
{
    private static Project Show(EndAction firstEnd = EndAction.Hold, long firstNs = 20_000_000_000)
    {
        var p = ShowRig.OneScreen();
        var gen = p.Generators[0].Id;
        PlaylistItem Item(string id, TransitionKind kind, long ns, EndAction end = EndAction.Hold, long length = 20_000_000_000) =>
            PlaylistItem.For(id, SourceRef.Generator(gen), id) with { DurationNs = length, End = end, Transition = kind, TransitionNs = ns };
        return p with
        {
            Playlists =
            [
                new Playlist("pl_show", "Show",
                [
                    Item("it_a", TransitionKind.Cut, 0, firstEnd, firstNs),
                    Item("it_b", TransitionKind.Fade, 1_000_000_000),
                    Item("it_c", TransitionKind.Fade, 1_000_000_000),
                ], false, null),
            ],
        };
    }

    [Fact]
    public async Task AGoIntoAFadeKeepsTheItemOnTheAirThroughTheWindow()
    {
        await using var rig = new ShowRig(Show());
        await rig.Ok("PLAYLIST GO pl_show");
        rig.Advance(2_000);
        var t = rig.Now;
        await rig.Ok("PLAYLIST GO pl_show AT +100");
        var lane = rig.Engine.Playback.Lane("pl_show")!;
        Assert.Equal(("it_a", "it_b"), (lane.ItemId, lane.NextItemId));
        Assert.Equal((t + 100_000_000, t + 1_100_000_000), (lane.MixStartNs, lane.SwitchAtLeaderNs));
        Assert.Equal(TransportState.Playing, lane.NextClip.State);
        Assert.Equal(t + 100_000_000, lane.NextClip.Anchor.LeaderNs);
        Assert.Null(lane.MixAt(t + 50_000_000));
        Assert.Equal(0.5, lane.MixAt(t + 600_000_000)!.Value.Progress, 6);

        // Inside the window the incoming item is on the air: the next GO steps from it, and mixes out of it.
        rig.Clock.NowNs = t + 600_000_000;
        await rig.Ok("PLAYLIST GO pl_show");
        lane = rig.Engine.Playback.Lane("pl_show")!;
        Assert.Equal(("it_b", "it_c"), (lane.ItemId, lane.NextItemId));
        Assert.Equal(t + 600_000_000, lane.MixStartNs);

        rig.Clock.NowNs = t + 1_600_000_000;
        await rig.Ticked();
        lane = rig.Engine.Playback.Lane("pl_show")!;
        Assert.Equal(("it_c", (string?)null), (lane.ItemId, lane.NextItemId));
    }

    [Fact]
    public async Task AnItemThatEndsInsideTheWindowHoldsUntilTheSwitch()
    {
        await using var rig = new ShowRig(Show(EndAction.Stop, 2_500_000_000));
        await rig.Ok("PLAYLIST GO pl_show");
        rig.Advance(2_000);
        var t = rig.Now;
        await rig.Ok("PLAYLIST GO pl_show");
        // it_a's own end (2.5 s) falls inside it_b's window: the lane does not go idle under the mix.
        rig.Clock.NowNs = t + 700_000_000;
        await rig.Ticked();
        var lane = rig.Engine.Playback.Lane("pl_show")!;
        Assert.False(lane.IsIdle);
        Assert.Equal(("it_a", "it_b"), (lane.ItemId, lane.NextItemId));
        rig.Clock.NowNs = t + 1_000_000_000;
        await rig.Ticked();
        Assert.Equal("it_b", rig.Engine.Playback.Lane("pl_show")!.ItemId);
    }

    [Fact]
    public async Task ACutStaysACutAndTheSameItemAgainRestartsIt()
    {
        await using var rig = new ShowRig(Show());
        await rig.Ok("PLAYLIST GO pl_show #2");
        rig.Advance(2_000);
        await rig.Ok("PLAYLIST GO pl_show #1");
        var lane = rig.Engine.Playback.Lane("pl_show")!;
        Assert.Equal(("it_a", (string?)null), (lane.ItemId, lane.NextItemId));
        await rig.Ok("PLAYLIST GO pl_show #1");
        Assert.Null(rig.Engine.Playback.Lane("pl_show")!.NextItemId);
    }
}
