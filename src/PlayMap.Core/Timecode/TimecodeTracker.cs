namespace PlayMap.Core.Tc;

/// <summary>A fitted timecode line: at instant <see cref="AtNs"/> (mono, or leader once mapped) the code was at <see cref="TcNs"/>,
/// advancing <see cref="Rate"/> TC ns per ns while <see cref="Running"/>; the fit's residual σ, its points, its run (a counter that
/// rises at every discontinuity) and the direction.</summary>
public readonly record struct TcLine(long AtNs, long TcNs, double Rate, double SigmaNs, int Points, long Run, int Direction, bool Running)
{
    /// <summary>The code at an instant on the line (held when not running).</summary>
    public long TcAt(long ns) => Running ? TcNs + (long)Math.Round((ns - AtNs) * Rate) : TcNs;

    /// <summary>The instant the line reaches a code (long.MaxValue when it never does).</summary>
    public long At(long tcNs) => !Running || Rate <= 0 ? long.MaxValue : AtNs + (long)Math.Round((tcNs - TcNs) / Rate);
}

/// <summary>A source's error counts (cumulative; TIMECODE SOURCE RESET clears them).</summary>
public readonly record struct TcCounters(long Biphase, long Missing, long Duplicate, long Discontinuity, long Invalid, long BadBcd, long Parity)
{
    public static readonly TcCounters Zero = default;

    public TcCounters Plus(TcCounters o) => new(Biphase + o.Biphase, Missing + o.Missing, Duplicate + o.Duplicate, Discontinuity + o.Discontinuity,
        Invalid + o.Invalid, BadBcd + o.BadBcd, Parity + o.Parity);
}

/// <summary>A source as the chase reads it: its state ("none", "unavailable", "silent", "acquiring", "rolling", "parked", "reverse",
/// "dropout", "lost"), the fitted line, the format (configured, else detected), the frame rate measured from the fit, the last
/// observation, the label then, the counts and the words.</summary>
public sealed record TcReading(string State, TcLine? Line, TcFormat? Format, double MeasuredFps, long LastMonoNs, long Frame, TcCounters Counters,
    uint UserBits, string Words)
{
    public static readonly TcReading None = new("none", null, null, 0, long.MinValue, 0, TcCounters.Zero, 0, "no code yet");

    /// <summary>The label of the last code ("" before any).</summary>
    public string Text => Format is { } f && State != "none" ? f.Format(Frame) : "";
}

/// <summary>One timecode source's observations into a <see cref="TcReading"/> (pure; the caller serialises calls). A run is the
/// frames since the last discontinuity; its line is a robust least-squares fit over the window (outliers beyond max(4 × 1.4826 ×
/// MAD, 0.3 frame) dropped, then refitted), its slope bounded to 0.85–1.15 ("varispeed out of range" outside, and nothing
/// locks). A jump — an observation off the line by more than the jump threshold (a chase's <c>JUMP</c>, else auto: max(2 frames,
/// 5σ)) — is taken only when the next observation agrees with it, and starts a new run (a relocate); code off the line by no more
/// than the threshold is drift, and the fit takes it in. A threshold set wider than auto's widens drift only to steps the fit can
/// glide onto within the slope bounds: a step past auto's threshold whose glide would leave them is a jump whatever the threshold.
/// A lone outlier is counted invalid. No observation for 2.5 frames is a dropout; for 1 s, lost. A frame seen three times is
/// parked; an MTC full frame parks at once; a backward run is reverse.</summary>
public sealed class TimecodeTracker
{
    private const long LostNs = 1_000_000_000;
    private const double MinRate = 0.85;
    private const double MaxRate = 1.15;

    private readonly TcFormat? _configured;
    private readonly long _windowNs;
    private readonly int _minPoints;
    private readonly long _jumpNs;
    private readonly List<(long Mono, long Tc)> _points = [];
    private TcObservation? _last;
    private TcObservation? _pendingJump;
    private TcLine? _line;
    private TcFormat? _detected;
    private long _run;
    private int _repeats;
    private bool _located;
    private string _state = "none";
    private string _fault = "";
    private TcCounters _counts;

