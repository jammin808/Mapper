using System.Buffers.Binary;
using PlayMap.Core.Numerics;
using PlayMap.Core.Time;
using PlayMap.Core.Tc;

namespace PlayMap.Core.Tests.Tc;

/// <summary>LTC words and audio, from SMPTE ST 12-1 (linear timecode: the bit assignment table — BCD units and tens, least significant
/// bit first; the user-bit groups; drop frame (bit 10) and colour frame (bit 11); the polarity correction bit (27, or 59 at 25 fps)
/// that makes the zeros even; the binary group flags; the sync word 0011 1111 1111 1101 in bits 64–79 — and biphase mark coding: a
/// transition at every cell's start, another at mid-cell for a 1). The three words below are laid out by hand from that table.</summary>
public sealed class LtcDecoderTests
{
    /// <summary>10:20:30:12 at 25 fps: frames 2 (0100) tens 1 (10), seconds 0 tens 3 (110), minutes 0 tens 2 (010), hours 0 tens 1
    /// (10); 6 data ones and 13 in the sync word leave 61 zeros, so the polarity bit (59 at 25 fps) is 1.</summary>
    private const string Word25 = "01000000 10000000 00000000 11000000 00000000 01000000 00000000 10010000 0011111111111101";

    /// <summary>00:01:00;02 at 29.97 drop frame (the first label of minute 1: ;00 and ;01 are dropped): frames 2 (0100), the drop
    /// flag (bit 10), minutes 1 (1000); 3 data ones and 13 sync ones leave 64 zeros: the polarity bit (27) stays 0.</summary>
    private const string Word2997Df = "01000000 00100000 00000000 00000000 10000000 00000000 00000000 00000000 0011111111111101";

    /// <summary>23:59:59:29 at 30 fps with the colour frame flag and user group 1 = 1: frames 9 (1001) and user 1 (1000) tens 2
    /// (01), colour frame (bit 11), seconds 9 (1001) tens 5 (101), minutes 9 tens 5, hours 3 (1100) tens 2 (01); 16 data ones and 13
    /// sync ones leave 51 zeros, so the polarity bit (27) is 1.</summary>
    private const string Word30 = "10011000 01010000 10010000 10110000 10010000 10100000 11000000 01000000 0011111111111101";

    [Theory]
    [InlineData(Word25, 10, 20, 30, 12, false, false, 0u, true)]
    [InlineData(Word2997Df, 0, 1, 0, 2, true, false, 0u, false)]
    [InlineData(Word30, 23, 59, 59, 29, false, true, 1u, false)]
    public void TheWordIsTheStandardsBitTable(string bits, int h, int m, int s, int f, bool df, bool colour, uint user, bool frames25)
    {
        var expected = LtcCodeword.FromBitString(bits);
        var built = LtcCodeword.Build(new LtcFields(h, m, s, f, df, colour, user, false, false, false), frames25);
        Assert.Equal(expected.ToBitString(), built.ToBitString());
        Assert.True(built.HasSync);
        Assert.True(built.ParityOk);
        Assert.True(expected.TryRead(frames25, out var read));
        Assert.Equal((h, m, s, f, df, colour, user), (read.Hours, read.Minutes, read.Seconds, read.Frames, read.DropFrame, read.ColourFrame, read.UserBits));
    }

    [Theory]
    [InlineData(Word25, 25)]
    [InlineData(Word2997Df, 30)]
    [InlineData(Word30, 30)]
    public void TheHandWordsDecodeFromAudio(string bits, int labelFps)
    {
        var word = LtcCodeword.FromBitString(bits);
        var format = labelFps == 25 ? TcFormat.Fps25 : TcFormat.Fps30;
        var encoder = new LtcEncoder(format, 48000);
        var (samples, ends) = encoder.Render([word, word, word, word, word], leadSamples: 100);
        var frames = Decode(samples, 48000);
        Assert.True(frames.Count >= 3, $"{frames.Count} frames");
        Assert.All(frames, f => Assert.Equal(word.ToBitString(), f.Word.ToBitString()));
        Assert.All(frames, f => Assert.Equal(1, f.Direction));
        // Each frame ends at its last bit's end (the next frame's first transition), seen through the encoder's rise time.
        var lag = 40e-6 * 48000 / 2.197 * Math.Log(2);
        foreach (var f in frames)
            Assert.Contains(ends, e => Math.Abs(f.EndSample - e - lag) < 0.25);
    }

