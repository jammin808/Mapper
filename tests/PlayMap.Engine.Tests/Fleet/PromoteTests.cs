using System.Collections.Concurrent;
using System.Text.Json;
using PlayMap.Core.Time;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Sync;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>Controller death in a sync group of three real nodes on one hand-moved clock: the leader goes mid-item; both followers
/// free-wheel and switch to the next item at the instant they computed; the context says who should act; FLEET PROMOTE on follower 1
/// makes it the group's leader under a higher epoch on the same time line, and follower 2 — whose leader is silent — takes its clock
/// from it; leader time read on follower 2 never runs backwards and is continuous across the change.</summary>
public sealed class PromoteTests
{
    private const long Second = SyncRig.Second;

    private static JsonElement Sync(HostRig rig) => rig.State().GetProperty("sync");

    private static JsonElement? Item(HostRig rig, string id) =>
        JsonDocument.Parse(rig.Engine.Context()).RootElement.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("id").GetString() == id).Select(i => (JsonElement?)i.Clone()).FirstOrDefault();

    [Fact]
    public async Task AFollowerPromotedLeadsOnTheSameTimeLineAndTheOtherFollowsIt()
    {
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        var leader = await BackupRig.StartAsync("FOH", BackupRig.PrimaryId, new OffsetClock(clock, 3 * Second), time);
        await using var one = await BackupRig.StartAsync("Stage 1", BackupRig.BackupId, clock, time);
        await using var two = await BackupRig.StartAsync("Stage 2", BackupRig.ThirdId, clock, time);
        try
        {
            foreach (var rig in new[] { leader, one, two })
                await SyncRig.OpenShowAsync(rig);
            await leader.OkAsync("SYNC LEAD show");
            await BackupRig.MeetAsync(leader, one, two);
            Assert.True(await Eventually.TrueAsync(() => one.Network.Hello!.Peers.Find(leader.Id)?.Hello.Port("sync") == leader.Network.SyncPort
                && two.Network.Hello!.Peers.Find(leader.Id)?.Hello.Port("sync") == leader.Network.SyncPort));
            await one.OkAsync("SYNC FOLLOW show " + leader.Id);
            await two.OkAsync("SYNC FOLLOW show " + leader.Id);
            await SyncRig.CaughtUpAsync(leader, one);
            await SyncRig.CaughtUpAsync(leader, two);

            await leader.OkAsync("PLAYLIST GO pl_show");
            Assert.True(await Eventually.TrueAsync(() => one.Engine.AnchorJson("pl_show") == leader.Engine.AnchorJson("pl_show")
                && two.Engine.AnchorJson("pl_show") == leader.Engine.AnchorJson("pl_show")));
            var go = leader.Engine.LeaderNowNs;
            clock.Advance(5 * Second);   // mid-item

            // The leader goes. Its followers keep reading its time line, free-wheeling once it has been silent for 3 s.
            await leader.StopAsync();
            clock.Advance(3 * Second + Second / 2);
            Assert.True(await Eventually.TrueAsync(() => Sync(one).GetProperty("freewheelS").GetDouble() >= 0 && one.Engine.Services.Clock.IsHolding
                && two.Engine.Services.Clock.IsHolding), Sync(one).ToString());
            Assert.Equal(one.Engine.LeaderNowNs, two.Engine.LeaderNowNs);
            Assert.Equal("leader gone — FLEET PROMOTE on the node that should lead", Item(one, "fleet:leader")?.GetProperty("state").GetString());
            Assert.Equal("red", Item(one, "fleet:leader")?.GetProperty("light").GetString());

            // Both switch to the next item at the instant each computed from the anchor (10 s after the GO), with no leader.
            clock.Advance(go + 10 * Second - one.Engine.LeaderNowNs - 1_000_000);
            Assert.True(await Eventually.TrueAsync(() => one.Engine.Playback.Lane("pl_show")!.ItemId == "it_a" && two.Engine.Playback.Lane("pl_show")!.ItemId == "it_a"));
            clock.Advance(2_000_000);
            Assert.True(await Eventually.TrueAsync(() => one.Engine.Playback.Lane("pl_show")!.ItemId == "it_b" && two.Engine.Playback.Lane("pl_show")!.ItemId == "it_b"));
            Assert.Equal(one.Engine.AnchorJson("pl_show"), two.Engine.AnchorJson("pl_show"));
            Assert.Equal(go + 10 * Second, JsonDocument.Parse(one.Engine.AnchorJson("pl_show")!).RootElement.GetProperty("leaderNs").GetInt64());

            // Follower 2's leader time, sampled all through the promotion and the change of leader.
            // Follower 2's leader time against the machines' own clock, sampled all through the promotion and the change of leader while
            // that clock runs on (1 µs a sample, so the exchange's own measurement stays exact).
            var samples = new ConcurrentQueue<(long Local, long Leader)>();
            using var stop = new CancellationTokenSource();
            var sampling = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    clock.Advance(1_000);
                    samples.Enqueue((clock.NowNs, two.Engine.LeaderNowNs));
                    await Task.Yield();
                }
            });
            var promoted = await one.RunAsync("FLEET PROMOTE");
            Assert.True(promoted.Ok, promoted.Payload);
            Assert.Equal("leader", Sync(one).GetProperty("role").GetString());
            Assert.Equal(1, Sync(one).GetProperty("epoch").GetInt64());
            Assert.True(await Eventually.TrueAsync(() => Sync(two).GetProperty("leader").GetString() == one.Id && Sync(two).GetProperty("feed").GetString() == "live"
                && !two.Engine.Services.Clock.IsHolding, 15000), Sync(two) + "\n" + two.Journal());
            await stop.CancelAsync();
            await sampling;
            Assert.InRange(Math.Abs(two.Engine.LeaderNowNs - one.Engine.LeaderNowNs), 0, 1_000_000);
            var seen = samples.ToArray();
            Assert.True(seen.Length > 10, $"{seen.Length} samples");
            for (var i = 1; i < seen.Length; i++)
                Assert.True(seen[i].Leader >= seen[i - 1].Leader, $"leader time ran backwards on Stage 2: {seen[i - 1].Leader} then {seen[i].Leader}");
            // Continuous: the leader time it reads keeps its place against the running clock within 1 ms across the change of leader.
            var offsets = seen.Select(x => x.Leader - x.Local).ToArray();
            Assert.InRange(offsets.Max() - offsets.Min(), 0, 1_000_000);
            Assert.Equal(1, Sync(two).GetProperty("epoch").GetInt64());
            Assert.Null(Item(two, "fleet:leader"));
            Assert.Contains("leads show now (epoch 1)", two.Journal(), StringComparison.Ordinal);
            // A promotion of a node that leads already says so.
            Assert.Contains("leads show already", (await one.RunAsync("FLEET PROMOTE")).Payload, StringComparison.Ordinal);
        }
        finally
        {
            await leader.DisposeAsync();
        }
    }
}
