using PlayMap.Core.Fleet;
using PlayMap.Supervisor.Policy;
using PlayMap.Supervisor.Status;

namespace PlayMap.Supervisor.Tests.Policy;

/// <summary><see cref="SupervisorCore"/> on a manual clock: the backoff, the freeze rule (never on a HEALTH that answers at least once in
/// every 10 s of on-time steps while frames move), HEALTH silent counted only over steps the supervisor ran on time (a lost answer from a
/// step that ran late, or whose ask returned late, is the supervisor held, at any length and however the holds add up; a truly silent
/// engine is still stopped after 10 s of on-time steps), the rule inactive while the count is −1, a start and a rebuild exempt within
/// the budget and frozen past it, the five-in-ten-minutes stop, adoption, a person's hold and resume — each asserted by the action, the
/// state and the status fields.</summary>
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

    private const long Ms = 1_000_000;

    /// <summary>The supervisor's margin and its ask's deadline, as the core states them.</summary>
    private const long Margin = 500 * Ms;
    private const long Deadline = 400 * Ms;

    /// <summary>A step whose HEALTH ask was lost (no answer), the step reaching its ask <paramref name="late"/> ns after it was due and
    /// the ask taking <paramref name="ask"/> ns (−1: no ask made).</summary>
    private static SupervisorFacts Lost(long late = 0, long ask = -1) => new(true, true, false, -1, false, GpuPhase.Unknown, 0, -1, late, ask);

    /// <summary>An adopted engine answered with moving frames every half second for 10 s: the freeze rule watches, the last answer at
    /// 10 s.</summary>
    private static (SupervisorCore Core, ManualClock Clock) TenSecondsOfAnswers(bool open = true)
    {
        var core = new SupervisorCore(SupervisorPolicy.Default);
        var clock = new ManualClock();
        core.Step(clock.Now, Session(true));
        var frames = 0L;
        for (var i = 0; i < 20; i++)
        {
            if (open)
                frames += 30;
            Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(S / 2), Running(frames, open: open)));
        }
        Assert.True(core.FreezeRuleActive);
        Assert.Equal(10 * S, clock.Now);
        return (core, clock);
    }

    [Theory]
    // A single hold of H ms after 10 s of answers, the step's ask lost: before the step reached its ask (the step ran late) or inside
    // the ask (it ended past its deadline). Counted as silence, the last three would read 10 s or more and stop the engine.
    [InlineData(9_400, false)]
    [InlineData(9_500, false)]
    [InlineData(12_000, false)]
    [InlineData(20_000, false)]
    [InlineData(9_400, true)]
    [InlineData(9_500, true)]
    [InlineData(12_000, true)]
    [InlineData(20_000, true)]
    public void ALostAskFromAHeldStepIsTheSupervisorHeldNeverTheEngineSilent(int holdMs, bool insideTheAsk)
    {
        var (core, clock) = TenSecondsOfAnswers();
        var hold = holdMs * Ms;
        var facts = insideTheAsk ? Lost(late: 3 * Ms, ask: Deadline + hold) : Lost(late: hold, ask: Deadline);
        Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(S / 2 + hold), facts));
        Assert.Equal(SupervisorState.Running, core.State);
        Assert.Equal("", core.LastCause);
        Assert.True(core.LastStepHeld);
        Assert.Equal(1, core.HeldAsks);
        Assert.StartsWith("the supervisor was held: ", core.LastHeld, StringComparison.Ordinal);
        Assert.EndsWith(": the lost HEALTH answer is not counted as the engine's silence", core.LastHeld, StringComparison.Ordinal);
        // The engine answers again: watched as before, nothing stopped.
        for (var i = 1; i <= 10; i++)
            Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(S / 2), Running(600 + i * 30)));
        Assert.False(core.LastStepHeld);
        Assert.Equal(SupervisorState.Running, core.State);
    }

    [Theory]
    // The control: the same hold on a step whose ask was answered, frames moved. The stale rule compares answered counts, each made
    // after the one before, so no hold exposes it.
    [InlineData(9_400)]
    [InlineData(12_000)]
    [InlineData(20_000)]
    public void AHeldStepWhoseAskWasAnsweredWithMovingFramesStopsNothing(int holdMs)
    {
        var (core, clock) = TenSecondsOfAnswers();
        Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(S / 2 + holdMs * Ms), Running(630) with { LateNs = holdMs * Ms, AskNs = 2 * Ms }));
        for (var i = 1; i <= 10; i++)
            Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(S / 2), Running(630 + i * 30)));
        Assert.Equal(SupervisorState.Running, core.State);
        Assert.False(core.LastStepHeld);
    }

    [Fact]
    public void HoldsThatAddUpAreNeverTheEngineSilentAndOnTimeSilenceAfterThemIsCountedFromThem()
    {
        var (core, clock) = TenSecondsOfAnswers();
        // Two holds of 5 s, each lost: 10 s since the last answer, none of it counted.
        Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(5 * S), Lost(late: 5 * S - S / 2, ask: Deadline)));
        Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(5 * S), Lost(late: 3 * Ms, ask: Deadline + 5 * S - S / 2)));
        Assert.Equal(SupervisorState.Running, core.State);
        Assert.Equal(2, core.HeldAsks);
        // Then the supervisor runs on time and the engine stays silent: stopped once 10 s of that silence are counted, no sooner.
        var from = clock.Now;
        SupervisorAction a;
        while ((a = core.Step(clock.Advance(S / 2), Lost(late: 20 * Ms, ask: Deadline + 15 * Ms))) == SupervisorAction.None)
            Assert.True(clock.Now - from < 10 * S);
        Assert.Equal(SupervisorAction.RequestStop, a);
        Assert.Equal(10 * S, clock.Now - from);
        Assert.Equal(
            "HEALTH silent for 10 s of on-time steps, beside 10 s the supervisor was held (the last answer, 20 s before, said the GPU ran; no shown frames were read since)",
            core.LastCause);
    }

    [Fact]
    public void AHoldBetweenOnTimeSilentStepsNeitherAddsToTheSilenceNorClearsWhatWasCounted()
    {
        var (core, clock) = TenSecondsOfAnswers();
        // 6 s of on-time silence, counted.
        for (var i = 0; i < 12; i++)
            Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(S / 2), Lost(late: 20 * Ms, ask: Deadline + 15 * Ms)));
        // One held lost step of 5 s: its time is the supervisor's, and the 6 s already counted stand.
        Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(5 * S), Lost(late: 5 * S - S / 2, ask: Deadline)));
        Assert.True(core.LastStepHeld);
        // On time and silent again: stopped once 10 s of on-time silence are counted, 4 s after the hold, no sooner.
        var from = clock.Now;
        SupervisorAction a;
        while ((a = core.Step(clock.Advance(S / 2), Lost(late: 20 * Ms, ask: Deadline + 15 * Ms))) == SupervisorAction.None)
            Assert.True(clock.Now - from < 4 * S);
        Assert.Equal(SupervisorAction.RequestStop, a);
        Assert.Equal(4 * S, clock.Now - from);
        Assert.Equal(1, core.HeldAsks);
        Assert.Equal(
            "HEALTH silent for 10 s of on-time steps, beside 5 s the supervisor was held (the last answer, 15 s before, said the GPU ran; no shown frames were read since)",
            core.LastCause);
    }

    [Fact]
    public void ALostAskFromAHeldStepWithEveryOutputClosedStopsNothing()
    {
        var (core, clock) = TenSecondsOfAnswers(open: false);
        Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(S / 2 + 9_500 * Ms), Lost(late: 9_500 * Ms, ask: Deadline)));
        Assert.Equal(SupervisorState.Running, core.State);
        Assert.True(core.LastStepHeld);
    }

    [Fact]
    public void AnEngineSilentWhileTheSupervisorRunsOnTimeIsStoppedAfterTenSecondsOfItsSilence()
    {
        // The readings of a supervisor that runs: each step reaches its ask 20 ms after it was due, and each lost ask ends 15 ms after its
        // deadline.
        var (core, clock) = TenSecondsOfAnswers();
        var from = clock.Now;
        SupervisorAction a;
        while ((a = core.Step(clock.Advance(S / 2), Lost(late: 20 * Ms, ask: Deadline + 15 * Ms))) == SupervisorAction.None)
            Assert.True(clock.Now - from < 10 * S);
        Assert.Equal(SupervisorAction.RequestStop, a);
        Assert.Equal(10 * S, clock.Now - from);
        Assert.False(core.LastStepHeld);
        Assert.Equal(0, core.HeldAsks);
        Assert.Equal("HEALTH silent for 10 s of on-time steps (the last answer, 10 s before, said the GPU ran; no shown frames were read since)", core.LastCause);
    }

    [Theory]
    // The margin: a step that reaches its ask up to the margin after it was due, or whose ask ends up to the margin past its deadline,
    // is the supervisor on time; a nanosecond more is the supervisor held. A step with no ask made is judged by its lateness alone.
    [InlineData(Margin, Deadline, true)]
    [InlineData(Margin + 1, Deadline, false)]
    [InlineData(0, Deadline + Margin, true)]
    [InlineData(0, Deadline + Margin + 1, false)]
    [InlineData(Margin, -1, true)]
    [InlineData(Margin + 1, -1, false)]
    public void TheMarginDividesTheSupervisorOnTimeFromTheSupervisorHeld(long late, long ask, bool stops)
    {
        var (core, clock) = TenSecondsOfAnswers();
        var stopped = false;
        for (var i = 0; i < 40 && !stopped; i++)
            stopped = core.Step(clock.Advance(S / 2), Lost(late, ask)) == SupervisorAction.RequestStop;
        Assert.Equal(stops, stopped);
        Assert.Equal(!stops, core.LastStepHeld);
    }

    [Fact]
    public void TheMarginAndTheDeadlineAreTheCoresOwn()
    {
        Assert.Equal(TimeSpan.FromTicks(Margin / 100), SupervisorCore.HeldMargin);
        Assert.Equal(TimeSpan.FromTicks(Deadline / 100), SupervisorCore.AskDeadline);
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
    public void ANodeWithNoOpenOutputAndNoGpuPhaseSaysTheRuleWaitsForThePhase()
    {
        // HEALTH answers, no output is open (its shown frames read 0) and the GPU's phase is not reported: the rule waits, in the words
        // the engine's preflight reads as information.
        var core = new SupervisorCore(SupervisorPolicy.Default);
        var clock = new ManualClock();
        core.Step(clock.Now, Session(true));
        for (var i = 0; i < 20; i++)
            Assert.Equal(SupervisorAction.None, core.Step(clock.Advance(S / 2), new SupervisorFacts(true, true, true, 0, false, GpuPhase.Unknown, 0, -1)));
        Assert.False(core.FreezeRuleActive);
        Assert.Equal("the engine reports no GPU phase: the freeze rule waits for it", SupervisorStatusWords.Of(core, 7, 1).FreezeRule);
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
    public void AStopEventIsUsedOnlyWhenSystemOrAdministratorsMadeIt(string owner, bool kept) =>
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

    [Fact]
    public void TheSupervisorCardListsEveryStateTheStatusFileSays()
    {
        // The desk's Supervisor card (tests/fixtures/state/install.cards.json, written by the engine's fixture test) lights each state the
        // supervisor writes, and no other: the block's light and the preflight row are held to the card's for each of them.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        using var cards = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "tests", "fixtures", "state", "install.cards.json")));
        var light = cards.RootElement.EnumerateArray().Single(c => c.GetProperty("title").GetString() == "Supervisor").GetProperty("light");
        var listed = new[] { "red", "amber", "green" }.SelectMany(w => light.GetProperty(w).EnumerateArray().Select(e => e.GetString()!)).Order(StringComparer.Ordinal);
        var written = Enum.GetValues<SupervisorState>().Select(SupervisorStatusWords.Word).Order(StringComparer.Ordinal);
        Assert.Equal(written, listed);
    }
}
