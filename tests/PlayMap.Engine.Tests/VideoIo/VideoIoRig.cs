using System.Runtime.InteropServices;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Media.Video;

namespace PlayMap.Engine.Tests.VideoIo;

/// <summary>A protocol the test drives: its endpoints are set by hand, each receiver it opens is a <see cref="FakeLiveSource"/> the
/// test pushes frames into on the rig's clock, and each sender a <see cref="FakeSink"/> whose receivers and tally the test sets.</summary>
internal sealed class FakeVideoProvider(LiveProtocol protocol, IMonoClock clock) : ILiveInputProvider, ISendProvider, IVideoRuntimeFacts
{
    public LiveProtocol Protocol => protocol;

    public ProviderStatus Status { get; set; } = new("ready", "fake 1.0", "/fake/" + protocol.ToString().ToLowerInvariant(), "", "");

    public List<LiveEndpoint> Found { get; } = [];

    public IReadOnlyList<LiveEndpoint> Endpoints => [.. Found];

    public event Action? EndpointsChanged;

    public int Refreshes { get; private set; }

    public List<FakeLiveSource> Opened { get; } = [];

    public List<FakeSink> Sinks { get; } = [];

    public string Vmx => protocol == LiveProtocol.Omt ? "avx2" : "";

    public string Mdns => protocol == LiveProtocol.Omt ? "none" : "";

    public void Refresh() => Refreshes++;

    public void Announce(LiveEndpoint endpoint)
    {
        Found.Add(endpoint);
        EndpointsChanged?.Invoke();
    }

    public ILiveSource Open(string address, bool byUrl, LiveSettings settings, string key)
    {
        var s = new FakeLiveSource(protocol, key, settings, clock, address);
        Opened.Add(s);
        return s;
    }

    public ISendSink Create(SendSpec spec)
    {
        var sink = new FakeSink(spec, protocol);
        Sinks.Add(sink);
        return sink;
    }

    public void Dispose()
    {
        foreach (var s in Opened)
            s.Dispose();
        foreach (var s in Sinks)
            s.Dispose();
    }
}

/// <summary>A receiver whose frames the test pushes: each a flat UYVY picture of one luma code, arriving at the clock's now.</summary>
internal sealed class FakeLiveSource(LiveProtocol protocol, string key, LiveSettings settings, IMonoClock clock, string address)
    : LiveSource(protocol, key, settings, clock)
{
    public string Address => address;

    public List<(bool Program, bool Preview)> TallySent { get; } = [];

    public bool Closed { get; private set; }

    public unsafe void Push(int width = 64, int height = 36, byte luma = 128, PixelLayout layout = PixelLayout.Uyvy)
    {
        if (!Pool.TryBeginWrite(layout, width, height, out var planes))
            return;
        foreach (var plane in planes)
            new Span<byte>((void*)plane.Data, plane.Stride * plane.Height).Fill(luma);
        Pool.Publish(Clock.NowNs / 100 * 100, Clock.NowNs);
        Delivered(layout, width, height, LivePlanes.FrameBytes(layout, width, height), Clock.NowNs);
    }

    public void Says(string words) => Fail(words);

    protected override void SendTally(bool program, bool preview) => TallySent.Add((program, preview));

    protected override void Close() => Closed = true;
}

/// <summary>A sender that keeps a copy of the first bytes of each frame it is offered; the test sets its receivers and tally.</summary>
internal sealed class FakeSink : SendSink
{
    private readonly LiveProtocol _protocol;

    public FakeSink(SendSpec spec, LiveProtocol protocol) : base(spec)
    {
        _protocol = protocol;
        Start();
    }

    public override string FullName => $"FAKEHOST ({Spec.Name})";

    public List<byte[]> Frames { get; } = [];

    public int Closes { get; private set; }

    public void Watch(int receivers, bool program = false, bool preview = false)
    {
        Connections = receivers;
        Tally = (program, preview);
    }

