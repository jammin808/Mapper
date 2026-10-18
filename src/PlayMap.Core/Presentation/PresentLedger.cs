namespace PlayMap.Core.Presentation;

/// <summary>Missed vblanks counted from the display's side, against the output's own refresh: every present records the refresh
/// it was meant for (<see cref="OnPresent"/>), and when the display says which refresh first showed it (<see cref="OnShown"/>)
/// the difference is how many refreshes the previous picture stayed up too long.
///
/// <list type="bullet">
/// <item><c>late = shown − expected</c>: when positive, one glitch and <c>late</c> missed vblanks.</item>
/// <item>Between two shown presents (p₀, r₀) → (p, r): <c>(r − r₀) − (p − p₀)</c> refreshes repeated an old picture when
/// positive (repeats); presents replaced before they showed when negative (discarded).</item>
/// <item>A present the output's queue could not take is held (the previous picture stays up; never a block).</item>
/// <item>Per-second buckets give the last 60 s (<see cref="Missed60s"/>) and the late share of the last 10 s
/// (<see cref="LateShare"/>), the output light's evidence.</item>
/// </list>
/// Present ids and refresh indices are unsigned and every difference is taken unsigned, so a counter that wraps is still
/// counted right. Pure: the instants are given; allocation-free. One thread.</summary>
public sealed class PresentLedger
{
    private const int Ring = 64;
    private const int Buckets = 60;

    private readonly ulong[] _ids = new ulong[Ring];
    private readonly ulong[] _expected = new ulong[Ring];
    private readonly bool[] _used = new bool[Ring];
    private readonly long[] _bucketSecond = new long[Buckets];
    private readonly long[] _bucketRefreshes = new long[Buckets];
    private readonly long[] _bucketMissed = new long[Buckets];
    private bool _hasShown;
    private ulong _lastShownId;
    private ulong _lastShownRefresh;

    public PresentLedger()
    {
        Array.Fill(_bucketSecond, long.MinValue);
    }

    /// <summary>Presents recorded.</summary>
    public long Presented { get; private set; }

    /// <summary>Presents the display said it showed.</summary>
    public long Shown { get; private set; }

    /// <summary>Refreshes an old picture stayed up because the new one came late (Σ late).</summary>
    public long MissedVblanks { get; private set; }

    /// <summary>Presents that showed later than the refresh they were meant for.</summary>
    public long Glitches { get; private set; }

    /// <summary>Refreshes that showed no new picture, whatever the cause.</summary>
    public long Repeats { get; private set; }

    /// <summary>Presents replaced before they were shown.</summary>
    public long Discarded { get; private set; }

    /// <summary>Presents the output's queue could not take (the previous picture held).</summary>
    public long Held { get; private set; }

    /// <summary>The monotonic instant of the last shown present (−1 before any).</summary>
    public long LastShownNs { get; private set; } = -1;

    /// <summary>A present was queued, meant for refresh <paramref name="expectedRefresh"/>.</summary>
    public void OnPresent(ulong presentId, ulong expectedRefresh)
    {
        var slot = (int)(presentId % Ring);
        _ids[slot] = presentId;
        _expected[slot] = expectedRefresh;
        _used[slot] = true;
        Presented++;
    }

    /// <summary>A present the queue could not take.</summary>
    public void OnHeld() => Held++;

    /// <summary>The display first showed present <paramref name="presentId"/> at refresh <paramref name="refresh"/>, at the
    /// monotonic instant <paramref name="shownNs"/>. A present it has no record of (older than 64 presents) still counts its
    /// repeats; one shown twice is ignored.</summary>
    public void OnShown(ulong presentId, ulong refresh, long shownNs)
    {
        if (_hasShown && presentId - _lastShownId - 1 >= ulong.MaxValue / 2)
            return;
        long late = 0;
        var slot = (int)(presentId % Ring);
        if (_used[slot] && _ids[slot] == presentId)
        {
            var d = refresh - _expected[slot];
            if (d is > 0 and < ulong.MaxValue / 2)
                late = (long)d;
            _used[slot] = false;
        }
        long refreshes = 1;
        if (_hasShown)
        {
            var dr = refresh - _lastShownRefresh;
            var dp = presentId - _lastShownId;
            refreshes = dr < ulong.MaxValue / 2 ? (long)dr : 0;
            var presents = dp < ulong.MaxValue / 2 ? (long)dp : 0;
            if (refreshes > presents)
                Repeats += refreshes - presents;
            else if (presents > refreshes)
                Discarded += presents - refreshes;
        }
        if (late > 0)
        {
            Glitches++;
            MissedVblanks += late;
        }
        Shown++;
        _hasShown = true;
        _lastShownId = presentId;
        _lastShownRefresh = refresh;
        LastShownNs = shownNs;
        AddToBucket(shownNs, refreshes, late);
    }

    /// <summary>Missed vblanks in the last 60 s before <paramref name="nowNs"/>.</summary>
    public long Missed60s(long nowNs) => Sum(nowNs, 60, missed: true);

    /// <summary>Refreshes the display ran in the last <paramref name="seconds"/> s (1–60) before <paramref name="nowNs"/>.</summary>
    public long RefreshesIn(long nowNs, int seconds) => Sum(nowNs, seconds, missed: false);

    /// <summary>Missed vblanks in the last <paramref name="seconds"/> s (1–60) before <paramref name="nowNs"/>.</summary>
    public long MissedIn(long nowNs, int seconds) => Sum(nowNs, seconds, missed: true);

    /// <summary>The share of the last <paramref name="seconds"/> s of refreshes that showed an old picture because a new one was
    /// late (0 with no refreshes counted).</summary>
    public double LateShare(long nowNs, int seconds = 10)
    {
        var refreshes = RefreshesIn(nowNs, seconds);
        return refreshes > 0 ? MissedIn(nowNs, seconds) / (double)refreshes : 0;
    }

    /// <summary>Zeroes every counter (the caller journals the old totals first).</summary>
    public void Reset()
    {
        Presented = Shown = MissedVblanks = Glitches = Repeats = Discarded = Held = 0;
        Array.Fill(_bucketSecond, long.MinValue);
        Array.Clear(_bucketRefreshes);
        Array.Clear(_bucketMissed);
        Array.Clear(_used);
        _hasShown = false;
        LastShownNs = -1;
    }

    private void AddToBucket(long ns, long refreshes, long missed)
    {
        var second = Math.DivRem(ns, 1_000_000_000L, out var rem);
        if (rem < 0)
            second--;
        var b = (int)(((second % Buckets) + Buckets) % Buckets);
        if (_bucketSecond[b] != second)
        {
            _bucketSecond[b] = second;
            _bucketRefreshes[b] = 0;
            _bucketMissed[b] = 0;
        }
        _bucketRefreshes[b] += refreshes;
        _bucketMissed[b] += missed;
    }

    private long Sum(long nowNs, int seconds, bool missed)
    {
        seconds = Math.Clamp(seconds, 1, Buckets);
        var now = Math.DivRem(nowNs, 1_000_000_000L, out var rem);
        if (rem < 0)
            now--;
        long sum = 0;
        for (var b = 0; b < Buckets; b++)
        {
            var s = _bucketSecond[b];
            if (s > now - seconds && s <= now)
                sum += missed ? _bucketMissed[b] : _bucketRefreshes[b];
        }
        return sum;
    }
}
