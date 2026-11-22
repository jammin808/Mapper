using System.Globalization;
using PlayMap.Core.Fleet;

namespace PlayMap.Supervisor.Policy;

/// <summary>What the supervisor is doing about the engine.</summary>
public enum SupervisorState
{
    /// <summary>No show user's session to start the engine in.</summary>
    WaitingForSession,
    /// <summary>An engine was started and is coming up.</summary>
    Starting,
    /// <summary>An engine runs (started or adopted).</summary>
    Running,
    /// <summary>The engine was asked to stop (frozen, or a person asked); it is terminated after the grace.</summary>
    Stopping,
    /// <summary>The engine stopped; the next start waits out the backoff.</summary>
    Backoff,
    /// <summary>Five restarts in ten minutes: nothing is started until <c>resume</c>.</summary>
    StoppedTooManyRestarts,
    /// <summary>A person stopped the engine: nothing is started until <c>resume</c>.</summary>
    Held,
}

/// <summary>What the supervisor does after a step.</summary>
public enum SupervisorAction
{
    None,
    /// <summary>Start the engine in the show user's session.</summary>
    Start,
    /// <summary>Set the engine's stop event: a normal stop.</summary>
    RequestStop,
    /// <summary>End the engine's process: the grace has passed.</summary>
    Terminate,
}

/// <summary>What the supervisor saw at one step. <see cref="ShownFrames"/> is the sum of the open outputs' shown-frame counts by
/// HEALTH (−1 unknown: an engine that reports none, or a HEALTH not answered); <see cref="GpuPhaseSinceNs"/> is the phase's start on
/// the supervisor's own clock. <see cref="LateNs"/> is how long after the step was due (one tick after the step before began) it
/// reached its HEALTH ask, and <see cref="AskNs"/> how long that ask took to return (−1: no ask was made), both on the supervisor's own
/// monotonic clock: the two readings by which the supervisor tells its own hold from the engine's silence
/// (<see cref="SupervisorCore.WasHeld"/>).</summary>
public readonly record struct SupervisorFacts(
    bool ShowSessionActive,
    bool EngineAlive,
    bool HealthAnswered,
    long ShownFrames,
    bool OutputsOpen,
    GpuPhase GpuPhase,
    long GpuPhaseSinceNs,
    long GpuEpoch,
    long LateNs = 0,
    long AskNs = -1);

/// <summary>Pure: given the time on a monotonic clock (ns) and what it observed, what the supervisor does next. It restarts an engine
/// that exits with backoff, stops trying after too many restarts in the window, and stops an engine whose open outputs' shown frames
/// have stood still for the stale time while HEALTH says the GPU runs, or whose HEALTH went unanswered for <c>HealthSilent</c> (10 s)
/// of steps the supervisor ran on time after an answer that said the GPU ran — never while the count is unknown, never during a start
/// or a rebuild within its budget, never for a HEALTH that answers at least once in every 10 s of on-time steps. A lost answer from a
/// step the supervisor ran late, or whose ask returned late, is the supervisor held (<see cref="WasHeld"/>): it is counted neither as
/// the engine's silence nor toward a stop. The cost: on a machine kept so busy that the supervisor is held at every step, a truly
/// frozen engine is caught only once the supervisor has run on time for 10 s. Every stop and restart keeps its cause in words, which
/// name only what an answer said.</summary>
public sealed class SupervisorCore
{
    private const long NsPerTick = 100;

    private readonly SupervisorPolicy _policy;
    private readonly Queue<long> _restarts = new();
    private int _failures;
    private long _startedAtNs = -1;
    private long _dueNs;
    private long _stopAtNs;
    private long _lastFrames = -1;
    private long _framesMovedNs;
    private long _lastEpoch = long.MinValue;
    private long _lastAnswerNs = -1;
    private long _lastStepNs;
    private long _silentNs;
    private long _heldNs;
    private GpuPhase _lastPhase = GpuPhase.Unknown;
    private SupervisorState _afterStop = SupervisorState.Backoff;

    public SupervisorCore(SupervisorPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
    }

    public SupervisorState State { get; private set; } = SupervisorState.WaitingForSession;

    /// <summary>How long the supervisor's HEALTH ask waits for its answer before it is silence (the host arms it at every ask).</summary>
    public static readonly TimeSpan AskDeadline = TimeSpan.FromMilliseconds(400);