    /// <param name="configured">The format the source is set to (null = detect it from the code).</param>
    /// <param name="minPoints">Points before a run is rolling (LTC and Art-Net 3, MTC 2 sequences = 16 pieces).</param>
    /// <param name="jumpNs">The jump threshold the code is judged by (0 = auto: max(2 frames, 5σ)).</param>
    public TimecodeTracker(TcFormat? configured, int fitWindowMs = 1500, int minPoints = 3, long jumpNs = 0)
    {
        _configured = configured;
        _windowNs = Math.Max(200, fitWindowMs) * 1_000_000L;
        _minPoints = Math.Max(2, minPoints);
        _jumpNs = Math.Max(0, jumpNs);
    }

    private TimecodeTracker(TimecodeTracker from, long jumpNs)
        : this(from._configured, (int)(from._windowNs / 1_000_000), from._minPoints, jumpNs)
    {
        _points.AddRange(from._points);
        _last = from._last;
        _pendingJump = from._pendingJump;
        _line = from._line;
        _detected = from._detected;
        _run = from._run;
        _repeats = from._repeats;
        _located = from._located;
        _state = from._state;
        _fault = from._fault;
        _counts = from._counts;
        Reading = from.Reading;
    }

    public TcReading Reading { get; private set; } = TcReading.None;

    /// <summary>The jump threshold this tracker judges the code by (0 = auto).</summary>
    public long JumpNs => _jumpNs;

    /// <summary>The jump threshold in force: how far code may land off its fitted line and still be drift the fit takes in. Past it
    /// the code has jumped (relocated). <paramref name="jumpNs"/> when set, else auto: max(2 frames, 5σ).</summary>
    public static double JumpThresholdNs(long jumpNs, TcFormat format, double sigmaNs) =>
        jumpNs > 0 ? jumpNs : Math.Max(2 * format.FrameNs, 5 * sigmaNs);

    /// <summary>This source's code as it stands (its points, line, run, state and counts), judged from here on at another jump
    /// threshold: a chase that sets its threshold keeps its lock.</summary>
    public TimecodeTracker JudgedAt(long jumpNs) => new(this, jumpNs);

    /// <summary>The input could not open, or died: words for STATE (cleared by the next observation).</summary>
    public void Fault(string words, long nowNs)
    {
        _fault = words;
        _state = "unavailable";
        Publish(nowNs);
    }

    /// <summary>The input is open but below its floor.</summary>
    public void Silent(long nowNs)
    {
        if (_state is "silent")
            return;
        _state = "silent";
        _points.Clear();
        _line = null;
        Publish(nowNs);
    }

    /// <summary>Adds the decoder's counts (biphase, bad BCD, parity) to the tracker's own.</summary>
    public void Count(TcCounters more)
    {
        _counts = _counts.Plus(more);
        Reading = Reading with { Counters = _counts };
    }

    public void ResetCounts()
    {
        _counts = TcCounters.Zero;
        Reading = Reading with { Counters = _counts };
    }

    public void Observe(in TcObservation o)
    {
        _fault = "";
        var format = _configured ?? o.Format;
        _detected = o.Format;
        var obs = o with { Format = format };
        if (o.Kind == TcObsKind.Locate)
        {
            NewRun();
            _located = true;
            _state = "parked";
            _line = new TcLine(o.MonoNs, obs.TcNs, 0, 0, 1, _run, 0, false);
            _last = obs;
            Publish(o.MonoNs);
            return;
        }
        _located = false;
        if (o.Direction < 0)
        {
            if (_state != "reverse")
                NewRun();
            _state = "reverse";
            _line = new TcLine(o.MonoNs, obs.TcNs, 0, 0, 1, _run, -1, false);
            _last = obs;
            Publish(o.MonoNs);
            return;
        }
        Steps(obs);
        if (_line is { Running: true } line && _points.Count >= _minPoints)
        {
            var jump = JumpThresholdNs(_jumpNs, format, line.SigmaNs);
            var off = Math.Abs(obs.TcNs - line.TcAt(obs.MonoNs));
            // A step judged a jump stays one: the next observation that agrees with it relocates the run, whatever a rehearsal from
            // that observation would say.
            var agrees = _pendingJump is { } held && Math.Abs(obs.TcNs - (held.TcNs + (obs.MonoNs - held.MonoNs))) <= jump;
            if (off > jump || off > JumpThresholdNs(0, format, line.SigmaNs) && (agrees || !Glides(obs, line.Rate, format)))
            {
                if (agrees && _pendingJump is { } pending)
                {
                    _counts = _counts with { Discontinuity = _counts.Discontinuity + 1 };
                    NewRun();
                    _points.Add((pending.MonoNs, pending.TcNs));
                    _pendingJump = null;
                }
                else
                {
                    if (_pendingJump is not null)
                        _counts = _counts with { Invalid = _counts.Invalid + 1 };
                    _pendingJump = obs;
                    _last = obs;
                    Publish(o.MonoNs);
                    return;
                }
            }
            else if (_pendingJump is not null)
            {
                _counts = _counts with { Invalid = _counts.Invalid + 1 };
                _pendingJump = null;
            }
        }
        else if (_points.Count > 0 && Math.Abs(obs.TcNs - _points[^1].Tc - (obs.MonoNs - _points[^1].Mono)) > Math.Max(2 * format.FrameNs, 200_000_000))
        {
            // Before a line exists, a point far off the last one restarts the run.
            _counts = _counts with { Discontinuity = _counts.Discontinuity + 1 };
            NewRun();
        }
        _points.Add((obs.MonoNs, obs.TcNs));
        while (_points.Count > _minPoints && obs.MonoNs - _points[0].Mono > _windowNs)
            _points.RemoveAt(0);
        _last = obs;
        Fit(format);
        _state = _repeats >= 3 ? "parked" : _points.Count < _minPoints ? "acquiring" : _line is { Running: true } ? "rolling" : "acquiring";
        Publish(o.MonoNs);
    }

