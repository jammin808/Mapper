namespace PlayMap.Render.Timing;

/// <summary>Vblank truth for one display: a least-squares fit of <c>t = a + b·k</c> over samples (refresh index k, monotonic
/// instant t in ns), so the refresh period, its ppm against the nominal rate, and the instant of any refresh index are known to
/// well under a microsecond after a few seconds — whatever the scheduler's jitter on each sample.
///
/// <list type="bullet">
/// <item>Two windows: the period <c>b</c> from the long one (default 3 600 samples, 60 s at 60 Hz); the phase <c>a</c> from the
/// short one (240 samples) with <c>b</c> fixed, so a slow drift of the phase is followed within seconds.</item>
/// <item>Each fit is two-pass on values centred in the window (no running sums, so no precision lost over days of uptime), then
/// refitted without the samples whose residual exceeds a quarter period (<see cref="Outliers"/> counts them once each).</item>
/// <item>Samples come either from the display (<see cref="Add"/>: a refresh count, strictly newer each time) or from a vsync'd
/// swap's return (<see cref="AddSwapReturn"/>: the index is counted from the gap to the previous return, and a gap of n periods
/// counts n − 1 missed vblanks, once). A swap's return is near, not at, the vblank, so on that path the phase is approximate.</item>
/// <item>Settled after 30 samples; <see cref="Disjoint"/> (a mode change, a display change) starts the fit again.</item>
/// </list>
/// Pure arithmetic on the samples it is given: a test feeds it a sequence. Allocation-free after construction. One thread.</summary>
public sealed class VblankClock
{
    public const int DefaultShortWindow = 240;
    public const int DefaultLongWindow = 3600;
    public const int SettleSamples = 30;

    private readonly int _shortWindow;
    private readonly ulong[] _k;
    private readonly long[] _t;
    private readonly bool[] _outlier;
    private int _count;
    private int _next;
    private double _periodNs;
    private ulong _baseK;
    private long _baseT;
    private double _baseOffsetNs;
    private bool _hasBase;
    private long _lastReturnNs = -1;
    private ulong _lastK;

