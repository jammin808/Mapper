namespace PlayMap.Render.Timing;

/// <summary>An output's pacing from its swaps: the refresh period estimated from the intervals between
/// presents (the median of those near the nominal period, so a dropped frame does not stretch it), the next
/// vertical blank predicted from the last present, presents that took more than one refresh counted as
/// missed slots, and the frame interval's mean and 95th percentile. Fed the instant each vsync'd swap returned;
/// pure arithmetic on those instants, so a test feeds it a sequence.</summary>
public sealed class FramePacer
{
    private const int Window = 120;

    private readonly long[] _intervals = new long[Window];
    private readonly long[] _scratch = new long[Window];
    private int _count;
    private int _next;
    private long _lastPresentNs = -1;
    private long _periodNs;

    public FramePacer(double nominalHz)
    {
        NominalHz = nominalHz > 1 ? nominalHz : 60;
        _periodNs = (long)Math.Round(1e9 / NominalHz);
    }

    public double NominalHz { get; }

    /// <summary>The estimated refresh period, nanoseconds.</summary>
    public long PeriodNs => _periodNs;

    public double RefreshHz => 1e9 / _periodNs;

    /// <summary>Presents recorded.</summary>
    public long Presented { get; private set; }

    /// <summary>Refresh slots that passed without a new present (dropped frames).</summary>
    public long Missed { get; private set; }

    public long LastPresentNs => _lastPresentNs;

    /// <summary>Records a present: the instant its swap returned.</summary>
    public void OnPresented(long presentNs)
    {
        if (_lastPresentNs >= 0 && presentNs > _lastPresentNs)
        {
            var dt = presentNs - _lastPresentNs;
            _intervals[_next] = dt;
            _next = (_next + 1) % Window;
            if (_count < Window)
                _count++;
            var slots = (long)Math.Round(dt / (double)_periodNs);
            if (slots > 1)
                Missed += slots - 1;
            EstimatePeriod();
        }
        _lastPresentNs = presentNs;
        Presented++;
    }

    /// <summary>The next vertical blank after <paramref name="nowNs"/> (the last present plus whole periods);
    /// before any present, now plus one period.</summary>
    public long PredictNextVblank(long nowNs)
    {
        if (_lastPresentNs < 0)
            return nowNs + _periodNs;
        var k = Math.Max(1, (long)Math.Ceiling((nowNs - _lastPresentNs) / (double)_periodNs));
        return _lastPresentNs + k * _periodNs;
    }

    /// <summary>Mean present interval over the window, milliseconds (0 before two presents).</summary>
    public double FrameMs
    {
        get
        {
            if (_count == 0)
                return 0;
            long sum = 0;
            for (var i = 0; i < _count; i++)
                sum += _intervals[i];
            return sum / (double)_count / 1e6;
        }
    }

    /// <summary>The 95th percentile present interval, milliseconds.</summary>
    public double FrameMsP95 => Percentile(0.95) / 1e6;

    public double Fps => FrameMs > 0 ? 1000 / FrameMs : 0;

    private double Percentile(double q)
    {
        if (_count == 0)
            return 0;
        Array.Copy(_intervals, _scratch, _count);
        Array.Sort(_scratch, 0, _count);
        var i = Math.Clamp((int)Math.Ceiling(q * _count) - 1, 0, _count - 1);
        return _scratch[i];
    }

    private void EstimatePeriod()
    {
        var nominal = (long)Math.Round(1e9 / NominalHz);
        var n = 0;
        for (var i = 0; i < _count; i++)
        {
            var v = _intervals[i];
            if (v > nominal / 2 && v < nominal * 3 / 2)
                _scratch[n++] = v;
        }
        if (n < 8)
            return;
        Array.Sort(_scratch, 0, n);
        _periodNs = _scratch[n / 2];
    }
}

/// <summary>One output's numbers for STATE: presents a second, frames dropped, the mean and 95th-percentile
/// present interval, the CPU and GPU time its passes took, and the refresh the pacer measured.</summary>
public readonly record struct OutputStats(
    double Fps,
    long Presented,
    long Dropped,
    double FrameMs,
    double FrameMsP95,
    double RenderMs,
    double GpuMs,
    double RefreshHz);
