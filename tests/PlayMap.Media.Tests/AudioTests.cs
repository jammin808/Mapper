using PlayMap.Core.Playback;
using PlayMap.Core.Time;
using PlayMap.Media.Audio;
using PlayMap.Media.Ffmpeg;

namespace PlayMap.Media.Tests;

/// <summary>Sound: decoding to float stereo 48 kHz with exact sample counts and sample-exact seeks, and the
/// output slaved to a timeline — through OpenAL Soft's loopback device, rendered block by block against a hand-moved
/// clock, so "in step" is measured, not hoped.</summary>
public class AudioTests
{
    private const int Block = AudioOut.BlockFrames;
    private const long BlockNs = 10_000_000;

    private static long DecodeAll(FfmpegAudioDecoder decoder)
    {
        var buffer = new float[4096 * 2];
        long total = 0;
        int n;
        while ((n = decoder.Read(buffer)) > 0)
            total += n;
        return total;
    }

    [Fact]
    public void A48KilohertzWavYieldsExactlyItsSamples()
    {
        TestMedia.Ensure();
        using var decoder = new FfmpegAudioDecoder(TestMedia.Clip("tone48.wav"));
        Assert.Equal(2_000_000_000, decoder.DurationNs);
        Assert.Equal(96_000, DecodeAll(decoder));
        Assert.True(decoder.EndOfStream);
        Assert.Equal(2_000_000_000, decoder.PositionNs);
        Assert.StartsWith("pcm_s16le", decoder.Codec, StringComparison.Ordinal);
    }

    [Fact]
    public void AMono44KilohertzWavIsResampledToStereo48()
    {
        TestMedia.Ensure();
        using var decoder = new FfmpegAudioDecoder(TestMedia.Clip("tone44.wav"));
        Assert.InRange(DecodeAll(decoder), 96_000 - 2, 96_000 + 2);
        decoder.Seek(500_000_000);
        var buffer = new float[2 * 2];
        Assert.Equal(2, decoder.Read(buffer));
        // Mono is spread to both channels.
        Assert.Equal(buffer[0], buffer[1], 4);
        Assert.Equal(buffer[2], buffer[3], 4);
    }

    [Fact]
    public void AnAacFileYieldsItsSamplesWithinOneCodecFrame()
    {
        TestMedia.Ensure();
        using var decoder = new FfmpegAudioDecoder(TestMedia.Clip("tone.m4a"));
        Assert.InRange(DecodeAll(decoder), 96_000 - 1024, 96_000 + 1024);
    }

    [Fact]
    public void ASeekIsSampleExact()
    {
        TestMedia.Ensure();
        using var linear = new FfmpegAudioDecoder(TestMedia.Clip("tone48.wav"));
        var all = new float[96_000 * 2];
        Assert.Equal(96_000, linear.Read(all));
        using var seeking = new FfmpegAudioDecoder(TestMedia.Clip("tone48.wav"));
        foreach (var ms in new[] { 1000.0, 250.5, 1999.0, 0, 1337.25 })
        {
            var ns = Durations.FromMs(ms);
            seeking.Seek(ns);
            var start = FfmpegAudioDecoder.NsToSample(ns);
            Assert.Equal(start, seeking.PositionSamples);
            var got = new float[64 * 2];
            var n = seeking.Read(got);
            Assert.Equal((int)Math.Min(64, 96_000 - start), n);
            for (var i = 0; i < n * 2; i++)
                Assert.Equal(all[start * 2 + i], got[i]);
        }
    }

    [Fact]
    public void AFileWithoutSoundSaysSo()
    {
        TestMedia.Ensure();
        var e = Assert.Throws<MediaOpenException>(() => new FfmpegAudioDecoder(TestMedia.Clip("h264_25.mp4")));
        Assert.Equal(MediaProblem.NoStream, e.Problem);
        var missing = Assert.Throws<MediaOpenException>(() => new FfmpegAudioDecoder(TestMedia.Clip("notag.wav")));
        Assert.Equal(MediaProblem.DecoderNotAvailable, missing.Problem);
    }

    /// <summary>A running timeline on a hand-moved clock.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly ManualClock Clock = new(1_000_000_000);
        public TimelineAnchor Anchor;
        public readonly FfmpegAudioDecoder Decoder;
        public readonly AudioOut Out = AudioOut.OpenLoopback();
        public readonly float[] Buffer = new float[Block * 2];

        public Rig(string clip, double rate = 1)
        {
            TestMedia.Ensure();
            Decoder = new FfmpegAudioDecoder(TestMedia.Clip(clip));
            Anchor = new TimelineAnchor(0, Clock.NowNs, rate, true);
            Out.Play(Decoder, new AnchorTimeline(() => Anchor, Clock));
        }

        /// <summary>Renders one block of the given frames, then moves the timeline's clock 10 ms.</summary>
        public void Step(int frames = Block)
        {
            Out.Render(frames == Block ? Buffer : new float[frames * 2]);
            Clock.Advance(BlockNs);
        }