    /// <summary>How long after its due time a step may reach its ask, and how long past <see cref="AskDeadline"/> a lost ask may
    /// return, and still be a supervisor that ran on time: one tick of the step loop (500 ms), since a step more than a tick late
    /// missed a tick; and, for the ask, the same number as a generous bound, since an ask cancelled at its deadline returns within about
    /// one timer period of it on a supervisor that runs. Set equal to a tick, not measured on a real node.</summary>
    public static readonly TimeSpan HeldMargin = TimeSpan.FromMilliseconds(500);

    /// <summary>Whether the last step's lost ask was counted as the supervisor held.</summary>
    internal bool LastStepHeld { get; private set; }

    /// <summary>Lost asks counted as the supervisor held since this core was made.</summary>
    internal long HeldAsks { get; private set; }

    /// <summary>What the last lost ask counted as the supervisor held read ("" none yet).</summary>
    internal string LastHeld { get; private set; } = "";

    /// <summary>Pure: whether a lost ask's step was the supervisor held rather than the engine silent. Its ask was armed only when the
    /// step ran, so a step that reached its ask more than <see cref="HeldMargin"/> after it was due was not run when due; and an ask
    /// cancelled at <see cref="AskDeadline"/> returns within the margin of it on a supervisor that runs, so one that returned later had
    /// its own continuation held past the deadline, and the answer it missed may have come while it was held. An ask that failed at
    /// once (no listener) or ended at its deadline is the engine's, and a step that made no ask (−1) is judged by its lateness alone.</summary>
    internal static bool WasHeld(in SupervisorFacts facts) =>
        facts.LateNs > HeldMargin.Ticks * NsPerTick || facts.AskNs > (AskDeadline + HeldMargin).Ticks * NsPerTick;

    /// <summary>Restarts within the window now.</summary>
    public int Restarts => _restarts.Count;

    /// <summary>Why the engine last stopped or was stopped ("" none yet).</summary>
    public string LastCause { get; private set; } = "";

    /// <summary>The freeze rule as the status file says it: active, or what it waits for.</summary>
    public string FreezeRule { get; private set; } = "the engine reports no shown frames: the freeze rule waits for them";

    /// <summary>Whether the freeze rule watches the engine now (a known count while the GPU runs or within a phase's budget).</summary>
    public bool FreezeRuleActive { get; private set; }

    /// <summary>A person stopped the engine: a running engine is asked to stop (and terminated after the grace), and nothing starts
    /// it again until <see cref="Resume"/>.</summary>
    public SupervisorAction Hold(long nowNs, string why)
    {
        LastCause = why;
        if (State is SupervisorState.Running or SupervisorState.Starting)
        {
            BeginStop(nowNs, SupervisorState.Held);
            return SupervisorAction.RequestStop;
        }
        if (State == SupervisorState.Stopping)
            _afterStop = SupervisorState.Held;
        else
            State = SupervisorState.Held;
        return SupervisorAction.None;
    }

    /// <summary>Ends a hold or the too-many-restarts stop: the window is cleared and the next step may start the engine. False, with
    /// nothing changed, when there is neither.</summary>
    public bool Resume()
    {
        if (State == SupervisorState.Stopping && _afterStop == SupervisorState.Held)
            _afterStop = SupervisorState.Backoff;
        else if (State is SupervisorState.Held or SupervisorState.StoppedTooManyRestarts)
            State = SupervisorState.Backoff;
        else
            return false;
        _restarts.Clear();
        _failures = 0;
        _dueNs = long.MinValue;
        return true;
    }

    /// <summary>Pure: whether the engine's stop event is cleared after a step that answered <paramref name="action"/>, with the policy
    /// in <paramref name="state"/> and the engine <paramref name="engineAlive"/>. Before a start, so a new engine never sees an old
    /// stop; otherwise only when no stop is pending and no engine runs — never while one is pending, so an engine that looks at the
    /// event between steps, or starts waiting after it was set, still sees it.</summary>
    public static bool ShouldResetStopEvent(SupervisorAction action, SupervisorState state, bool engineAlive) =>
        action == SupervisorAction.Start || (state != SupervisorState.Stopping && !engineAlive);