    protected override bool Transmit(SendFrame frame)
    {
        var copy = new byte[frame.Bytes];
        Marshal.Copy(frame.Data, copy, 0, copy.Length);
        lock (Frames)
            Frames.Add(copy);
        return _protocol == LiveProtocol.Ndi;
    }

    protected override void Refresh()
    {
        // The test sets the receivers and the tally by hand.
    }

    protected override void Close() => Closes++;
}

/// <summary>An engine on a hand-moved clock with a fake NDI and a fake OMT, the hub reconciled and ticked by hand.</summary>
internal sealed class VideoIoRig : IAsyncDisposable
{
    public static readonly SessionInfo Desk = new("t1", "desk", "127.0.0.1", true, "PlayMapDesk", true);
    public const long StartNs = 5_000_000_000;

    public readonly ManualClock Clock = new(StartNs);
    public readonly EngineCore Engine;
    public readonly FakeVideoProvider Ndi;
    public readonly FakeVideoProvider Omt;

    public VideoIoRig(Project? project = null, bool ndi = true, VideoIoSettings? settings = null)
    {
        Ndi = new FakeVideoProvider(LiveProtocol.Ndi, Clock);
        Omt = new FakeVideoProvider(LiveProtocol.Omt, Clock);
        var services = EngineServices.ForTests(Clock);
        services = services with
        {
            Settings = services.Settings with { VideoIo = settings ?? VideoIoSettings.Default },
            LiveInputs = ndi ? [Omt, Ndi] : [Omt],
            SendProviders = ndi ? [Omt, Ndi] : [Omt],
        };
        Engine = new EngineCore(services, project ?? Show());
    }

    /// <summary>A show with one 1280×720 output (op_1) on a canvas (cv_main) and a surface on the stage.</summary>
    public static Project Show()
    {
        var canvas = new Canvas("cv_main", "Main", 1920, 1080, "#000000", []);
        var output = new Output("op_1", "Left", OutputKind.Virtual, "", 1280, 720, 60, OutputMode.Canvas, null, SourceRef.Canvas("cv_main"),
            new RectD(0, 0, 1920, 1080), OutputWarp.None, BlendSettings.Default, ColorTrim.Neutral, [], new RectD(0, 0, 1280, 720), 0, true);
        return Project.CreateDefault("Video show") with { Canvases = [canvas], Outputs = [output] };
    }

    public async Task<CommandReply> Run(string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await Engine.HandleAsync(parsed, Desk, CancellationToken.None);
    }

    public async Task<CommandReply> Ok(string line)
    {
        var r = await Run(line);
        Assert.True(r.Ok, $"{line} → {r.Payload}");
        return r;
    }

    public async Task<string> Refused(string line)
    {
        var r = await Run(line);
        Assert.False(r.Ok, $"{line} was taken: {r.Effect}");
        return r.Payload!;
    }

    public void AdvanceMs(long ms) => Clock.NowNs += ms * 1_000_000;

    /// <summary>STATE.videoio now: the hub ticked (tally, reconcile) and the engine told the facts moved, so the counters are fresh.</summary>
    public JsonElement State()
    {
        Engine.VideoIo.Tick();
        Engine.FactsChanged();
        return JsonDocument.Parse(Engine.State().Json).RootElement.GetProperty("videoio").Clone();
    }

    public JsonElement Input(string id) => State().GetProperty("inputs").EnumerateArray().Single(i => i.GetProperty("id").GetString() == id);

    public JsonElement Send(string id) => State().GetProperty("sends").EnumerateArray().Single(i => i.GetProperty("id").GetString() == id);

    public static string IdOf(CommandReply r) => JsonDocument.Parse(r.Payload!).RootElement.GetProperty("id").GetString()!;

    public async ValueTask DisposeAsync()
    {
        Engine.VideoIo.Dispose();
        await Engine.DisposeAsync();
        Ndi.Dispose();
        Omt.Dispose();
    }
}
