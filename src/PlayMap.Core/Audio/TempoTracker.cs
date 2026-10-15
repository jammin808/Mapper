namespace PlayMap.Core.Audio;

/// <summary>What the tracker hears: a tempo, the leader instant of the latest beat it places, and how sure it is (0–1).</summary>
public readonly record struct TempoEstimate(double Bpm, long BeatLeaderNs, double Confidence);

/// <summary>Tempo and beat detection from the onset strength (written from the published descriptions of comb-filtered
/// autocorrelation tempo estimation and cumulative beat scoring — Ellis 2007, Davies and Plumbley 2007 — with no tracker's code):
/// the last 8 s of the strength at 200 frames a second; every update, its autocorrelation summed at 1, 2, 3 and 4 periods for each
/// tempo in the window (0.05 BPM steps, the lags interpolated), weighted by a log-Gaussian prior an octave wide around 120 BPM; a
/// new tempo replaces the current one only when it is 1.2 × as salient on three updates running (an octave error does not flicker
/// in); the beat's phase from the strength summed at the period back from now. Pure and single-threaded; nothing is allocated per
/// frame.</summary>
public sealed class TempoTracker
{
    public const int Rate = AudioAnalyser.SampleRate / AudioAnalyser.Hop;
    public const int Seconds = 8;
    private const int Length = Rate * Seconds;
    private const double PriorBpm = 120;
    private const double PriorOctaves = 1;
    private const int Combs = 4;
    private const double Switch = 1.2;
    private const int SwitchRuns = 3;

    private readonly double _minBpm;
    private readonly double _maxBpm;
    private readonly float[] _strength = new float[Length];
    private readonly long[] _stamps = new long[Length];
    private readonly double[] _x = new double[Length];
    private readonly double[] _acf;
    private long _count;
    private double _current;
    private double _challenger;
    private int _challengerRuns;

    public TempoTracker(double minBpm = 75, double maxBpm = 180)
    {
        if (!(minBpm >= 30 && maxBpm > minBpm && maxBpm <= 300))
            throw new ArgumentOutOfRangeException(nameof(minBpm), "the window is 30 to 300 BPM, its low end below its high end");
        _minBpm = minBpm;
        _maxBpm = maxBpm;
        _acf = new double[(int)Math.Ceiling(Combs * 60.0 * Rate / minBpm) + 2];
    }

    /// <summary>The tempo window searched.</summary>
    public double MinBpm => _minBpm;

    public double MaxBpm => _maxBpm;

    /// <summary>Frames taken since the last reset.</summary>
    public long Frames => _count;

    /// <summary>The tempo held now (0 before the first estimate).</summary>
    public double Bpm => _current;

    /// <summary>Takes one frame's onset strength and its leader instant (frames in order, one per hop).</summary>
    public void Push(float strength, long leaderNs)
    {
        var i = (int)(_count % Length);
        _strength[i] = Math.Max(0, strength);
        _stamps[i] = leaderNs;
        _count++;
    }

    public void Reset()
    {
        _count = 0;
        _current = 0;
        _challenger = 0;
        _challengerRuns = 0;
    }

    /// <summary>Estimates from what it holds (at least 3 s); null before that, or when the strength is flat (nothing to follow).</summary>
    public TempoEstimate? Update()
    {
        var n = (int)Math.Min(_count, Length);
        if (n < 3 * Rate)
            return null;
        // The strength in time order, its mean taken off.
        var first = _count - n;
        var mean = 0.0;
        for (var k = 0; k < n; k++)
            mean += _strength[(int)((first + k) % Length)];
        mean /= n;
        var energy = 0.0;
        for (var k = 0; k < n; k++)
        {
            var v = _strength[(int)((first + k) % Length)] - mean;
            _x[k] = v;
            energy += v * v;
        }
        if (energy <= 1e-9)
            return null;
        var maxLag = Math.Min(_acf.Length - 1, n - 1);
        for (var lag = 0; lag <= maxLag; lag++)
        {
            var s = 0.0;
            for (var k = lag; k < n; k++)
                s += _x[k] * _x[k - lag];
            _acf[lag] = s / (n - lag);
        }
        var (best, bestScore, meanScore) = Scan(maxLag);
        if (best <= 0)
            return null;
        Adopt(best, bestScore, maxLag);
        var period = 60.0 * Rate / _current;
        var (beatIndex, phaseScore) = Phase(n, period);
        var confidence = Math.Clamp((bestScore - meanScore) / Math.Max(1e-12, bestScore) * Math.Min(1, phaseScore), 0, 1);
        return new TempoEstimate(_current, StampAt(first, beatIndex), confidence);
    }

