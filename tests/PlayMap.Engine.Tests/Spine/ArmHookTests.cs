using System.Text.Json;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Fleet;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>The arm hooks on a clock the test moves: SHOW ARM arms at once, runs every registered hook and answers once each has, naming
/// what each did; a hook past its deadline is cancelled and named with words (one that heeds its token and one that does not), one that
/// fails is named with its words, and the show is armed either way; DISARM runs the hooks' disarming half; a node with no hook answers
/// as it always has.</summary>
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
}
