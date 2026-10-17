using PlayMap.Core.Playback;
using PlayMap.Core.Time;
using PlayMap.Media.Audio;
using PlayMap.Media.Ffmpeg;
using Xunit.Abstractions;

namespace PlayMap.Media.Tests;

/// <summary>Sound: decoding to float stereo 48 kHz with exact sample counts and sample-exact seeks, and the
/// output slaved to a timeline — through OpenAL Soft's loopback device, rendered block by block against a hand-moved
/// clock, so "in step" is measured, not hoped. Every output's service step reads its clock once and asks the timeline
/// about that one instant.</summary>
public class AudioTests(ITestOutputHelper log)
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
        public readonly AudioOut Out;
        public readonly float[] Buffer = new float[Block * 2];

        public Rig(string clip, double rate = 1)
        {
            TestMedia.Ensure();
            Out = AudioOut.OpenLoopback(Clock);
            Decoder = new FfmpegAudioDecoder(TestMedia.Clip(clip));
            Anchor = new TimelineAnchor(0, Clock.NowNs, rate, true);
            Out.Play(Decoder, new AnchorTimeline(() => Anchor));
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

    /// <summary>A timeline that tells the test which instants its output's steps asked about, and checks each against the clock's
    /// last answer.</summary>
    private sealed class WatchedTimeline(Func<TimelineAnchor> anchor, CountingClock? clock = null) : IAudioTimeline, IDisposable
    {
        private readonly ManualResetEventSlim _asked = new();
        private long _latest = long.MinValue;
        private long _steps;
        private long _strangers;

        /// <summary>The steps that asked.</summary>
        public long Steps => Interlocked.Read(ref _steps);

        /// <summary>The steps that asked about an instant other than the one their clock had just answered.</summary>
        public long Strangers => Interlocked.Read(ref _strangers);

        public TimelinePosition At(long localNs)
        {
            if (clock is not null && localNs != clock.Last)
                Interlocked.Increment(ref _strangers);
            Interlocked.Increment(ref _steps);
            Volatile.Write(ref _latest, localNs);
            _asked.Set();
            return AnchorTimeline.Position(anchor(), localNs);
        }

        /// <summary>Waits until a step has asked about <paramref name="instant"/> or later (false past the hang guard).</summary>
        public bool AskedAt(long instant)
        {
            while (Volatile.Read(ref _latest) < instant)
            {
                _asked.Reset();
                if (Volatile.Read(ref _latest) >= instant)
                    break;
                if (!_asked.Wait(TimeSpan.FromSeconds(30)))
                    return false;
            }
            return true;
        }

        public void Dispose() => _asked.Dispose();
    }

    /// <summary>The process's monotonic clock, counting its reads and keeping its last answer.</summary>
    private sealed class CountingClock : IMonoClock
    {
        private long _reads;
        private long _last;

        public long Reads => Interlocked.Read(ref _reads);

        public long Last => Interlocked.Read(ref _last);

        public long NowNs
        {
            get
            {
                var now = MonoClock.Instance.NowNs;
                Interlocked.Exchange(ref _last, now);
                Interlocked.Increment(ref _reads);
                return now;
            }
        }
    }

    [Fact]
    public void TheNullOutputConsumesOnTheClock()
    {
        TestMedia.Ensure();
        using var decoder = new FfmpegAudioDecoder(TestMedia.Clip("tone48.wav"));
        // The null output's own thread consumes by the clock it is given: a hand-moved one makes its pace exact however loaded the
        // machine is, and each step is waited for by the instant it asked about.
        var clock = new ManualClock(5_000_000_000);
        using var output = AudioOut.OpenNull("tests have no sound card", clock);
        Assert.Equal(AudioOutMode.Null, output.Mode);
        var anchor = new TimelineAnchor(0, clock.NowNs, 1, true);
        using var timeline = new WatchedTimeline(() => anchor);
        output.Play(decoder, timeline);
        Assert.True(timeline.AskedAt(5_000_000_000), "the output did not start");
        Assert.Equal(0, output.AudioClockNs);
        for (var i = 1; i <= 40; i++)
        {
            clock.Advance(BlockNs);
            Assert.True(timeline.AskedAt(clock.NowNs), $"no step at {i * BlockNs} ns");
            Assert.Equal(i * BlockNs, output.AudioClockNs);
        }
        Assert.Equal(48_000 * 4 / 10, output.FramesFed);
        Assert.Equal(19_200, decoder.PositionSamples);
        Assert.Equal(0, output.Resyncs);
    }

    /// <summary>A hand-moved clock whose next read can move it: the step lands inside whatever reads it, right after that read.</summary>
    private sealed class SteppingClock(long startNs) : IMonoClock
    {
        private long _now = startNs;
        private long _afterRead;

        public long NowNs
        {
            get
            {
                var now = _now;
                _now += _afterRead;
                _afterRead = 0;
                return now;
            }
        }

        public void Advance(long ns) => _now += ns;

        /// <summary>The next read answers the instant as it stands; the clock then moves <paramref name="ns"/>.</summary>
        public void StepAfterNextRead(long ns) => _afterRead = ns;
    }

    /// <summary>The null output is a function of its clock: wherever the clock steps — inside a service step, right after the step's
    /// first read of it, or between two steps — the output holds, after a step, exactly the frames of the instant that step read, and
    /// nothing the clock did is lost or won back later.</summary>
    [Fact]
    public void AClockStepInsideAServiceStepLosesNoFrame()
    {
        TestMedia.Ensure();
        using var decoder = new FfmpegAudioDecoder(TestMedia.Clip("tone48.wav"));
        var clock = new SteppingClock(5_000_000_000);
        using var output = AudioOut.OpenNullPulled("tests have no sound card", clock);
        var anchor = new TimelineAnchor(0, 5_000_000_000, 1, true);
        output.Play(decoder, new AnchorTimeline(() => anchor));
        output.Service();
        Assert.Equal(0, output.AudioClockNs);
        for (var i = 1; i <= 40; i++)
        {
            // A block whose step lands inside a service step, then one whose step lands between two steps.
            clock.StepAfterNextRead(BlockNs);
            output.Service();
            clock.Advance(BlockNs);
            output.Service();
            Assert.Equal((2 * i * BlockNs, 2 * i * 480L), (output.AudioClockNs, output.FramesFed));
        }
        Assert.Equal(38_400, decoder.PositionSamples);
        Assert.Equal((1.0, 0.0, 0L), (output.Pitch, output.LastErrorMs, output.Resyncs));
    }

    /// <summary>A playhead between frames: steps a third of a frame apart consume the whole frames before each step's instant and
    /// carry the rest, so the output at every instant holds exactly that instant's whole frames.</summary>
    [Fact]
    public void AStepBetweenFramesCarriesThePartOfAFrame()
    {
        TestMedia.Ensure();
        using var decoder = new FfmpegAudioDecoder(TestMedia.Clip("tone48.wav"));
        var clock = new ManualClock(5_000_000_000);
        using var output = AudioOut.OpenNullPulled("tests have no sound card", clock);
        var anchor = new TimelineAnchor(0, 5_000_000_000, 1, true);
        output.Play(decoder, new AnchorTimeline(() => anchor));
        output.Service();
        // A third of a frame is 6944.4 ns: steps of 6944 ns, 3 × 3000 of them.
        for (var i = 1; i <= 9_000; i++)
        {
            clock.Advance(6_944);
            output.Service();
            var elapsed = clock.NowNs - 5_000_000_000;
            Assert.Equal(elapsed * 48_000 / 1_000_000_000, output.FramesFed);
        }
        Assert.Equal(clock.NowNs - 5_000_000_000, output.AudioClockNs);
        Assert.Equal((1.0, 0L), (output.Pitch, output.Resyncs));
    }

    /// <summary>The machine's output — a device where OpenAL Soft opens one (WASAPI on Windows, PulseAudio or ALSA on Linux), else
    /// the null one it falls back to — is serviced on its own thread through the same step: each step reads the output's clock once
    /// and asks the timeline about that instant, and no other.</summary>
    [Fact]
    public void TheMachinesOutputReadsItsClockOncePerServiceStep()
    {
        TestMedia.Ensure();
        using var decoder = new FfmpegAudioDecoder(TestMedia.Clip("tone48.wav"));
        var clock = new CountingClock();
        using var output = AudioOut.OpenDevice(null, clock);
        Assert.Same(clock, output.Clock);
        log.WriteLine($"{output.Mode}: {output.Status}");
        var anchor = new TimelineAnchor(0, clock.NowNs, 1, true);
        var before = clock.Reads;
        using var timeline = new WatchedTimeline(() => anchor, clock);
        output.Play(decoder, timeline);
        // Twenty steps of its thread; stopped under the output's gate, so no step is under way once Stop returns.
        Assert.True(timeline.AskedAt(anchor.LeaderNs + 100_000_000), $"the {output.Mode} output's thread did not step");
        output.Stop();
        Assert.Equal((clock.Reads - before, 0L), (timeline.Steps, timeline.Strangers));
        Assert.True(output.FramesFed > 0, $"the {output.Mode} output took nothing from the decoder");
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