    public SupervisorAction Step(long nowNs, in SupervisorFacts facts)
    {
        switch (State)
        {
            case SupervisorState.Stopping:
                if (!facts.EngineAlive)
                    return Exited(nowNs, facts, LastCause);
                return nowNs >= _stopAtNs ? SupervisorAction.Terminate : SupervisorAction.None;

            case SupervisorState.Starting or SupervisorState.Running:
                if (!facts.EngineAlive)
                    return Exited(nowNs, facts, "the engine exited");
                State = SupervisorState.Running;
                return Watch(nowNs, facts);

            case SupervisorState.Held or SupervisorState.StoppedTooManyRestarts:
                return SupervisorAction.None;

            default:
                if (facts.EngineAlive)
                {
                    // An engine already running (a supervisor restart, an upgrade's re-start) is adopted, never started twice.
                    Adopt(nowNs);
                    return Watch(nowNs, facts);
                }
                if (!facts.ShowSessionActive)
                {
                    State = SupervisorState.WaitingForSession;
                    return SupervisorAction.None;
                }
                if (State == SupervisorState.Backoff && nowNs < _dueNs)
                    return SupervisorAction.None;
                State = SupervisorState.Starting;
                Adopt(nowNs);
                return SupervisorAction.Start;
        }
    }

    private void Adopt(long nowNs)
    {
        if (State != SupervisorState.Starting)
            State = SupervisorState.Running;
        _startedAtNs = nowNs;
        _lastFrames = -1;
        _framesMovedNs = nowNs;
        _lastAnswerNs = nowNs;
        _lastStepNs = nowNs;
        _silentNs = 0;
        _heldNs = 0;
        LastStepHeld = false;
        _lastEpoch = long.MinValue;
        _lastPhase = GpuPhase.Unknown;
        FreezeRuleActive = false;
    }

    private SupervisorAction Exited(long nowNs, in SupervisorFacts facts, string cause)
    {
        LastCause = cause;
        FreezeRuleActive = false;
        if (_afterStop == SupervisorState.Held)
        {
            _afterStop = SupervisorState.Backoff;
            State = SupervisorState.Held;
            return SupervisorAction.None;
        }
        var ran = _startedAtNs < 0 ? 0 : nowNs - _startedAtNs;
        if (ran >= _policy.HealthyRun.Ticks * NsPerTick)
            _failures = 0;
        while (_restarts.Count > 0 && nowNs - _restarts.Peek() >= _policy.RestartWindow.Ticks * NsPerTick)
            _restarts.Dequeue();
        if (_restarts.Count >= _policy.MaxRestarts)
        {
            State = SupervisorState.StoppedTooManyRestarts;
            LastCause = string.Create(CultureInfo.InvariantCulture,
                $"{cause}; {_restarts.Count} restarts in {_policy.RestartWindow.TotalMinutes:0} minutes: the supervisor stopped trying (PlayMap.Supervisor.exe resume)");
            return SupervisorAction.None;
        }
        _restarts.Enqueue(nowNs);
        _dueNs = nowNs + _policy.Backoff(_failures).Ticks * NsPerTick;
        _failures++;
        State = SupervisorState.Backoff;
        return facts.ShowSessionActive && _dueNs <= nowNs ? Step(nowNs, facts) : SupervisorAction.None;
    }

