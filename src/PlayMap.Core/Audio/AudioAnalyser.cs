namespace PlayMap.Core.Audio;

/// <summary>What one hop of sound was: Patterns' four numbers, their auto-gained twins, the peak, sixteen filterbank bands, a
/// 64-bin log spectrum, the onset strength and the centroid. Times are sample indices of the analysed stream; whoever stamps the
/// frame (the listener) adds <see cref="LeaderNs"/> and <see cref="LastOnsetLeaderNs"/>. A frame object is reused: a reader copies
/// it (<see cref="CopyTo"/>).</summary>
public sealed class AnalysisFrame
{
    public const int BandCount = FilterBank.Count;
    public const int SpectrumBins = 64;

    /// <summary>The hop's number since the stream opened.</summary>
    public long Index;
    /// <summary>The sample at the middle of the hop's window: the instant the frame stands for.</summary>
    public long CentreSample;
    /// <summary>The leader instant of <see cref="CentreSample"/> (set by whoever stamps the frame).</summary>
    public long LeaderNs;
    public float Level;
    public float Bass;
    public float Mid;
    public float High;
    public float LevelN;
    public float BassN;
    public float MidN;
    public float HighN;
    /// <summary>The hop's largest sample in dBFS (−120 floor).</summary>
    public float PeakDb;
    public readonly float[] Bands = new float[BandCount];
    public readonly float[] BandsN = new float[BandCount];
    /// <summary>Magnitudes on 64 log-spaced bins from 30 Hz to 16 kHz, as 0–1 over −80…0 dB.</summary>
    public readonly float[] Spectrum = new float[SpectrumBins];
    /// <summary>The onset strength over its slow peak, 0–1.</summary>
    public float Flux;
    /// <summary>The spectral centroid, log-mapped from 30 Hz–16 kHz to 0–1.</summary>
    public float Centroid;
    /// <summary>True when the hop before this frame's was an onset (found one hop late, so its sample is in the past).</summary>
    public bool Onset;
    /// <summary>The window-centre sample of the latest onset so far (−1 = none).</summary>
    public long LastOnsetSample = -1;
    /// <summary>The leader instant of <see cref="LastOnsetSample"/> (long.MinValue = none).</summary>
    public long LastOnsetLeaderNs = long.MinValue;
    /// <summary>Below the gate for 300 ms.</summary>
    public bool Silent = true;

    public void CopyTo(AnalysisFrame to)
    {
        ArgumentNullException.ThrowIfNull(to);
        to.Index = Index;
        to.CentreSample = CentreSample;
        to.LeaderNs = LeaderNs;
        to.Level = Level;
        to.Bass = Bass;
        to.Mid = Mid;
        to.High = High;
        to.LevelN = LevelN;
        to.BassN = BassN;
        to.MidN = MidN;
        to.HighN = HighN;
        to.PeakDb = PeakDb;
        Bands.AsSpan().CopyTo(to.Bands);
        BandsN.AsSpan().CopyTo(to.BandsN);
        Spectrum.AsSpan().CopyTo(to.Spectrum);
        to.Flux = Flux;
        to.Centroid = Centroid;
        to.Onset = Onset;
        to.LastOnsetSample = LastOnsetSample;
        to.LastOnsetLeaderNs = LastOnsetLeaderNs;
        to.Silent = Silent;
    }
}

/// <summary>Receives each frame as it is made (on the analysing thread; the frame is reused after the call returns).</summary>
public interface IAnalysisSink
{
    void Frame(AnalysisFrame frame);
}

/// <summary>The analyser of one listening input (pure; one thread pushes): mono samples at 48 kHz in, one frame per hop of 240
/// samples (5 ms, 200 frames a second) out, each over the last 1024 samples. Level/Bass/Mid/High follow Patterns'
/// <c>Spectrum.Analyse</c> exactly — Hann window (n − 1 denominator), |X|·4/n, bands 20–250 / 250–2000 / 2000–8000 Hz, √Σmag²·1.5,
/// level RMS·2.5, each clamped 0–1 — so a look reads the same in both apps. Gain is applied to the samples first; auto-gain makes
/// the …N twins (value ÷ max(gate, the value's slow peak with a 10 s release)); below the gate for 300 ms the frame is silent.
/// Nothing is allocated per hop.</summary>
public sealed class AudioAnalyser
{
    public const int SampleRate = 48000;
    public const int Hop = 240;
    public const int Window = 1024;
    public const double LowHz = 20, LowMidHz = 250, MidHighHz = 2000, HighHz = 8000;