    private static List<LtcFrameEvent> Decode(float[] samples, int rate, int block = 512)
    {
        var decoder = new LtcDecoder(rate);
        var frames = new List<LtcFrameEvent>();
        for (var at = 0; at < samples.Length; at += block)
            decoder.Process(samples.AsSpan(at, Math.Min(block, samples.Length - at)), at, frames);
        return frames;
    }

    /// <summary>An external fixture: tests/fixtures/ltc/ltc-30fps-44k1.wav, written by tests/fixtures/ltc/make_ltc_wav.py — an
    /// independent writer (a plain square wave, flips at the nearest sample, no LTC library) from the same bit table — one second of
    /// 30 fps from 10:00:00:00 at 44.1 kHz.</summary>
    [Fact]
    public void AnExternalWavDecodesFrameAfterFrame()
    {
        var path = Path.Combine(PlayMap.Core.Tests.Model.RoundTwoSample.RepoRoot(), "tests", "fixtures", "ltc", "ltc-30fps-44k1.wav");
        var (samples, rate) = ReadWav(File.ReadAllBytes(path));
        Assert.Equal(44100, rate);
        var frames = Decode(samples, rate);
        Assert.True(frames.Count >= 27, $"{frames.Count} frames");
        var labels = frames.Select(f => f.Fields.Frames).ToList();
        Assert.All(frames, f => Assert.Equal((10, 0, 0), (f.Fields.Hours, f.Fields.Minutes, f.Fields.Seconds)));
        for (var i = 1; i < labels.Count; i++)
            Assert.Equal(labels[i - 1] + 1, labels[i]);
        Assert.InRange(rate / (frames[^1].SamplesPerBit * 80), 29.8, 30.2);
    }

