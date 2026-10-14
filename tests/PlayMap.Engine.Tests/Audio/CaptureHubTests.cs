using PlayMap.Core.Audio;
using PlayMap.Core.Time;
using PlayMap.Media.Audio;

namespace PlayMap.Engine.Tests.Audio;

/// <summary>The input hub: one device opened once for every listener, each listener with its own channel pick; the engine listening
/// through it; the test inputs on a hand-moved clock; and FFmpeg's capture devices on this machine (none) answered with words.</summary>
public sealed class CaptureHubTests
{
    /// <summary>Keeps what it is given.</summary>
    private sealed class Sink : IAudioCaptureSink
    {
        public readonly List<float> Samples = [];
        public int Channels;
        public int Faults;

        void IAudioCaptureSink.Samples(ReadOnlySpan<float> interleaved, int channels, int sampleRate, long firstSampleIndex, long readMonoNs)
        {
            Channels = channels;
            Samples.AddRange(interleaved.ToArray());
        }

        public void Faulted(string words) => Faults++;
    }

    [Fact]
    public void OneDeviceTwoListenersDifferentPicksOneOpen()
    {
        var fake = new FakeAudioInputs(channels: 8);
        using var hub = new AudioInputHub().Add("fake:", fake);
        var analysis = new Sink();
        var timecode = new Sink();
        var a = hub.Open("fake:desk@1+2", 48000, analysis);
        var b = hub.Open("fake:desk@7", 48000, timecode);
        Assert.Equal(1, fake.Opens);
        Assert.Equal(1, hub.Opens);
        Assert.Equal(2, hub.OpenDevices()["fake:desk"]);
        // Channel c carries the value c (1-based) on every frame.
        float[] gains = [1, 2, 3, 4, 5, 6, 7, 8];
        fake.Captures.Single().Push([1f, 1f, 1f], 1_000, gains);
        Assert.Equal(1, analysis.Channels);
        Assert.Equal([1.5f, 1.5f, 1.5f], analysis.Samples);
        Assert.Equal(1, timecode.Channels);
        Assert.Equal([7f, 7f, 7f], timecode.Samples);
        Assert.Equal(1, a.Channels);
        Assert.Equal(1, b.Channels);
        a.Dispose();
        Assert.False(fake.Captures.Single().Disposed);
        b.Dispose();
        Assert.True(fake.Captures.Single().Disposed);
        Assert.Empty(hub.OpenDevices());
    }

    [Fact]
    public void PicksReadPairsMidAndSide()
    {
        float[] row = [0.5f, 0.25f, 0.1f, 0.2f];
        var into = new float[2];
        Assert.True(ChannelPick.TryParse("3-4", out var pair, out _));
        pair.Apply(row, 4, into);
        Assert.Equal([0.1f, 0.2f], into);
        Assert.True(ChannelPick.TryParse("M", out var mid, out _));
        mid.Apply(row, 4, into);
        Assert.Equal(0.375f, into[0]);
        Assert.True(ChannelPick.TryParse("s", out var side, out _));
        side.Apply(row, 4, into);
        Assert.Equal(0.125f, into[0]);
        Assert.False(ChannelPick.TryParse("1+1", out _, out var why));
        Assert.Contains("not a channel pick", why, StringComparison.Ordinal);
        Assert.False(ChannelPick.TryParse("0", out _, out _));
        Assert.Equal(("fake:desk", ChannelPick.All), AudioInputHub.Split("fake:desk"));
        Assert.Equal("alsa@weird", AudioInputHub.Split("alsa@weird").Device);
    }

    [Fact]
    public void AKeyNoBackendServesIsRefusedWithWords()
    {
        using var hub = new AudioInputHub().Add("fake:", new FakeAudioInputs());
        var e = Assert.Throws<AudioUnavailableException>(() => hub.Open("pulse:default", 48000, new Sink()));
        Assert.Contains("no sound backend", e.Message, StringComparison.Ordinal);
        Assert.Throws<AudioUnavailableException>(() => hub.Open("fake:other", 48000, new Sink()));
        Assert.Empty(hub.OpenDevices());
    }

