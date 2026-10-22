namespace PlayMap.Tests;

/// <summary>A clock a test moves by hand: timestamps, UTC and timers. <see cref="Advance"/> steps through the timers due in order,
/// firing each on the caller's thread at its own instant (a periodic timer as often as its period fits); a timer created due now
/// fires on the next advance, never inside its creation. Nothing on it moves while the test does not, however slowly the test runs.</summary>
internal sealed class ManualTime(DateTimeOffset? start = null) : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly DateTimeOffset _start = start ?? new DateTimeOffset(2026, 10, 15, 20, 0, 0, TimeSpan.Zero);
    private long _ticks;
    private long _order;
    private int _made;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
            return _ticks;
    }

    public override DateTimeOffset GetUtcNow() => _start + TimeSpan.FromTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_gate)
            _made++;
        return timer;
    }

    /// <summary>Timers waiting now.</summary>
    public int Waiting
    {
        get
        {
            lock (_gate)
                return _timers.Count;
        }
    }

    /// <summary>Timers made on this clock so far.</summary>
    public int Made
    {
        get
        {
            lock (_gate)
                return _made;
        }
    }

    public void Advance(TimeSpan by)
    {
        long target;
        lock (_gate)
            target = _ticks + by.Ticks;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.DueTicks <= target).OrderBy(t => t.DueTicks).ThenBy(t => t.Order).FirstOrDefault();
                if (next is null)
                {
                    _ticks = target;
                    return;
                }
                _ticks = Math.Max(_ticks, next.DueTicks);
                if (next.PeriodTicks > 0)
                    next.DueTicks += next.PeriodTicks;
                else
                    _timers.Remove(next);
            }
            next.Fire();
        }
    }

    private void Schedule(ManualTimer timer, TimeSpan due, TimeSpan period)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
            if (due == Timeout.InfiniteTimeSpan)
                return;
            timer.DueTicks = _ticks + Math.Max(0, due.Ticks);
            timer.PeriodTicks = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? 0 : period.Ticks;
            timer.Order = ++_order;
            _timers.Add(timer);
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
            _timers.Remove(timer);
    }

    private sealed class ManualTimer(ManualTime time, TimerCallback callback, object? state) : ITimer
    {
        public long DueTicks { get; set; }

        public long PeriodTicks { get; set; }

        public long Order { get; set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            time.Schedule(this, dueTime, period);
            return true;
        }

        public void Dispose() => time.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
