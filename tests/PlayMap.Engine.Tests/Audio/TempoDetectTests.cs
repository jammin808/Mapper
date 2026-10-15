using PlayMap.Core.Audio;

namespace PlayMap.Engine.Tests.Audio;

/// <summary>TEMPO SOURCE DETECT on a running engine: a drum loop at 128 BPM heard while the show says 120 locks the clock to 128 with
/// its beats on the kicks; silence holds the clock; back to MANUAL the show's tempo returns. The show's tempo is never written.</summary>
public sealed class TempoDetectTests
{
    /// <summary>Plays a signal into the fake device in 10 ms blocks, the clock moving with it, the audio tick every 100 ms.</summary>
    private static async Task Play(AudioRig rig, TestSignal signal, double seconds, long fromSample = 0)
    {
        var capture = rig.Fake.Captures[^1];
        var block = new float[480];
        var blocks = (int)(seconds * 100);
        for (var b = 0; b < blocks; b++)
        {
            signal.Fill(block, fromSample + b * 480L);
            rig.AdvanceMs(10);
            capture.Push(block, rig.Clock.NowNs);
            if (b % 10 == 9)
                await rig.Engine.Lane.RunAsync(rig.Engine.RunAudioTick);
        }
    }

    [Fact]
    public async Task ADrumLoopLocksTheClockToItsTempoAndItsBeats()
    {
        await using var rig = new AudioRig();
        await rig.Ok("TEMPO SOURCE DETECT");
        Assert.Equal("searching", AudioRig.Json(await rig.Ok("TEMPO STATUS")).GetProperty("lock").GetString());
        await rig.Ok("AUDIO LISTEN fake:desk CHANNELS 1");
        var start = rig.Clock.NowNs;
        await Play(rig, new DrumSignal(128), 12);
        var status = AudioRig.Json(await rig.Ok("TEMPO STATUS"));
        Assert.Equal("locked", status.GetProperty("lock").GetString());
        Assert.True(status.GetProperty("confidence").GetDouble() >= 0.3);
        // The clock settles at the sound's tempo, and its beats land on the kicks (sample k·22500 from the start).
        var clock = rig.Engine.AudioRt.Tempo;
        var settled = Math.Max(rig.Clock.NowNs, clock.Anchors[^1].LeaderNs);
        Assert.InRange(clock.BpmAt(settled), 127.5, 128.5);
        var beatNs = 60e9 / 128;
        for (var k = 0; k < 8; k++)
        {
            var kick = start + (long)Math.Round((Math.Ceiling((settled - start) / beatNs) + k) * beatNs);
            var b = clock.BeatAt(kick);
            var offMs = Math.Abs(b - Math.Round(b)) * beatNs / 1e6;
            Assert.True(offMs <= 15, $"kick {k} is {offMs:0.#} ms off the beat");
        }
        Assert.Equal(120, rig.Engine.Store.Project.Tempo.Bpm);
        var state = rig.State().GetProperty("audio").GetProperty("tempo");
        Assert.Equal("locked", state.GetProperty("lock").GetString());
        Assert.Equal("detect", state.GetProperty("source").GetString());

        // A nudge moves the beat it is heard against: the detected tempo, never the show's 120.
        var detected = rig.Engine.AudioRt.Tempo.Bpm;
        await rig.Ok("TEMPO NUDGE FASTER");
        Assert.Equal(detected * 1.04, rig.Engine.AudioRt.Tempo.BpmAt(rig.Clock.NowNs + 50_000_000), 9);
        Assert.Equal(detected, rig.Engine.AudioRt.Tempo.Bpm, 9);
        await rig.Ok("TEMPO NUDGE STOP");
        Assert.Equal(detected, rig.Engine.AudioRt.Tempo.BpmAt(rig.Clock.NowNs + 50_000_000), 9);

        // Silence holds the clock: it keeps counting at the locked tempo.
        await Play(rig, new SilenceSignal(), 3);
        Assert.Equal("holding", AudioRig.Json(await rig.Ok("TEMPO STATUS")).GetProperty("lock").GetString());
        Assert.InRange(rig.Engine.AudioRt.Tempo.BpmAt(rig.Clock.NowNs), 127.5, 128.5);

        // Manual again: the show's tempo comes back on the next tick, one lead ahead.
        await rig.Ok("TEMPO SOURCE MANUAL");
        await rig.Engine.Lane.RunAsync(rig.Engine.RunAudioTick);
        Assert.Equal("manual", AudioRig.Json(await rig.Ok("TEMPO STATUS")).GetProperty("lock").GetString());
        Assert.Equal(120, rig.Engine.AudioRt.Tempo.BpmAt(rig.Clock.NowNs + 50_000_000));
    }

    private sealed class SilenceSignal : TestSignal
    {
        public override void Fill(Span<float> mono, long first) => mono.Clear();
    }
}