    [Fact]
    public void DeviceNamesMatchAcrossSockets()
    {
        AudioInputInfo Info(string name) => new("k:" + name, name, 2, [48000], "test");
        IReadOnlyList<AudioInputInfo> inputs = [Info("Microphone (3- USB Audio CODEC)"), Info("Line In (Realtek Audio)")];
        Assert.Equal("Microphone (3- USB Audio CODEC)", AudioInputMatch.Find(inputs, "Microphone (2- USB Audio CODEC)")!.Name);
        Assert.Equal("Line In (Realtek Audio)", AudioInputMatch.Find(inputs, "line in (realtek audio)")!.Name);
        Assert.Null(AudioInputMatch.Find(inputs, "Microphone (2- Other Box)"));
        Assert.Equal("microphone (usb audio codec)", AudioInputMatch.Key("Microphone  (12- USB Audio CODEC) "));
    }

    [Fact]
    public async Task TheEngineListensThroughTheHubAndAnswersWhatItHears()
    {
        await using var rig = new AudioRig();
        var r = await rig.Ok("AUDIO LISTEN fake:desk CHANNELS 7");
        var state = AudioRig.Json(r);
        Assert.Equal("listening", state.GetProperty("state").GetString());
        Assert.Equal("fake:desk", state.GetProperty("key").GetString());
        Assert.Equal("Desk mixer", state.GetProperty("name").GetString());
        Assert.Equal("7", state.GetProperty("channels").GetString());
        Assert.Equal("fake:desk", rig.Engine.Store.Project.Audio.Input);
        Assert.Contains("already listens", await rig.Refused("AUDIO LISTEN fake:desk CHANNELS 7"), StringComparison.Ordinal);
        // A second listener on the same device (a timecode reader) shares the open.
        var other = new Sink();
        using (rig.Engine.AudioRt.Hub.Open("fake:desk@1", 48000, other))
            Assert.Equal(1, rig.Fake.Opens);
        // Another input closes the first device.
        await rig.Ok("AUDIO LISTEN TEST CLICK");
        Assert.True(rig.Fake.Captures.Single().Disposed);
        Assert.Equal("test:click", rig.State().GetProperty("audio").GetProperty("input").GetProperty("key").GetString());
        // An input this node does not have: applied to the show, observed as retrying with words.
        var missing = await rig.Ok("AUDIO LISTEN \"Stage Mic\"");
        Assert.Equal("retrying", AudioRig.Json(missing).GetProperty("state").GetString());
        Assert.Contains("no input 'Stage Mic'", AudioRig.Json(missing).GetProperty("words").GetString(), StringComparison.Ordinal);
        Assert.Contains("cannot hear it yet", missing.Effect, StringComparison.Ordinal);
        await rig.Ok("AUDIO LISTEN OFF");
        Assert.Equal("off", rig.State().GetProperty("audio").GetProperty("input").GetProperty("state").GetString());
        await rig.Refused("AUDIO LISTEN fake:desk CHANNELS 9-9");
        await rig.Refused("AUDIO LISTEN fake:desk GAIN 40");
        await rig.Refused("AUDIO LISTEN TEST WHISTLE");
    }

    [Fact]
    public async Task HereKeepsThisMachinesOwnInputBesideTheShows()
    {
        await using var rig = new AudioRig();
        await rig.Ok("AUDIO LISTEN TEST NOISE");
        var here = await rig.Ok("AUDIO LISTEN fake:desk HERE");
        Assert.Contains("this machine listens", here.Effect, StringComparison.Ordinal);
        Assert.Equal("test:noise", rig.Engine.Store.Project.Audio.Input);
        Assert.Equal("fake:desk", rig.Engine.AudioRt.Node.Input);
        var input = rig.State().GetProperty("audio").GetProperty("input");
        Assert.True(input.GetProperty("here").GetBoolean());
        Assert.Equal("fake:desk", input.GetProperty("key").GetString());
        await rig.Ok("AUDIO LISTEN OFF HERE");
        Assert.Equal("", rig.Engine.AudioRt.Node.Input);
        Assert.Equal("test:noise", rig.State().GetProperty("audio").GetProperty("input").GetProperty("key").GetString());
    }

