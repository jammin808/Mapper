using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PlayMap.Core.Audio;
using PlayMap.Core.Control.Osc;
using PlayMap.Core.Playback;
using PlayMap.Core.Tc;
using PlayMap.Core.Time;
using PlayMap.Engine.Audio;
using PlayMap.Engine.Playback;
using PlayMap.Engine.Tests.Control;
using PlayMap.Media.Audio;

namespace PlayMap.Engine.Tests.Tc;

/// <summary>control × audio: LTC written by control's encoder arrives through audio's capture hub (EngineServices.AudioInputs) and
/// a playlist chases it. The timecode reader opens "&lt;name&gt;@&lt;channel&gt;", so it shares one open of the device with the
/// analyser listening on other channels; a sound file carrying LTC is chased the same way. Opening an input (the hub holds its lock
/// while a device opens) and listing the inputs wait on devices: neither holds up the control loop or the lane.</summary>
public sealed class LtcThroughCaptureHubTests
{
    /// <summary>A mixer with eight channels: the test pushes each block, LTC on one channel and a tone on 1+2, with the instant it
    /// was read. Its open can be made to wait (a device slow to open), its listing too (a sound server slow to answer).</summary>
    private sealed class Mixer : IAudioInputs, IDisposable
    {
        public const string Key = "desk:mixer";
        public const int Channels = 8;
        private readonly ManualResetEventSlim _release = new(true);
        private readonly List<Capture> _captures = [];

        public int Opens { get; private set; }

        public SemaphoreSlim Waiting { get; } = new(0);

        /// <summary>From now on an open (or a listing) waits until <see cref="Release"/>.</summary>
        public void Hold() => _release.Reset();

        public void Release() => _release.Set();

        public bool HoldListing { get; set; }

        public IReadOnlyList<AudioInputInfo> Inputs()
        {
            if (HoldListing)
                Wait();
            return [new AudioInputInfo(Key, "Desk mixer", Channels, [48000], "desk") { IsDefault = true }];
        }

        public IAudioCapture Open(string key, int preferredRate, IAudioCaptureSink sink)
        {
            if (key != Key)
                throw new AudioUnavailableException($"no desk device {key}");
            Wait();
            Opens++;
            var c = new Capture(sink);
            lock (_captures)
                _captures.Add(c);
            return c;
        }

        private void Wait()
        {
            if (_release.IsSet)
                return;
            Waiting.Release();
            Assert.True(_release.Wait(TimeSpan.FromSeconds(30)), "the test never released the device");
        }

