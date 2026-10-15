using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Rendering;

/// <summary>Transitions on a manual GO, NEXT and PREV (Mesa is enough): the item on the air stays through the incoming item's
/// window, which opens at the GO's instant, and the two are mixed in linear light — a fade's midpoint reads the 50/50 mix of the
/// two pictures, a dip's midpoint black; a cut item still cuts.</summary>
[Collection("render host")]
public sealed class ManualTransitionTests(ITestOutputHelper log)
{
    private const long T0 = 5_000_000_000;

    private static Project Show()
    {
        PlaylistItem Item(string id, string color, TransitionKind kind, long ns) =>
            PlaylistItem.For(id, SourceRef.Solid(color), id) with { DurationNs = 20_000_000_000, End = EndAction.Hold, Transition = kind, TransitionNs = ns };
        return HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Playlist("pl_show"), 64, 48)) with
        {
            Playlists =
            [
                new Playlist("pl_show", "Show",
                [
                    Item("it_white", "#FFFFFF", TransitionKind.Cut, 0),
                    Item("it_black", "#000000", TransitionKind.Fade, 1_000_000_000),
                    Item("it_red", "#FF0000", TransitionKind.Dip, 400_000_000),
                ], false, null),
            ],
        };
    }

    private static bool Near((int R, int G, int B) p, double r, double g, double b) =>
        Math.Abs(p.R - r) <= 1 && Math.Abs(p.G - g) <= 1 && Math.Abs(p.B - b) <= 1;

    private static async Task<(int R, int G, int B)> At(HostRig rig, long leaderNs, double r, double g, double b)
    {
        rig.Clock.NowNs = leaderNs;
        var px = await rig.ReadUntil("op_1", p => Near(HostRig.At(p, 64, 32, 24), r, g, b));
        return HostRig.At(px, 64, 32, 24);
    }

    [Fact]
    public async Task AGoIntoAFadingItemMixesBothPicturesAcrossTheWindow()
    {
        await using var rig = HostRig.Start(Show());
        await rig.Ok("PLAYLIST GO pl_show");
        await At(rig, T0 + 100_000_000, 255, 255, 255);

        var t = T0 + 2_000_000_000;
        rig.Clock.NowNs = t;
        await rig.Ok("PLAYLIST GO pl_show");
        var lane = rig.Engine.Playback.Lane("pl_show")!;
        Assert.Equal(("it_white", "it_black", TransitionKind.Fade), (lane.ItemId, lane.NextItemId, lane.NextTransition));
        Assert.Equal((t, t + 1_000_000_000), (lane.MixStartNs, lane.SwitchAtLeaderNs));

        var half = Transitions.Mix8(255, 0, TransitionKind.Fade, 0.5);
        var mid = await At(rig, t + 500_000_000, half, half, half);
        log.WriteLine($"midpoint {mid} (a 50/50 mix in linear light is {half:0.0})");
        Assert.True(Near(mid, half, half, half), $"the midpoint read {mid}");
        var quarter = Transitions.Mix8(255, 0, TransitionKind.Fade, 0.25);
        await At(rig, t + 250_000_000, quarter, quarter, quarter);
        await At(rig, t + 1_000_000_000, 0, 0, 0);
        Assert.True(await PlayMap.Engine.Tests.Show.ShowRig.Eventually(() => rig.Engine.Playback.Lane("pl_show")!.ItemId == "it_black"));

        // NEXT into a dipping item: half way it is black, then the red comes up.
        var n = t + 3_000_000_000;
        rig.Clock.NowNs = n;
        await rig.Ok("PLAYLIST NEXT pl_show");
        await At(rig, n + 200_000_000, 0, 0, 0);
        await At(rig, n + 300_000_000, Transitions.Mix8(0, 255, TransitionKind.Dip, 0.75), 0, 0);
        await At(rig, n + 400_000_000, 255, 0, 0);

        // PREV into an item with a fade (black) mixes too; GO back to the first (a cut) cuts.
        var p = n + 2_000_000_000;
        rig.Clock.NowNs = p;
        await rig.Ok("PLAYLIST PREV pl_show");
        var redToBlack = Transitions.Mix8(255, 0, TransitionKind.Fade, 0.5);
        await At(rig, p + 500_000_000, redToBlack, 0, 0);
        var g = p + 2_000_000_000;
        rig.Clock.NowNs = g;
        await rig.Ok("PLAYLIST GO pl_show #1");
        Assert.Null(rig.Engine.Playback.Lane("pl_show")!.NextItemId);
        await At(rig, g, 255, 255, 255);
    }
}
