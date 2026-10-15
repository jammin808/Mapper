namespace PlayMap.Core.Time;

/// <summary>One NTP-style exchange: the client sends at t1 (its clock), the server receives at t2 and
/// answers at t3 (its clock), the client receives at t4. Units are whatever the link speaks — Show Bus
/// lines use Unix milliseconds, PlayMap's sync channel nanoseconds.</summary>
public readonly record struct ClockSample(long T1, long T2, long T3, long T4)
{
    /// <summary>Server clock minus client clock.</summary>
    public double Offset => ((T2 - T1) + (double)(T3 - T4)) / 2;

    public long RoundTrip => (T4 - T1) - (T3 - T2);
}

/// <summary>The offset of a peer's clock (Show Bus §5): keep the sample with the smallest round trip of
/// the last eight. Beside it, a drift estimate — the least-squares slope of the best samples' offsets
/// against local time — so a follower between exchanges extrapolates instead of stepping.</summary>
public sealed class ClockOffsetEstimator
{
    private readonly ClockSample[] _window;
    private readonly List<(double Local, double Offset)> _trend = [];
    private readonly int _trendCapacity;
    private int _count;
    private int _next;

    public ClockOffsetEstimator(int window = 8, int trendCapacity = 32)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);
        _window = new ClockSample[window];
        _trendCapacity = trendCapacity;
    }

    public int Samples => _count;
    public bool HasEstimate => _count > 0;

    /// <summary>The sample with the smallest round trip in the window.</summary>
    public ClockSample Best { get; private set; }

    /// <summary>Peer minus local, from the best sample.</summary>
    public double Offset => Best.Offset;

    public long RoundTrip => Best.RoundTrip;

    /// <summary>Peer clock rate relative to local, minus one (parts per unit): 1e-5 is 10 ppm fast.</summary>
    public double Drift { get; private set; }

    /// <summary>Adds a sample; one where either clock ran backwards mid-exchange (a negative round trip, a
    /// reply sent before it was received) is refused.</summary>
    public bool Add(ClockSample s)
    {
        if (s.RoundTrip < 0 || s.T4 < s.T1 || s.T3 < s.T2)
            return false;
        _window[_next] = s;
        _next = (_next + 1) % _window.Length;
        _count = Math.Min(_count + 1, _window.Length);
        var best = _window[0];
        for (var i = 1; i < _count; i++)
            if (_window[i].RoundTrip < best.RoundTrip)
                best = _window[i];
        Best = best;

        // The trend keeps only samples whose round trip is within twice the best: queueing delay is the
        // noise, and it only ever adds.
        if (s.RoundTrip <= Math.Max(1, 2 * best.RoundTrip))
        {
            _trend.Add(((s.T1 + s.T4) / 2.0, s.Offset));
            if (_trend.Count > _trendCapacity)
                _trend.RemoveAt(0);
            Drift = Slope(_trend);
        }
        return true;
    }

    /// <summary>The estimated offset at a local instant, the drift applied from the best sample's midpoint.</summary>
    public double OffsetAt(long local)
    {
        if (_count == 0)
            return 0;
        var mid = (Best.T1 + Best.T4) / 2.0;
        return Best.Offset + Drift * (local - mid);
    }

    public void Reset()
    {
        _count = 0;
        _next = 0;
        _trend.Clear();
        Drift = 0;
        Best = default;
    }

    private static double Slope(List<(double Local, double Offset)> pts)
    {
        if (pts.Count < 4)
            return 0;
        double mx = 0, my = 0;
        foreach (var (x, y) in pts)
        {
            mx += x;
            my += y;
        }
        mx /= pts.Count;
        my /= pts.Count;
        double sxx = 0, sxy = 0;
        foreach (var (x, y) in pts)
        {
            sxx += (x - mx) * (x - mx);
            sxy += (x - mx) * (y - my);
        }
        return sxx > 0 ? sxy / sxx : 0;
    }
}
