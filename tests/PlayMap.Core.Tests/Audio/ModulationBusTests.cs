using PlayMap.Core.Audio;
using PlayMap.Core.Modulation;
using PlayMap.Tests;

namespace PlayMap.Core.Tests.Audio;

/// <summary>The bus as the render thread reads it: the wave and the spectrum answered at a frame's instant (ahead of the newest sound),
/// smoothing that lags its source on the 5 ms grid and agrees between a reader at 144 frames a second and one at 50 that joined late,
/// and reads of resolved sources that allocate nothing.</summary>
public sealed class ModulationBusTests
{
    private const int Rate = AudioAnalyser.SampleRate;
    private const long T0 = 10_000_000_000;

    /// <summary>A listening input whose blocks are read exactly when their last sample is due, from leader instant T0 on.</summary>
    private sealed class Heard
    {
        public readonly AudioFeed Feed = new();
        public readonly AudioListener Listener;
        public long Samples;

        public Heard()
        {
            Listener = new AudioListener(new AudioAnalyser(), Feed.Ring, mono => mono);
            Feed.Listener = Listener;
        }

        public long NowNs => T0 + Samples * 1_000_000_000L / Rate;

        public void Play(Func<long, float> sample, double seconds)
        {
            var block = new float[480];
            for (var b = 0; b < seconds * 100; b++)
            {
                for (var i = 0; i < block.Length; i++)
                    block[i] = sample(Samples + i);
                Listener.Samples(block, 1, Rate, Samples, T0 + (Samples + block.Length) * 1_000_000_000L / Rate);
                Samples += block.Length;
            }
        }
    }

    private static float Tone(long n) => (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * n / Rate));

    [Fact]
    public void TheWaveAndTheSpectrumAnswerAtAFramesInstantAheadOfTheSound()
    {
        var heard = new Heard();
        heard.Play(Tone, 1);
        var bus = new ModulationBus(heard.Feed, () => TempoTimeline.Start(120));
        var end = heard.Samples;
        var newest = heard.NowNs;

        // A frame is drawn for an instant after the newest sound (the display's lead): the newest samples answer.
        var wave = new float[256];
        Assert.True(bus.TryWave(newest + 16_000_000, wave.Length, wave));
        for (var i = 0; i < wave.Length; i++)
            Assert.Equal(Tone(end - wave.Length + i), wave[i]);
        // An instant in the past: the samples that end there.
        Assert.True(bus.TryWave(newest - 100_000_000, wave.Length, wave));
        for (var i = 0; i < wave.Length; i++)
            Assert.Equal(Tone(end - Rate / 10 - wave.Length + i), wave[i]);

        var spectrum = new float[64];
        Assert.True(bus.TrySpectrum(newest + 16_000_000, spectrum.Length, spectrum));
        // 1 kHz on 64 log bins over 30 Hz–16 kHz: bin 64·ln(1000/30)/ln(16000/30) ≈ 35.7.
        Assert.InRange(Array.IndexOf(spectrum, spectrum.Max()), 34, 37);
        Assert.Equal(1, bus.Channels);

        // Sound that stopped more than a second before the instant is not answered; nor is a node listening to nothing.
        Assert.False(bus.TryWave(newest + 1_500_000_000, wave.Length, wave));
        Assert.False(bus.TrySpectrum(newest + 1_500_000_000, spectrum.Length, spectrum));
        heard.Feed.Listener = null;
        Assert.False(bus.TryWave(newest + 16_000_000, wave.Length, wave));
        Assert.False(bus.TrySpectrum(newest + 16_000_000, spectrum.Length, spectrum));
        Assert.False(bus.TryRead("audio:level", newest, out var missing));
        Assert.Equal(0.5, missing);
    }

    [Fact]
    public void ASmoothedSourceLagsItsSourceAndAReaderThatJoinsLateAtAnotherRateAgrees()
    {
        var clock = TempoTimeline.Start(120);
        const string smoothed = "beat:pulse~30/200";
        var early = new ModulationBus(null, () => clock);
        var late = new ModulationBus(null, () => clock);
        var start = 100_000_000_000L;
        var join = start + 6_000_000_000L;
        var end = start + 12_000_000_000L;
        // The early reader draws at 144 frames a second (and at the late reader's instants); the late one at 50, from 6 s on.
        var instants = new SortedSet<long>();
        for (var t = start; t < end; t += 1_000_000_000L / 144)
            instants.Add(t);
        var lateInstants = new List<long>();
        for (var t = join + 3_141_593; t < end; t += 1_000_000_000L / 50)
        {
            instants.Add(t);
            lateInstants.Add(t);
        }
        var seen = new Dictionary<long, double>();
        foreach (var t in instants)
        {
            Assert.True(early.TryRead(smoothed, t, out var v));
            seen[t] = v;
        }
        foreach (var t in lateInstants)
        {
            Assert.True(late.TryRead(smoothed, t, out var v));
            Assert.Equal(seen[t], v);
        }

        // It lags: one tick after a beat line the follower has made a small part of the rise; 300 ms on it is still above the
        // source, which has fallen with its 100 ms decay.
        var beat = clock.NextBoundary(start + 3_000_000_000L, 1);
        var probe = new ModulationBus(null, () => clock);
        Assert.True(probe.TryRead(smoothed, beat - 5_000_000, out _));
        Assert.True(probe.TryRead(smoothed, beat + 5_000_000, out var rising));
        Assert.True(probe.TryRead("beat:pulse", beat + 5_000_000, out var rawRising));
        Assert.True(rising < 0.5 * rawRising, $"one tick after the beat the smoothed pulse is {rising:0.###} against {rawRising:0.###}");
        Assert.True(probe.TryRead(smoothed, beat + 300_000_000, out var falling));
        Assert.True(probe.TryRead("beat:pulse", beat + 300_000_000, out var rawFalling));
        Assert.True(falling > rawFalling + 0.05, $"300 ms after the beat the smoothed pulse is {falling:0.###} against {rawFalling:0.###}");
    }

    [Fact]
    public void ReadingResolvedSourcesAllocatesNothing()
    {
        var heard = new Heard();
        heard.Play(Tone, 2);
        var clock = TempoTimeline.Start(128);
        var bus = new ModulationBus(heard.Feed, () => clock);
        string[] names = ["audio:bass", "audio:bass~10/250", "audio:onset", "audio:band/3/n", "beat", "bar:pulse", "beat:pulse~20/100",
            "bpm", "lfo:sine/4b", "lfo:random/1", "lfo:random/0.5b~50"];
        var slots = names.Select(bus.Resolve).ToArray();
        Assert.DoesNotContain(-1, slots);
        var t = heard.NowNs - 500_000_000;
        var wave = new float[512];
        var spectrum = new float[64];
        void Frame()
        {
            bus.BeginFrame(t);
            foreach (var s in slots)
                bus.TryRead(s, t, out _);
            bus.TryRead("lfo:random/1", t, out _);
            bus.TryWave(t, wave.Length, wave);
            bus.TrySpectrum(t, spectrum.Length, spectrum);
            t += 16_666_667;
        }
        for (var i = 0; i < 10; i++)
            Frame();
        var before = Allocations.Start();
        for (var i = 0; i < 25; i++)
            Frame();
        Assert.Equal(0, Allocations.Since(before));
    }
}
