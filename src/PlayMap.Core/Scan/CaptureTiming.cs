using System.Globalization;

namespace PlayMap.Core.Scan;

/// <summary>A camera frame's mean brightness (0–255) at the instant the frame was received (ms on the clock the
/// flashes are stamped with).</summary>
public readonly record struct BrightnessSample(double TimeMs, double Brightness);

/// <summary>The glass-to-capture latency: from a change of the projected picture to the first frame that shows
/// it half changed. <see cref="SpreadMs"/> is the largest deviation of one transition from the median.</summary>
public sealed record LatencyEstimate(double LatencyMs, double SpreadMs, int Transitions, double FramePeriodMs)
{
    public string Words => string.Create(CultureInfo.InvariantCulture,
        $"Camera latency {LatencyMs:0} ms (± {SpreadMs:0} ms over {Transitions} flashes, a frame every {FramePeriodMs:0.0} ms)");
}

/// <summary>
/// Measures how late the camera sees what the projector shows: the engine flashes white (and back to black)
/// a few times, stamping each change at the instant it reached the glass, and feeds every camera frame's mean
/// brightness with its arrival time. Per change: the level before (median of the frames before the change),
/// the level after (median of the last third of the frames before the next change), and the instant the
/// frames crossed half-way, interpolated between the two frames either side — never from before the change
/// itself, which no frame can have seen, so a camera that sees the change whole in the first frame after it is
/// never made out to see it early; a crossing must hold for two frames, so one noisy frame does not count. The
/// latency is the median over the changes, and is never below zero unless the camera's stamps are.
/// </summary>
public sealed class LatencyEstimator
{
    private readonly List<BrightnessSample> _samples = [];
    private readonly List<double> _changes = [];

    /// <summary>The frames a change is measured from: those stamped after it, before the next.</summary>
    public const int FramesAfterChange = 4;

    /// <summary>The smallest brightness step a flash must make to be measured.</summary>
    public double MinStep { get; init; } = 10;

    public int SampleCount => _samples.Count;

    public void AddFrame(double timeMs, double meanBrightness) => _samples.Add(new BrightnessSample(timeMs, meanBrightness));

    /// <summary>Adds a gray8 frame by its mean brightness (every <paramref name="stride"/>-th pixel).</summary>
    public void AddFrame(double timeMs, ReadOnlySpan<byte> frame, int stride = 4) => AddFrame(timeMs, MeanBrightness(frame, stride));

    /// <summary>The instant the projected picture changed (white on, or back to black).</summary>
    public void AddChange(double timeMs) => _changes.Add(timeMs);

    public static double MeanBrightness(ReadOnlySpan<byte> frame, int stride = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, 1);
        long sum = 0;
        var n = 0;
        for (var i = 0; i < frame.Length; i += stride)
        {
            sum += frame[i];
            n++;
        }
        return n > 0 ? (double)sum / n : 0;
    }

    /// <summary>The estimate, or null when no change was seen clearly.</summary>
    public LatencyEstimate? Estimate()
    {
        if (_samples.Count < 4 || _changes.Count == 0)
            return null;
        var samples = _samples.OrderBy(s => s.TimeMs).ToArray();
        var changes = _changes.Order().ToArray();
        var latencies = new List<double>();
        for (var c = 0; c < changes.Length; c++)
        {
            var start = changes[c];
            var previous = c > 0 ? changes[c - 1] : double.NegativeInfinity;
            var next = c + 1 < changes.Length ? changes[c + 1] : double.PositiveInfinity;
            if (Measure(samples, previous, start, next) is { } latency)
                latencies.Add(latency);
        }
        if (latencies.Count == 0)
            return null;
        latencies.Sort();
        var median = latencies[latencies.Count / 2];
        if (latencies.Count % 2 == 0)
            median = 0.5 * (latencies[latencies.Count / 2 - 1] + latencies[latencies.Count / 2]);
        var spread = latencies.Max(l => Math.Abs(l - median));
        var periods = new List<double>();
        for (var i = 1; i < samples.Length; i++)
            periods.Add(samples[i].TimeMs - samples[i - 1].TimeMs);
        periods.Sort();
        return new LatencyEstimate(median, spread, latencies.Count, periods[periods.Count / 2]);
    }

    private double? Measure(BrightnessSample[] samples, double previous, double start, double next)
    {
        // Frames that arrived before the change cannot have seen it: the level before.
        var before = samples.Where(s => s.TimeMs <= start && s.TimeMs > previous).Select(s => s.Brightness).ToList();
        var during = samples.Where(s => s.TimeMs > start && s.TimeMs < next).ToArray();
        if (before.Count == 0 || during.Length < FramesAfterChange)
            return null;
        var levelBefore = Median(before);
        var levelAfter = Median(during.Skip(during.Length * 2 / 3).Select(s => s.Brightness).ToList());
        var step = levelAfter - levelBefore;
        if (Math.Abs(step) < MinStep)
            return null;
        double Progress(BrightnessSample s) => (s.Brightness - levelBefore) / step;
        for (var i = 0; i < during.Length; i++)
        {
            if (Progress(during[i]) < 0.5 || (i + 1 < during.Length && Progress(during[i + 1]) < 0.5))
                continue;
            // The frame before the crossing: the last one before the change when the crossing is the first after it.
            var prior = i > 0 ? during[i - 1] : samples.Last(s => s.TimeMs <= start);
            var p0 = Progress(prior);
            var p1 = Progress(during[i]);
            var t = p1 - p0 > 1e-9 ? Math.Clamp((0.5 - p0) / (p1 - p0), 0, 1) : 1;
            // The light changed at the change at the earliest: a frame from before it bounds nothing earlier.
            var from = Math.Max(prior.TimeMs, start);
            return from + t * (during[i].TimeMs - from) - start;
        }
        return null;
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        var n = values.Count;
        return n % 2 == 1 ? values[n / 2] : 0.5 * (values[n / 2 - 1] + values[n / 2]);
    }
}

