namespace PlayMap.Core.Tc;

/// <summary>One LTC frame read from audio: its codeword and fields, the sample time at which it ended (fractional; forward: the end
/// of bit 79, so its TC is the start of the next frame; reverse: the end of its bit 0, so its TC is the start of its own frame),
/// the bit period then, the direction and the peak level of the frame.</summary>
public readonly record struct LtcFrameEvent(LtcCodeword Word, LtcFields Fields, double EndSample, double SamplesPerBit, int Direction, float PeakDbfs, bool ParityOk);

/// <summary>The decoder's running counts (SMPTE timecode readers' error classes): biphase (a half-bit alone or a glitch), invalid (a
/// sync word before 80 bits), bad BCD, parity (an odd number of zeros), dropouts (no edge for 2.5 bits).</summary>
public readonly record struct LtcCounts(long Biphase, long Invalid, long BadBcd, long Parity, long Dropouts);

/// <summary>LTC from audio samples (pure, streaming): a 10 Hz DC block; a peak envelope (instant attack, 50 ms release) with a
/// floor below which there is no signal; a Schmitt trigger at ±25 % of the envelope whose flips are timed at the most recent zero
/// crossing, interpolated between the two samples that straddle zero (hysteresis for noise immunity, the crossing for sub-sample
/// time); intervals between edges classified against the bit period (acquired from 32 intervals split into two clusters near a
/// 1:2 ratio, then tracked); biphase mark decoded (a long interval is a 0, two short ones a 1); a 128-bit shift register that finds
/// the sync word forward (0011 1111 1111 1101 after at least 80 bits) and reversed. Written from SMPTE ST 12-1's description of
/// the code; any sample rate, 24–30 frames (and pairs) ±15 %.</summary>
public sealed class LtcDecoder
{
    private const int ForwardSync = 0x3FFD;
    private const int ReverseSync = 0xBFFC;

    private readonly int _sampleRate;
    private readonly double _floor;
    private readonly double _r;
    private readonly double _release;
    private readonly double _minPeriod;
    private readonly double _maxPeriod;
    private readonly List<double> _acquire = new(32);
    private readonly double[] _bitEnds = new double[128];
    private double _x1;
    private double _y1;
    private double _env;
    private double _prev;
    private double _zc = double.NaN;
    private double _lastEdge = double.NaN;
    private int _state;
    private bool _signal;
    private double _period;
    private bool _acquired;
    private bool _half;
    private UInt128 _reg;
    private int _sinceForward;
    private int _sinceReverse;
    private int _bitCount;
    private bool _synced;
    private double _peak;
    private long _biphase;
    private long _invalid;
    private long _badBcd;
    private long _parity;
    private long _dropouts;