    private const double HopSeconds = Hop / (double)SampleRate;
    private const int SilentHops = 60;
    private static readonly double[] Hann = BuildHann();

    private readonly RealFft _fft = new(Window);
    private readonly FilterBank _bank = new(SampleRate);
    private readonly OnsetDetector _onsets;
    private readonly float[] _ring = new float[Window];
    private readonly double[] _windowed = new double[Window];
    private readonly double[] _re = new double[Window / 2 + 1];
    private readonly double[] _im = new double[Window / 2 + 1];
    private readonly double[] _mag = new double[Window / 2 + 1];
    private readonly double[] _peaks = new double[4 + FilterBank.Count];
    private readonly AnalysisFrame _frame = new();
    private readonly double _peakDecay = Math.Exp(-HopSeconds / 10.0);
    private readonly int[] _specLo = new int[AnalysisFrame.SpectrumBins];
    private readonly int[] _specHi = new int[AnalysisFrame.SpectrumBins];
    private readonly double[] _specCentre = new double[AnalysisFrame.SpectrumBins];
    private int _write;
    private int _inHop;
    private long _samples;
    private float _hopPeak;
    private int _belowGate = SilentHops;
    private double _gain = 1;

    public AudioAnalyser()
    {
        _onsets = new OnsetDetector(Window / 2 + 1, HopSeconds);
        var binHz = SampleRate / (double)Window;
        for (var j = 0; j < AnalysisFrame.SpectrumBins; j++)
        {
            var lo = 30 * Math.Pow(16000 / 30.0, j / (double)AnalysisFrame.SpectrumBins);
            var hi = 30 * Math.Pow(16000 / 30.0, (j + 1) / (double)AnalysisFrame.SpectrumBins);
            _specLo[j] = (int)Math.Ceiling(lo / binHz);
            _specHi[j] = (int)Math.Ceiling(hi / binHz) - 1;
            _specCentre[j] = Math.Sqrt(lo * hi) / binHz;
        }
    }

    /// <summary>The gain applied to the samples before analysis, in dB.</summary>
    public double GainDb
    {
        get => 20 * Math.Log10(_gain);
        set => _gain = Math.Pow(10, Math.Clamp(value, -60, 60) / 20);
    }

    /// <summary>The level below which the input is silent (and the floor auto-gain divides by), in dBFS.</summary>
    public double GateDb { get; set; } = -60;

    /// <summary>On: the …N twins are auto-gained. Off (a fixed gain): they equal the plain values.</summary>
    public bool AutoGain { get; set; } = true;

    /// <summary>Samples taken since the stream opened (the next sample's index).</summary>
    public long Samples => _samples;

    /// <summary>Analyses the samples, calling <paramref name="sink"/> once per completed hop.</summary>
    public void Push(ReadOnlySpan<float> mono, IAnalysisSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        foreach (var raw in mono)
        {
            var s = (float)(raw * _gain);
            _ring[_write] = s;
            _write = (_write + 1) & (Window - 1);
            _bank.Process(s);
            var a = Math.Abs(s);
            if (a > _hopPeak)
                _hopPeak = a;
            _samples++;
            if (++_inHop == Hop)
            {
                _inHop = 0;
                MakeFrame();
                sink.Frame(_frame);
            }
        }
    }

    public void Reset()
    {
        Array.Clear(_ring);
        Array.Clear(_peaks);
        _bank.Reset();
        _onsets.Reset();
        _write = 0;
        _inHop = 0;
        _samples = 0;
        _hopPeak = 0;
        _belowGate = SilentHops;
        _frame.Index = 0;
        _frame.LastOnsetSample = -1;
        _frame.LastOnsetLeaderNs = long.MinValue;
    }

