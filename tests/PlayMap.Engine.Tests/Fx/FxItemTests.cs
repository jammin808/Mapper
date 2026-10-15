using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Playback;
using PlayMap.Engine.Rendering;

namespace PlayMap.Engine.Tests.Fx;

/// <summary>A playlist item's effects count their time from the item's start: FX ADD on an item leaves the slot's origin to the
/// item (any other owner's slot counts from the instant it was added), and the engine names the item a playlist shows with the
/// leader instant it started — the current item, each side of a fade with its own, the next one once its switch came.</summary>
public sealed class FxItemTests
{
    private const long S = 1_000_000_000;

    private static Project Show() => Project.CreateDefault("Items") with
    {
        Playlists =
        [
            new Playlist("pl_fx", "Looks", [
                PlaylistItem.For("it_a", SourceRef.Generator("gn_testcard"), "Card A") with { DurationNs = 4 * S },
                PlaylistItem.For("it_b", SourceRef.Generator("gn_testcard"), "Card B") with
                {
                    DurationNs = 4 * S,
                    Transition = TransitionKind.Fade,
                    TransitionNs = S,
                },
            ], false, null),
        ],
    };

    [Fact]
    public async Task AnItemsEffectCountsFromTheItemsStartAndAnyOtherFromTheInstantItWasAdded()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(7 * S)), Show());
        async Task<CommandReply> Add(string line)
        {
            Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
            var reply = await engine.HandleAsync(parsed, SessionInfo.Desk("t"), CancellationToken.None);
            Assert.True(reply.Ok, reply.Payload);
            return reply;
        }
        var onItem = await Add("FX ADD it_a fx.invert");
        Assert.EndsWith("— compiling when its item is on the air", onItem.Effect, StringComparison.Ordinal);
        Assert.Equal(0, engine.Store.Project.FindFx(onItem.Payload!)!.Value.Slot.OriginNs);
        var onLayer = await Add("FX ADD ly_base fx.invert");
        Assert.Equal(engine.LeaderNowNs, engine.Store.Project.FindFx(onLayer.Payload!)!.Value.Slot.OriginNs);
        Assert.NotEqual(0, engine.LeaderNowNs);
    }

    [Fact]
    public void TheEngineNamesTheItemOnTheAirWithTheInstantItStarted()
    {
        var project = Show();
        var lanes = new PlaybackLanes(_ => 0);
        lanes.Reconcile(project);
        Assert.True(lanes.Go(project.FindPlaylist("pl_fx")!, 0, 10 * S).Ok);
        var lane = lanes.Snapshot.Playlists["pl_fx"];
        // A plays from 10 s for 4 s; B fades in over the last second, so it starts as the window opens at 13 s.
        Assert.Equal((14 * S, 13 * S), (lane.SwitchAtLeaderNs, lane.MixStartNs));
        using var sources = new EngineSources(null);
        sources.BeginFrame(new EngineSnapshot(new ProjectSnapshot(project, 1, ""), lanes.Snapshot, AirState.Initial), 1);
        var playlist = SourceRef.Playlist("pl_fx");

        var a = sources.Item(playlist, 12 * S);
        Assert.Equal(("it_a", 10 * S), (a?.Item.Id, a?.StartNs));
        var mix = sources.Mix(playlist, 13 * S + S / 2);
        Assert.NotNull(mix);
        Assert.Equal(("it_a", 10 * S, "it_b", 13 * S), (mix.Value.FromItem?.Item.Id, mix.Value.FromItem?.StartNs, mix.Value.ToItem?.Item.Id, mix.Value.ToItem?.StartNs));
        var b = sources.Item(playlist, 15 * S);
        Assert.Equal(("it_b", 13 * S), (b?.Item.Id, b?.StartNs));

        // A source that is not a playlist names no item.
        Assert.Null(sources.Item(SourceRef.Generator("gn_testcard"), 12 * S));
    }
}
