using PlayMap.Core.Audio;
using PlayMap.Core.Numerics;
using PlayMap.Tests;

namespace PlayMap.Core.Tests.Audio;

/// <summary>The analyser against its references: the FFT against a naive DFT, Patterns' four numbers against Patterns' formula
/// computed the slow way, the filterbank at its band centres, the peak meter, auto-gain and the gate, and no allocation per hop.</summary>
public sealed class AnalyserTests
{
    private const int Rate = AudioAnalyser.SampleRate;

    /// <summary>Keeps the last frame it was given.</summary>
    private sealed class Last : IAnalysisSink
    {
        public readonly AnalysisFrame Got = new();
        public int Count;

        public void Frame(AnalysisFrame frame)
        {
            frame.CopyTo(Got);
            Count++;
        }
    }

    private static float[] Sine(double hz, double amplitude, int n, double phase = 0)
    {
        var s = new float[n];
        for (var i = 0; i < n; i++)
            s[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate + phase));
        return s;
    }

    /// <summary>Patterns' Spectrum.Analyse, written the slow way (a naive DFT) as the reference: Hann with an n − 1 denominator,
    /// |X|·4/n, bands 20–250 / 250–2000 / 2000–8000 Hz, √Σmag²·1.5, level RMS·2.5, clamped 0–1.</summary>
    private static (double Level, double Low, double Mid, double High) PatternsReference(ReadOnlySpan<float> window)
    {
        const int n = AudioAnalyser.Window;
        double sumSq = 0, low = 0, mid = 0, high = 0;
        var x = new double[n];
        for (var i = 0; i < n; i++)
        {
            var s = window[i];
            sumSq += s * s;
            x[i] = s * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)));
        }
        for (var k = 1; k < n / 2; k++)
        {
            var hz = k * Rate / (double)n;
            if (hz < 20 || hz > 8000)
                continue;
            double re = 0, im = 0;
            for (var t = 0; t < n; t++)
            {
                re += x[t] * Math.Cos(2 * Math.PI * k * t / n);
                im -= x[t] * Math.Sin(2 * Math.PI * k * t / n);
            }
            var mag = Math.Sqrt(re * re + im * im) * 4.0 / n;
            if (hz < 250) low += mag * mag;
            else if (hz < 2000) mid += mag * mag;
            else high += mag * mag;
        }
        return (Math.Clamp(Math.Sqrt(sumSq / n) * 2.5, 0, 1), Math.Clamp(Math.Sqrt(low) * 1.5, 0, 1),
            Math.Clamp(Math.Sqrt(mid) * 1.5, 0, 1), Math.Clamp(Math.Sqrt(high) * 1.5, 0, 1));
    }

    [Fact]
    public void TheRealFftIsTheDftToANanoPart()
    {
        var fft = new RealFft(1024);
        var rng = new Pcg32(7);
        var x = new double[1024];
        for (var i = 0; i < x.Length; i++)
            x[i] = rng.NextDouble() * 2 - 1;
        var re = new double[513];
        var im = new double[513];
        fft.Forward(x, re, im);
        for (var k = 0; k <= 512; k++)
        {
            double r = 0, m = 0;
            for (var t = 0; t < 1024; t++)
            {
                r += x[t] * Math.Cos(2 * Math.PI * k * t / 1024);
                m -= x[t] * Math.Sin(2 * Math.PI * k * t / 1024);
            }
            Assert.True(Math.Abs(r - re[k]) < 1e-9, $"bin {k}: re {re[k]} against {r}");
            Assert.True(Math.Abs(m - im[k]) < 1e-9, $"bin {k}: im {im[k]} against {m}");
        }
    }

    /// <summary>Fifty vectors (sines across the bands at several levels, mixtures, noise, clicks, full-scale squares): the analyser's
    /// four numbers equal Patterns' formula within 1e-4.</summary>
    [Fact]
    public void LevelBassMidHighArePatternsNumbers()
    {
        var rng = new Pcg32(42);
        var vectors = new List<float[]>();
        double[] tones = [35, 60, 120, 240, 260, 440, 1000, 1990, 2010, 5000, 7900, 12000];
        foreach (var hz in tones)
            foreach (var amp in new[] { 0.05, 0.5, 1.0 })
                vectors.Add(Sine(hz, amp, 1200, rng.NextDouble()));
        while (vectors.Count < 44)
        {
            var mix = new float[1200];
            for (var c = 0; c < 3; c++)
            {
                var tone = Sine(20 + rng.NextDouble() * 9000, rng.NextDouble() * 0.4, 1200, rng.NextDouble() * 6);
                for (var i = 0; i < mix.Length; i++)
                    mix[i] += tone[i];
            }
            vectors.Add(mix);
        }
        var noise = new NoiseSignal(0.8f, 9);
        for (var v = 0; v < 3; v++)
        {
            var n = new float[1200];
            noise.Fill(n, 0);
            vectors.Add(n);
        }
        var clicks = new float[1200];
        new ClickSignal(240).Fill(clicks, 11_900);
        vectors.Add(clicks);
        vectors.Add([.. Enumerable.Range(0, 1200).Select(i => (i / 60) % 2 == 0 ? 1f : -1f)]);
        vectors.Add(new float[1200]);
        Assert.Equal(50, vectors.Count);

        foreach (var (vector, index) in vectors.Select((v, i) => (v, i)))
        {
            var analyser = new AudioAnalyser();
            var sink = new Last();
            analyser.Push(vector, sink);
            Assert.Equal(5, sink.Count);
            var (level, low, mid, high) = PatternsReference(vector.AsSpan(1200 - 1024));
            var f = sink.Got;
            Assert.True(Math.Abs(f.Level - level) < 1e-4, $"vector {index}: level {f.Level} against {level}");
            Assert.True(Math.Abs(f.Bass - low) < 1e-4, $"vector {index}: bass {f.Bass} against {low}");
            Assert.True(Math.Abs(f.Mid - mid) < 1e-4, $"vector {index}: mid {f.Mid} against {mid}");
            Assert.True(Math.Abs(f.High - high) < 1e-4, $"vector {index}: high {f.High} against {high}");
        }
    }

    /// <summary>Patterns' own expectations of the formula: a 440 Hz sine is mid, 60 Hz low, 5 kHz high; silence is zero.</summary>
    [Fact]
    public void ToneBandsLandWherePatternsPutsThem()
    {
        AnalysisFrame Of(float[] s)
        {
            var sink = new Last();
            new AudioAnalyser().Push(s, sink);
            return sink.Got;
        }
        var mid = Of(Sine(440, 0.5, 2400));
        Assert.True(mid.Mid > 0.3 && mid.Mid > mid.Bass && mid.Mid > mid.High, $"mid {mid.Mid} bass {mid.Bass} high {mid.High}");
        var low = Of(Sine(60, 0.5, 2400));
        Assert.True(low.Bass > 0.3 && low.Bass > low.Mid, $"bass {low.Bass} mid {low.Mid}");
        var high = Of(Sine(5000, 0.5, 2400));
        Assert.True(high.High > 0.3 && high.High > high.Mid, $"high {high.High} mid {high.Mid}");
        var silence = Of(new float[2400]);
        Assert.Equal(0f, silence.Level);
        Assert.Equal(0f, silence.Bass);
        Assert.Equal(0f, silence.Mid);
        Assert.Equal(0f, silence.High);
    }

    /// <summary>A sine at each band's centre passes its own band at unity (±0.5 dB) and its neighbours at −6 dB or less.</summary>
    [Fact]
    public void TheFilterbankAnswersAtTheBandCentres()
    {
        for (var band = 0; band < FilterBank.Count; band++)
        {
            var centre = FilterBank.Centre(band);
            var analyser = new AudioAnalyser();
            var sink = new Last();
            // A second of settling, then the frame read: long enough for the 30 Hz band's ring-up.
            analyser.Push(Sine(centre, 0.5, Rate), sink);
            var bands = sink.Got.Bands;
            var own = bands[band] / 0.5;
            Assert.True(Math.Abs(20 * Math.Log10(own)) < 0.5, $"band {band} at {centre:0.#} Hz reads {own:0.###} of the sine");
            Assert.Equal(band, Array.IndexOf(bands, bands.Max()));
            foreach (var n in new[] { band - 1, band + 1 }.Where(n => n is >= 0 and < FilterBank.Count))
                Assert.True(20 * Math.Log10(bands[n] / bands[band]) <= -6, $"band {n} beside {band}: {bands[n]} against {bands[band]}");
        }
        Assert.Equal(30, FilterBank.Edge(0), 9);
        Assert.Equal(16000, FilterBank.Edge(16), 6);
    }

    [Fact]
    public void AMinusTwentyDecibelSineReadsMinusTwenty()
    {
        var sink = new Last();
        new AudioAnalyser().Push(Sine(997, Math.Pow(10, -20 / 20.0), 4800), sink);
        Assert.InRange(sink.Got.PeakDb, -20.1, -19.9);
        Assert.False(sink.Got.Silent);
    }

    /// <summary>Auto-gain: a quiet bass reads near the top of its range in the normalised twin while Patterns' number stays small;
    /// silence below the gate is silent after 300 ms and never before.</summary>
    [Fact]
    public void AutoGainUsesTheWholeRangeAndTheGateSaysSilent()
    {
        var analyser = new AudioAnalyser();
        var sink = new Last();
        analyser.Push(Sine(80, 0.02, Rate), sink);
        Assert.True(sink.Got.Bass < 0.1, $"bass {sink.Got.Bass}");
        Assert.True(sink.Got.BassN > 0.9, $"normalised bass {sink.Got.BassN}");
        Assert.False(sink.Got.Silent);
        analyser.Push(new float[Rate * 290 / 1000], sink);
        Assert.False(sink.Got.Silent);
        analyser.Push(new float[Rate * 20 / 1000], sink);
        Assert.True(sink.Got.Silent);
        analyser.GainDb = 0;
        Assert.Equal(0, analyser.GainDb, 9);
    }

    [Fact]
    public void GainIsAppliedBeforeTheAnalysis()
    {
        var plain = new Last();
        new AudioAnalyser().Push(Sine(440, 0.1, 2400), plain);
        var louder = new Last();
        new AudioAnalyser { GainDb = 6.0206 }.Push(Sine(440, 0.1, 2400), louder);
        Assert.Equal(plain.Got.Mid * 2, louder.Got.Mid, 3);
    }

    [Fact]
    public void AHopAllocatesNothingOnceWarm()
    {
        var analyser = new AudioAnalyser();
        var sink = new Last();
        var block = Sine(220, 0.4, 480);
        analyser.Push(block, sink);
        var before = Allocations.Start();
        for (var i = 0; i < 5000; i++)
            analyser.Push(block, sink);
        var allocated = Allocations.Since(before);
        Assert.Equal(10_002, sink.Count);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void TheSpectrumAndCentroidFollowTheTone()
    {
        var lowTone = new Last();
        new AudioAnalyser().Push(Sine(100, 0.5, 4800), lowTone);
        var highTone = new Last();
        new AudioAnalyser().Push(Sine(8000, 0.5, 4800), highTone);
        Assert.True(highTone.Got.Centroid > lowTone.Got.Centroid + 0.4, $"{highTone.Got.Centroid} against {lowTone.Got.Centroid}");
        var lowBin = Array.IndexOf(lowTone.Got.Spectrum, lowTone.Got.Spectrum.Max());
        var highBin = Array.IndexOf(highTone.Got.Spectrum, highTone.Got.Spectrum.Max());
        Assert.InRange(lowBin, 10, 16);
        Assert.InRange(highBin, 53, 58);
    }
}
