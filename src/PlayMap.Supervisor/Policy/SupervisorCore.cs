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
/// the supervisor's own clock.</summary>
public readonly record struct SupervisorFacts(
    bool ShowSessionActive,
    bool EngineAlive,
    bool HealthAnswered,
    long ShownFrames,
    bool OutputsOpen,
    GpuPhase GpuPhase,
    long GpuPhaseSinceNs,
    long GpuEpoch);

/// <summary>Pure: given the time on a monotonic clock (ns) and what it observed, what the supervisor does next. It restarts an engine
/// that exits with backoff, stops trying after too many restarts in the window, and stops an engine whose open outputs' shown frames
/// have stood still for the stale time while HEALTH says the GPU runs — never while the count is unknown, never during a start or a
/// rebuild within its budget, never for a slow HEALTH while frames move. Every stop and restart keeps its cause in words.</summary>
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
    private GpuPhase _lastPhase = GpuPhase.Unknown;
    private SupervisorState _afterStop = SupervisorState.Backoff;

    public SupervisorCore(SupervisorPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
    }

    public SupervisorState State { get; private set; } = SupervisorState.WaitingForSession;

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
        if (facts.HealthAnswered)
            _lastAnswerNs = nowNs;
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
            var silent = nowNs - _lastAnswerNs;
            if (FreezeRuleActive && _lastPhase == GpuPhase.Running && silent >= _policy.HealthSilent.Ticks * NsPerTick
                && nowNs - _framesMovedNs >= _policy.HealthSilent.Ticks * NsPerTick)
                return Frozen(nowNs, string.Create(CultureInfo.InvariantCulture,
                    $"HEALTH silent for {silent / 1_000_000_000.0:0.#} s with no shown frames moving while the GPU ran"));
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