/// <summary>What the settle detector made of a frame.</summary>
public enum SettleState
{
    /// <summary>The picture is still changing, or it is the pattern already taken.</summary>
    Waiting,

    /// <summary>This frame shows the new pattern, steady: take it.</summary>
    Settled,
}

/// <summary>Thresholds for <see cref="SettleDetector"/> (levels are mean absolute differences, 0–255).</summary>
public sealed record SettleOptions
{
    /// <summary>Two frames this close agree: the projector and the exposure are not mid-change.</summary>
    public double AgreeTolerance { get; init; } = 3;

    /// <summary>A frame at least this far from the last accepted one shows a new pattern.</summary>
    public double MinChange { get; init; } = 8;

    /// <summary>Consecutive agreeing frames needed (the frame offered and those before it).</summary>
    public int StableFrames { get; init; } = 2;

    /// <summary>Compares every <c>Stride</c>-th pixel of every <c>Stride</c>-th row.</summary>
    public int Stride { get; init; } = 2;
}

/// <summary>
/// Accepts a camera frame for a pattern once the picture has settled: the last
/// <see cref="SettleOptions.StableFrames"/> frames agree with each other (so the projector is not mid-change
/// and the exposure did not straddle two patterns) and differ from the frame accepted for the previous pattern
/// (so a late camera is not taken for the next pattern). Without a timestamp or a frame tag this needs nothing
/// from the camera but its pictures. Compares a strided subsample by mean absolute difference.
/// </summary>
public sealed class SettleDetector
{
    private readonly int _width;
    private readonly int _height;
    private readonly SettleOptions _options;
    private byte[]? _previous;
    private byte[]? _accepted;
    private byte[] _current;
    private int _stable;

    public SettleDetector(int width, int height, SettleOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        _width = width;
        _height = height;
        _options = options ?? new SettleOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.Stride, 1);
        _current = new byte[SubsampleLength];
    }

    /// <summary>Mean absolute difference from the frame before (levels).</summary>
    public double LastChange { get; private set; }

    /// <summary>Mean absolute difference from the last accepted frame (levels; infinity before the first).</summary>
    public double FromAccepted { get; private set; } = double.PositiveInfinity;

    public int Accepted { get; private set; }

    private int SubsampleLength => ((_width + _options.Stride - 1) / _options.Stride) * ((_height + _options.Stride - 1) / _options.Stride);

    /// <summary>Offers the next camera frame (gray8, width × height).</summary>
    public SettleState Offer(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < _width * _height)
            throw new ArgumentException("The frame is smaller than the camera's raster.", nameof(frame));
        var s = _options.Stride;
        var i = 0;
        for (var y = 0; y < _height; y += s)
        {
            var row = frame.Slice(y * _width, _width);
            for (var x = 0; x < _width; x += s)
                _current[i++] = row[x];
        }
        LastChange = _previous is null ? double.PositiveInfinity : MeanDifference(_current, _previous);
        _stable = LastChange <= _options.AgreeTolerance ? _stable + 1 : 1;
        FromAccepted = _accepted is null ? double.PositiveInfinity : MeanDifference(_current, _accepted);
        var settled = _stable >= _options.StableFrames && FromAccepted >= _options.MinChange;
        // The buffers rotate: the current subsample becomes the previous one, and the accepted one when taken.
        var spare = _previous ?? new byte[_current.Length];
        _previous = _current;
        if (settled)
        {
            _accepted = (byte[])_current.Clone();
            Accepted++;
            _stable = 0;
        }
        _current = spare;
        return settled ? SettleState.Settled : SettleState.Waiting;
    }

    /// <summary>Forgets everything (a new scan).</summary>
    public void Reset()
    {
        _previous = null;
        _accepted = null;
        _stable = 0;
        Accepted = 0;
        LastChange = 0;
        FromAccepted = double.PositiveInfinity;
    }

    private static double MeanDifference(byte[] a, byte[] b)
    {
        long sum = 0;
        for (var i = 0; i < a.Length; i++)
            sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }
}

