namespace PlayMap.Core.Presentation;

/// <summary>Vblank truth for one display: a least-squares fit of <c>t = a + b·k</c> over samples (refresh index k, monotonic
/// instant t in ns), so the refresh period, its ppm against the nominal rate, and the instant of any refresh index are known to
/// well under a microsecond after a few seconds — whatever the scheduler's jitter on each sample.
///
/// <list type="bullet">
/// <item>Two windows: the period <c>b</c> from the long one (default 3 600 samples, 60 s at 60 Hz); the phase <c>a</c> from the
/// short one (240 samples) with <c>b</c> fixed, so a slow drift of the phase is followed within seconds.</item>
/// <item>Each fit is two-pass on values centred in the window (no running sums, so no precision lost over days of uptime), then
/// refitted without the samples whose residual lies more than a quarter period from the median residual (<see cref="Outliers"/>
/// counts them once each). The median, not the fit, is the centre: a minority of stamps late by a thread that waited for the CPU
/// cannot move it, so they never bend the period they are judged against.</item>
/// <item>Until it has settled the clock fits no slope, which a few late stamps would bend: the period is the nominal one (or, from
/// five samples, the median gap per refresh when that differs from the nominal by more than a tenth and the gaps agree with it:
/// a display that is not the rate it reports) and the phase is the median residual.</item>
/// <item>Samples come either from the display (<see cref="Add"/>: a refresh count, strictly newer each time) or from a vsync'd
/// swap's return (<see cref="AddSwapReturn"/>: a gap of n periods counts n − 1 missed vblanks, once). A return lands at or after
/// the vblank it waited for — late by however long its thread then waited — so its refresh is counted from the fitted instant
/// of the previous one, never from the previous return (a late return would shorten the next gap and shift every index after
/// it). On that path the phase is approximate.</item>
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
    private readonly bool[] _counted;
    private readonly double[] _residuals;
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
        _counted = new bool[longWindow];
        _residuals = new double[longWindow];
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

    /// <summary>Samples left out of a fit for a residual more than a quarter period from the median, each counted once.</summary>
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

    /// <summary>Adds the instant a vsync'd swap returned (at or after the vblank it waited for). Answers how many refreshes it
    /// moved on: 1 normally, n &gt; 1 when n − 1 vblanks passed with no swap (added to <see cref="Missed"/>), 0 when it came within
    /// half a period of the previous return (the same vblank; ignored). The count runs from the fitted instant of the previous
    /// refresh with a quarter period's allowance for the fit's phase, so a return late by up to three quarters of a period names
    /// its own refresh, and one later than that names at most itself wrongly.</summary>
    public int AddSwapReturn(long tNs)
    {
        if (_lastReturnNs < 0)
        {
            _lastReturnNs = tNs;
            Push(0, tNs);
            return 1;
        }
        if (tNs - _lastReturnNs < 0.5 * _periodNs)
            return 0;
        var step = Math.Max(1L, (long)Math.Floor((tNs - TimeOf(_lastK)) / _periodNs + 0.25));
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
        _k[_next] = k;
        _t[_next] = tNs;
        _outlier[_next] = false;
        _counted[_next] = false;
        _next = (_next + 1) % _k.Length;
        if (_count < _k.Length)
            _count++;
        _lastK = k;
        Samples++;
        Fit();
    }

    /// <summary>The period from the long window — a least-squares fit, then again without the samples far from the median
    /// residual — then the phase from the short window with the period fixed; all on values centred on the newest sample.</summary>
    private void Fit()
    {
        var newest = (_next - 1 + _k.Length) % _k.Length;
        var k0 = _k[newest];
        var t0 = _t[newest];
        if (_count >= 2 && !FixedPeriod)
        {
            for (var i = 0; i < _count; i++)
                _outlier[(newest - i + _k.Length) % _k.Length] = false;
            double b;
            if (Settled)
            {
                b = Slope(_count, k0, t0, ignoreOutliers: false);
                if (!Plausible(b))
                    b = _periodNs;
            }
            else
            {
                b = EarlyPeriod(newest);
            }
            if (_count >= 3)
            {
                Trim(newest, k0, t0, b);
                var trimmed = Settled ? Slope(_count, k0, t0, ignoreOutliers: true) : b;
                if (Plausible(trimmed))
                    b = trimmed;
            }
            _periodNs = b;
        }
        // The phase: the mean of t − b·k over the short window (its median until settled), as the fitted time of the newest
        // sample's refresh.
        var n = Math.Min(_count, _shortWindow);
        double sum = 0;
        var used = 0;
        for (var i = 0; i < n; i++)
        {
            var j = (newest - i + _k.Length) % _k.Length;
            if (_outlier[j])
                continue;
            var r = (_t[j] - t0) - _periodNs * (double)(long)(_k[j] - k0);
            _residuals[used] = r;
            sum += r;
            used++;
        }
        _baseK = k0;
        _baseT = t0;
        _baseOffsetNs = used == 0 ? 0 : Settled || FixedPeriod ? sum / used : Select(_residuals.AsSpan(0, used), used / 2);
        _hasBase = true;
    }

    private bool Plausible(double periodNs) => periodNs > 0.25 * NominalPeriodNs && periodNs < 4 * NominalPeriodNs;

    /// <summary>The period before the fit has settled: the nominal one, unless (from five samples) the median gap per refresh
    /// between consecutive samples differs from it by more than a tenth and two gaps in three agree with that median within a
    /// twentieth. A late stamp lengthens one gap and shortens the next; a display not at the rate it reports repeats its gap.</summary>
    private double EarlyPeriod(int newest)
    {
        if (_count < 5)
            return NominalPeriodNs;
        var gaps = _count - 1;
        for (var i = 0; i < gaps; i++)
        {
            var a = (newest - i + _k.Length) % _k.Length;
            var b = (newest - i - 1 + _k.Length) % _k.Length;
            _residuals[i] = (_t[a] - _t[b]) / (double)(long)(_k[a] - _k[b]);
        }
        var median = Select(_residuals.AsSpan(0, gaps), (gaps - 1) / 2);
        if (Math.Abs(median / NominalPeriodNs - 1) <= 0.1 || !Plausible(median))
            return NominalPeriodNs;
        // Late stamps scatter the gaps; a display at another rate than it reports gives the same gap again and again.
        var agree = 0;
        for (var i = 0; i < gaps; i++)
        {
            if (Math.Abs(_residuals[i] / median - 1) <= 0.05)
                agree++;
        }
        return 3 * agree >= 2 * gaps ? median : NominalPeriodNs;
    }

    /// <summary>Leaves out of the fit the samples whose residual against period <paramref name="b"/> lies more than a quarter of the
    /// nominal period from the median residual, counting each the first time.</summary>
    private void Trim(int newest, ulong k0, long t0, double b)
    {
        var n = _count;
        for (var i = 0; i < n; i++)
        {
            var j = (newest - i + _k.Length) % _k.Length;
            _residuals[i] = (_t[j] - t0) - b * (double)(long)(_k[j] - k0);
        }
        var median = Select(_residuals.AsSpan(0, n), n / 2);
        var limit = 0.25 * NominalPeriodNs;
        for (var i = 0; i < n; i++)
        {
            var j = (newest - i + _k.Length) % _k.Length;
            var r = (_t[j] - t0) - b * (double)(long)(_k[j] - k0);
            if (Math.Abs(r - median) <= limit)
                continue;
            _outlier[j] = true;
            if (_counted[j])
                continue;
            _counted[j] = true;
            Outliers++;
        }
    }

    /// <summary>The value that would stand at <paramref name="rank"/> were <paramref name="values"/> sorted (Hoare's selection,
    /// in place, no allocation).</summary>
    private static double Select(Span<double> values, int rank)
    {
        var lo = 0;
        var hi = values.Length - 1;
        while (lo < hi)
        {
            var pivot = values[lo + (hi - lo) / 2];
            var i = lo;
            var j = hi;
            while (i <= j)
            {
                while (values[i] < pivot)
                    i++;
                while (values[j] > pivot)
                    j--;
                if (i > j)
                    break;
                (values[i], values[j]) = (values[j], values[i]);
                i++;
                j--;
            }
            if (rank <= j)
                hi = j;
            else if (rank >= i)
                lo = i;
            else
                return values[rank];
        }
        return values[rank];
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