    public VblankClock(double nominalHz, int shortWindow = DefaultShortWindow, int longWindow = DefaultLongWindow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(shortWindow, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(longWindow, shortWindow);
        NominalHz = nominalHz is > 1 and <= 1000 && double.IsFinite(nominalHz) ? nominalHz : 60;
        NominalPeriodNs = 1e9 / NominalHz;
        _periodNs = NominalPeriodNs;
        _shortWindow = shortWindow;
        _k = new ulong[longWindow];
        _t = new long[longWindow];
        _outlier = new bool[longWindow];
    }

    public double NominalHz { get; }

    /// <summary>The period stays the nominal one (an offscreen output, a window whose presents do not wait): samples only count
    /// refreshes and phase, they fit no rate.</summary>
    public bool FixedPeriod { get; init; }

    public double NominalPeriodNs { get; }

    /// <summary>The fitted refresh period, ns (the nominal one until two samples).</summary>
    public double PeriodNs => _periodNs;

    /// <summary>The fitted period in picoseconds, an integer every node computes the same way from the same fit.</summary>
    public long PeriodPs => (long)Math.Round(_periodNs * 1000);

    public double RefreshHz => 1e9 / _periodNs;

    /// <summary>The measured period against the nominal, parts per million (positive: the display runs slow).</summary>
    public double Ppm => (_periodNs - NominalPeriodNs) / NominalPeriodNs * 1e6;

    /// <summary>Samples in the fit now.</summary>
    public int Count => _count;

    /// <summary>Samples taken since construction (or the last <see cref="Disjoint"/>).</summary>
    public long Samples { get; private set; }

    /// <summary>Samples left out of a fit for a residual over a quarter period, each counted once.</summary>
    public long Outliers { get; private set; }

    /// <summary>Vblanks a swap's return skipped (a gap of n periods is n − 1 of them), each counted once.</summary>
    public long Missed { get; private set; }

    public bool Settled => _count >= SettleSamples;

    public bool HasPhase => _hasBase;

    /// <summary>The refresh index of the newest sample.</summary>
    public ulong LastIndex => _lastK;

    /// <summary>The fitted instant of the newest sample's refresh (−1 before any).</summary>
    public long LastVblankNs => _hasBase ? TimeOf(_lastK) : -1;

    /// <summary>Adds a display-side sample: refresh <paramref name="k"/> began at <paramref name="tNs"/>. A sample that is not
    /// strictly newer than the last is ignored (false).</summary>
    public bool Add(ulong k, long tNs)
    {
        if (_count > 0 && k <= _lastK)
            return false;
        Push(k, tNs);
        return true;
    }

    /// <summary>Adds the instant a vsync'd swap returned (just after the vblank it waited for). Answers how many refreshes it
    /// moved on: 1 normally, n &gt; 1 when n − 1 vblanks passed with no swap (added to <see cref="Missed"/>), 0 when it came within
    /// half a period of the previous return (the same vblank; ignored).</summary>
    public int AddSwapReturn(long tNs)
    {
        if (_lastReturnNs < 0)
        {
            _lastReturnNs = tNs;
            Push(0, tNs);
            return 1;
        }
        var dt = tNs - _lastReturnNs;
        var step = (long)Math.Round(dt / _periodNs);
        if (step < 1)
            return 0;
        _lastReturnNs = tNs;
        Missed += step - 1;
        Push(_lastK + (ulong)step, tNs);
        return (int)Math.Min(step, int.MaxValue);
    }

    /// <summary>The fitted instant refresh <paramref name="k"/> begins.</summary>
    public long TimeOf(ulong k)
    {
        if (!_hasBase)
            return 0;
        var dk = (double)(long)(k - _baseK);
        return _baseT + (long)Math.Round(_baseOffsetNs + _periodNs * dk);
    }

    /// <summary>The refresh in progress at <paramref name="tNs"/> (the last one begun at or before it); 0 before any sample.</summary>
    public ulong IndexAt(long tNs)
    {
        if (!_hasBase)
            return 0;
        var x = Math.Floor((tNs - _baseT - _baseOffsetNs) / _periodNs);
        return Offset(x);
    }

    /// <summary>The refresh whose start is nearest <paramref name="tNs"/> (a swap's return, which lands just after it).</summary>
    public ulong NearestIndex(long tNs)
    {
        if (!_hasBase)
            return 0;
        var x = Math.Round((tNs - _baseT - _baseOffsetNs) / _periodNs);
        return Offset(x);
    }

    /// <summary>The first refresh that begins at or after <paramref name="tNs"/> (where a picture finished at that instant first
    /// shows on a display that takes it at the next vblank).</summary>
    public ulong FirstIndexAtOrAfter(long tNs)
    {
        if (!_hasBase)
            return 0;
        var x = Math.Ceiling((tNs - _baseT - _baseOffsetNs) / _periodNs);
        return Offset(x);
    }

    /// <summary>The next vblank after <paramref name="tNs"/>; before any sample, one nominal period on.</summary>
    public long NextVblank(long tNs) => _hasBase ? TimeOf(IndexAt(tNs) + 1) : tNs + (long)Math.Round(_periodNs);

    /// <summary>Starts the fit again (a mode change, a display change, a stall the samples cannot bridge); the period estimate
    /// stays as the starting point.</summary>
    public void Disjoint()
    {
        _count = 0;
        _next = 0;
        Samples = 0;
        _hasBase = false;
        _lastReturnNs = -1;
        _lastK = 0;
    }

    /// <summary>Anchors the index to an instant with no fit (an offscreen output, or one whose presents never wait): refresh 0
    /// begins at <paramref name="tNs"/> and every period after it is the nominal one.</summary>
    public void AnchorNominal(long tNs)
    {
        Disjoint();
        _periodNs = NominalPeriodNs;
        _baseK = 0;
        _baseT = tNs;
        _baseOffsetNs = 0;
        _hasBase = true;
    }

    private ulong Offset(double x)
    {
        if (x >= 0)
            return _baseK + (ulong)x;
        var back = (ulong)(-x);
        return back >= _baseK ? 0 : _baseK - back;
    }

    private void Push(ulong k, long tNs)
    {
        var predicted = _hasBase && _count >= 2 ? TimeOf(k) : tNs;
        _k[_next] = k;
        _t[_next] = tNs;
        _outlier[_next] = false;
        var newest = _next;
        _next = (_next + 1) % _k.Length;
        if (_count < _k.Length)
            _count++;
        _lastK = k;
        Samples++;
        Fit();
        // The newest sample is counted as an outlier once, when it arrives (a later window that keeps leaving it out does not
        // count it again).
        if (_count >= SettleSamples && !FixedPeriod && Math.Abs(tNs - predicted) > 0.25 * _periodNs)
        {
            _outlier[newest] = true;
            Outliers++;
            Fit();
        }
    }

    /// <summary>The period from the long window, then the phase from the short window with the period fixed; both on values
    /// centred on the newest sample.</summary>
    private void Fit()
    {
        var newest = (_next - 1 + _k.Length) % _k.Length;
        var k0 = _k[newest];
        var t0 = _t[newest];
        if (_count >= 2 && !FixedPeriod)
        {
            var b = Slope(_count, k0, t0, ignoreOutliers: true);
            if (b > 0.25 * NominalPeriodNs && b < 4 * NominalPeriodNs)
                _periodNs = b;
        }
        // The phase: the mean of t − b·k over the short window, as the fitted time of the newest sample's refresh.
        var n = Math.Min(_count, _shortWindow);
        double sum = 0;
        var used = 0;
        for (var i = 0; i < n; i++)
        {
            var j = (newest - i + _k.Length) % _k.Length;
            if (_outlier[j])
                continue;
            sum += (_t[j] - t0) - _periodNs * (double)(long)(_k[j] - k0);
            used++;
        }
        _baseK = k0;
        _baseT = t0;
        _baseOffsetNs = used > 0 ? sum / used : 0;
        _hasBase = true;
    }

    /// <summary>The least-squares slope over the newest <paramref name="n"/> samples, centred (two passes).</summary>
    private double Slope(int n, ulong k0, long t0, bool ignoreOutliers)
    {
        var newest = (_next - 1 + _k.Length) % _k.Length;
        double mk = 0, mt = 0;
        var used = 0;
        for (var i = 0; i < n; i++)
        {
            var j = (newest - i + _k.Length) % _k.Length;
            if (ignoreOutliers && _outlier[j])
                continue;
            mk += (double)(long)(_k[j] - k0);
            mt += _t[j] - t0;
            used++;
        }
        if (used < 2)
            return _periodNs;
        mk /= used;
        mt /= used;
        double skk = 0, skt = 0;
        for (var i = 0; i < n; i++)
        {
            var j = (newest - i + _k.Length) % _k.Length;
            if (ignoreOutliers && _outlier[j])
                continue;
            var dk = (double)(long)(_k[j] - k0) - mk;
            var dt = (_t[j] - t0) - mt;
            skk += dk * dk;
            skt += dk * dt;
        }
        return skk > 0 ? skt / skk : _periodNs;
    }
}