    /// <param name="floorDbfs">Below this level there is no signal (default −46 dBFS).</param>
    public LtcDecoder(int sampleRate, double floorDbfs = -46)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
        _floor = Math.Pow(10, floorDbfs / 20);
        _r = 1 - 2 * Math.PI * 10 / sampleRate;
        _release = Math.Exp(-1.0 / (0.05 * sampleRate));
        _minPeriod = sampleRate / (2400 * 1.15);
        _maxPeriod = sampleRate / (1920 * 0.85);
    }

    public int SampleRate => _sampleRate;

    public LtcCounts Counts => new(_biphase, _invalid, _badBcd, _parity, _dropouts);

    /// <summary>Whether a signal above the floor is present now.</summary>
    public bool HasSignal => _signal;

    /// <summary>The envelope now, in dBFS.</summary>
    public double LevelDbfs => _env > 0 ? 20 * Math.Log10(_env) : -120;

    /// <summary>The bit period in samples once acquired (0 before).</summary>
    public double SamplesPerBit => _acquired ? _period : 0;

    /// <summary>Clears the counts (the frames keep decoding).</summary>
    public void ResetCounts() => (_biphase, _invalid, _badBcd, _parity, _dropouts) = (0, 0, 0, 0, 0);

    /// <summary>Samples from one channel; <paramref name="firstSampleIndex"/> is the index of samples[0] in the stream. Frames read go
    /// to <paramref name="frames"/>.</summary>
    public void Process(ReadOnlySpan<float> samples, long firstSampleIndex, List<LtcFrameEvent> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        for (var k = 0; k < samples.Length; k++)
            Sample(samples[k], firstSampleIndex + k, frames);
    }

    /// <summary>Interleaved samples: one channel (0-based) of <paramref name="channels"/>.</summary>
    public void Process(ReadOnlySpan<float> interleaved, int channels, int channel, long firstSampleIndex, List<LtcFrameEvent> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (channels <= 0 || channel < 0 || channel >= channels)
            throw new ArgumentOutOfRangeException(nameof(channel));
        var n = interleaved.Length / channels;
        for (var k = 0; k < n; k++)
            Sample(interleaved[k * channels + channel], firstSampleIndex + k, frames);
    }

    private void Sample(float x, long i, List<LtcFrameEvent> frames)
    {
        var y = x - _x1 + _r * _y1;
        _x1 = x;
        _y1 = y;
        var a = Math.Abs(y);
        _env = a > _env ? a : _env * _release;
        if (a > _peak)
            _peak = a;
        if (_env < _floor)
        {
            if (_signal)
                Lost();
            _prev = y;
            return;
        }
        _signal = true;
        if ((_prev <= 0 && y > 0) || (_prev >= 0 && y < 0))
        {
            var d = _prev - y;
            _zc = Math.Abs(d) < 1e-30 ? i : (i - 1) + _prev / d;
        }
        _prev = y;
        var threshold = 0.25 * _env;
        if (_state <= 0 && y > threshold)
        {
            var first = _state == 0;
            _state = 1;
            if (!first)
                Edge(_zc, frames);
        }
        else if (_state >= 0 && y < -threshold)
        {
            var first = _state == 0;
            _state = -1;
            if (!first)
                Edge(_zc, frames);
        }
    }

    private void Lost()
    {
        _signal = false;
        _state = 0;
        _lastEdge = double.NaN;
        _zc = double.NaN;
        ResetParser();
    }

    private void ResetParser()
    {
        _half = false;
        _reg = UInt128.Zero;
        _sinceForward = 0;
        _sinceReverse = 0;
    }

    private void Edge(double t, List<LtcFrameEvent> frames)
    {
        if (double.IsNaN(t))
            return;
        if (double.IsNaN(_lastEdge))
        {
            _lastEdge = t;
            return;
        }
        var d = t - _lastEdge;
        if (!_acquired)
        {
            _lastEdge = t;
            _acquire.Add(d);
            if (_acquire.Count >= 32)
                Acquire();
            return;
        }
        if (d > 2.5 * _period)
        {
            _dropouts++;
            _lastEdge = t;
            ResetParser();
            return;
        }
        if (d <= 0.3 * _period)
        {
            // A glitch: counted and merged into the next interval (the previous edge stays the start).
            _biphase++;
            return;
        }
        _lastEdge = t;
        if (d > 0.75 * _period)
        {
            if (_half)
            {
                _biphase++;
                _half = false;
            }
            _period += 0.1 * (d - _period);
            Bit(0, t, frames);
        }
        else
        {
            _period += 0.1 * (2 * d - _period);
            if (_half)
            {
                _half = false;
                Bit(1, t, frames);
            }
            else
            {
                _half = true;
            }
        }
        _period = Math.Clamp(_period, _minPeriod * 0.9, _maxPeriod * 1.1);
    }

    /// <summary>Two clusters of 32 intervals near 1:2 give the bit period (the long cluster's mean); all long, the mean itself.</summary>
    private void Acquire()
    {
        double lo = _acquire.Min(), hi = _acquire.Max();
        double shortMean = lo, longMean = hi;
        for (var it = 0; it < 10; it++)
        {
            double s = 0, l = 0;
            int ns = 0, nl = 0;
            foreach (var d in _acquire)
            {
                if (Math.Abs(d - shortMean) <= Math.Abs(d - longMean))
                {
                    s += d;
                    ns++;
                }
                else
                {
                    l += d;
                    nl++;
                }
            }
            if (ns > 0)
                shortMean = s / ns;
            if (nl > 0)
                longMean = l / nl;
        }
        var ratio = longMean / Math.Max(1e-9, shortMean);
        var period = ratio is > 1.6 and < 2.4 ? longMean : ratio < 1.2 ? (shortMean + longMean) / 2 : 0;
        if (period >= _minPeriod && period <= _maxPeriod)
        {
            _period = period;
            _acquired = true;
            _acquire.Clear();
            ResetParser();
            return;
        }
        _acquire.RemoveRange(0, 16);
    }

    private void Bit(int bit, double endTime, List<LtcFrameEvent> frames)
    {
        _reg = (_reg << 1) | (UInt128)(uint)bit;
        _bitEnds[_bitCount & 127] = endTime;
        _bitCount++;
        _sinceForward++;
        _sinceReverse++;
        var low = (int)(_reg & 0xFFFF);
        if (low == ForwardSync)
        {
            if (_sinceForward >= 80)
            {
                UInt128 word = 0;
                for (var b = 0; b < 80; b++)
                    if (((_reg >> (79 - b)) & UInt128.One) != UInt128.Zero)
                        word |= UInt128.One << b;
                Emit(new LtcCodeword(word), endTime, 1, frames);
            }
            else if (_synced)
            {
                // A sync word before 80 bits (once frames have been read: the first sync after acquisition is only a start).
                _invalid++;
            }
            _sinceForward = 0;
        }
        else if (low == ReverseSync)
        {
            if (_sinceReverse >= 80 && (int)((_reg >> 80) & 0xFFFF) == ReverseSync)
            {
                UInt128 word = 0;
                for (var b = 0; b < 64; b++)
                    if (((_reg >> (16 + b)) & UInt128.One) != UInt128.Zero)
                        word |= UInt128.One << b;
                for (var b = 0; b < 16; b++)
                    if (((LtcCodeword.SyncWord >> b) & 1) != 0)
                        word |= UInt128.One << (64 + b);
                // The frame's bit 0 ended 16 bits ago (before the reversed sync).
                Emit(new LtcCodeword(word), _bitEnds[(_bitCount - 17) & 127], -1, frames);
            }
            _sinceReverse = 0;
        }
    }

    private void Emit(LtcCodeword word, double endTime, int direction, List<LtcFrameEvent> frames)
    {
        var peak = _peak > 0 ? (float)(20 * Math.Log10(_peak)) : -120f;
        _peak = 0;
        if (!word.ParityOk)
            _parity++;
        if (!word.TryRead(false, out var fields))
        {
            _badBcd++;
            return;
        }
        _synced = true;
        frames.Add(new LtcFrameEvent(word, fields, endTime, _period, direction, peak, word.ParityOk));
    }
}
