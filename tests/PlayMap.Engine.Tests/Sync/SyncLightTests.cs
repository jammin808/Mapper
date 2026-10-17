using System.Text.Json;
using PlayMap.Core.Sync;
using PlayMap.Core.Time;
using PlayMap.Engine.Host;
using PlayMap.Engine.Sync;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Sync;

/// <summary>STATE.sync.light is the light the node reads with its verdict, the same as its context's sync:&lt;group&gt; item and its INDEX
/// row, for every verdict: a leader's from its sync board over the outputs it hears (grey with none heard, green on one vblank or
/// free-running at ease, amber for mixed rates, for a locked pair off one vblank among free-running outputs, for an output joining the
/// lock for over 5 s and for a pair that slips within ten minutes while the show plays, red while one fell out of the lock), a
/// follower's from its feed and clock (green while the feed is live, red while it free-wheels), grey in no group or in one it is
/// configured into and has not joined. It is STATE.sync's last key. Synthetic vblanks on one manual clock; each step waits for the
/// reports, not for wall time.</summary>
public sealed class SyncLightTests
{
    private const long Second = SyncRig.Second;
    private const long Base = 1_000 * Second;

    /// <summary>The leader's own outputs for a scenario: their rate, their vblank on its clock and their lock state.</summary>
    private static OutputVblank[] Outputs(string scenario) => scenario switch
    {
        "nothing heard" => [],
        "one vblank" => [FakeVblanks.At("op_1", 60, Base, LockState.Framelocked), FakeVblanks.At("op_2", 60, Base, LockState.HouseLocked)],
        "free at ease" => [FakeVblanks.At("op_1", 60, Base), FakeVblanks.At("op_2", 60, Base + 3_000_000)],
        "mixed rate" => [FakeVblanks.At("op_1", 60, Base), FakeVblanks.At("op_2", 59.94, Base)],
        "fell out" => [FakeVblanks.At("op_1", 60, Base), FakeVblanks.At("op_2", 60, Base, LockState.FellOut)],
        "locked pair apart" =>
        [
            FakeVblanks.At("op_1", 60, Base, LockState.Framelocked), FakeVblanks.At("op_2", 60, Base + 3_000_000, LockState.Framelocked),
            FakeVblanks.At("op_3", 60, Base),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
    };

    [Theory]
    [InlineData("nothing heard", "unknown", "grey")]
    [InlineData("one vblank", "same vblank", "green")]
    [InlineData("free at ease", "free-running", "green")]
    [InlineData("mixed rate", "mixed rate", "amber")]
    [InlineData("fell out", "free-running", "red")]
    [InlineData("locked pair apart", "free-running", "amber")]
    public async Task ALeaderPublishesItsBoardsLightWithItsVerdict(string scenario, string verdict, string light)
    {
        var clock = new ManualClock(Base);
        var outputs = Outputs(scenario);
        await using var leader = await Leader(clock, new FakeVblanks(outputs));
        Assert.True(await Eventually.TrueAsync(() => Heard(leader) == outputs.Length && SyncRig.Sync(leader).GetProperty("verdict").GetString() == verdict),
            SyncRig.Sync(leader).ToString());
        AssertLight(leader, light);
    }

    /// <summary>An output joining the lock is not amber by itself: it is once it has been joining for over 5 s of the leader's clock.</summary>
    [Fact]
    public async Task AnOutputJoiningTheLockForOverFiveSecondsIsAmber()
    {
        var clock = new ManualClock(Base);
        await using var leader = await Leader(clock, new FakeVblanks(FakeVblanks.At("op_1", 60, Base), FakeVblanks.At("op_2", 60, Base, LockState.Joining)));
        Assert.True(await Eventually.TrueAsync(() => Heard(leader) == 2), SyncRig.Sync(leader).ToString());
        AssertLight(leader, "green");

        clock.AdvanceMs(6_000);
        Assert.True(await Eventually.TrueAsync(() => SyncRig.Sync(leader).GetProperty("light").GetString() == "amber"), SyncRig.Sync(leader).ToString());
        Assert.Equal("free-running", SyncRig.Sync(leader).GetProperty("verdict").GetString());
        Assert.Contains("has been joining the lock for over 5 s", SyncRig.Sync(leader).GetProperty("words").GetString(), StringComparison.Ordinal);
        AssertLight(leader, "amber");
    }

    /// <summary>A free-running pair that slips within ten minutes is green while nothing plays and amber once the show plays.</summary>
    [Fact]
    public async Task APairThatSlipsSoonIsAmberOnlyWhileTheShowPlays()
    {
        var clock = new ManualClock(Base);
        await using var leader = await Leader(clock, new FakeVblanks(FakeVblanks.At("op_1", 60, Base), FakeVblanks.At("op_2", 60.001, Base)));
        Assert.True(await Eventually.TrueAsync(() => Heard(leader) == 2 && SyncRig.Sync(leader).GetProperty("pairs").GetArrayLength() == 1),
            SyncRig.Sync(leader).ToString());
        Assert.True(SyncRig.Sync(leader).GetProperty("pairs")[0].GetProperty("nextSlipS").GetDouble() < 600);
        AssertLight(leader, "green");

        await leader.OkAsync("PLAYLIST GO pl_show");
        Assert.True(await Eventually.TrueAsync(() => SyncRig.Sync(leader).GetProperty("light").GetString() == "amber"), SyncRig.Sync(leader).ToString());
        Assert.Equal("free-running", SyncRig.Sync(leader).GetProperty("verdict").GetString());
        AssertLight(leader, "amber");
    }

    /// <summary>A follower's light is its own feed and clock (its verdict is the leader's to give): green while the feed is live, red
    /// once its leader went quiet and it free-wheels. A node in no group publishes grey.</summary>
    [Fact]
    public async Task AFollowerPublishesItsFeedsLightAndANodeInNoGroupGrey()
    {
        var clock = new ManualClock(Base);
        await using var follower = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock);
        await SyncRig.OpenShowAsync(follower);
        var solo = SyncRig.Sync(follower);
        Assert.Equal(("", "unknown", "grey"), (solo.GetProperty("group").GetString(), solo.GetProperty("verdict").GetString(), solo.GetProperty("light").GetString()));
        Assert.Equal("light", solo.EnumerateObject().Last().Name);

        await using (var leader = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, new OffsetClock(clock, 3 * Second)))
        {
            await SyncRig.OpenShowAsync(leader);
            await SyncRig.GroupAsync(leader, follower);
            Assert.Equal(("unknown", "live"), (SyncRig.Sync(follower).GetProperty("verdict").GetString(), SyncRig.Sync(follower).GetProperty("feed").GetString()));
            AssertLight(follower, "green");
        }