    /// <summary>Time passing without code: dropout after 2.5 frames, lost after 1 s (the line is dropped).</summary>
    public void Tick(long nowNs)
    {
        if (_last is not { } last || _located || _state is "none" or "unavailable" or "silent" or "lost")
            return;
        var age = nowNs - last.MonoNs;
        var next = age > LostNs ? "lost" : age > 2.5 * last.Format.FrameNs ? "dropout" : _state;
        if (next == _state)
            return;
        if (next == "lost")
        {
            _points.Clear();
            _line = null;
        }
        _state = next;
        Publish(nowNs);
    }

    private void Steps(in TcObservation obs)
    {
        if (_last is not { } last || last.Kind == TcObsKind.QuarterFrame || obs.Kind == TcObsKind.QuarterFrame)
        {
            _repeats = 0;
            return;
        }
        var step = obs.Frame - last.Frame;
        if (step == 0)
        {
            _repeats++;
            _counts = _counts with { Duplicate = _counts.Duplicate + 1 };
            return;
        }
        _repeats = 0;
        if (step is 2 or 3 && !obs.Format.Pairs)
            _counts = _counts with { Missing = _counts.Missing + step - 1 };
    }

    private void NewRun()
    {
        _run++;
        _points.Clear();
        _line = null;
        _repeats = 0;
        _pendingJump = null;
    }

    private void Fit(TcFormat format)
    {
        if (_points.Count == 0)
            return;
        var (lastMono, lastTc) = _points[^1];
        if (_points.Count == 1 || _repeats >= 3)
        {
            _line = new TcLine(lastMono, lastTc, _repeats >= 3 ? 0 : 1, 0, _points.Count, _run, 1, _repeats < 3 && _points.Count > 1);
            return;
        }
        var use = Enumerable.Range(0, _points.Count).ToList();
        var (slope, intercept, sigma) = Solve(use, lastMono, lastTc);
        var residuals = use.Select(i => Residual(i, slope, intercept, lastMono, lastTc)).ToList();
        var median = Median(residuals.Select(Math.Abs).ToList());
        var limit = Math.Max(4 * 1.4826 * median, 0.3 * format.FrameNs);
        var kept = use.Where((_, k) => Math.Abs(residuals[k]) <= limit).ToList();
        if (kept.Count >= 2 && kept.Count < use.Count)
            (slope, intercept, sigma) = Solve(kept, lastMono, lastTc);
        var running = slope is >= MinRate and <= MaxRate;
        _line = new TcLine(lastMono, lastTc + (long)Math.Round(intercept), running ? slope : 1, sigma, kept.Count, _run, 1, running);
        if (!running)
            _fault = "varispeed out of range (" + slope.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "×) — nothing locks";
    }

