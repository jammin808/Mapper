using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Engine.Sync;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Presentation;
using PlayMap.Net.Sync;

namespace PlayMap.Engine.Tests.Sync;

/// <summary>The group's air latency (SYNC LATENCY): the leader carries it to every member on the feed (<c>ANCHOR &lt;group&gt; @latency
/// {"ms":…}</c>) — AUTO the largest output latency its board hears, its own outputs included, else the stated one — and every node
/// raises its own air latency to it through the output seam; STATE's <c>sync.groupLatencyMs</c> and the context's group item say it on
/// both. A follower's latency is its leader's (refused with the way there), setting what is set is refused, and a record that is not
/// one whole is never taken.</summary>
public sealed class SyncLatencyTests
{
    private const long Second = SyncRig.Second;

    private static double Latency(HostRig rig) => SyncRig.Sync(rig).GetProperty("groupLatencyMs").GetDouble();

    private static string GroupWords(HostRig rig) => SyncRig.GroupItem(rig).GetProperty("state").GetString()!;

    [Fact]
    public async Task TheLeaderCarriesTheGroupsAirLatencyToEveryMemberAndSyncLatencySetsIt()
    {
        var clock = new ManualClock(1_000 * Second);
        var leaderV = new FakeVblanks(FakeVblanks.At("op_1", 60, clock.NowNs + 3 * Second) with { AirLatencyUs = 17_000 });
        var memberV = new FakeVblanks(FakeVblanks.At("op_1", 60, clock.NowNs) with { AirLatencyUs = 50_000 });
        await using var leader = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, new OffsetClock(clock, 3 * Second), SyncRig.Fast(leaderV));
        await using var follower = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock, SyncRig.Fast(memberV));
        await SyncRig.OpenShowAsync(leader);
        await SyncRig.OpenShowAsync(follower);
        await SyncRig.GroupAsync(leader, follower);

        // AUTO: the member's slower display (50 ms) is the group's, on the leader and, through the feed, on the member.
        Assert.True(await Eventually.TrueAsync(() => Latency(leader) == 50 && Latency(follower) == 50),
            $"leader {Latency(leader)}, member {Latency(follower)}\n{follower.Journal()}");
        Assert.Equal(50, follower.Engine.Services.GroupSync!.GroupLatencyMs);
        Assert.EndsWith("; the group's air latency 50 ms", GroupWords(leader), StringComparison.Ordinal);
        Assert.EndsWith("; the group's air latency 50 ms", GroupWords(follower), StringComparison.Ordinal);
        Assert.Contains(follower.Engine.Journal.Recent(50), r => r.Text.StartsWith("the group's air latency is 50 ms (FOH", StringComparison.Ordinal));

        // Stated: kept in the leader's project, carried to the member.
        var set = await leader.RunAsync("SYNC LATENCY 80");
        Assert.True(set.Ok, set.Payload);
        Assert.Equal(80, leader.Engine.Store.Project.Settings.SyncLatencyMs);
        Assert.True(await Eventually.TrueAsync(() => Latency(leader) == 80 && Latency(follower) == 80), $"leader {Latency(leader)}, member {Latency(follower)}");

        // What is set already, and a member's own word, are refused with the reason; nothing changes.
        var again = await leader.RunAsync("SYNC LATENCY 80");
        Assert.Equal((false, "the group's air latency is 80 ms already"), (again.Ok, again.Payload));
        var member = await follower.RunAsync("SYNC LATENCY 30");
        Assert.False(member.Ok);
        Assert.Equal($"this node follows {SyncRig.LeaderId} in show: the group's air latency is its leader's — SYNC LATENCY there", member.Payload);
        Assert.Equal(-1, follower.Engine.Store.Project.Settings.SyncLatencyMs);
        Assert.False((await leader.RunAsync("SYNC LATENCY 501")).Ok);
        Assert.False((await leader.RunAsync("SYNC LATENCY -2")).Ok);

        // Back to AUTO: the largest heard again.
        Assert.True((await leader.RunAsync("SYNC LATENCY AUTO")).Ok);
        Assert.Equal(-1, leader.Engine.Store.Project.Settings.SyncLatencyMs);
        Assert.True(await Eventually.TrueAsync(() => Latency(leader) == 50 && Latency(follower) == 50), $"leader {Latency(leader)}, member {Latency(follower)}");
    }

    [Fact]
    public void ALatencyRecordReadsBackAsItselfAndAnythingElseIsRefused()
    {
        foreach (var ms in new[] { 0, 16.667, 33, 500 })
        {
            Assert.True(SyncNode.TryReadLatency(SyncNode.LatencyJson(ms), out var back));
            Assert.Equal(ms, back);
        }
        Assert.Equal("{\"ms\":16.667}", SyncNode.LatencyJson(16.66666));
        foreach (var bad in new[] { "", "[]", "{}", "{\"ms\":\"33\"}", "{\"ms\":-1}", "{\"ms\":500.5}", "{\"ms\":null}", "{\"latency\":33}", "{\"ms\":33" })
            Assert.False(SyncNode.TryReadLatency(bad, out _), bad);
    }

    [Fact]
    public void TheLeadersLatencyIsTheStatedOneElseTheLargestHeard()
    {
        var heard = SyncPicture.Empty with { GroupLatencyMs = 50 };
        var auto = Project.CreateDefault("Latency");
        Assert.Equal(50, SyncNode.GroupLatencyOf(auto, heard));
        Assert.Equal(80, SyncNode.GroupLatencyOf(auto with { Settings = auto.Settings with { SyncLatencyMs = 80 } }, heard));
        Assert.Equal(0, SyncNode.GroupLatencyOf(auto with { Settings = auto.Settings with { SyncLatencyMs = 0 } }, heard));
        Assert.Equal(0, SyncNode.GroupLatencyOf(auto, SyncPicture.Empty));
        Assert.Equal(SyncNode.MaxLatencyMs, SyncNode.GroupLatencyOf(auto, heard with { GroupLatencyMs = 900 }));
    }

    [Fact]
    public async Task TheGroupsLatencyIsWrittenThroughToThisNodesOutputs()
    {
        var outputs = new ScriptedOutputs();
        await using var rig = PresentRig.Start(PresentRig.With(PresentRig.Screen("op_a")), outputs, new RoleNetwork("follower", SyncRig.LeaderId));
        rig.Engine.SetGroupAirLatency(50);
        rig.Engine.SetGroupAirLatency(0);
        Assert.Equal([50, 0], outputs.GroupLatencies);
    }
}