        /// <summary>One block read at <paramref name="readMonoNs"/>: <paramref name="ltc"/> on channel <paramref name="ltcChannel"/>
        /// (1-based), a 440 Hz tone on channels 1 and 2, silence elsewhere.</summary>
        public void Push(ReadOnlySpan<float> ltc, int ltcChannel, long firstSample, long readMonoNs)
        {
            var block = new float[ltc.Length * Channels];
            for (var i = 0; i < ltc.Length; i++)
            {
                var tone = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * (firstSample + i) / 48000.0));
                block[i * Channels] = tone;
                block[i * Channels + 1] = tone;
                block[i * Channels + ltcChannel - 1] = ltc[i];
            }
            Capture[] open;
            lock (_captures)
                open = [.. _captures.Where(c => !c.Disposed)];
            foreach (var c in open)
                c.Sink.Samples(block, Channels, 48000, firstSample, readMonoNs);
        }

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
            Waiting.Dispose();
        }

        private sealed class Capture(IAudioCaptureSink sink) : IAudioCapture
        {
            public IAudioCaptureSink Sink { get; } = sink;

            public bool Disposed { get; private set; }

            public int SampleRate => 48000;

            public int Channels => Mixer.Channels;

            public void Dispose() => Disposed = true;
        }
    }

    /// <summary>A device whose open waits until the test lets it (a sound server slow to open it): while it waits, the hub is held.</summary>
    private sealed class SlowDevice : IAudioInputs, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);

        public SemaphoreSlim Waiting { get; } = new(0);

        public IReadOnlyList<AudioInputInfo> Inputs() => [new AudioInputInfo("slow:device", "Slow device", 2, [48000], "slow")];

        public IAudioCapture Open(string key, int preferredRate, IAudioCaptureSink sink)
        {
            Waiting.Release();
            Assert.True(_release.Wait(TimeSpan.FromSeconds(30)), "the test never released the device");
            throw new AudioUnavailableException("the slow device gave up");
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
            Waiting.Dispose();
        }
    }

    /// <summary>The node's audio inputs as the packages see them (EngineServices.AudioInputs): the hub, with each key opened through
    /// it and the channels the open hands back recorded.</summary>
    private sealed class Seen(AudioInputHub hub) : IAudioInputs
    {
        public AudioInputHub Hub { get; } = hub;

        public ConcurrentQueue<(string Key, int Channels)> Opened { get; } = new();

        public IReadOnlyList<AudioInputInfo> Inputs() => Hub.Inputs();

        public IAudioCapture Open(string key, int preferredRate, IAudioCaptureSink sink)
        {
            var capture = Hub.Open(key, preferredRate, sink);
            Opened.Enqueue((key, capture.Channels));
            return capture;
        }
    }

    /// <summary>The audio part as the exe composes it (AudioExtension): its runtime, and the runtime's hub as the node's audio
    /// inputs (seen through <see cref="Seen"/>), with the given backends.</summary>
    private static Func<EngineServices, EngineServices> WithAudio(Action<AudioRuntime, Seen> made, bool paced, params (string Prefix, IAudioInputs Backend)[] backends) => s =>
    {
        var runtime = new AudioRuntime(s.Mono, s.Clock, paced);
        foreach (var (prefix, backend) in backends)
            runtime.AddBackend(prefix, backend);
        var seen = new Seen(runtime.Hub);
        made(runtime, seen);
        return s with { AudioRuntime = runtime, AudioInputs = seen, Modulation = runtime.RenderBus, AudioFrames = runtime.RenderBus };
    };

    [Fact]
    public async Task ThePlaylistChasesLtcThatArrivesThroughTheHubBesideTheAnalyser()
    {
        var clock = new ManualClock(5_000_000_000);
        using var mixer = new Mixer();
        AudioRuntime? audio = null;
        Seen? seen = null;
        await using var rig = await ControlRig.StartAsync(TimecodeEngineTests.Show(), service: true, clock: clock,
            with: WithAudio((r, s) => (audio, seen) = (r, s), false, ("desk:", mixer)));
        using var runtime = audio!;
        Assert.Same(runtime.Hub, seen!.Hub);
        Assert.Same(seen, rig.Engine.Services.AudioInputs);
        await rig.Ok("AUDIO LISTEN desk:mixer CHANNELS 1+2");
        Assert.Equal("listening", runtime.State.State);
        // The show names the device as people do, with the channel the code is on: the reader joins the analyser's open.
        await rig.Ok("TIMECODE SOURCE ADD FOH LTC \"Desk mixer:7\"");
        await ControlRig.Until(() => runtime.Hub.OpenDevices().GetValueOrDefault(Mixer.Key) == 2, "the LTC reader joined the device");
        Assert.Equal((1, 1), (mixer.Opens, runtime.Hub.Opens));
        // The reader opened "<the show's name>@<channel>" through the node's audio inputs, and the hub handed it that one channel.
        Assert.Equal([("Desk mixer@7", 1)], seen.Opened.ToArray());
        await rig.Ok("TIMECODE CHASE Show FOH");

        // Two seconds of code from 01:00:03:00 on channel 7, in 10 ms blocks, the node's clock moving with the audio.
        Assert.True(TcFormat.Fps25.TryParseLabel("01:00:03:00", out var from));
        var (ltc, _) = new LtcEncoder(TcFormat.Fps25, 48000).Render(from, 51);
        var start = clock.NowNs;
        for (var at = 0; at < 2 * 48000; at += 480)
        {
            clock.NowNs = start + (long)Math.Round((at + 480) * 1e9 / 48000);
            mixer.Push(ltc.AsSpan(at, 480), 7, at, clock.NowNs);
            if (at % 4800 == 0)
                await rig.Engine.StepChasesAsync();
        }
        await rig.Engine.StepChasesAsync();

        var state = rig.State().GetProperty("timecode");
        var source = state.GetProperty("sources")[0];
        Assert.Equal(("rolling", "25", "Desk mixer:7", true), (source.GetProperty("state").GetString(), source.GetProperty("detected").GetString(),
            source.GetProperty("device").GetString(), source.GetProperty("open").GetBoolean()));
        Assert.StartsWith("01:00:04:", source.GetProperty("text").GetString(), StringComparison.Ordinal);
        var chase = state.GetProperty("chases")[0];
        Assert.Equal(("locked", "it_a", "locked to FOH"), (chase.GetProperty("state").GetString(), chase.GetProperty("item").GetString(),
            chase.GetProperty("words").GetString()));
        // The lane is a function of the code: the item's media is the code minus its TC in (01:00:00:00), 5 s at the node's now.
        var lane = rig.Engine.Playback.Lane("pl_show")!;
        Assert.Equal(("it_a", TransportState.Playing), (lane.ItemId, lane.Clip.State));
        Assert.InRange(lane.Clip.Anchor.MediaAt(rig.Engine.LeaderNowNs), 5_000_000_000 - 10_000_000, 5_000_000_000 + 10_000_000);
        // The analyser heard its own channels the whole time: one device, two listeners.
        Assert.Equal(("listening", "desk:mixer", "1+2"), (runtime.State.State, runtime.State.Key, runtime.State.Channels));
        Assert.Equal(1, mixer.Opens);

        // Removed, the reader gives its listener back; the analyser keeps the device open.
        await rig.Ok("TIMECODE CHASE Show OFF");
        await rig.Ok("TIMECODE SOURCE REMOVE FOH");
        await ControlRig.Until(() => runtime.Hub.OpenDevices().GetValueOrDefault(Mixer.Key) == 1, "the LTC reader let go of the device");
        Assert.Equal(1, mixer.Opens);
    }

    [Fact]
    public async Task ASoundFileCarryingLtcIsChasedThroughTheHub()
    {
        // A stereo WAV: silence on the left, 8 s of 25 fps code from 01:00:03:00 on the right, written by control's encoder.
        Assert.True(TcFormat.Fps25.TryParseLabel("01:00:03:00", out var from));
        var (ltc, _) = new LtcEncoder(TcFormat.Fps25, 48000).Render(from, 8 * 25);
        var path = Path.Combine(Path.GetTempPath(), $"playmap-ltc-{Guid.NewGuid():N}.wav");
        WriteWav(path, new float[ltc.Length], ltc, 48000);
        try
        {
            AudioRuntime? audio = null;
            Seen? seen = null;
            await using var rig = await ControlRig.StartAsync(TimecodeEngineTests.Show(), service: true,
                with: WithAudio((r, s) => (audio, seen) = (r, s), true, (FileAudioInputs.Prefix, new FileAudioInputs())));
            using var runtime = audio!;
            await rig.Ok($"TIMECODE SOURCE ADD FOH LTC \"file:{path}:2\"");
            await rig.Ok("TIMECODE CHASE Show FOH");
            // The chase and its source are read from one STATE, and the lane at that instant: a source's state is judged when STATE
            // is read (no code for 2.5 frames is a dropout), and the file's blocks arrive on a timer a loaded machine runs late.
            JsonElement timecode = default;
            PlaylistPlayback? lane = null;
            await ControlRig.Until(() =>
            {
                timecode = rig.State().GetProperty("timecode");
                lane = rig.Engine.Playback.Lane("pl_show");
                return timecode.GetProperty("chases")[0].GetProperty("state").GetString() == "locked"
                    && timecode.GetProperty("sources")[0].GetProperty("state").GetString() == "rolling";
            }, "the chase locked to the file's code, rolling");
            Assert.Equal("25", timecode.GetProperty("sources")[0].GetProperty("detected").GetString());
            Assert.Equal("it_a", timecode.GetProperty("chases")[0].GetProperty("item").GetString());
            Assert.Equal(1, runtime.Hub.OpenDevices()["file:" + path]);
            Assert.Equal([($"file:{path}@2", 1)], seen!.Opened.ToArray());
            Assert.Equal(("it_a", TransportState.Playing), (lane!.ItemId, lane.Clip.State));
            Assert.InRange(lane.Clip.Anchor.Rate, 0.99, 1.01);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A device that takes its time to open: the control loop opens the LTC reader beside itself, so OSC is still answered
    /// meanwhile, and the reader says it listens once the device is open.</summary>
    [Fact]
    public async Task OpeningTheLtcInputNeverHoldsUpTheControlLoop()
    {
        using var mixer = new Mixer();
        AudioRuntime? audio = null;
        await using var rig = await ControlRig.StartAsync(TimecodeEngineTests.Show(), service: true,
            with: WithAudio((r, _) => audio = r, true, ("desk:", mixer)));
        using var runtime = audio!;
        mixer.Hold();
        await rig.Ok("TIMECODE SOURCE ADD FOH LTC desk:mixer:7");
        Assert.True(await mixer.Waiting.WaitAsync(TimeSpan.FromSeconds(10)), "the reader asked for the device");
        // The device is still opening: the control loop answers a ping.
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        client.Bind(new IPEndPoint(IPAddress.Parse(ControlRig.Loopback), 0));
        await client.SendToAsync(OscCodec.Write(OscMessage.Of("/playmap/ping")), SocketFlags.None, rig.Service!.OscLocal!);
        var buffer = new byte[2048];
        var got = await client.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(OscCodec.TryRead(buffer.AsSpan(0, got), out var pong, out var why), why);
        Assert.Equal("/playmap/pong", ((OscMessage)pong!).Address);
        Assert.False(rig.State().GetProperty("timecode").GetProperty("sources")[0].GetProperty("open").GetBoolean());
        mixer.Release();
        await ControlRig.Until(() => rig.State().GetProperty("timecode").GetProperty("sources")[0].GetProperty("open").GetBoolean(), "the reader open");
        Assert.Equal(1, runtime.Hub.OpenDevices()[Mixer.Key]);
    }

    /// <summary>A source moved to another channel hears nothing more of the old one, even while another device's open holds the hub
    /// (the old reader's close, off the control loop, waits behind that open); once the hub is free the old reader is given back and
    /// the new one listens.</summary>
    [Fact]
    public async Task ASourceMovedToAnotherChannelHearsNothingOfTheOldOneWhileTheHubIsHeld()
    {
        var clock = new ManualClock(5_000_000_000);
        using var mixer = new Mixer();
        using var slow = new SlowDevice();
        AudioRuntime? audio = null;
        await using var rig = await ControlRig.StartAsync(TimecodeEngineTests.Show(), service: true, clock: clock,
            with: WithAudio((r, _) => audio = r, false, ("desk:", mixer), ("slow:", slow)));
        using var runtime = audio!;
        try
        {
            var id = JsonDocument.Parse((await rig.Ok("TIMECODE SOURCE ADD FOH LTC desk:mixer:7")).Payload!).RootElement.GetProperty("id").GetString()!;
            await ControlRig.Until(() => runtime.Hub.OpenDevices().GetValueOrDefault(Mixer.Key) == 1, "FOH's reader open");
            Assert.True(TcFormat.Fps25.TryParseLabel("01:00:03:00", out var from));
            var (ltc, _) = new LtcEncoder(TcFormat.Fps25, 48000).Render(from, 25 * 10);
            var at = 0;
            async Task Push(int channel, int blocks)
            {
                for (var i = 0; i < blocks; i++, at += 480)
                {
                    clock.NowNs += 10_000_000;
                    mixer.Push(ltc.AsSpan(at, 480), channel, at, clock.NowNs);
                }
                await rig.Engine.StepChasesAsync();
            }
            JsonElement Foh() => rig.State().GetProperty("timecode").GetProperty("sources").EnumerateArray().First(s => s.GetProperty("id").GetString() == id);
            await Push(7, 100);
            Assert.Equal(("rolling", true), (Foh().GetProperty("state").GetString(), Foh().GetProperty("open").GetBoolean()));

            // Another source's device takes its time to open: the hub is held, and every open or close behind it waits.
            await rig.Ok("TIMECODE SOURCE ADD Spare LTC slow:device:1");
            Assert.True(await slow.Waiting.WaitAsync(TimeSpan.FromSeconds(10)), "the spare's reader asked for its device");
            // FOH moves to channel 8 (it starts afresh at once) while the code stays on 7: two seconds of it reach FOH not at all.
            await rig.Ok($"SET {id} channel 8");
            await rig.Engine.StepChasesAsync();
            await Push(7, 200);
            var moved = Foh();
            Assert.Equal(("desk:mixer:8", false), (moved.GetProperty("device").GetString(), moved.GetProperty("open").GetBoolean()));
            Assert.NotEqual("rolling", moved.GetProperty("state").GetString());

            // The hub free again, the old reader is given back and FOH listens on channel 8.
            slow.Release();
            await ControlRig.Until(() => Foh().GetProperty("open").GetBoolean(), "FOH's reader on channel 8 open");
            Assert.Equal(1, runtime.Hub.OpenDevices()[Mixer.Key]);
            await Push(8, 100);
            Assert.Equal(("rolling", true), (Foh().GetProperty("state").GetString(), Foh().GetProperty("open").GetBoolean()));
        }
        finally
        {
            slow.Release();
        }
    }

    /// <summary>TIMECODE DEVICES lists the hub's inputs, which waits on the backends: the lane answers other lines meanwhile.</summary>
    [Fact]
    public async Task ListingTheInputsNeverHoldsUpTheLane()
    {
        using var mixer = new Mixer();
        AudioRuntime? audio = null;
        await using var rig = await ControlRig.StartAsync(TimecodeEngineTests.Show(), with: WithAudio((r, _) => audio = r, true, ("desk:", mixer)));
        using var runtime = audio!;
        mixer.Hold();
        mixer.HoldListing = true;
        var devices = rig.Run("TIMECODE DEVICES");
        Assert.True(await mixer.Waiting.WaitAsync(TimeSpan.FromSeconds(10)), "the listing asked the mixer");
        var status = await rig.Run("TIMECODE STATUS").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(status.Ok);
        Assert.False(devices.IsCompleted);
        mixer.Release();
        var listed = JsonDocument.Parse((await devices.WaitAsync(TimeSpan.FromSeconds(10))).Payload!).RootElement;
        Assert.Contains(listed.GetProperty("audioIn").EnumerateArray(), i => i.GetProperty("key").GetString() == Mixer.Key);
    }

    private static void WriteWav(string path, float[] left, float[] right, int rate)
    {
        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        var bytes = left.Length * 4;
        w.Write("RIFF"u8);
        w.Write(36 + bytes);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)2);
        w.Write(rate);
        w.Write(rate * 4);
        w.Write((short)4);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(bytes);
        for (var i = 0; i < left.Length; i++)
        {
            w.Write(Pcm(left[i]));
            w.Write(Pcm(right[i]));
        }

        static short Pcm(float v) => (short)Math.Clamp(Math.Round(v * 32767.0), short.MinValue, short.MaxValue);
    }
}