/// <summary>The camera and display timing a capture schedule is built from (milliseconds). A frame received at
/// t holds the glass's light from t − latency − exposure/2 to t − latency + exposure/2 (the latency is what
/// <see cref="LatencyEstimator"/> measures: to the middle of the exposure).</summary>
public sealed record CaptureTiming(
    double LatencyMs,
    double ExposureMs,
    double CameraFramePeriodMs,
    double DisplayRefreshMs = 1000.0 / 60,
    double MarginMs = 8,
    bool Pipelined = true);

/// <summary>When a pattern is on the glass and which camera frames may stand for it: frames received from
/// <see cref="EarliestCaptureMs"/> to <see cref="LatestCaptureMs"/> saw this pattern and nothing else.</summary>
public readonly record struct CaptureSlot(int FrameIndex, double DisplayAtMs, double EarliestCaptureMs, double LatestCaptureMs)
{
    public double WindowMs => LatestCaptureMs - EarliestCaptureMs;
}

/// <summary>
/// The show-and-capture timetable of a scan. Each pattern is held for a whole number of display refreshes,
/// long enough that at least one camera frame's exposure falls entirely inside it with a margin either side.
/// Pipelined, the next pattern goes up as soon as that frame's exposure is over (the camera's latency only
/// delays when frames arrive, not when patterns change); otherwise the next pattern waits for the frame to
/// arrive. The earliest capture is display + latency + exposure + margin.
/// </summary>
public sealed class CaptureSchedule
{
    private readonly CaptureSlot[] _slots;

    private CaptureSchedule(CaptureSlot[] slots, double holdMs, CaptureTiming timing)
    {
        _slots = slots;
        HoldMs = holdMs;
        Timing = timing;
    }

    public IReadOnlyList<CaptureSlot> Slots => _slots;

    /// <summary>How long each pattern stays on the glass.</summary>
    public double HoldMs { get; }

    public CaptureTiming Timing { get; }

    /// <summary>From the first pattern up to the last pattern's last useful frame.</summary>
    public double TotalMs => _slots.Length == 0 ? 0 : _slots[^1].LatestCaptureMs - _slots[0].DisplayAtMs;

    public static CaptureSchedule Build(int frameCount, CaptureTiming timing, double startMs = 0)
    {
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        if (!(timing.CameraFramePeriodMs > 0) || timing.ExposureMs < 0 || timing.LatencyMs < 0 || !(timing.DisplayRefreshMs > 0) || timing.MarginMs < 0)
            throw new ArgumentException("The timing is not physical.", nameof(timing));
        var e = timing.ExposureMs;
        var m = timing.MarginMs;
        // A window [earliest, latest] of length hold − 1.5e − 2m must hold one frame arrival: ≥ one frame period.
        var needed = 1.5 * e + 2 * m + timing.CameraFramePeriodMs;
        if (!timing.Pipelined)
            needed += timing.LatencyMs;
        var hold = Math.Ceiling(needed / timing.DisplayRefreshMs - 1e-9) * timing.DisplayRefreshMs;
        var slots = new CaptureSlot[frameCount];
        for (var i = 0; i < frameCount; i++)
        {
            var display = startMs + i * hold;
            slots[i] = new CaptureSlot(i, display, display + timing.LatencyMs + e + m, display + hold + timing.LatencyMs - e / 2 - m);
        }
        return new CaptureSchedule(slots, hold, timing);
    }

    /// <summary>The pattern a frame received at <paramref name="timeMs"/> stands for, or −1 when it may have
    /// seen two patterns (or none).</summary>
    public int SlotFor(double timeMs)
    {
        int lo = 0, hi = _slots.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var s = _slots[mid];
            if (timeMs < s.EarliestCaptureMs)
                hi = mid - 1;
            else if (timeMs > s.LatestCaptureMs)
                lo = mid + 1;
            else
                return mid;
        }
        return -1;
    }
}
