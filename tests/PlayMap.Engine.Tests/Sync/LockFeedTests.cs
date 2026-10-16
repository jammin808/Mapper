using PlayMap.Core.Sync;
using PlayMap.Engine.Sync;
using PlayMap.Engine.Tests.Presentation;
using PlayMap.Engine.Tests.Show;

namespace PlayMap.Engine.Tests.Sync;

/// <summary>The group's frame lock on the feed (<c>ANCHOR &lt;group&gt; @lock</c>): the record reads back as itself and nothing that is not
/// one whole is taken; a member offers the leader's counter anchor to its outputs, joins when the leader applied the lock — not
/// while armed and on air (it waits for a blackout, as SYNC APPLY would), never when its own frame lock is off — and leaves when
/// the leader released it. The leader's record is its outputs' wish and anchor.</summary>
public sealed class LockFeedTests
{
    private static readonly LockAnchor Counter = new(0x123456, 987_654_321_000, 16_666_666_667, 0x123456 + 120, 5);

    [Fact]
    public void ARecordReadsBackAsItselfAndAnythingElseIsRefused()
    {
        foreach (var r in new[] { new LockRecord(true, 1, 1, Counter), new LockRecord(false, 2, 0, null) })
        {
            Assert.True(LockRecord.TryParse(r.ToJson(), out var back));
            Assert.Equal(r, back);
        }
        foreach (var bad in new[]
        {
            "", "[]", "{}", "{\"apply\":1,\"swapGroup\":1,\"barrier\":1,\"counter\":null}",
            "{\"apply\":true,\"swapGroup\":-1,\"barrier\":1,\"counter\":null}",
            "{\"apply\":true,\"swapGroup\":1,\"barrier\":1}",
            "{\"apply\":true,\"swapGroup\":1,\"barrier\":1,\"counter\":\"soon\"}",
            "{\"apply\":true,\"swapGroup\":\"1\",\"barrier\":1,\"counter\":null}",
            "{\"apply\":true,\"swapGroup\":1,\"barrier\":1,\"counter\":{\"count24\":\"x\",\"leaderNs\":0,\"periodPs\":1,\"effectiveAt24\":0,\"seq\":1}}",
            "{\"apply\":true,\"swapGroup\":1,\"barrier\":1,\"counter\":{\"count24\":16777216,\"leaderNs\":0,\"periodPs\":1,\"effectiveAt24\":0,\"seq\":1}}",
            "{\"apply\":true,\"swapGroup\":1,\"barrier\":1,\"counter\":{\"count24\":1,\"leaderNs\":0,\"periodPs\":0,\"effectiveAt24\":0,\"seq\":1}}",
        })
            Assert.False(LockRecord.TryParse(bad, out _), bad);
    }

    [Fact]
    public async Task AMemberTakesTheLeadersCounterAndWishButNotWhileArmedOnAir()
    {
        var outputs = new ScriptedOutputs().WithFreeLocks("op_a");
        await using var rig = PresentRig.Start(PresentRig.With(PresentRig.Screen("op_a")), outputs, new RoleNetwork("follower", "0badc0de"));
        rig.Gate.Armed = true;

        var (taken, words) = rig.Engine.AdoptLeaderLock(new LockRecord(true, 1, 1, Counter));
        Assert.False(taken);
        Assert.Contains("armed", words, StringComparison.Ordinal);
        Assert.Equal(["anchor 5"], outputs.Calls);   // the counter is taken at once: it moves no picture

        Assert.True((await rig.Run("BLACKOUT ON")).Ok);
        (taken, words) = rig.Engine.AdoptLeaderLock(new LockRecord(true, 1, 1, Counter));
        Assert.True(taken);
        Assert.Equal("joining the leader's frame lock", words);
        Assert.Equal(["anchor 5", "apply 1 1"], outputs.Calls);
        Assert.True(rig.Engine.AdoptLeaderLock(new LockRecord(true, 1, 1, Counter)).Taken);
        Assert.Equal(2, outputs.Calls.Count);   // already joined: nothing asked twice

        // The leader releases while this armed node is on air: leaving waits for the next blackout too.
        Assert.True((await rig.Run("BLACKOUT OFF")).Ok);
        (taken, words) = rig.Engine.AdoptLeaderLock(new LockRecord(false, 1, 1, Counter));
        Assert.False(taken);
        Assert.Contains("leaves at the next blackout or once disarmed", words, StringComparison.Ordinal);
        Assert.Equal("apply 1 1", outputs.Calls[^1]);
        Assert.True((await rig.Run("BLACKOUT ON")).Ok);
        (taken, words) = rig.Engine.AdoptLeaderLock(new LockRecord(false, 1, 1, Counter));
        Assert.True(taken);
        Assert.Equal("left the frame lock with the leader", words);
        Assert.Equal("leave all", outputs.Calls[^1]);
    }

    [Fact]
    public async Task AMemberThatHasBegunToStopTakesNothingOfTheLeadersLock()
    {
        var outputs = new ScriptedOutputs().WithFreeLocks("op_a");
        await using var rig = PresentRig.Start(PresentRig.With(PresentRig.Screen("op_a")), outputs, new RoleNetwork("follower", "0badc0de"));

        // The leader's record is a line from outside this node: once it stops it neither offers the counter nor joins.
        rig.Engine.BeginStop();
        var (taken, words) = await rig.Engine.AdoptLeaderLockAsync(new LockRecord(true, 1, 1, Counter), () => true);
        Assert.True(taken);   // never asked again: the node is going
        Assert.Equal(EngineCore.StoppingWords, words);
        Assert.Empty(outputs.Calls);
    }