    private void MakeFrame()
    {
        var f = _frame;
        f.Index = _samples / Hop - 1;
        f.CentreSample = _samples - Window / 2;
        // The window in time order, oldest first (the ring's write position is the oldest sample).
        var sumSq = 0.0;
        for (var i = 0; i < Window; i++)
        {
            double s = _ring[(_write + i) & (Window - 1)];
            sumSq += s * s;
            _windowed[i] = s * Hann[i];
        }
        _fft.Forward(_windowed, _re, _im);
        double low = 0, mid = 0, high = 0, weighted = 0, total = 0;
        for (var k = 0; k <= Window / 2; k++)
        {
            var mag = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) * 4.0 / Window;
            _mag[k] = mag;
            var hz = k * SampleRate / (double)Window;
            if (k >= 1 && k < Window / 2 && hz >= LowHz && hz <= HighHz)
            {
                var power = mag * mag;
                if (hz < LowMidHz) low += power;
                else if (hz < MidHighHz) mid += power;
                else high += power;
            }
            if (hz >= 30 && hz <= 16000)
            {
                weighted += hz * mag;
                total += mag;
            }
        }
        f.Level = Clamp01(Math.Sqrt(sumSq / Window) * 2.5);
        f.Bass = Clamp01(Math.Sqrt(low) * 1.5);
        f.Mid = Clamp01(Math.Sqrt(mid) * 1.5);
        f.High = Clamp01(Math.Sqrt(high) * 1.5);
        _bank.TakeHop(f.Bands);
        f.PeakDb = _hopPeak <= 1e-6f ? -120f : (float)Math.Max(-120, 20 * Math.Log10(_hopPeak));
        _belowGate = f.PeakDb < GateDb ? Math.Min(_belowGate + 1, SilentHops) : 0;
        f.Silent = _belowGate >= SilentHops;
        _hopPeak = 0;
        Spectrum(f.Spectrum);
        f.Centroid = total <= 1e-9 ? 0f : Clamp01(Math.Log(Math.Max(30, weighted / total) / 30) / Math.Log(16000 / 30.0));
        f.Onset = _onsets.Hop(_mag);
        // The onset is the hop before this one; within it, its middle is the least biased guess.
        if (f.Onset)
            f.LastOnsetSample = f.CentreSample - Hop / 2;
        f.Flux = _onsets.SlowPeak <= 1e-9 ? 0f : Clamp01(_onsets.Strength / _onsets.SlowPeak);
        Twins(f);
    }

    private void Twins(AnalysisFrame f)
    {
        var floor = Math.Pow(10, GateDb / 20);
        f.LevelN = Normalise(0, f.Level, floor);
        f.BassN = Normalise(1, f.Bass, floor);
        f.MidN = Normalise(2, f.Mid, floor);
        f.HighN = Normalise(3, f.High, floor);
        for (var k = 0; k < FilterBank.Count; k++)
            f.BandsN[k] = Normalise(4 + k, f.Bands[k], floor);
    }

    private float Normalise(int i, float value, double floor)
    {
        var peak = Math.Max(value, _peaks[i] * _peakDecay);
        _peaks[i] = peak;
        return AutoGain ? Clamp01(value / Math.Max(floor, peak)) : value;
    }

    private void Spectrum(float[] into)
    {
        for (var j = 0; j < into.Length; j++)
        {
            double mag;
            if (_specHi[j] >= _specLo[j])
            {
                mag = 0;
                for (var k = _specLo[j]; k <= _specHi[j]; k++)
                    mag = Math.Max(mag, _mag[k]);
            }
            else
            {
                // No FFT bin falls inside a low log bin: read the magnitude at its centre between the two bins around it.
                var c = _specCentre[j];
                var k0 = (int)Math.Floor(c);
                var t = c - k0;
                mag = _mag[k0] * (1 - t) + _mag[Math.Min(k0 + 1, _mag.Length - 1)] * t;
            }
            var db = 20 * Math.Log10(mag + 1e-12);
            into[j] = Clamp01((db + 80) / 80);
        }
    }

    private static float Clamp01(double v) => (float)Math.Clamp(v, 0, 1);

    private static double[] BuildHann()
    {
        var w = new double[Window];
        for (var i = 0; i < Window; i++)
            w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Window - 1));
        return w;
    }
}
