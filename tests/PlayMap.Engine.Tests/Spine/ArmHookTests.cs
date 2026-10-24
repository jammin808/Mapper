using System.Collections.Immutable;
using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Fleet;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>The arm hooks on a clock the test moves: SHOW ARM arms at once, runs every registered hook and answers once each has, naming
/// what each did; a hook past its deadline is cancelled and named with words (one that heeds its token and one that does not), one that
/// fails is named with its words, and the show is armed either way; a deadline past the ceiling, an infinite one or one that cannot be read
/// is held to the ceiling or named as failed, never faulting the answer; DISARM runs the hooks' disarming half; a node with no hook answers
/// as it always has; ALL sends the line to the group before this node's hooks end and names them beside the members' receipts; a line from
/// the sync leader is answered once the mode has changed, its hooks run behind the answer and journalled; a node that comes back armed
/// after a restart runs its hooks once, each journalled.</summary>
public sealed class ArmHookTests
{
    private static readonly SessionInfo Desk = SessionInfo.Desk("t");

    private static Task<CommandReply> Run(EngineCore engine, string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return engine.HandleAsync(parsed, Desk, CancellationToken.None).AsTask();
    }

    /// <summary>A hook a test drives: it says when it started, and either answers, waits on its token, ignores it, or throws.</summary>
    private sealed class Hook(string name, TimeSpan deadline, Func<CancellationToken, Task<string>> arming, string disarming = "") : IShowArmHook
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token;

        public string Name => name;

        public TimeSpan Deadline => deadline;

        public Task<string> ArmingAsync(CancellationToken ct)
        {
            Token = ct;
            Started.TrySetResult();
            return arming(ct);
        }

