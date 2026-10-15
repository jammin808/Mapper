using System.Diagnostics;

namespace PlayMap.Core.Time;

/// <summary>A monotonic clock in nanoseconds from an arbitrary epoch. It never steps: NTP corrections and
/// daylight saving cannot move a playing timeline. Everything that counts reads one of these.</summary>
public interface IMonoClock
{
    long NowNs { get; }
}

/// <summary>The process's monotonic clock: Stopwatch's counter in nanoseconds.</summary>
public sealed class MonoClock : IMonoClock
{
    public static readonly MonoClock Instance = new();

    private static readonly double NsPerTick = 1e9 / Stopwatch.Frequency;

    private MonoClock()
    {
    }

    public long NowNs => (long)(Stopwatch.GetTimestamp() * NsPerTick);
}

/// <summary>A clock the tests move by hand.</summary>
public sealed class ManualClock(long startNs = 0) : IMonoClock
{
    public long NowNs { get; set; } = startNs;

    public void Advance(long ns) => NowNs += ns;

    public void AdvanceMs(double ms) => NowNs += (long)(ms * 1e6);
}

/// <summary>Wall time for stamping (hellos, journals, Show Bus pongs): the UTC time read once at start
/// plus monotonic elapsed time, so it never steps backwards inside a run (Patterns' ShowClock rule).</summary>
public sealed class ShowWallClock
{
    private readonly IMonoClock _mono;
    private readonly long _startUnixMs;
    private readonly long _startMonoNs;

    public ShowWallClock(IMonoClock mono, long startUnixMs)
    {
        _mono = mono;
        _startUnixMs = startUnixMs;
        _startMonoNs = mono.NowNs;
    }

    public static ShowWallClock System() =>
        new(MonoClock.Instance, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public long UnixMs => _startUnixMs + (_mono.NowNs - _startMonoNs) / 1_000_000;

    /// <summary>Unix milliseconds for a monotonic instant read from the same clock.</summary>
    public long UnixMsAt(long monoNs) => _startUnixMs + (monoNs - _startMonoNs) / 1_000_000;

    public IMonoClock Mono => _mono;
}

public static class Durations
{
    public const long NsPerMs = 1_000_000;
    public const long NsPerSecond = 1_000_000_000;

    public static long FromSeconds(double s) => (long)Math.Round(s * NsPerSecond);
    public static long FromMs(double ms) => (long)Math.Round(ms * NsPerMs);
    public static double ToSeconds(long ns) => ns / (double)NsPerSecond;
    public static double ToMs(long ns) => ns / (double)NsPerMs;
}

/// <summary>The leader's time line as this node reads it: leader nanoseconds from local monotonic nanoseconds. A node that
/// follows applies the estimator of its exchange with the leader; a leader or a solo node reads its own clock (or, once
/// promoted, the time line it took over).
/// <list type="bullet">
/// <item><b>Continuous and strictly monotonic.</b> A new estimate whose jump at now is at most <see cref="SlewLimitNs"/> is
/// absorbed by a residual that decays at <see cref="SlewPpm"/>: leader time runs at 1 + drift ± <see cref="SlewPpm"/>, never
/// backwards. A slewed correction moves a content frame's boundary by at most 1 ms (6 % of a 60 Hz frame) and 200 ppm is far
/// inside the ±0.5 % range audio follows without an audible pitch change.</item>
/// <item><b>A larger jump steps.</b> The new mapping takes effect at once and <see cref="Stepped"/> is raised with
/// Δ = new − old leader time at that instant, so whoever holds leader instants (the playlists' anchors, scheduled starts)
/// rebases them by +Δ and keeps its place. <see cref="StepOn"/> names where a step is taken (the engine's control lane, so
/// the step and the rebase are one action there); a step taken there is judged again against the clock as it stands then.</item>
/// <item><b>Readers never lock.</b> The state is one immutable value behind a volatile reference: the render thread and the
/// lane read it without waiting; writers (the exchange's receive thread, the lane) take a lock among themselves.</item>
/// </list></summary>
public sealed class NetworkClock
{
    /// <summary>The largest correction slewed; anything larger steps.</summary>
    public const long SlewLimitNs = 1_000_000;

    /// <summary>The default slew rate, parts per million.</summary>
    public const double DefaultSlewPpm = 200;

    /// <summary>The mapping: offset(t) = (the estimator's offset at t, or <paramref name="BaseOffsetNs"/> with none) plus the
    /// residual, which decays linearly from <paramref name="ResidualNs"/> at <paramref name="ResidualAtLocalNs"/> to zero.</summary>
    private sealed record Mapping(ClockOffsetEstimator? Estimator, long BaseOffsetNs, double ResidualNs, long ResidualAtLocalNs,
        long HeardLocalNs, long HoldSinceLocalNs)
    {
        public static readonly Mapping Own = new(null, 0, 0, 0, long.MinValue, long.MinValue);

        public bool Exact => Estimator is null && ResidualNs == 0;
    }

