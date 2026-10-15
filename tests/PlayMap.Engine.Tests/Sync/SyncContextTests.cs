using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Sync;

/// <summary>The group in the context and in INDEX: each node publishes the item sync:&lt;group&gt; (the Eye merges them by id; the
/// leader's carries the verdict), the follower's machine follows it and its group playlists follow it, the leader's group follows
/// its machine, every link an Eye verb; INDEX's sync section lists the group lit as STATE lights it, with its status key.</summary>
public sealed class SyncContextTests
{
    private const long Second = SyncRig.Second;

    private static JsonElement Context(HostRig rig) => JsonDocument.Parse(rig.Engine.Context()).RootElement.Clone();

    private static bool Link(JsonElement context, string from, string to, string kind) =>
        context.GetProperty("links").EnumerateArray().Any(l => l.GetProperty("from").GetString() == from
            && l.GetProperty("to").GetString() == to && l.GetProperty("kind").GetString() == kind);

    [Fact]
    public async Task EachNodePublishesTheGroupItemAndTheLinksTheEyeDraws()
    {
        var clock = new ManualClock(1_000 * Second);
        var leaderV = new FakeVblanks(FakeVblanks.At("op_1", 60, clock.NowNs + 3 * Second));
        await using var leader = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, new OffsetClock(clock, 3 * Second), SyncRig.Fast(leaderV));
        await using var follower = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock);
        await SyncRig.OpenShowAsync(leader);
        await SyncRig.OpenShowAsync(follower);
        await SyncRig.GroupAsync(leader, follower);
        Assert.True(await Eventually.TrueAsync(() => SyncRig.Sync(leader).GetProperty("outputs").GetArrayLength() == 1));

        var theirs = Context(leader);
        var item = theirs.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "sync:show");
        Assert.Equal("sync", item.GetProperty("kind").GetString());
        Assert.Equal("green", item.GetProperty("light").GetString());
        Assert.StartsWith("leading show with 1 member", item.GetProperty("state").GetString(), StringComparison.Ordinal);
        Assert.True(Link(theirs, "sync:show", "machine", "follows"));
        Assert.True(Link(theirs, "output:op_1", "sync:show", "hears"));   // free-running: it hears the group's clock

        var ours = Context(follower);
        Assert.Contains(ours.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == "sync:show");
        Assert.True(Link(ours, "machine", "sync:show", "follows"));
        Assert.True(Link(ours, "playlist:pl_show", "sync:show", "follows"));
        Assert.False(Link(ours, "playlist:pl_local", "sync:show", "follows"));
        var verbs = new[] { ContextLinkKinds.Feeds, ContextLinkKinds.Shows, ContextLinkKinds.Drives, ContextLinkKinds.Carries, ContextLinkKinds.Controls,
            ContextLinkKinds.Mirrors, ContextLinkKinds.Follows, ContextLinkKinds.Hears, ContextLinkKinds.Routes, ContextLinkKinds.Serves, ContextLinkKinds.Sends };
        foreach (var c in new[] { theirs, ours })
            Assert.All(c.GetProperty("links").EnumerateArray(), l => Assert.Contains(l.GetProperty("kind").GetString(), verbs));

        var index = JsonDocument.Parse(await leader.OkAsync("INDEX")).RootElement;
        var section = index.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("section").GetString() == "sync");
        Assert.Equal("SYNC STATUS", section.GetProperty("hint").GetProperty("keys")[0].GetProperty("line").GetString());
        var row = Assert.Single(section.GetProperty("rows").EnumerateArray());
        Assert.Equal(("sync:show", "show", "green", "free-running"), (row.GetProperty("id").GetString(), row.GetProperty("label").GetString(),
            row.GetProperty("light").GetString(), row.GetProperty("state").GetString()));
        var followerRow = Assert.Single(JsonDocument.Parse(await follower.OkAsync("INDEX")).RootElement.GetProperty("sections").EnumerateArray()
            .Single(s => s.GetProperty("section").GetString() == "sync").GetProperty("rows").EnumerateArray());
        Assert.Equal("live", followerRow.GetProperty("state").GetString());
    }
}