        public Task<string> DisarmingAsync(CancellationToken ct) => Task.FromResult(disarming);
    }

    private static async Task<string> UntilCancelled(CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return "never";
    }

    [Fact]
    public async Task AHookPastItsDeadlineIsCancelledAndNamedAndTheShowArmsEitherWay()
    {
        var time = new ManualTime();
        var ignored = new TaskCompletionSource<string>();
        var plans = new Hook("plans", TimeSpan.FromSeconds(5), _ => Task.FromResult("12 plans compiled"));
        var pipelines = new Hook("pipelines", TimeSpan.FromSeconds(2), UntilCancelled);
        var stubborn = new Hook("stubborn", TimeSpan.FromSeconds(1), _ => ignored.Task);
        var broken = new Hook("broken", TimeSpan.FromSeconds(1), _ => throw new InvalidOperationException("no device"));
        await using var engine = new EngineCore(EngineServices.ForTests() with { ArmHooks = [plans, pipelines, stubborn, broken], ArmTimers = time });

        var arming = Run(engine, "SHOW ARM");
        await pipelines.Started.Task;
        await stubborn.Started.Task;

        // Armed before any hook answers: armed is the safe side, and no hook holds it back.
        Assert.True(engine.ShowNow.Armed);
        Assert.False(arming.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(arming.IsCompleted);
        Assert.False(pipelines.Token.IsCancellationRequested);
        time.Advance(TimeSpan.FromSeconds(1));
        var reply = await arming;

        Assert.True(reply.Ok, reply.Payload);
        Assert.True(pipelines.Token.IsCancellationRequested);
        Assert.True(stubborn.Token.IsCancellationRequested);
        using var doc = JsonDocument.Parse(reply.Payload!);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("armed").GetBoolean());
        var hooks = root.GetProperty("hooks").EnumerateArray()
            .Select(h => (h.GetProperty("name").GetString(), h.GetProperty("outcome").GetString(), h.GetProperty("words").GetString())).ToList();
        Assert.Equal(
        [
            ("plans", "done", "12 plans compiled"),
            ("pipelines", "cancelled", "missed its 2000 ms deadline and was cancelled: the show is armed without it"),
            ("stubborn", "cancelled", "missed its 1000 ms deadline and was cancelled: the show is armed without it"),
            ("broken", "failed", "failed (no device): the show is armed without it"),
        ], hooks);
        Assert.StartsWith("armed by Desk · plans: 12 plans compiled; pipelines: missed its 2000 ms deadline", reply.Effect, StringComparison.Ordinal);
        Assert.Contains("broken: failed (no device)", reply.Effect, StringComparison.Ordinal);
        Assert.True(engine.ShowNow.Armed);
        Assert.Contains(engine.Journal.Recent(engine.Journal.RecentCapacity), r => r.Text.Contains("stubborn: missed its 1000 ms deadline", StringComparison.Ordinal));

        // A hook that heeds nothing runs on unawaited; its end changes nothing.
        ignored.SetResult("late");
        Assert.True(engine.ShowNow.Armed);
    }

    [Fact]
    public async Task DisarmRunsTheDisarmingHalfAndNamesTheHooksThatSaySomething()
    {
        var time = new ManualTime();
        var gc = new Hook("gc", TimeSpan.FromSeconds(1), _ => Task.FromResult("sustained low latency"), "the collector's own mode again");
        var plans = new Hook("plans", TimeSpan.FromSeconds(1), _ => Task.FromResult("3 plans compiled"));
        await using var engine = new EngineCore(EngineServices.ForTests() with { ArmHooks = [gc, plans], ArmTimers = time });
        Assert.True((await Run(engine, "SHOW ARM")).Ok);

        var reply = await Run(engine, "SHOW DISARM");

        Assert.True(reply.Ok, reply.Payload);
        Assert.False(engine.ShowNow.Armed);
        Assert.Equal("disarmed by Desk · gc: the collector's own mode again", reply.Effect);
        using var doc = JsonDocument.Parse(reply.Payload!);
        Assert.Equal(new[] { "gc", "plans" }, doc.RootElement.GetProperty("hooks").EnumerateArray().Select(h => h.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task ANodeWithNoHookAnswersAsItAlwaysHasAndARefusedArmRunsNoHook()
    {
        await using (var bare = new EngineCore(EngineServices.ForTests()))
        {
            var reply = await Run(bare, "SHOW ARM");
            Assert.True(reply.Ok);
            Assert.Equal("armed by Desk", reply.Effect);
            using var doc = JsonDocument.Parse(reply.Payload!);
            Assert.False(doc.RootElement.TryGetProperty("hooks", out _));
        }

        var runs = 0;
        var counted = new Hook("counted", TimeSpan.FromSeconds(1), _ => Task.FromResult($"run {++runs}"));
        await using var engine = new EngineCore(EngineServices.ForTests() with { ArmHooks = [counted], ArmTimers = new ManualTime() });
        Assert.True((await Run(engine, "SHOW ARM")).Ok);
        var again = await Run(engine, "SHOW ARM");
        Assert.False(again.Ok);
        Assert.StartsWith("already armed by Desk", again.Payload, StringComparison.Ordinal);
        Assert.Equal(1, runs);
    }

    /// <summary>A hook whose deadline cannot be read.</summary>
    private sealed class UnreadableDeadline : IShowArmHook
    {
        public string Name => "unreadable";

        public TimeSpan Deadline => throw new InvalidOperationException("no deadline");

        public Task<string> ArmingAsync(CancellationToken ct) => Task.FromResult("never asked");
    }

    private static List<(string? Name, string? Outcome, string? Words)> Hooks(JsonElement root) =>
        [.. root.GetProperty("hooks").EnumerateArray()
            .Select(h => (h.GetProperty("name").GetString(), h.GetProperty("outcome").GetString(), h.GetProperty("words").GetString()))];

    [Fact]
    public async Task ADeadlinePastTheCeilingIsHeldToItAndOneThatCannotBeReadIsNamedAsFailed()
    {
        var time = new ManualTime();
        var forever = new Hook("forever", TimeSpan.MaxValue, _ => Task.FromResult("ready"));
        var infinite = new Hook("infinite", Timeout.InfiniteTimeSpan, UntilCancelled);
        await using var engine = new EngineCore(EngineServices.ForTests() with { ArmHooks = [forever, infinite, new UnreadableDeadline()], ArmTimers = time });

        var arming = Run(engine, "SHOW ARM");
        await infinite.Started.Task;
        Assert.True(engine.ShowNow.Armed);
        time.Advance(EngineCore.MaxArmHookDeadline - TimeSpan.FromSeconds(1));
        Assert.False(arming.IsCompleted);
        Assert.False(infinite.Token.IsCancellationRequested);
        time.Advance(TimeSpan.FromSeconds(1));
        var reply = await arming;

        Assert.True(reply.Ok, reply.Payload);
        Assert.True(engine.ShowNow.Armed);
        using var doc = JsonDocument.Parse(reply.Payload!);
        Assert.True(doc.RootElement.GetProperty("armed").GetBoolean());
        Assert.Equal(
        [
            ("forever", "done", "ready"),
            ("infinite", "cancelled", "missed its 300000 ms deadline and was cancelled: the show is armed without it"),
            ("unreadable", "failed", "failed (no deadline): the show is armed without it"),
        ], Hooks(doc.RootElement));
    }

    /// <summary>The group of one member: the line it was sent, and whether this node's hooks had ended by then.</summary>
    private sealed class OneMember(Func<bool> hooksEnded) : IFleetFanout
    {
        public readonly TaskCompletionSource<bool> Sent = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> Members => ["nb"];

        public Task<IReadOnlyList<FanoutReceipt>> SendAsync(string line, long atLeaderNs, CancellationToken ct)
        {
            Sent.TrySetResult(hooksEnded());
            return Task.FromResult<IReadOnlyList<FanoutReceipt>>([new FanoutReceipt("nb", "FOH-B", "applied", atLeaderNs, "armed by Lead")]);
        }
    }

    [Fact]
    public async Task AllSendsTheLineToTheGroupBeforeThisNodesHooksEndAndNamesThemBesideTheReceipts()
    {
        var time = new ManualTime();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var plans = new Hook("plans", TimeSpan.FromSeconds(30), _ => release.Task);
        var group = new OneMember(() => release.Task.IsCompleted);
        await using var engine = new EngineCore(EngineServices.ForTests() with { ArmHooks = [plans], ArmTimers = time, Fanout = group });

        var arming = Run(engine, "SHOW ARM ALL");

        Assert.False(await group.Sent.Task.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.True(engine.ShowNow.Armed);
        release.SetResult("12 plans compiled");
        var reply = await arming;

        Assert.True(reply.Ok, reply.Payload);
        using var doc = JsonDocument.Parse(reply.Payload!);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("members").GetInt32());
        var nodes = root.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(["applied", "applied"], nodes.Select(n => n.GetProperty("stage").GetString()));
        Assert.Equal("armed by Desk · plans: 12 plans compiled", nodes[0].GetProperty("words").GetString());
        Assert.Equal([("plans", "done", "12 plans compiled")], Hooks(root));
    }

    [Fact]
    public async Task ALineFromTheSyncLeaderIsAnsweredOnceTheModeHasChangedAndItsHooksRunBehindTheAnswer()
    {
        var time = new ManualTime();
        var slow = new Hook("pipelines", TimeSpan.FromSeconds(3), UntilCancelled);
        await using var engine = new EngineCore(EngineServices.ForTests() with { ArmHooks = [slow], ArmTimers = time });
        var leader = new SessionInfo("sync:nd_lead", EngineCore.LeaderTransport, "10.0.0.1", false, "FOH-A (sync leader)", true);
        Assert.True(WireGrammar.TryParse("SHOW ARM", out var line, out _));

        var reply = await engine.HandleAsync(line!, leader, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(reply.Ok, reply.Payload);
        Assert.True(engine.ShowNow.Armed);
        await slow.Started.Task;
        Assert.False(slow.Token.IsCancellationRequested);
        using (var doc = JsonDocument.Parse(reply.Payload!))
            Assert.Equal([("pipelines", "running", "runs behind this answer: its outcome is journalled on this node")], Hooks(doc.RootElement));
        time.Advance(TimeSpan.FromSeconds(3));
        var row = await Journalled(engine, r => r.Text.Contains("pipelines", StringComparison.Ordinal) && r.Text.Contains("deadline", StringComparison.Ordinal));
        Assert.Equal("show", row.Kind);
        Assert.Equal("SHOW ARM's arm hook pipelines: missed its 3000 ms deadline and was cancelled: the show is armed without it", row.Text);
        Assert.False(row.Ok);
    }

    [Fact]
    public async Task ANodeThatComesBackArmedRunsItsHooksOnceAndJournalsEach()
    {
        var runs = 0;
        var plans = new Hook("plans", TimeSpan.FromSeconds(5), _ => Task.FromResult($"compiled {Interlocked.Increment(ref runs)}"));
        await using var engine = new EngineCore(EngineServices.ForTests() with { ArmHooks = [plans], ArmTimers = new ManualTime() });
        var record = new RecoveryRecord(0, null, ProjectJson.Serialize(Project.CreateDefault("Back")), false, null, null, [])
        {
            Modules = ImmutableSortedDictionary<string, string>.Empty.Add("show", "{\"armed\":true,\"by\":\"Desk\",\"since\":1,\"words\":\"doors\"}"),
        };
        await engine.RestoreAsync(record);
        Assert.True(engine.ShowNow.Armed);
        Assert.Equal(0, runs);

        var results = await engine.ArmAgainAsync(CancellationToken.None);

        Assert.Equal(new[] { new ArmHookResult("plans", "done", "compiled 1") }, results.ToArray());
        Assert.Equal(1, runs);
        var rows = engine.Journal.Recent(engine.Journal.RecentCapacity).Where(r => r.Kind == "recovery" && r.Text.Contains("plans", StringComparison.Ordinal)).ToList();
        Assert.Equal("armed again: plans — done: compiled 1", Assert.Single(rows).Text);
        // Once: a second call, or a node that did not come back armed, runs nothing.
        Assert.Empty(await engine.ArmAgainAsync(CancellationToken.None));
        Assert.Equal(1, runs);
    }

    private static async Task<JournalRow> Journalled(EngineCore engine, Func<JournalRow, bool> match)
    {
        for (var tries = 0; tries < 2000; tries++)
        {
            if (engine.Journal.Recent(engine.Journal.RecentCapacity).FirstOrDefault(match) is { } row)
                return row;
            await Task.Delay(10);
        }
        throw new Xunit.Sdk.XunitException("the row was never journalled");
    }
}