        public void Dispose()
        {
            Out.Dispose();
            Decoder.Dispose();
        }
    }

    [Fact]
    public void TheLoopbackOutputConsumesInStepWithTheTimeline()
    {
        using var rig = new Rig("tone48.wav");
        double worst = 0;
        double peak = 0;
        for (var i = 0; i < 150; i++)
        {
            rig.Step();
            if (i > 0)
                worst = Math.Max(worst, Math.Abs(rig.Out.LastErrorMs));
            foreach (var s in rig.Buffer)
                peak = Math.Max(peak, Math.Abs(s));
        }
        Assert.True(worst <= 1.0, $"worst error {worst} ms");
        Assert.Equal(0, rig.Out.Resyncs);
        Assert.Equal(1.0, rig.Out.Pitch, 3);
        // 1.5 s rendered: the clock is there, and what was fed is that plus the queue ahead.
        Assert.InRange(rig.Out.AudioClockNs, 1_490_000_000, 1_510_000_000);
        Assert.InRange(rig.Out.FramesFed, 72_000, 72_000 + AudioOut.QueueBlocks * Block);
        Assert.True(peak > 0.05, $"the sine (an eighth of full scale) did not reach the mix: peak {peak}");
    }

    [Fact]
    public void ADeviceClockRunningFastIsSteeredWithinTenMilliseconds()
    {
        // The output consumes 0.2 % more than the timeline advances (a device crystal off by 2000 ppm): the
        // resampler must slow it so the error settles and stays inside ±10 ms.
        using var rig = new Rig("tone48.wav");
        double worst = 0;
        for (var i = 0; i < 180; i++)
        {
            rig.Step(Block + 1);
            worst = Math.Max(worst, Math.Abs(rig.Out.LastErrorMs));
        }
        Assert.True(worst <= 10, $"worst error {worst} ms");
        Assert.Equal(0, rig.Out.Resyncs);
        Assert.InRange(rig.Out.Pitch, 0.995, 0.9995);
    }

    [Fact]
    public void ATimelineJumpResyncsHard()
    {
        using var rig = new Rig("tone48.wav");
        for (var i = 0; i < 20; i++)
            rig.Step();
        rig.Anchor = rig.Anchor.Seek(1_200_000_000, rig.Clock.NowNs);
        rig.Step();
        Assert.Equal(1, rig.Out.Resyncs);
        rig.Step();
        Assert.True(Math.Abs(rig.Out.LastErrorMs) <= 1, $"error {rig.Out.LastErrorMs} ms after the resync");
        Assert.InRange(rig.Out.AudioClockNs, 1_210_000_000, 1_230_000_000);
    }

    [Fact]
    public void APausedTimelineHoldsTheAudioClock()
    {
        using var rig = new Rig("tone48.wav");
        for (var i = 0; i < 10; i++)
            rig.Step();
        rig.Anchor = rig.Anchor.Pause(rig.Clock.NowNs);
        var held = rig.Out.AudioClockNs;
        for (var i = 0; i < 10; i++)
            rig.Step();
        Assert.Equal(held, rig.Out.AudioClockNs);
        Assert.All(rig.Buffer, s => Assert.Equal(0f, s));
        rig.Anchor = rig.Anchor.Resume(rig.Clock.NowNs);
        for (var i = 0; i < 10; i++)
            rig.Step();
        Assert.Equal(0, rig.Out.Resyncs);
        Assert.True(Math.Abs(rig.Out.LastErrorMs) <= 1);
    }

    [Fact]
    public void VolumeScalesTheMix()
    {
        using var loud = new Rig("tone48.wav");
        using var quiet = new Rig("tone48.wav");
        quiet.Out.Volume = 0.5f;
        double a = 0, b = 0;
        for (var i = 0; i < 20; i++)
        {
            loud.Step();
            quiet.Step();
            if (i < 2)
                continue;
            foreach (var s in loud.Buffer)
                a = Math.Max(a, Math.Abs(s));
            foreach (var s in quiet.Buffer)
                b = Math.Max(b, Math.Abs(s));
        }
        Assert.Equal(0.5, b / a, 2);
    }

    [Fact]
    public async Task TheNullOutputConsumesOnTheClock()
    {
        TestMedia.Ensure();
        using var decoder = new FfmpegAudioDecoder(TestMedia.Clip("tone48.wav"));
        using var output = AudioOut.OpenNull("tests have no sound card");
        Assert.Equal(AudioOutMode.Null, output.Mode);
        var start = MonoClock.Instance.NowNs;
        var anchor = new TimelineAnchor(0, start, 1, true);
        output.Play(decoder, new AnchorTimeline(() => anchor, MonoClock.Instance));
        await Task.Delay(400);
        var timeline = MonoClock.Instance.NowNs - start;
        Assert.InRange(output.AudioClockNs, timeline - 60_000_000, timeline + 10_000_000);
        Assert.InRange(output.FramesFed, 48_000 * 0.25, 48_000 * 0.45);
        Assert.True(Math.Abs(output.LastErrorMs) <= 10, $"error {output.LastErrorMs} ms");
    }

    [Fact]
    public void ADeviceOutputFallsBackToNullWithoutASoundCard()
    {
        TestMedia.Ensure();
        using var output = AudioOut.OpenDevice();
        // This machine has no sound card: the output says why it is the null one. With a card, it is a device.
        if (output.Mode == AudioOutMode.Null)
            Assert.Contains("no audio device", output.Status, StringComparison.Ordinal);
        else
            Assert.Equal(AudioOutMode.Device, output.Mode);
    }
}
