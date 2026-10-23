using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PlayMap.Core.Audio;
using PlayMap.Core.Bus;
using PlayMap.Core.Control.Midi;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Control;
using PlayMap.Net.Control;

namespace PlayMap.Engine.Tests.Control;

/// <summary>An engine with the control side attached as the host attaches it: its runtime always, and — when asked — the control
/// service with its OSC socket on this slot's loopback address (port 0), a MIDI backend (the fake by default), the Art-Net socket on
/// a loopback address of the slot's own, and audio inputs; on a manual clock or the real one; with any other services a test adds
/// (another package's, such as the audio hub).</summary>
internal sealed class ControlRig : IAsyncDisposable
{
    public static readonly SessionInfo Desk = SessionInfo.Desk("t");

    private ControlRig(EngineCore engine, ManualClock? clock, ControlService? service, ArtNetEndpoint? artNet, IMidiBackend? midi)
    {
        Engine = engine;
        Clock = clock;
        Service = service;
        ArtNet = artNet;
        Midi = midi;
    }

    public EngineCore Engine { get; }

    public ManualClock? Clock { get; }

    public ControlService? Service { get; }

    public ArtNetEndpoint? ArtNet { get; }

    public IMidiBackend? Midi { get; }

    /// <summary>This slot's loopback address (PLAYMAP_TEST_LOOPBACK), or 127.0.0.1.</summary>
    public static string Loopback => Environment.GetEnvironmentVariable("PLAYMAP_TEST_LOOPBACK") is { Length: > 0 } a ? a : "127.0.0.1";

    /// <summary>Another address of the slot's /24 (Art-Net binds its spec port, so each test takes an address of its own).</summary>
    public static string Address(int last)
    {
        var parts = Loopback.Split('.');
        return $"{parts[0]}.{parts[1]}.{parts[2]}.{last.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    public static async Task<ControlRig> StartAsync(Project? project = null, bool service = false, IMidiBackend? midi = null, IAudioInputs? audio = null,
        string? artNetAddress = null, ManualClock? clock = null, Func<ControlSettings, ControlSettings>? settings = null, Func<NodeSettings, NodeSettings>? node = null,
        Func<EngineServices, EngineServices>? with = null)
    {
        var services = EngineServices.ForTests(clock ?? (IMonoClock)MonoClock.Instance);
        var control = settings?.Invoke(ControlSettings.Default) ?? ControlSettings.Default;
        services = services with { Settings = services.Settings with { NodeId = Guid.NewGuid().ToString("N")[..8], Control = control } };
        if (node is not null)
            services = services with { Settings = node(services.Settings) };
        ArtNetEndpoint? art = artNetAddress is null ? null : new ArtNetEndpoint(artNetAddress);
        ControlService? svc = null;
        if (service)
        {
            midi ??= new PlayMap.Devices.Midi.FakeMidiBackend();
            svc = new ControlService(new ControlServiceOptions { Midi = midi, ArtNet = art, Audio = audio, OscBind = IPAddress.Parse(Loopback), OscPort = 0 });
            services = services with { Control = svc, ArtNet = art };
        }
        if (with is not null)
            services = with(services);
        var engine = new EngineCore(services, project);
        if (svc is not null)
            await svc.AttachAsync(engine);
        return new ControlRig(engine, clock, svc, art, midi);
    }

    public async Task<CommandReply> Run(string line, SessionInfo? session = null)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await Engine.HandleAsync(parsed, session ?? Desk, CancellationToken.None);
    }

    public async Task<CommandReply> Ok(string line, SessionInfo? session = null)
    {
        var r = await Run(line, session);
        Assert.True(r.Ok, $"{line}: {r.Payload}");
        return r;
    }

    public async Task<string> Refused(string line, SessionInfo? session = null)
    {
        var r = await Run(line, session);
        Assert.False(r.Ok, $"{line} was taken: {r.Effect}");
        return r.Payload ?? "";
    }

    public JsonElement State() => JsonDocument.Parse(Engine.State().Json).RootElement.Clone();

    /// <summary>Waits until a condition on the engine holds (real-time work: sockets, the control loop), or fails after 10 s.</summary>
    public static Task Until(Func<bool> condition, string what) => Until(condition, what, null);

    /// <summary>Waits until a condition on the engine holds, or fails after 10 s with what <paramref name="saw"/> reads then.</summary>
    public static async Task Until(Func<bool> condition, string what, Func<string>? saw)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("timed out waiting for " + what + (saw is null ? "" : ": " + saw()));
            await Task.Delay(10);
        }
    }

    /// <summary>A UDP socket of the test's own on an address and port.</summary>
    public static Socket Udp(string address, int port)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        s.Bind(new IPEndPoint(IPAddress.Parse(address), port));
        s.ReceiveTimeout = 5000;
        return s;
    }

    public async ValueTask DisposeAsync()
    {
        if (Service is not null)
            await Service.DisposeAsync();
        if (ArtNet is not null)
            await ArtNet.DisposeAsync();
        await Engine.DisposeAsync();
        Midi?.Dispose();
    }
}

/// <summary>Audio inputs for a test: one synthetic input whose blocks the test pushes, stamped as it says.</summary>
internal sealed class FakeAudioInputs : IAudioInputs
{
    public IAudioCaptureSink? Sink { get; private set; }

    public string? Refuse { get; set; }

    public IReadOnlyList<AudioInputInfo> Inputs() => [new AudioInputInfo("synthetic", "Synthetic LTC", 2, [48000], "test") { Kind = AudioInputKind.Synthetic, IsDefault = true }];

    public IAudioCapture Open(string key, int preferredRate, IAudioCaptureSink sink)
    {
        if (Refuse is { } why)
            throw new AudioUnavailableException(why);
        Sink = sink;
        return new Capture();
    }

    /// <summary>A mono block as channel 1 of a stereo input (channel 2 silent), its last sample read at <paramref name="readMonoNs"/>.</summary>
    public void Push(ReadOnlySpan<float> mono, long firstSampleIndex, long readMonoNs)
    {
        var interleaved = new float[mono.Length * 2];
        for (var i = 0; i < mono.Length; i++)
            interleaved[2 * i] = mono[i];
        Sink?.Samples(interleaved, 2, 48000, firstSampleIndex, readMonoNs);
    }

    private sealed class Capture : IAudioCapture
    {
        public int SampleRate => 48000;

        public int Channels => 2;

        public void Dispose()
        {
            // The test's blocks simply stop.
        }
    }
}
