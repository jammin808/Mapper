namespace PlayMap.Core.Tc;

/// <summary>LTC audio from codewords (SMPTE ST 12-1 biphase mark: a transition at every bit cell's start, and one more at mid-cell for
/// a 1). The bit phase accumulates as a double at 80 × the real frame rate (frame pairs above 30 fps run at the pair rate), so 20.02
/// samples a bit at 29.97 never round per bit. Each transition is placed at its fractional sample time and the output is the
/// analytic one-pole step response from that instant (rise time 10–90 %), so edges are timed to well under a sample. A speed
/// other than 1 runs the code faster or slower (varispeed).</summary>
public sealed class LtcEncoder
{
    private readonly TcFormat _format;
    private readonly int _sampleRate;
    private readonly double _amplitude;
    private readonly double _tau;
    private readonly double _samplesPerBit;

    public LtcEncoder(TcFormat format, int sampleRate, double levelDbfs = -12, double riseUs = 40, double speed = 1)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _format = format;
        _sampleRate = sampleRate;
        _amplitude = Math.Pow(10, levelDbfs / 20);
        _tau = Math.Max(1e-9, riseUs * 1e-6 * sampleRate / 2.197);
        var codeFps = format.Rate.Value / (format.Pairs ? 2 : 1);
        _samplesPerBit = sampleRate / (80 * codeFps * speed);
    }

    public int SampleRate => _sampleRate;

    public double SamplesPerBit => _samplesPerBit;

    public double SamplesPerFrame => _samplesPerBit * 80;

    /// <summary>The codeword of a frame (a frame pair's first frame above 30 fps).</summary>
    public LtcCodeword WordOf(long frame, uint userBits = 0, bool colourFrame = false)
    {
        var codeFrame = _format.Pairs ? frame / 2 * 2 : frame;
        var (h, m, s, f) = _format.Label(codeFrame);
        return LtcCodeword.Build(new LtcFields(h, m, s, _format.Pairs ? f / 2 : f, _format.DropFrame, colourFrame, userBits, false, false, false),
            _format.LabelFps == 25 || _format.LabelFps == 50);
    }

    /// <summary>The samples of consecutive frames from <paramref name="firstFrame"/>; with <paramref name="leadSamples"/> of
    /// silence first. Answers the samples and the sample time at which each frame's codeword ends (its last bit's end).</summary>
    public (float[] Samples, double[] FrameEnds) Render(long firstFrame, int frames, double leadSamples = 0, uint userBits = 0)
    {
        var step = _format.Pairs ? 2 : 1;
        var words = new LtcCodeword[frames];
        for (var i = 0; i < frames; i++)
            words[i] = WordOf(firstFrame + i * step, userBits);
        return Render(words, leadSamples);
    }

    /// <summary>The samples of codewords back to back, after a lead of silence.</summary>
    public (float[] Samples, double[] FrameEnds) Render(IReadOnlyList<LtcCodeword> words, double leadSamples = 0)
    {
        ArgumentNullException.ThrowIfNull(words);
        var transitions = new List<double>(words.Count * 120);
        var ends = new double[words.Count];
        var t = leadSamples;
        for (var w = 0; w < words.Count; w++)
        {
            for (var bit = 0; bit < 80; bit++)
            {
                transitions.Add(t);
                if (words[w][bit])
                    transitions.Add(t + _samplesPerBit / 2);
                t += _samplesPerBit;
            }
            ends[w] = t;
        }
        transitions.Add(t);
        var length = (int)Math.Ceiling(t + 4 * _tau) + 1;
        var samples = new float[length];
        var level = 0.0;
        var y = 0.0;
        var at = 0.0;
        var k = 0;
        for (var n = 0; n < length; n++)
        {
            while (k < transitions.Count && transitions[k] <= n)
            {
                var tk = transitions[k];
                y = level + (y - level) * Math.Exp(-(tk - at) / _tau);
                at = tk;
                level = k == transitions.Count - 1 ? 0 : level == 0 ? _amplitude : -level;
                k++;
            }
            y = level + (y - level) * Math.Exp(-(n - at) / _tau);
            at = n;
            samples[n] = (float)y;
        }
        return (samples, ends);
    }
}