    private static (float[] Samples, int Rate) ReadWav(byte[] wav)
    {
        var rate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24));
        var at = 12;
        while (at + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, at, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(at + 4));
            if (id == "data")
            {
                var samples = new float[size / 2];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(at + 8 + 2 * i)) / 32768f;
                return (samples, rate);
            }
            at += 8 + size;
        }
        throw new InvalidDataException("no data chunk");
    }

    [Theory]
    [InlineData("24", 44100)]
    [InlineData("24", 48000)]
    [InlineData("25", 44100)]
    [InlineData("25", 48000)]
    [InlineData("29.97df", 44100)]
    [InlineData("29.97df", 48000)]
    [InlineData("29.97", 44100)]
    [InlineData("29.97", 48000)]
    [InlineData("30", 44100)]
    [InlineData("30", 48000)]
    public void EveryRateDecodesAtEverySampleRate(string rate, int sampleRate)
    {
        Assert.True(TcFormat.TryParse(rate, out var format));
        Assert.True(format.TryFrame(1, 2, 59, 0, out var start));
        var encoder = new LtcEncoder(format, sampleRate);
        var (samples, ends) = encoder.Render(start, 60, leadSamples: 37.5);
        var frames = Decode(samples, sampleRate);
        Assert.True(frames.Count >= 57, $"{frames.Count} of 60 frames");
        var labels = frames.Select(f => format.TryFrame(f.Fields.Hours, f.Fields.Minutes, f.Fields.Seconds, f.Fields.Frames, out var n) ? n : -1).ToList();
        Assert.DoesNotContain(-1, labels);
        // Consecutive frames, strictly (a drop-frame minute skips labels, never frames).
        for (var i = 1; i < labels.Count; i++)
            Assert.Equal(labels[i - 1] + 1, labels[i]);
        Assert.All(frames, f => Assert.Equal(format.DropFrame, f.Fields.DropFrame));
        var lag = 40e-6 * sampleRate / 2.197 * Math.Log(2);
        foreach (var f in frames)
        {
            var i = (int)(labels[frames.IndexOf(f)] - start);
            Assert.True(Math.Abs(f.EndSample - ends[i] - lag) < 0.25, $"frame {i}: {f.EndSample} vs {ends[i] + lag}");
        }
    }

    [Fact]
    public void ADropFrameMinuteSkipsItsFirstTwoLabelsAndThoseLabelsDoNotExist()
    {
        var df = TcFormat.Fps2997Df;
        Assert.True(df.TryFrame(0, 0, 59, 29, out var last));
        Assert.True(df.TryFrame(0, 1, 0, 2, out var first));
        Assert.Equal(last + 1, first);
        Assert.False(df.TryFrame(0, 1, 0, 0, out _));
        Assert.False(df.TryFrame(0, 1, 0, 1, out _));
        Assert.True(df.TryFrame(0, 10, 0, 0, out var tenth));
        Assert.Equal("00:10:00;00", df.Format(tenth));
        Assert.True(df.TryFrame(1, 0, 0, 0, out var hour));
        Assert.Equal(107_892, hour);
        Assert.Equal(3_599_996_400_000L, df.Ns(hour));
        Assert.Equal("01:00:00;00", df.Format(hour));
        var encoder = new LtcEncoder(df, 48000);
        var (samples, _) = encoder.Render(last - 3, 10);
        var labels = Decode(samples, 48000).Select(f => df.Format(df.TryFrame(f.Fields.Hours, f.Fields.Minutes, f.Fields.Seconds, f.Fields.Frames, out var n) ? n : 0)).ToList();
        Assert.Contains("00:00:59;29", labels);
        Assert.Contains("00:01:00;02", labels);
        Assert.DoesNotContain("00:01:00;00", labels);
    }

    [Theory]
    [InlineData(0.9)]
    [InlineData(1.1)]
    public void TenPercentOffSpeedStillDecodes(double speed)
    {
        var encoder = new LtcEncoder(TcFormat.Fps25, 48000, speed: speed);
        Assert.True(TcFormat.Fps25.TryFrame(0, 0, 10, 0, out var start));
        var (samples, _) = encoder.Render(start, 50);
        var frames = Decode(samples, 48000);
        Assert.True(frames.Count >= 47, $"{frames.Count} of 50 frames at {speed}×");
        Assert.InRange(48000 / (frames[^1].SamplesPerBit * 80), 25 * speed - 0.2, 25 * speed + 0.2);
    }

    [Fact]
    public void TwentyDecibelsOfNoiseLosesNoFrame()
    {
        var encoder = new LtcEncoder(TcFormat.Fps30, 48000, levelDbfs: -12);
        Assert.True(TcFormat.Fps30.TryFrame(0, 0, 10, 0, out var start));
        var (samples, _) = encoder.Render(start, 60);
        // The signal's RMS is its amplitude (a square wave); the noise's RMS a tenth of it: 20 dB SNR.
        var sigma = Math.Pow(10, -12 / 20.0) / 10;
        var rng = new Pcg32(2024);
        for (var i = 0; i < samples.Length; i++)
        {
            var u1 = Math.Max(1e-12, rng.NextDouble());
            var u2 = rng.NextDouble();
            samples[i] += (float)(sigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }
        var decoder = new LtcDecoder(48000);
        var frames = new List<LtcFrameEvent>();
        decoder.Process(samples, 0, frames);
        Assert.True(frames.Count >= 57, $"{frames.Count} of 60 frames");
        Assert.Equal(0, decoder.Counts.BadBcd);
    }

    [Fact]
    public void BackwardsAudioReadsAsReverseFramesWithTheirLabels()
    {
        var encoder = new LtcEncoder(TcFormat.Fps25, 48000);
        Assert.True(TcFormat.Fps25.TryFrame(0, 5, 0, 0, out var start));
        var (samples, _) = encoder.Render(start, 30, leadSamples: 50);
        Array.Reverse(samples);
        var frames = Decode(samples, 48000);
        Assert.True(frames.Count >= 25, $"{frames.Count} reverse frames");
        Assert.All(frames, f => Assert.Equal(-1, f.Direction));
        var labels = frames.Select(f => f.Fields.Seconds * 25 + f.Fields.Frames).ToList();
        for (var i = 1; i < labels.Count; i++)
            Assert.Equal(labels[i - 1] - 1, labels[i]);
    }

    [Fact]
    public void BelowTheFloorThereIsNoSignalAndNoFrame()
    {
        var encoder = new LtcEncoder(TcFormat.Fps25, 48000, levelDbfs: -60);
        var (samples, _) = encoder.Render(0, 10);
        var decoder = new LtcDecoder(48000, floorDbfs: -46);
        var frames = new List<LtcFrameEvent>();
        decoder.Process(samples, 0, frames);
        Assert.Empty(frames);
        Assert.False(decoder.HasSignal);
    }
}