    private SupervisorAction Watch(long nowNs, in SupervisorFacts facts)
    {
        var sinceStep = nowNs - _lastStepNs;
        _lastStepNs = nowNs;
        LastStepHeld = false;
        if (facts.HealthAnswered)
        {
            _lastAnswerNs = nowNs;
            _silentNs = 0;
            _heldNs = 0;
        }
        if (facts.GpuEpoch != _lastEpoch && facts.HealthAnswered)
        {
            // A new device epoch starts the count again: frames of the old device say nothing of the new one.
            _lastEpoch = facts.GpuEpoch;
            _lastFrames = -1;
        }
        if (facts.HealthAnswered)
            _lastPhase = facts.GpuPhase;

        if (!facts.HealthAnswered)
        {
            // The time since the step before is the engine's silence only when this step ran on time; a held step's time is the
            // supervisor's, so a hold of any length, or holds that add up, never stand for a silent engine.
            if (WasHeld(facts))
            {
                _heldNs += sinceStep;
                HeldAsks++;
                LastStepHeld = true;
                var ask = facts.AskNs < 0
                    ? "no ask was made"
                    : string.Create(CultureInfo.InvariantCulture, $"the ask took {facts.AskNs / 1_000_000_000.0:0.0#} s against its {AskDeadline.TotalMilliseconds:0} ms deadline");
                LastHeld = string.Create(CultureInfo.InvariantCulture,
                    $"the supervisor was held: the step reached its ask {facts.LateNs / 1_000_000_000.0:0.0#} s after it was due and {ask}: the lost HEALTH answer is not counted as the engine's silence");
                return SupervisorAction.None;
            }
            _silentNs += sinceStep;
            // Frames are read only from answers, so none were read during the silence: the words say what the last answer said.
            if (FreezeRuleActive && _lastPhase == GpuPhase.Running && _silentNs >= _policy.HealthSilent.Ticks * NsPerTick)
            {
                var held = _heldNs > 0 ? string.Create(CultureInfo.InvariantCulture, $", beside {_heldNs / 1_000_000_000.0:0.#} s the supervisor was held") : "";
                return Frozen(nowNs, string.Create(CultureInfo.InvariantCulture,
                    $"HEALTH silent for {_silentNs / 1_000_000_000.0:0.#} s of on-time steps{held} (the last answer, {(nowNs - _lastAnswerNs) / 1_000_000_000.0:0.#} s before, said the GPU ran; no shown frames were read since)"));
            }
            return SupervisorAction.None;
        }

        if (facts.ShownFrames < 0)
        {
            FreezeRuleActive = false;
            FreezeRule = "the engine reports no shown frames: the freeze rule waits for them";
            return SupervisorAction.None;
        }

        if (facts.ShownFrames != _lastFrames)
        {
            _lastFrames = facts.ShownFrames;
            _framesMovedNs = nowNs;
        }

        switch (facts.GpuPhase)
        {
            case GpuPhase.Starting or GpuPhase.Rebuilding:
            {
                FreezeRuleActive = true;
                var budget = _policy.PhaseBudget.Ticks * NsPerTick;
                var since = nowNs - facts.GpuPhaseSinceNs;
                FreezeRule = "exempt while " + HealthGpu.Word(facts.GpuPhase) + ", up to " + _policy.PhaseBudgetWords;
                // While the phase lasts the frames are not expected to move; the stale clock starts again when it ends.
                _framesMovedNs = nowNs;
                return since > budget
                    ? Frozen(nowNs, string.Create(CultureInfo.InvariantCulture,
                        $"the GPU stayed {HealthGpu.Word(facts.GpuPhase)} for {since / 1_000_000_000.0:0.#} s, past its budget of {_policy.PhaseBudgetWords}"))
                    : SupervisorAction.None;
            }
            case GpuPhase.Running:
                FreezeRuleActive = true;
                FreezeRule = string.Create(CultureInfo.InvariantCulture,
                    $"active: open outputs' shown frames may stand still {_policy.StaleTime.TotalSeconds:0.#} s while the GPU runs");
                if (!facts.OutputsOpen)
                {
                    _framesMovedNs = nowNs;
                    return SupervisorAction.None;
                }
                var still = nowNs - _framesMovedNs;
                return still >= _policy.StaleTime.Ticks * NsPerTick
                    ? Frozen(nowNs, string.Create(CultureInfo.InvariantCulture,
                        $"frozen: shown frames stood at {facts.ShownFrames} for {still / 1_000_000_000.0:0.#} s while the GPU ran"))
                    : SupervisorAction.None;
            default:
                FreezeRuleActive = false;
                FreezeRule = "the engine reports no GPU phase: the freeze rule waits for it";
                _framesMovedNs = nowNs;
                return SupervisorAction.None;
        }
    }

    private SupervisorAction Frozen(long nowNs, string cause)
    {
        LastCause = cause;
        BeginStop(nowNs, SupervisorState.Backoff);
        return SupervisorAction.RequestStop;
    }

    private void BeginStop(long nowNs, SupervisorState after)
    {
        _afterStop = after;
        State = SupervisorState.Stopping;
        _stopAtNs = nowNs + _policy.StopGrace.Ticks * NsPerTick;
    }
}
