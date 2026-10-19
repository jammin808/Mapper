using System.Runtime.Versioning;
using PlayMap.Core.Machine;
using PlayMap.Core.Time;
using PlayMap.Platform.Windows.Interop;

namespace PlayMap.Platform.Windows;

/// <summary>A deadline wait on a high-resolution waitable timer (<c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c>), which wakes near its due
/// time without raising the system's timer resolution: the due time is set relative from the machine's monotonic clock, and the wait
/// takes the timer and the cancel handle together. It waits again while it woke before the instant, so it never answers early, and
/// records how late each wait woke.</summary>
[SupportedOSPlatform("windows")]
internal sealed class HighResolutionTimer : IDeadlineTimer
{
    private readonly IMonoClock _clock;
    private readonly Lateness _late;
    private readonly TimerHandle _timer;
    private readonly ManualResetEvent _never = new(false);
    private readonly WaitHandle[] _pair = new WaitHandle[2];

    private HighResolutionTimer(TimerHandle timer, IMonoClock clock, Lateness late)
    {
        _timer = timer;
        _clock = clock;
        _late = late;
        _pair[0] = timer;
    }

    /// <summary>The timer, or null with the words when Windows does not make one.</summary>
    public static HighResolutionTimer? TryCreate(IMonoClock clock, Lateness late, out string words)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(late);
        var handle = Native.CreateWaitableTimerExW(0, null, Native.CreateWaitableTimerHighResolution, Native.TimerAllAccess);
        if (handle.IsInvalid)
        {
            words = "Windows made no high-resolution timer (" + Native.LastErrorWords() + ")";
            handle.Dispose();
            return null;
        }
        words = "";
        return new HighResolutionTimer(new TimerHandle(handle), clock, late);
    }

    public double LateP99Us => _late.P99Us;

    /// <summary>One caller at a time (the thread whose deadlines it keeps).</summary>
    public bool WaitUntil(long monoNs, WaitHandle? cancel = null)
    {
        _pair[1] = cancel ?? _never;
        while (true)
        {
            var left = monoNs - _clock.NowNs;
            if (left <= 0)
                break;
            // A relative due time: negative, in 100 ns.
            var due = -Math.Max(1, left / 100);
            if (!Native.SetWaitableTimer(_timer.SafeWaitHandle, due, 0, 0, 0, false))
                throw new InvalidOperationException("the high-resolution timer was not set: " + Native.LastErrorWords());
            if (WaitHandle.WaitAny(_pair) == 1)
                return false;
        }
        if (cancel?.WaitOne(0) == true)
            return false;
        _late.Record((_clock.NowNs - monoNs) / 1e3);
        return true;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _never.Dispose();
    }

    /// <summary>The waitable timer as a wait handle, so it waits beside a cancel handle through the runtime's own wait.</summary>
    private sealed class TimerHandle : WaitHandle
    {
        public TimerHandle(Microsoft.Win32.SafeHandles.SafeWaitHandle handle)
        {
            SafeWaitHandle = handle;
        }
    }
}

/// <summary>A deadline timer on the runtime's own wait, its lateness also recorded in the platform's window.</summary>
[SupportedOSPlatform("windows")]
internal sealed class RecordedTimer(IMonoClock clock, Lateness late) : IDeadlineTimer
{
    private readonly RuntimeDeadlineTimer _inner = new(clock);

    public double LateP99Us => _inner.LateP99Us;

    public bool WaitUntil(long monoNs, WaitHandle? cancel = null)
    {
        if (!_inner.WaitUntil(monoNs, cancel))
            return false;
        late.Record((clock.NowNs - monoNs) / 1e3);
        return true;
    }

    public void Dispose() => _inner.Dispose();
}

/// <summary>How late deadline waits woke, over the last 1 000 (µs): the platform's window across every timer it made.</summary>
[SupportedOSPlatform("windows")]
internal sealed class Lateness
{
    private const int Window = 1000;
    private readonly double[] _lateUs = new double[Window];
    private readonly Lock _gate = new();
    private int _next;

    public int Count { get; private set; }

    public void Record(double lateUs)
    {
        lock (_gate)
        {
            _lateUs[_next] = Math.Max(0, lateUs);
            _next = (_next + 1) % Window;
            Count = Math.Min(Count + 1, Window);
        }
    }

    public double P99Us
    {
        get
        {
            double[] late;
            lock (_gate)
            {
                if (Count == 0)
                    return 0;
                late = _lateUs.AsSpan(0, Count).ToArray();
            }
            Array.Sort(late);
            return late[Math.Min(late.Length - 1, (int)Math.Ceiling(0.99 * late.Length) - 1)];
        }
    }
}
