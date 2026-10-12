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
