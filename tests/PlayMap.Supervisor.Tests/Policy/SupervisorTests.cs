using PlayMap.Core.Fleet;
using PlayMap.Supervisor.Policy;
using PlayMap.Supervisor.Status;

namespace PlayMap.Supervisor.Tests.Policy;

/// <summary><see cref="SupervisorCore"/> on a manual clock: the backoff, the freeze rule (and never on a slow HEALTH while frames move),
/// the rule inactive while the count is −1, a start and a rebuild exempt within the budget and frozen past it, the five-in-ten-minutes
/// stop, adoption, a person's hold and resume — each asserted by the action, the state and the status fields.</summary>
public sealed class SupervisorTests
{
    private const long S = 1_000_000_000;

    /// <summary>The supervisor's monotonic clock, in nanoseconds, moved by hand.</summary>
    private sealed class ManualClock(long now = 0)
    {
        public long Now { get; private set; } = now;

        public long Advance(long by)
        {
            Now += by;
            return Now;
        }
    }

    private static SupervisorFacts Session(bool alive) => new(true, alive, false, -1, false, GpuPhase.Unknown, 0, -1);

    private static SupervisorFacts Running(long frames, long since = 0, long epoch = 1, bool open = true) =>
        new(true, true, true, frames, open, GpuPhase.Running, since, epoch);

    [Fact]
    public void TheBackoffDoublesFromOneSecondToThirty()
    {
        var p = SupervisorPolicy.Default;
        Assert.Equal(new[] { 1, 2, 4, 8, 16, 30, 30 }, Enumerable.Range(0, 7).Select(i => (int)p.Backoff(i).TotalSeconds));
    }

    [Fact]
    public void NoSessionStartsNothingAndASessionStartsTheEngine()
    {
        var core = new SupervisorCore(SupervisorPolicy.Default);
        Assert.Equal(SupervisorAction.None, core.Step(0, new SupervisorFacts(false, false, false, -1, false, GpuPhase.Unknown, 0, -1)));
        Assert.Equal(SupervisorState.WaitingForSession, core.State);
        Assert.Equal(SupervisorAction.Start, core.Step(S, Session(false)));
        Assert.Equal(SupervisorState.Starting, core.State);
        Assert.Equal(SupervisorAction.None, core.Step(2 * S, Session(true)));
        Assert.Equal(SupervisorState.Running, core.State);
    }