    [Fact]
    public async Task ATestClickDrivesTheChainOnAHandMovedClock()
    {
        await using var rig = new AudioRig();
        await rig.Ok("AUDIO LISTEN TEST CLICK");
        // Two seconds of the clock, pumped in 10 ms steps (the engine's tick does this on a running node).
        for (var i = 0; i < 200; i++)
        {
            rig.AdvanceMs(10);
            rig.Engine.AudioRt.Synthetic.Pump();
        }
        var status = AudioRig.Json(await rig.Ok("AUDIO STATUS"));
        Assert.True(status.GetProperty("heard").GetBoolean());
        Assert.Equal(16, status.GetProperty("bands").GetArrayLength());
        Assert.Equal(64, status.GetProperty("spectrum").GetArrayLength());
        Assert.InRange(status.GetProperty("frameAgeMs").GetDouble(), 0, 30);
        // A click every 500 ms: the newest onset is less than a beat ago.
        Assert.InRange(status.GetProperty("onsetAgoMs").GetDouble(), 0, 520);
        var inputs = AudioRig.Json(await rig.Ok("AUDIO INPUTS"));
        Assert.Contains(inputs.GetProperty("inputs").EnumerateArray(), i => i.GetProperty("key").GetString() == "test:click" && i.GetProperty("listening").GetBoolean());
        Assert.Contains(inputs.GetProperty("inputs").EnumerateArray(), i => i.GetProperty("key").GetString() == "fake:desk");
    }

    [Fact]
    public async Task GainAnswersAndRefusesNoOps()
    {
        await using var rig = new AudioRig();
        await rig.Ok("AUDIO GAIN 6");
        Assert.Equal(6, rig.Engine.Store.Project.Audio.GainDb);
        Assert.Contains("already 6", await rig.Refused("AUDIO GAIN 6"), StringComparison.Ordinal);
        await rig.Ok("AUDIO GAIN FIXED");
        Assert.False(rig.Engine.Store.Project.Audio.AutoGain);
        Assert.Contains("already fixed", await rig.Refused("AUDIO GAIN FIXED"), StringComparison.Ordinal);
        await rig.Refused("AUDIO GAIN 30");
    }

    /// <summary>FFmpeg's capture devices on this machine: the formats this build offers list no device here, and the backend says so
    /// instead of answering an empty list alone; opening one fails with words, never a crash.</summary>
    [Fact]
    public void TheDeviceBackendListsNoCaptureDeviceHereWithWords()
    {
        var ffmpeg = new FfmpegAudioInputs();
        var inputs = ffmpeg.Inputs();
        Assert.Equal("ffmpeg", ffmpeg.Backend);
        if (inputs.Count == 0)
            Assert.False(string.IsNullOrWhiteSpace(ffmpeg.Words), "an empty list comes with words");
        Assert.All(inputs, i => Assert.Contains(":", i.Key, StringComparison.Ordinal));
        // A device that is not there: words (the device's, FFmpeg's absence, or a key this platform does not read), never a crash.
        var e = Assert.Throws<AudioUnavailableException>(() => ffmpeg.Open("oss:/dev/no-such-dsp", 48000, new Sink()));
        Assert.False(string.IsNullOrWhiteSpace(e.Message));
        Assert.Throws<AudioUnavailableException>(() => ffmpeg.Open("alsa:default", 48000, new Sink()));
        Assert.Contains(OperatingSystem.IsWindows() ? "dshow" : OperatingSystem.IsMacOS() ? "avfoundation" : "pulse", FfmpegAudioInputs.PlatformFormats);
    }

    [Fact]
    public void ASyntheticCaptureMakesExactlyTheSamplesUpToTheClock()
    {
        var clock = new ManualClock(1_000_000_000);
        using var inputs = new SyntheticAudioInputs(clock, () => 120, paced: false);
        var sink = new Sink();
        using var capture = inputs.Open("test:click", 48000, sink);
        clock.AdvanceMs(105);
        inputs.Pump();
        // 105 ms is 5040 frames: ten whole blocks of 480 delivered, the rest waits for the next pump.
        Assert.Equal(4800 * 2, sink.Samples.Count);
        Assert.Throws<AudioUnavailableException>(() => inputs.Open("test:whistle", 48000, sink));
        Assert.Equal(0, sink.Faults);
    }
}