        clock.AdvanceMs(4_000);
        Assert.True(await Eventually.TrueAsync(() => follower.Journal().Contains("went quiet: free-wheeling", StringComparison.Ordinal)), follower.Journal());
        clock.AdvanceMs(1_500);
        Assert.True(await Eventually.TrueAsync(() => SyncRig.Sync(follower).GetProperty("freewheelS").GetDouble() > 0), SyncRig.Sync(follower).ToString());
        AssertLight(follower, "red");
    }

    /// <summary>A node configured to follow a leader it has not heard yet (its settings kept from before a start) is in no group yet:
    /// grey, its feed connecting, the same on its INDEX row as in STATE and the context.</summary>
    [Fact]
    public async Task ANodeConfiguredIntoAGroupItHasNotJoinedIsGrey()
    {
        var clock = new ManualClock(Base);
        await using var node = await HostRig.StartAsync("Stage", network: true, mono: clock,
            parts: new EngineParts { Extensions = [SyncExtension.With(SyncRig.Fast())] },
            settings: s => s with { NodeId = SyncRig.FollowerId, Sync = s.Sync with { Role = "follow", Group = "show", Leader = SyncRig.LeaderId } });
        await SyncRig.OpenShowAsync(node);
        var sync = SyncRig.Sync(node);
        Assert.Equal(("solo", "connecting"), (sync.GetProperty("role").GetString(), sync.GetProperty("feed").GetString()));
        AssertLight(node, "grey");
    }

    /// <summary>A leader of group "show" on the test show, its own outputs read from the given vblanks.</summary>
    private static async Task<HostRig> Leader(ManualClock clock, FakeVblanks vblanks)
    {
        var leader = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, clock, SyncRig.Fast(vblanks));
        await SyncRig.OpenShowAsync(leader);
        await leader.OkAsync("SYNC LEAD show");
        return leader;
    }

    private static int Heard(HostRig rig) => SyncRig.Sync(rig).GetProperty("outputs").EnumerateArray().Count(o => o.GetProperty("heard").GetBoolean());

    /// <summary>STATE.sync.light is the expected light, STATE.sync's last key, the light of the node's sync item in its context and the
    /// light of its INDEX row.</summary>
    private static void AssertLight(HostRig rig, string light)
    {
        var sync = SyncRig.Sync(rig);
        Assert.Equal(light, sync.GetProperty("light").GetString());
        Assert.Equal("light", sync.EnumerateObject().Last().Name);
        Assert.Equal(light, SyncRig.GroupItem(rig).GetProperty("light").GetString());
        Assert.Equal(JsonValueKind.String, sync.GetProperty("words").ValueKind);
        Assert.Equal(light, IndexRow(rig).GetProperty("light").GetString());
    }

    /// <summary>The node's INDEX row for its group, as the sync section writes it.</summary>
    private static JsonElement IndexRow(HostRig rig, string group = "show")
    {
        var section = Assert.Single(rig.Engine.Routes.IndexSections, x => x.Section == "sync");
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
            section.WriteRows(rig.Engine.Store.Project, rig.Engine.Snapshot, w);
        using var rows = JsonDocument.Parse(buffer.ToArray());
        return rows.RootElement.EnumerateArray().Single(r => r.GetProperty("id").GetString() == "sync:" + group).Clone();
    }
}
