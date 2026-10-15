using PlayMap.Core.Numerics;

namespace PlayMap.Core.Audio;

/// <summary>A test signal at 48 kHz: the samples from a given index on, in order (a signal may keep state between calls, so one
/// instance serves one stream). Seeded: the same signal gives the same samples on every machine.</summary>
public abstract class TestSignal
{
    public const int Rate = AudioAnalyser.SampleRate;

    /// <summary>Fills <paramref name="mono"/> with the samples from <paramref name="first"/> on (calls follow on from each other).</summary>
    public abstract void Fill(Span<float> mono, long first);

    /// <summary>"click", "sweep", "noise" or "drums" at a tempo; null for another word.</summary>
    public static TestSignal? Of(string kind, double bpm) => kind.ToLowerInvariant() switch
    {
        "click" => new ClickSignal(bpm),
        "sweep" => new SweepSignal(),
        "noise" => new NoiseSignal(0.3f, 0x5eed),
        "drums" => new DrumSignal(bpm),
        _ => null,
    };

    /// <summary>The test signals' names.</summary>
    public static IReadOnlyList<string> Kinds { get; } = ["click", "sweep", "noise", "drums"];

    /// <summary>The sample a beat starts on (beat 0 at sample 0).</summary>
    public static long BeatSample(long beat, double bpm) => (long)Math.Round(beat * 60.0 * Rate / bpm);
}

/// <summary>A 1 ms 2 kHz click on every beat, the first beat of each four-beat bar louder.</summary>
public sealed class ClickSignal(double bpm) : TestSignal
{
    public override void Fill(Span<float> mono, long first)
    {
        var length = Rate / 1000;
        var beatLength = 60.0 * Rate / bpm;
        for (var i = 0; i < mono.Length; i++)
        {
            var n = first + i;
            var beat = (long)Math.Floor(n / beatLength);
            var into = n - BeatSample(beat, bpm);
            if (into < 0)
            {
                beat--;
                into = n - BeatSample(beat, bpm);
            }
            mono[i] = into < length
                ? (float)((beat % 4 == 0 ? 0.9 : 0.6) * Math.Sin(2 * Math.PI * 2000 * into / Rate))
                : 0f;
        }
    }
}

/// <summary>A logarithmic sweep from 20 Hz to 20 kHz over 10 s, again and again, at half scale.</summary>
public sealed class SweepSignal : TestSignal
{
    private const double Seconds = 10;
    private const double From = 20, To = 20000;

    public override void Fill(Span<float> mono, long first)
    {
        var k = Math.Log(To / From);
        for (var i = 0; i < mono.Length; i++)
        {
            var t = (first + i) % (long)(Seconds * Rate) / (double)Rate;
            // Phase of an exponential sweep: 2π·f0·T/k·(e^(k·t/T) − 1).
            var phase = 2 * Math.PI * From * Seconds / k * (Math.Exp(k * t / Seconds) - 1);
            mono[i] = (float)(0.5 * Math.Sin(phase));
        }
    }
}

/// <summary>Pink noise (white noise through a three-pole pink filter), seeded.</summary>
public sealed class NoiseSignal(float amplitude, ulong seed) : TestSignal
{
    private Pcg32 _random = new(seed);
    private double _b0, _b1, _b2;

    public override void Fill(Span<float> mono, long first)
    {
        for (var i = 0; i < mono.Length; i++)
            mono[i] = Next();
    }

    /// <summary>The next sample.</summary>
    public float Next()
    {
        var white = _random.NextDouble() * 2 - 1;
        _b0 = 0.99765 * _b0 + white * 0.0990460;
        _b1 = 0.96300 * _b1 + white * 0.2965164;
        _b2 = 0.57000 * _b2 + white * 1.0526913;
        return (float)(amplitude * 0.25 * (_b0 + _b1 + _b2 + white * 0.1848));
    }
}

/// <summary>A drum loop: a kick on every beat (a sine falling from 120 to 50 Hz, decaying with a 50 ms time constant), a snare on beats 2 and 4 (a noise burst
/// over a 190 Hz tone) and a closed hat on every eighth (a 30 ms burst of differenced noise).</summary>
public sealed class DrumSignal(double bpm, bool snare = true, bool hats = true) : TestSignal
{
    private readonly NoiseSignal _noise = new(1f, 0xd5a3);
    private float _lastNoise;

    public override void Fill(Span<float> mono, long first)
    {
        var eighth = 30.0 * Rate / bpm;
        for (var i = 0; i < mono.Length; i++)
        {
            var n = first + i;
            var noise = _noise.Next();
            var bright = noise - _lastNoise;
            _lastNoise = noise;
            var e = (long)Math.Floor(n / eighth);
            var into = n - (long)Math.Round(e * eighth);
            if (into < 0)
            {
                e--;
                into = n - (long)Math.Round(e * eighth);
            }
            var t = into / (double)Rate;
            var v = 0.0;
            v += e % 2 == 0 ? Kick(t) : Kick(t + eighth / Rate);
            if (snare && e % 4 == 2)
                v += t < 0.1 ? (0.35 * noise + 0.25 * Math.Sin(2 * Math.PI * 190 * t)) * Math.Exp(-t / 0.03) : 0;
            if (hats)
                v += t < 0.03 ? 0.25 * bright * Math.Exp(-t / 0.008) : 0;
            mono[i] = (float)Math.Clamp(v, -1, 1);
        }
    }

    /// <summary>The kick at <paramref name="t"/> seconds after its start.</summary>
    public static double Kick(double t)
    {
        // Faded to nothing by 400 ms: a cut-off tail would be a click of its own.
        if (t >= 0.4)
            return 0;
        // Frequency 50 + 70·e^(−t/0.03) Hz, integrated for the phase.
        var phase = 2 * Math.PI * (50 * t + 70 * 0.03 * (1 - Math.Exp(-t / 0.03)));
        return 0.8 * Math.Sin(phase) * Math.Exp(-t / 0.05) * (1 - t / 0.4);
    }
}