    /// <summary>Whether the fit glides onto code stepped off its line within the slope bounds, rehearsed on the window as it stands:
    /// the code taken to run on from this observation at the line's rate, a point at the window's cadence (at least an eighth of a
    /// frame apart), and the window's least-squares slope at each point until the window holds only the new code; a slope outside
    /// the bounds at any means it cannot. At a glide's steepest, half the window on each code, the fit's outlier rule drops nothing,
    /// so the fit's slope is the least-squares one there. The rehearsal lets an old point go one cadence early: the fit's window may
    /// hold a point fewer than the rehearsal's as the observations' jitter moves its edge, and the shorter window glides steeper, so
    /// a step whose glide would only touch a bound is a jump. Running sums: no allocation, one step per point.</summary>
    private bool Glides(in TcObservation obs, double rate, TcFormat format)
    {
        var (mono0, tc0) = (obs.MonoNs, obs.TcNs);
        double n = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
        void Take(long mono, long tc, int sign)
        {
            double x = mono - mono0, y = tc - tc0;
            n += sign;
            sx += sign * x;
            sy += sign * y;
            sxx += sign * x * x;
            sxy += sign * x * y;
        }

        foreach (var (mono, tc) in _points)
            Take(mono, tc, 1);
        var cadence = Math.Max((_points[^1].Mono - _points[0].Mono) / (_points.Count - 1), format.FrameNs / 8);
        var oldest = 0;
        for (var k = 0L; ; k++)
        {
            var at = mono0 + k * cadence;
            Take(at, tc0 + (long)Math.Round(k * cadence * rate), 1);
            while (n > _minPoints && oldest < _points.Count && at - _points[oldest].Mono > _windowNs - cadence)
            {
                Take(_points[oldest].Mono, _points[oldest].Tc, -1);
                oldest++;
            }
            var spread = n * sxx - sx * sx;
            var slope = spread > 0 ? (n * sxy - sx * sy) / spread : 1;
            if (slope is < MinRate or > MaxRate)
                return false;
            if (oldest == _points.Count)
                return true;
        }
    }

    private double Residual(int i, double slope, double intercept, long mono0, long tc0)
    {
        var (m, t) = _points[i];
        return (t - tc0) - (intercept + slope * (m - mono0));
    }

    /// <summary>Least squares of tc on mono, both relative to the newest point; answers the slope, the intercept at the newest point
    /// and the residual σ.</summary>
    private (double Slope, double Intercept, double Sigma) Solve(List<int> idx, long mono0, long tc0)
    {
        double sx = 0, sy = 0;
        foreach (var i in idx)
        {
            sx += _points[i].Mono - mono0;
            sy += _points[i].Tc - tc0;
        }
        var n = idx.Count;
        double mx = sx / n, my = sy / n, sxx = 0, sxy = 0;
        foreach (var i in idx)
        {
            var dx = _points[i].Mono - mono0 - mx;
            var dy = _points[i].Tc - tc0 - my;
            sxx += dx * dx;
            sxy += dx * dy;
        }
        var slope = sxx > 0 ? sxy / sxx : 1;
        var intercept = my - slope * mx;
        double ss = 0;
        foreach (var i in idx)
        {
            var r = (_points[i].Tc - tc0) - (intercept + slope * (_points[i].Mono - mono0));
            ss += r * r;
        }
        return (slope, intercept, n > 2 ? Math.Sqrt(ss / (n - 2)) : 0);
    }

    private static double Median(List<double> v)
    {
        v.Sort();
        return v.Count == 0 ? 0 : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }

    private void Publish(long nowNs)
    {
        var format = _configured ?? _detected;
        var measured = _line is { Running: true } l && format is { } f ? l.Rate * f.Rate.Value : 0;
        var frame = _last is { } last ? last.Frame : 0;
        var words = _fault.Length > 0 ? _fault : _state switch
        {
            "rolling" => $"rolling at {format?.Text} fps",
            "parked" => "parked (the code stands still)",
            "reverse" => "timecode running backwards — holding",
            "acquiring" => "code arriving — fitting its line",
            "dropout" => "no code for a moment — riding the last line",
            "lost" => "the code stopped",
            "silent" => "the input is open but silent (below its floor)",
            _ => "no code yet",
        };
        if (format is { } cf && _detected is { } d && _configured is not null && d.LabelFps != cf.LabelFps)
            words = $"{words}; the code says {d.Text} but the source is set to {cf.Text}";
        Reading = new TcReading(_state, _line, format, measured, _last?.MonoNs ?? nowNs, frame, _counts, _last?.UserBits ?? 0, words);
    }
}
