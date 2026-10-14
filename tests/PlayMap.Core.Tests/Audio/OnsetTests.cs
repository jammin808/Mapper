using PlayMap.Core.Audio;

namespace PlayMap.Core.Tests.Audio;

/// <summary>Onsets against the truth of a synthetic kick, and tap tempo's median and outlier rules.</summary>
public sealed class OnsetTests
{
    private const int Rate = AudioAnalyser.SampleRate;

    /// <summary>Collects the onsets the analyser reports (their samples).</summary>
    private sealed class Onsets : IAnalysisSink
    {
        public readonly List<long> Samples = [];

        public void Frame(AnalysisFrame frame)
        {
            if (frame.Onset)
                Samples.Add(frame.LastOnsetSample);
        }
    }

    private sealed class Silence : TestSignal
    {
        public override void Fill(Span<float> mono, long first) => mono.Clear();
    }

    private static (double F1, double MaxErrorMs, List<double> ErrorsMs) Score(IReadOnlyList<long> found, IReadOnlyList<long> truth, double toleranceMs)
    {
        var tolerance = toleranceMs * Rate / 1000;
        var used = new bool[found.Count];
        var hits = 0;
        var errors = new List<double>();
        foreach (var t in truth)
        {
            var best = -1;
            for (var i = 0; i < found.Count; i++)
                if (!used[i] && Math.Abs(found[i] - t) <= tolerance && (best < 0 || Math.Abs(found[i] - t) < Math.Abs(found[best] - t)))
                    best = i;
            if (best < 0)
                continue;
            used[best] = true;
            hits++;
            errors.Add((found[best] - t) * 1000.0 / Rate);
        }
        var precision = found.Count == 0 ? 0 : hits / (double)found.Count;
        var recall = truth.Count == 0 ? 0 : hits / (double)truth.Count;
        var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        return (f1, errors.Count == 0 ? double.NaN : errors.Max(Math.Abs), errors);
    }

    private static List<long> Kicks(double bpm, int seconds)
    {
        var truth = new List<long>();
        for (var b = 0L; TestSignal.BeatSample(b, bpm) < seconds * (long)Rate - Rate / 10; b++)
            truth.Add(TestSignal.BeatSample(b, bpm));
        return truth;
    }

    private static List<long> Detect(TestSignal signal, int seconds, NoiseSignal? noise = null)
    {
        var analyser = new AudioAnalyser();
        var sink = new Onsets();
        var block = new float[480];
        var hiss = new float[480];
        for (long at = 0; at < seconds * (long)Rate; at += block.Length)
        {
            signal.Fill(block, at);
            if (noise is not null)
            {
                noise.Fill(hiss, at);
                for (var i = 0; i < block.Length; i++)
                    block[i] += hiss[i];
            }
            analyser.Push(block, sink);
        }
        return sink.Samples;
    }

    [Fact]
    public void AKickAt128BpmIsFoundWithinTenMilliseconds()
    {
        var truth = Kicks(128, 20);
        var found = Detect(new DrumSignal(128, snare: false, hats: false), 20);
        var (f1, maxError, errors) = Score(found, truth, 10);
        Assert.True(f1 >= 0.95, $"F1 {f1:0.###}: {found.Count} found for {truth.Count} kicks; errors {string.Join(", ", errors.Take(8).Select(e => e.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)))} ms");
        Assert.True(maxError <= 10, $"the largest error is {maxError:0.##} ms");
    }

    [Fact]
    public void KicksUnderNoiseAndAFullKitStillLandOnTheBeat()
    {
        var truth = Kicks(128, 20);
        var noisy = Detect(new DrumSignal(128, snare: false, hats: false), 20, new NoiseSignal(0.08f, 3));
        var (f1, maxError, _) = Score(noisy, truth, 10);
        Assert.True(f1 >= 0.95, $"F1 {f1:0.###} under noise ({noisy.Count} found for {truth.Count})");
        Assert.True(maxError <= 10, $"the largest error under noise is {maxError:0.##} ms");
        // With snare and hats every eighth is an onset; the kicks are among them.
        var kit = Detect(new DrumSignal(128), 20);
        var (_, _, kitErrors) = Score(kit, truth, 10);
        Assert.True(kitErrors.Count >= truth.Count * 0.95, $"{kitErrors.Count} of {truth.Count} kicks found in the full kit");
    }

    [Fact]
    public void OnsetsAreAtLeastThirtyMillisecondsApart()
    {
        var found = Detect(new DrumSignal(400), 5);
        for (var i = 1; i < found.Count; i++)
            Assert.True(found[i] - found[i - 1] >= OnsetDetector.MinGapHops * AudioAnalyser.Hop, $"onsets {i - 1} and {i} are {(found[i] - found[i - 1]) * 1000.0 / Rate:0.#} ms apart");
    }

    [Fact]
    public void SilenceAndASteadyToneHaveNoOnsets()
    {
        Assert.Empty(Detect(new Silence(), 3));
        var tone = new float[3 * Rate];
        for (var i = 0; i < tone.Length; i++)
            tone[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / Rate));
        var analyser = new AudioAnalyser();
        var sink = new Onsets();
        analyser.Push(tone, sink);
        // The tone's start is the one onset; after it, nothing.
        Assert.True(sink.Samples.Count <= 1, $"{sink.Samples.Count} onsets in a steady tone");
    }

    [Fact]
    public void TapTempoTakesTheMedianAndLeavesOutliers()
    {
        var tap = new TapTempo();
        long at = 1_000_000_000;
        Assert.Null(tap.Tap(at));
        // Six taps at 500 ms with ±20 ms of jitter: 120 BPM within 1.
        long[] jitter = [20, -15, 10, -20, 5];
        double? bpm = null;
        foreach (var j in jitter)
        {
            at += 500_000_000 + j * 1_000_000;
            bpm = tap.Tap(at);
        }
        Assert.NotNull(bpm);
        Assert.InRange(bpm!.Value, 119, 121);
        Assert.Equal(6, tap.Taps);
        // A tap 40 % late is left out of the median.
        at += 700_000_000;
        Assert.InRange(tap.Tap(at)!.Value, 119, 121);
        // More than 2 s later starts a new series.
        at += 2_500_000_000;
        Assert.Null(tap.Tap(at));
        Assert.Equal(1, tap.Taps);
        at += 400_000_000;
        Assert.Equal(150, tap.Tap(at)!.Value, 6);
    }
}
