namespace PlayMap.Core.Audio;

/// <summary>Onsets from the spectrum, causally, one hop behind. The onset strength of a hop is the log spectral flux against the
/// previous hop's spectrum widened by a 3-bin maximum filter: Σ max(0, L[k] − max(L'[k−1], L'[k], L'[k+1])) with
/// L = ln(1 + 100·|X|) (a vibrato or a slow glide moves energy to a neighbouring bin and is not an onset). A hop is an onset when
/// its strength is the largest of the two hops before it and the one after it, exceeds the mean of the 200 ms before it by an
/// adaptive margin — the largest of a floor, 0.15 × the strength's slow peak (2 s release) and five standard deviations of those
/// 200 ms (steady noise flickers; an onset stands out of it) — and comes at least 30 ms after the previous onset.</summary>
public sealed class OnsetDetector
{
    /// <summary>The shortest gap between two onsets, in hops (6 × 5 ms = 30 ms).</summary>
    public const int MinGapHops = 6;

    private const int MeanHops = 40;
    private const double Floor = 0.5;
    private const double PeakShare = 0.15;
    private const double Deviations = 5;

    private readonly int _bins;
    private readonly double[] _previous;
    private readonly double[] _current;
    private readonly double[] _history = new double[MeanHops];
    private double _historySum;
    private double _historySumSq;
    private long _hops;
    private double _odf0;
    private double _odf1;
    private double _odf2;
    private double _odf3;
    private double _slowPeak;
    private readonly double _peakDecay;
    private long _lastOnsetHop = long.MinValue / 2;

    /// <param name="bins">The spectrum's bins (DC to Nyquist).</param>
    /// <param name="hopSeconds">One hop's length, for the slow peak's 2 s release.</param>
    public OnsetDetector(int bins, double hopSeconds)
    {
        _bins = bins;
        _previous = new double[bins];
        _current = new double[bins];
        _peakDecay = Math.Exp(-hopSeconds / 2.0);
    }

    /// <summary>The onset strength of the hop just given.</summary>
    public double Strength => _odf0;

    /// <summary>The strength's slow peak (2 s release): what a strength is measured against.</summary>
    public double SlowPeak => _slowPeak;

    /// <summary>Takes one hop's magnitudes (scaled so a full-scale sine's bin reads about 1); true when the hop before this one is an
    /// onset.</summary>
    public bool Hop(ReadOnlySpan<double> magnitudes)
    {
        var flux = 0.0;
        for (var k = 0; k < _bins; k++)
        {
            var l = Math.Log(1 + 100 * magnitudes[k]);
            _current[k] = l;
            var lo = k > 0 ? _previous[k - 1] : _previous[k];
            var hi = k < _bins - 1 ? _previous[k + 1] : _previous[k];
            var reference = Math.Max(_previous[k], Math.Max(lo, hi));
            if (l > reference)
                flux += l - reference;
        }
        _current.AsSpan().CopyTo(_previous);
        var first = _hops == 0;
        _hops++;
        _odf3 = _odf2;
        _odf2 = _odf1;
        _odf1 = _odf0;
        // The first hop compares against silence: its flux is the whole spectrum and is not an onset of anything heard.
        _odf0 = first ? 0 : flux;
        // The history holds the hops before the candidate (it is added after the decision), so a peak never raises its own bar.
        var mean = _historySum / MeanHops;
        var deviation = Math.Sqrt(Math.Max(0, _historySumSq / MeanHops - mean * mean));
        var candidate = _odf1;
        var isOnset = _hops >= 3
            && candidate >= _odf2 && candidate >= _odf3 && candidate > _odf0
            && candidate > mean + Math.Max(Floor, Math.Max(PeakShare * _slowPeak, Deviations * deviation))
            && _hops - 1 - _lastOnsetHop >= MinGapHops;
        if (isOnset)
            _lastOnsetHop = _hops - 1;
        var slot = (int)(_hops % MeanHops);
        _historySum += candidate - _history[slot];
        _historySumSq += candidate * candidate - _history[slot] * _history[slot];
        _history[slot] = candidate;
        if (slot == 0)
        {
            // Summed afresh once a window, so hours of running sums carry no rounding drift.
            _historySum = 0;
            _historySumSq = 0;
            foreach (var h in _history)
            {
                _historySum += h;
                _historySumSq += h * h;
            }
        }
        _slowPeak = Math.Max(_odf0, _slowPeak * _peakDecay);
        return isOnset;
    }

    public void Reset()
    {
        Array.Clear(_previous);
        Array.Clear(_history);
        _historySum = 0;
        _historySumSq = 0;
        _hops = 0;
        _odf0 = _odf1 = _odf2 = _odf3 = 0;
        _slowPeak = 0;
        _lastOnsetHop = long.MinValue / 2;
    }
}