    [Fact]
    public async Task AMemberWhoseFrameLockIsOffNeverJoinsAndTheLeadersRecordIsItsWish()
    {
        var outputs = new ScriptedOutputs().WithFreeLocks("op_a");
        await using var member = PresentRig.Start(PresentRig.With(PresentRig.Screen("op_a")), outputs, new RoleNetwork("follower", "0badc0de"),
            settings: s => s with { Present = s.Present with { FrameLock = "off" } });
        var (taken, words) = member.Engine.AdoptLeaderLock(new LockRecord(true, 1, 1, null));
        Assert.True(taken);
        Assert.StartsWith("this node's frame lock is off", words, StringComparison.Ordinal);
        Assert.Empty(outputs.Calls);

        var leading = new ScriptedOutputs().WithFreeLocks("op_a");
        await using var leader = PresentRig.Start(PresentRig.With(PresentRig.Screen("op_a")), leading, new RoleNetwork("leader"));
        Assert.Equal(new LockRecord(false, 0, 0, null), leader.Engine.LockRecordNow());
        Assert.True((await leader.Run("SYNC APPLY")).Ok);
        Assert.Equal(new LockRecord(true, 1, 1, null), leader.Engine.LockRecordNow());
        await using var bare = new ShowRig();
        Assert.Null(bare.Engine.LockRecordNow());
        Assert.Equal("no render host: the leader's frame lock is not taken here", bare.Engine.AdoptLeaderLock(new LockRecord(true, 1, 1, null)).Words);
    }
}

/// <summary>Which of a leader's own lines go to its group, and as what: PANIC (and its alias), PANIC CLEAR, BLACKOUT (a toggle as the
/// state it left) and MASTER; a panic's AT &lt;unix ms&gt; as +ms from the leader's wall clock; HERE, and a line that names outputs, keep
/// a line local; nothing else is fanned out from the journal.</summary>
public sealed class FanoutLineTests
{
    [Theory]
    [InlineData("PANIC FADE 1000", false, "PANIC FADE 1000")]
    [InlineData("PANIC OFF FADE 0", false, "PANIC OFF FADE 0")]
    [InlineData("PANIC OFF ON op_1", false, null)]
    [InlineData("PANIC CLEAR FADE 500", false, "PANIC CLEAR FADE 500")]
    [InlineData("BLACKOUT TOGGLE", true, "BLACKOUT ON")]
    [InlineData("BLACKOUT TOGGLE", false, "BLACKOUT OFF")]
    [InlineData("BLACKOUT ON", true, "BLACKOUT ON")]
    [InlineData("MASTER 50 FADE 1000", false, "MASTER 50 FADE 1000")]
    [InlineData("PANIC FADE 1000 HERE", false, null)]
    [InlineData("MASTER 50 here", false, null)]
    [InlineData("PANIC SET op_1 BLACK", false, null)]
    [InlineData("SHOW ARM", false, null)]
    [InlineData("PLAYLIST GO pl_show", false, null)]
    [InlineData("SITE ORIGIN 512300 5401200 100", false, null)]
    [InlineData("SITE POINTS /survey/points.csv", false, null)]
    [InlineData("PROJECTOR PRIOR pj_1 THROW 3", false, null)]
    [InlineData("SURVEY IMPORT /survey/front.las", false, null)]
    [InlineData("NOT A VERB", false, null)]
    [InlineData("PANIC ON op_1 FADE 500", false, null)]
    [InlineData("PANIC CLEAR ON op_1", false, null)]
    [InlineData("PANIC CLEAR ALL FADE 0", false, "PANIC CLEAR ALL FADE 0")]
    [InlineData("PANIC SAFE AT 1760000030000", false, "PANIC SAFE AT +30000")]
    [InlineData("PANIC AT 1759999990000", false, "PANIC AT +0")]
    [InlineData("PANIC AT +250", false, "PANIC AT +250")]
    public void OnlyTheAirVerbsGoAndHereKeepsALineLocal(string line, bool blackoutNow, string? sent) =>
        Assert.Equal(sent, SyncNode.FanoutLine(line, blackoutNow, 1_760_000_000_000));

    /// <summary>A panic goes with the leader's lead in its AT (a member adds none of its own); a clear and the master have no lead.</summary>
    [Theory]
    [InlineData("PANIC FADE 1000", 200, "PANIC FADE 1000 AT +200")]
    [InlineData("PANIC FADE 1000", 0, "PANIC FADE 1000")]
    [InlineData("PANIC SAFE AT 1760000030000", 200, "PANIC SAFE AT +30200")]
    [InlineData("PANIC AT +250", 200, "PANIC AT +450")]
    [InlineData("PANIC AT +0.5", 40, "PANIC AT +40.5")]
    [InlineData("PANIC CLEAR FADE 500", 200, "PANIC CLEAR FADE 500")]
    [InlineData("PANIC OFF FADE 0", 200, "PANIC OFF FADE 0")]
    [InlineData("MASTER 50 FADE 1000", 200, "MASTER 50 FADE 1000")]
    public void APanicCarriesTheLeadersLeadInItsAt(string line, int panicLeadMs, string sent) =>
        Assert.Equal(sent, SyncNode.FanoutLine(line, false, 1_760_000_000_000, panicLeadMs));
}