    [ThreadStatic]
    private static NetworkClock? t_pinned;

    [ThreadStatic]
    private static long t_pinnedNs;

    private readonly Lock _write = new();
    private volatile Mapping _map = Mapping.Own;
    private double _slewPerNs = DefaultSlewPpm * 1e-6;
    private long _watermark = long.MinValue;
    private long _steps;
    private long _lastStepNs;

    public NetworkClock(IMonoClock local)
    {
        Local = local ?? throw new ArgumentNullException(nameof(local));
    }

    public IMonoClock Local { get; }

    /// <summary>How fast a correction under <see cref="SlewLimitNs"/> is slewed, parts per million (1–500).</summary>
    public double SlewPpm
    {
        get => Volatile.Read(ref _slewPerNs) * 1e6;
        set => Volatile.Write(ref _slewPerNs, Math.Clamp(double.IsFinite(value) ? value : DefaultSlewPpm, 1, 500) * 1e-6);
    }

    /// <summary>Raised after a step, with Δ = new − old leader time (ns), on the thread that took it (see <see cref="StepOn"/>).</summary>
    public event Action<long>? Stepped;

    /// <summary>Where a step is taken: the action is handed here (the engine posts it to its control lane). Null: at once, on the
    /// thread that brought the estimate.</summary>
    public Action<Action>? StepOn { get; set; }

    /// <summary>True while an estimate of a leader's clock is applied (following, or free-wheeling on the last one).</summary>
    public bool IsFollowing => _map.Estimator is { HasEstimate: true };

    /// <summary>True while a slewed correction is still being absorbed.</summary>
    public bool IsSlewing => Residual(_map, Local.NowNs) != 0;

    /// <summary>The steps taken since this clock was made.</summary>
    public long Steps => Interlocked.Read(ref _steps);

    /// <summary>The last step's Δ, ns (0 before any).</summary>
    public long LastStepNs => Interlocked.Read(ref _lastStepNs);

    /// <summary>The local instant the last estimate arrived (long.MinValue: never, or not following).</summary>
    public long HeardLocalNs => _map.HeardLocalNs;

    /// <summary>True while the leader is silent and the clock free-wheels on its last estimate (<see cref="Hold"/>).</summary>
    public bool IsHolding => _map.HoldSinceLocalNs != long.MinValue;

    /// <summary>How long the clock has free-wheeled, ns (0 when not holding).</summary>
    public long HoldingNs => _map.HoldSinceLocalNs is var since && since != long.MinValue ? Math.Max(0, Local.NowNs - since) : 0;

    /// <summary>The leader's time now, in nanoseconds (the pinned instant inside <see cref="Pin"/> on this thread).</summary>
    public long NowNs
    {
        get
        {
            if (ReferenceEquals(t_pinned, this))
                return t_pinnedNs;
            var map = _map;
            var local = Local.NowNs;
            if (map.Exact)
                return local + map.BaseOffsetNs;
            // A slewed mapping is continuous to well under a nanosecond; rounding at a swap could still show one nanosecond back
            // to a reader in a tight loop, so readers of now never see less than the most recent reading.
            var v = local + (long)Math.Round(Offset(map, local));
            var last = Interlocked.Read(ref _watermark);
            while (v > last)
            {
                var seen = Interlocked.CompareExchange(ref _watermark, v, last);
                if (seen == last)
                    return v;
                last = seen;
            }
            return last;
        }
    }

    /// <summary>The leader instant of a local instant, through the mapping in force now.</summary>
    public long ToLeader(long localNs)
    {
        var map = _map;
        return map.Exact ? localNs + map.BaseOffsetNs : localNs + (long)Math.Round(Offset(map, localNs));
    }

    /// <summary>The local instant of a leader instant, through the mapping in force now.</summary>
    public long ToLocal(long leaderNs)
    {
        var map = _map;
        if (map.Exact)
            return leaderNs - map.BaseOffsetNs;
        // Fixed-point iteration on local = leader − offset(local): the offset moves at most drift + slew (≤ 7e-4) per ns, so each
        // step shrinks the error by that factor and four are exact to the nanosecond.
        var guess = leaderNs - (long)Math.Round(Offset(map, leaderNs));
        for (var i = 0; i < 4; i++)
            guess = leaderNs - (long)Math.Round(Offset(map, guess));
        return guess;
    }

    /// <summary>The uncertainty of the mapping: half the best round trip (the offset is exact only for a symmetric path).</summary>
    public double UncertaintyNs => _map.Estimator is { HasEstimate: true } e ? e.RoundTrip / 2.0 : 0;

    /// <summary>Takes a new estimate of the leader's clock (never written after it is handed in): slewed when its jump at now is at
    /// most <see cref="SlewLimitNs"/>, otherwise stepped where <see cref="StepOn"/> says. Ends a hold.</summary>
    public void Follow(ClockOffsetEstimator estimator)
    {
        ArgumentNullException.ThrowIfNull(estimator);
        Adopt(estimator, 0);
    }