    /// <summary>The most salient tempo in the window, its salience and the window's mean salience.</summary>
    private (double Bpm, double Score, double Mean) Scan(int maxLag)
    {
        double best = 0, bestScore = double.MinValue, sum = 0;
        var count = 0;
        for (var bpm = _minBpm; bpm <= _maxBpm + 1e-9; bpm += 0.05)
        {
            var score = Salience(bpm, maxLag);
            sum += score;
            count++;
            if (score > bestScore)
            {
                bestScore = score;
                best = bpm;
            }
        }
        return (best, bestScore, count == 0 ? 0 : sum / count);
    }

    private double Salience(double bpm, int maxLag)
    {
        var period = 60.0 * Rate / bpm;
        var s = 0.0;
        for (var k = 1; k <= Combs; k++)
        {
            var lag = k * period;
            var i = (int)Math.Floor(lag);
            if (i + 1 > maxLag)
                break;
            var t = lag - i;
            s += _acf[i] * (1 - t) + _acf[i + 1] * t;
        }
        var octaves = Math.Log2(bpm / PriorBpm) / PriorOctaves;
        return s * Math.Exp(-0.5 * octaves * octaves);
    }

    /// <summary>The candidate becomes the tempo at once when there is none, or after beating it by 1.2 × on three updates running.</summary>
    private void Adopt(double candidate, double score, int maxLag)
    {
        if (_current <= 0 || Math.Abs(candidate - _current) < 1.0)
        {
            // The first tempo, or the same one moving a little: taken as it is.
            _current = candidate;
            _challengerRuns = 0;
            return;
        }
        var held = Salience(_current, maxLag);
        if (score >= Switch * held && Math.Abs(candidate - _challenger) < 1.0)
            _challengerRuns++;
        else
            _challengerRuns = score >= Switch * held ? 1 : 0;
        _challenger = candidate;
        if (_challengerRuns >= SwitchRuns)
        {
            _current = candidate;
            _challengerRuns = 0;
        }
    }

    /// <summary>The index (from the oldest held frame) of the latest beat — the phase whose comb back from now collects the most
    /// strength — refined between frames, and how much of the strength the comb collects against its mean (0–1).</summary>
    private (double Index, double Score) Phase(int n, double period)
    {
        var steps = (int)Math.Ceiling(period);
        double best = double.MinValue, bestPhase = 0, total = 0;
        Span<double> scores = stackalloc double[steps + 2];
        for (var p = 0; p < steps; p++)
        {
            var s = 0.0;
            for (double at = n - 1 - p; at >= 0; at -= period)
                s += Interp(at);
            scores[p] = s;
            total += s;
            if (s > best)
            {
                best = s;
                bestPhase = p;
            }
        }
        // Parabolic refinement around the best phase (circular).
        var b = (int)bestPhase;
        var before = scores[(b - 1 + steps) % steps];
        var after = scores[(b + 1) % steps];
        var curve = before - 2 * best + after;
        var offset = Math.Abs(curve) > 1e-12 ? Math.Clamp(0.5 * (before - after) / curve, -0.5, 0.5) : 0;
        var meanScore = total / steps;
        return (n - 1 - (bestPhase + offset), best <= 0 ? 0 : Math.Clamp((best - meanScore) / best, 0, 1));
    }

    private double Interp(double at)
    {
        var i = (int)Math.Floor(at);
        var t = at - i;
        var a = _x[i];
        var b = i + 1 < _x.Length ? _x[i + 1] : a;
        return Math.Max(0, a * (1 - t) + b * t);
    }

    /// <summary>The leader instant at a fractional index of the held frames, between the two stamps around it.</summary>
    private long StampAt(long first, double index)
    {
        var i = (int)Math.Floor(index);
        var t = index - i;
        var a = _stamps[(int)((first + i) % Length)];
        var b = i + 1 < Length && first + i + 1 < _count ? _stamps[(int)((first + i + 1) % Length)] : a + 5_000_000;
        return a + (long)Math.Round((b - a) * t);
    }
}