    [Fact]
    public void AnEngineThatExitsIsRestartedAfterTheBackoffDoublingEachTime()
    {
        var core = new SupervisorCore(SupervisorPolicy.Default);
        var clock = new ManualClock();
        Assert.Equal(SupervisorAction.Start, core.Step(clock.Now, Session(false)));
        var starts = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(S / 2);
            Assert.Equal(SupervisorAction.None, core.Step(clock.Now, Session(false)));
            Assert.Equal(SupervisorState.Backoff, core.State);
            Assert.Equal("the engine exited", core.LastCause);
            var exited = clock.Now;
            while (core.Step(clock.Advance(S / 10), Session(false)) != SupervisorAction.Start)
                Assert.True(clock.Now - exited < 40 * S);
            starts.Add((clock.Now - exited) / (S / 10));
        }
        // 1 s, 2 s, 4 s, 8 s after each exit, to the tenth of a second the manual clock steps.
        Assert.Equal(new long[] { 10, 20, 40, 80 }, starts);
        Assert.Equal(4, core.Restarts);
    }

    [Fact]
    public void FiveRestartsInTenMinutesStopTheTryingUntilResume()
    {
        var core = new SupervisorCore(SupervisorPolicy.Default);
        var clock = new ManualClock();
        core.Step(clock.Now, Session(false));
        for (var i = 0; i < 5; i++)
        {
            core.Step(clock.Advance(S), Session(false));
            var waited = 0;
            while (core.Step(clock.Advance(S), Session(false)) != SupervisorAction.Start)
                Assert.True(++waited < 60);
        }
        Assert.Equal(5, core.Restarts);
        Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(S), Session(false)));
        Assert.Equal(SupervisorState.StoppedTooManyRestarts, core.State);
        Assert.Contains("5 restarts in 10 minutes: the supervisor stopped trying", core.LastCause, StringComparison.Ordinal);
        Assert.Equal("stopped-too-many-restarts", SupervisorStatusWords.Of(core, 0, 1).State);
        for (var i = 0; i < 100; i++)
            Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(S), Session(false)));
        Assert.True(core.Resume());
        Assert.Equal(0, core.Restarts);
        Assert.Equal(SupervisorAction.Start, core.Step(clock.Advance(S), Session(false)));
    }

    [Fact]
    public void RestartsOutsideTheWindowAreForgotten()
    {
        var core = new SupervisorCore(SupervisorPolicy.Default);
        var clock = new ManualClock();
        core.Step(clock.Now, Session(false));
        for (var i = 0; i < 12; i++)
        {
            // Each run lasts three minutes: never five exits within ten minutes.
            core.Step(clock.Advance(180 * S), Session(true));
            core.Step(clock.Advance(S), Session(false));
            var waited = 0;
            while (core.Step(clock.Advance(S), Session(false)) != SupervisorAction.Start)
                Assert.True(++waited < 60);
            Assert.NotEqual(SupervisorState.StoppedTooManyRestarts, core.State);
        }
        Assert.InRange(core.Restarts, 1, 4);
    }

    [Fact]
    public void ARunningEngineIsAdoptedNeverStartedTwice()
    {
        var core = new SupervisorCore(SupervisorPolicy.Default);
        Assert.Equal(SupervisorAction.None, core.Step(0, Session(true)));
        Assert.Equal(SupervisorState.Running, core.State);
        Assert.Equal(SupervisorAction.None, core.Step(S, Running(100)));
    }

    [Fact]
    public void ShownFramesThatStandStillWhileTheGpuRunsStopTheEngineThenTerminateAfterTheGrace()
    {
        var core = new SupervisorCore(SupervisorPolicy.Default);
        var clock = new ManualClock();
        core.Step(clock.Now, Session(true));
        var frames = 0L;
        for (var i = 0; i < 20; i++)
        {
            frames += 30;
            Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(S / 2), Running(frames)));
        }
        Assert.True(core.FreezeRuleActive);
        Assert.StartsWith("active: open outputs' shown frames may stand still 3 s", core.FreezeRule, StringComparison.Ordinal);
        var stopped = clock.Now;
        SupervisorAction a;
        while ((a = core.Step(clock.Advance(S / 2), Running(frames))) == SupervisorAction.None)
            Assert.True(clock.Now - stopped <= 3 * S);
        Assert.Equal(SupervisorAction.RequestStop, a);
        Assert.Equal(3 * S, clock.Now - stopped);
        Assert.Equal(SupervisorState.Stopping, core.State);
        Assert.Equal("frozen: shown frames stood at 600 for 3 s while the GPU ran", core.LastCause);
        // The grace: 5 s for the normal stop, then termination.
        for (var i = 0; i < 9; i++)
            Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(S / 2), Running(frames)));
        Assert.Equal(SupervisorAction.Terminate, core.Step(clock.Advance(S / 2), Running(frames)));
        // It ends: a restart after the backoff, the cause kept.
        Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(S / 2), Session(false)));
        Assert.Equal(SupervisorState.Backoff, core.State);
        Assert.Equal(1, core.Restarts);
        Assert.Equal("frozen: shown frames stood at 600 for 3 s while the GPU ran", core.LastCause);
    }

    [Fact]
    public void ASlowHealthWhileFramesMoveNeverStopsTheEngine()
    {
        var core = new SupervisorCore(SupervisorPolicy.Default);
        var clock = new ManualClock();
        core.Step(clock.Now, Session(true));
        var frames = 0L;
        for (var i = 0; i < 40; i++)
        {
            // HEALTH answers once in four steps (2 s apart, slower than the stale time's half), and frames move between answers.
            var answered = i % 4 == 0;
            if (answered)
                frames += 120;
            var facts = answered ? Running(frames) : new SupervisorFacts(true, true, false, -1, false, GpuPhase.Unknown, 0, -1);
            Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(S / 2), facts));
        }
        Assert.Equal(SupervisorState.Running, core.State);
    }

    [Fact]
    public void HealthSilentTenSecondsWithNoFramesMovingWhileTheGpuRanStopsTheEngine()
    {
        var core = new SupervisorCore(SupervisorPolicy.Default);
        var clock = new ManualClock();
        core.Step(clock.Now, Session(true));
        core.Step(clock.Advance(S / 2), Running(10));
        var silent = new SupervisorFacts(true, true, false, -1, false, GpuPhase.Unknown, 0, -1);
        var from = clock.Now;
        SupervisorAction a;
        while ((a = core.Step(clock.Advance(S / 2), silent)) == SupervisorAction.None)
            Assert.True(clock.Now - from <= 10 * S);
        Assert.Equal(SupervisorAction.RequestStop, a);
        Assert.Equal(10 * S, clock.Now - from);
        Assert.StartsWith("HEALTH silent for 10 s", core.LastCause, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRuleIsInactiveWhileTheCountIsUnknownAndTheStatusSaysSo()
    {
        var core = new SupervisorCore(SupervisorPolicy.Default);
        var clock = new ManualClock();
        core.Step(clock.Now, Session(true));
        for (var i = 0; i < 200; i++)
            Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(S / 2), Running(-1)));
        Assert.False(core.FreezeRuleActive);
        Assert.Equal("the engine reports no shown frames: the freeze rule waits for them", core.FreezeRule);
        Assert.Equal("the engine reports no shown frames: the freeze rule waits for them", SupervisorStatusWords.Of(core, 7, 1).FreezeRule);
        // Silence after a count that was never known stops nothing either.
        var silent = new SupervisorFacts(true, true, false, -1, false, GpuPhase.Unknown, 0, -1);
        for (var i = 0; i < 100; i++)
            Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(S / 2), silent));
    }

    [Fact]
    public void ClosedOutputsNeverReadAsFrozen()
    {
        var core = new SupervisorCore(SupervisorPolicy.Default);
        var clock = new ManualClock();
        core.Step(clock.Now, Session(true));
        for (var i = 0; i < 100; i++)
            Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(S / 2), Running(0, open: false)));
    }

    [Theory]
    [InlineData(GpuPhase.Starting, "starting")]
    [InlineData(GpuPhase.Rebuilding, "rebuilding")]
    public void AStartOrARebuildIsExemptWithinItsBudgetAndFrozenPastIt(GpuPhase phase, string word)
    {
        var core = new SupervisorCore(SupervisorPolicy.Default);
        var clock = new ManualClock(10 * S);
        core.Step(clock.Now, Session(true));
        var since = clock.Now;
        // 30 s while the first good frame is unmeasured: no stop at 30 s, a stop past it.
        while (clock.Now - since < 30 * S)
            Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(S / 2), new SupervisorFacts(true, true, true, 0, true, phase, since, 2)));
        Assert.Equal("exempt while " + word + ", up to 30 s (the rebuild's first good frame is unmeasured on a real node)", core.FreezeRule);
        Assert.Equal(SupervisorAction.RequestStop, core.Step(clock.Advance(S / 2), new SupervisorFacts(true, true, true, 0, true, phase, since, 2)));
        Assert.Equal($"the GPU stayed {word} for 30.5 s, past its budget of 30 s (the rebuild's first good frame is unmeasured on a real node)", core.LastCause);
    }

    [Fact]
    public void AMeasuredFirstGoodFrameSetsTheStaleTimeAndTheBudgetToFourOfIt()
    {
        var p = SupervisorPolicy.Default with { FirstGoodFrame = TimeSpan.FromSeconds(2) };
        Assert.Equal(TimeSpan.FromSeconds(8), p.StaleTime);
        Assert.Equal(TimeSpan.FromSeconds(8), p.PhaseBudget);
        Assert.Equal(TimeSpan.FromSeconds(3), (SupervisorPolicy.Default with { FirstGoodFrame = TimeSpan.FromMilliseconds(500) }).StaleTime);
        var core = new SupervisorCore(p);
        var clock = new ManualClock();
        core.Step(clock.Now, Session(true));
        core.Step(clock.Advance(S / 2), Running(5));
        var from = clock.Now;
        while (core.Step(clock.Advance(S / 2), Running(5)) == SupervisorAction.None)
            Assert.True(clock.Now - from <= 8 * S);
        Assert.Equal(8 * S, clock.Now - from);
    }

    [Fact]
    public void ANewEpochAfterARebuildStartsTheCountAgain()
    {
        var core = new SupervisorCore(SupervisorPolicy.Default);
        var clock = new ManualClock();
        core.Step(clock.Now, Session(true));
        core.Step(clock.Advance(S / 2), Running(500, epoch: 1));
        core.Step(clock.Advance(S / 2), new SupervisorFacts(true, true, true, 0, true, GpuPhase.Rebuilding, clock.Now, 2));
        // The new device's count starts at 0 and moves: no stop, though it is lower than the old device's.
        for (var i = 1; i <= 20; i++)
            Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(S / 2), Running(i * 30, epoch: 2)));
        Assert.Equal(SupervisorState.Running, core.State);
    }

    [Fact]
    public void APersonsHoldStopsTheEngineAndNothingStartsItUntilResume()
    {
        var core = new SupervisorCore(SupervisorPolicy.Default);
        var clock = new ManualClock();
        core.Step(clock.Now, Session(true));
        Assert.Equal(SupervisorAction.RequestStop, core.Hold(clock.Advance(S), "stopped by a person"));
        Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(S), Session(false)));
        Assert.Equal(SupervisorState.Held, core.State);
        Assert.Equal(0, core.Restarts);
        for (var i = 0; i < 100; i++)
            Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(S), Session(false)));
        Assert.Equal("held", SupervisorStatusWords.Of(core, 0, 1).State);
        Assert.True(core.Resume());
        Assert.False(core.Resume());
        Assert.Equal(SupervisorAction.Start, core.Step(clock.Advance(S), Session(false)));
    }

    [Theory]
    // A stop pending: the event stays set until the engine is gone, whatever the step answered, so an engine that looks at it
    // between steps, or starts waiting after it was set, still sees it.
    [InlineData(SupervisorAction.RequestStop, SupervisorState.Stopping, true, false)]
    [InlineData(SupervisorAction.None, SupervisorState.Stopping, true, false)]
    [InlineData(SupervisorAction.Terminate, SupervisorState.Stopping, true, false)]
    [InlineData(SupervisorAction.None, SupervisorState.Stopping, false, false)]
    // An engine running with no stop pending: nothing to clear.
    [InlineData(SupervisorAction.None, SupervisorState.Running, true, false)]
    // No engine and no stop pending: cleared; and always before a start, so a new engine never sees an old stop.
    [InlineData(SupervisorAction.None, SupervisorState.Backoff, false, true)]
    [InlineData(SupervisorAction.None, SupervisorState.Held, false, true)]
    [InlineData(SupervisorAction.Start, SupervisorState.Starting, false, true)]
    public void TheStopEventIsClearedOnlyWhenNoStopIsPending(SupervisorAction action, SupervisorState state, bool alive, bool reset) =>
        Assert.Equal(reset, SupervisorCore.ShouldResetStopEvent(action, state, alive));

    [Theory]
    [InlineData("S-1-5-18", true)]
    [InlineData("S-1-5-32-544", true)]
    [InlineData("S-1-5-21-1000-2000-3000-1001", false)]
    [InlineData("S-1-1-0", false)]
    [InlineData("", false)]
    public void AStopEventThatExistsAlreadyIsKeptOnlyFromSystemOrAdministrators(string owner, bool kept) =>
        Assert.Equal(kept, PlayMap.Supervisor.Install.MachinePlan.StopEventOwnerTrusted(owner));

    [Fact]
    public void AStopAskedOfARunningEngineStaysSetThroughEveryStepOfItsGrace()
    {
        var core = new SupervisorCore(SupervisorPolicy.Default);
        var clock = new ManualClock();
        core.Step(clock.Now, Session(false));
        core.Step(clock.Advance(S), Session(true));
        var asked = core.Hold(clock.Advance(S), "stopped by a person");
        Assert.Equal(SupervisorAction.RequestStop, asked);
        Assert.False(SupervisorCore.ShouldResetStopEvent(asked, core.State, true));
        // Every half-second step of the grace, the engine still alive: never cleared.
        for (var i = 0; i < 9; i++)
        {
            var action = core.Step(clock.Advance(S / 2), Session(true));
            Assert.Equal(SupervisorAction.None, action);
            Assert.False(SupervisorCore.ShouldResetStopEvent(action, core.State, true));
        }
        // The engine stopped within its grace: held, and the event cleared for the next engine.
        var after = core.Step(clock.Advance(S / 2), Session(false));
        Assert.Equal(SupervisorState.Held, core.State);
        Assert.True(SupervisorCore.ShouldResetStopEvent(after, core.State, false));
    }
}