    /// <summary>Back to this node's own clock (it leads, or is solo): slewed or stepped as <see cref="Follow"/>.</summary>
    public void Lead() => Adopt(null, 0);

    /// <summary>This node continues the time line it reads now as its own (a backup taking over from its leader): the mapping
    /// at now is frozen into a fixed offset, so leader time neither jumps nor slews.</summary>
    public void Promote()
    {
        lock (_write)
        {
            var now = Local.NowNs;
            var map = _map;
            var offset = map.Exact ? map.BaseOffsetNs : (long)Math.Round(Offset(map, now));
            _map = Mapping.Own with { BaseOffsetNs = offset };
        }
    }

    /// <summary>The leader is silent: the clock free-wheels on its last estimate (it already does; this marks since when, for
    /// STATE and the context). The next estimate ends it.</summary>
    public void Hold()
    {
        lock (_write)
        {
            if (_map.HoldSinceLocalNs == long.MinValue)
                _map = _map with { HoldSinceLocalNs = Local.NowNs };
        }
    }

    /// <summary>Reads <paramref name="leaderNs"/> as now on this thread until the scope is disposed: a line that a group's leader
    /// ran at an instant runs here as if at that instant (its fades start on the same frames). Other threads are not affected.</summary>
    public PinScope Pin(long leaderNs)
    {
        var scope = new PinScope(t_pinned, t_pinnedNs);
        t_pinned = this;
        t_pinnedNs = leaderNs;
        return scope;
    }

    /// <summary>A pinned reading of now on one thread; disposing it puts back what was pinned before.</summary>
    public readonly record struct PinScope(NetworkClock? Previous, long PreviousNs) : IDisposable
    {
        public void Dispose()
        {
            t_pinned = Previous;
            t_pinnedNs = PreviousNs;
        }
    }

    private void Adopt(ClockOffsetEstimator? estimator, long baseOffsetNs)
    {
        if (TrySlew(estimator, baseOffsetNs))
            return;
        if (StepOn is { } on)
            on(() => StepTo(estimator, baseOffsetNs));
        else
            StepTo(estimator, baseOffsetNs);
    }

    /// <summary>Swaps in the new mapping with the residual that keeps leader time continuous at now; false when the jump is too
    /// large to slew.</summary>
    private bool TrySlew(ClockOffsetEstimator? estimator, long baseOffsetNs)
    {
        lock (_write)
        {
            var now = Local.NowNs;
            var old = _map;
            var fresh = Target(estimator, baseOffsetNs, now);
            var current = Offset(old, now);
            var jump = fresh - current;
            if (Math.Abs(jump) > SlewLimitNs)
                return false;
            var heard = estimator is null ? long.MinValue : now;
            _map = new Mapping(estimator is { HasEstimate: true } ? estimator : null, baseOffsetNs, -jump, now, heard, long.MinValue);
            return true;
        }
    }

    private void StepTo(ClockOffsetEstimator? estimator, long baseOffsetNs)
    {
        long delta;
        lock (_write)
        {
            var now = Local.NowNs;
            var fresh = Target(estimator, baseOffsetNs, now);
            var jump = fresh - Offset(_map, now);
            var heard = estimator is null ? long.MinValue : now;
            if (Math.Abs(jump) <= SlewLimitNs)
            {
                // Judged again where the step was taken: an estimate that came in meanwhile left nothing to step.
                _map = new Mapping(estimator is { HasEstimate: true } ? estimator : null, baseOffsetNs, -jump, now, heard, long.MinValue);
                return;
            }
            _map = new Mapping(estimator is { HasEstimate: true } ? estimator : null, baseOffsetNs, 0, now, heard, long.MinValue);
            Interlocked.Exchange(ref _watermark, long.MinValue);
            delta = (long)Math.Round(jump);
            Interlocked.Increment(ref _steps);
            Interlocked.Exchange(ref _lastStepNs, delta);
        }
        Stepped?.Invoke(delta);
    }

    private static double Target(ClockOffsetEstimator? estimator, long baseOffsetNs, long localNs) =>
        estimator is { HasEstimate: true } e ? e.OffsetAt(localNs) : baseOffsetNs;

    private double Offset(Mapping map, long localNs) => Target(map.Estimator, map.BaseOffsetNs, localNs) + Residual(map, localNs);

    private double Residual(Mapping map, long localNs)
    {
        if (map.ResidualNs == 0)
            return 0;
        var decayed = Math.Max(0, localNs - map.ResidualAtLocalNs) * Volatile.Read(ref _slewPerNs);
        var left = Math.Abs(map.ResidualNs) - decayed;
        return left < 0.5 ? 0 : Math.CopySign(left, map.ResidualNs);
    }
}
