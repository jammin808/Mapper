using System.Collections.Immutable;
using System.Runtime.InteropServices;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Time;

namespace PlayMap.Media.Video.Spout;

/// <summary>How the Spout provider runs: the node's name (in default published names and the data map), how often discovery reads the
/// sender list (0: only on Refresh), how often a receiver steps (0: only when the test steps it), and the wall clock endpoints are
/// stamped with.</summary>
public sealed record SpoutOptions(string Node, int DiscoveryMs = 500, int ReceivePollMs = 2)
{
    public Func<long> UnixMs { get; init; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>Spout 2 on this node: videoio's live input provider (<c>spout:Name</c>, <c>spout:*</c> for the active sender), its send
/// provider (the send stage's packed frames — the same frames every sender is handed, so a Spout send follows the air as outputs show
/// it — uploaded into a shared texture) and its GPU send provider (a target the renderer draws into, on the device it is given).
/// Spout's own Direct3D 11 device is made on first use; the protocol layer (<see cref="SpoutSender"/>, <see cref="SpoutReceiver"/>)
/// runs over the kernel and GPU seams, so a fake kernel tests it anywhere. Off Windows, with no kernel given, the provider is
/// registered but unavailable: "Spout is Windows only".</summary>
public sealed class SpoutProvider : ILiveInputProvider, ISendProvider, ISendSinkProvider, IVideoRuntimeFacts
{
    public const string WindowsOnly = "Spout is Windows only";

    private readonly SpoutOptions _options;
    private readonly IMonoClock _clock;
    private readonly ISpoutKernel? _kernel;
    private readonly Func<ISpoutGpu>? _makeGpu;
    private readonly Func<IGpuShare, ISpoutGpu>? _shareGpu;
    private readonly Lock _gate = new();
    private readonly Timer? _discovery;
    private ISpoutGpu? _gpu;
    private string _gpuWords = "";
    private ImmutableArray<LiveEndpoint> _endpoints = [];
    private int _disposed;

    /// <summary>A provider over a kernel and a GPU maker (null both: unavailable, with <paramref name="words"/>).</summary>
    public SpoutProvider(SpoutOptions options, IMonoClock clock, ISpoutKernel? kernel, Func<ISpoutGpu>? gpu, string words = "",
        Func<IGpuShare, ISpoutGpu>? shareGpu = null)
    {
        _shareGpu = shareGpu;
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _options = options;
        _clock = clock;
        _kernel = kernel;
        _makeGpu = gpu;
        Words = kernel is null ? (words.Length > 0 ? words : WindowsOnly) : words;
        if (kernel is not null && options.DiscoveryMs > 0)
            _discovery = new Timer(static s => ((SpoutProvider)s!).Discover(), this, 0, options.DiscoveryMs);
    }

    /// <summary>This machine's provider: the Windows kernel and a Direct3D 11 device on the default adapter; unavailable elsewhere.</summary>
    public static SpoutProvider ForThisMachine(SpoutOptions options, IMonoClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows())
            return new SpoutProvider(options, clock, null, null);
        return Windows(options, clock, warp: false);
    }

    /// <summary>The Windows provider (WARP: the CI runner's software device).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static SpoutProvider Windows(SpoutOptions options, IMonoClock clock, bool warp) =>
        new(options, clock, new Win32SpoutKernel(), () => D3D11SpoutGpu.Create(warp), shareGpu: D3D11SpoutGpu.OnShare);

    public string Words { get; }

    public LiveProtocol Protocol => LiveProtocol.Spout;

    SendProtocol ISendSinkProvider.Protocol => SendProtocol.Spout;

    public ProviderStatus Status
    {
        get
        {
            if (_kernel is null)
                return new ProviderStatus("unavailable", "", "", Words, "https://spout.zeal.co");
            if (_kernel.MemoryShare)
                return new ProviderStatus("unavailable", "2.007", "", "this PC's Spout is set to 2.006 memory share (SpoutSettings); PlayMap shares GPU textures only", "https://spout.zeal.co");
            lock (_gate)
            {
                if (_gpuWords.Length > 0)
                    return new ProviderStatus("unavailable", "2.007", "", _gpuWords, "https://spout.zeal.co");
                return new ProviderStatus("ready", "2.007", _gpu?.Describe ?? "", "", "https://spout.zeal.co");
            }
        }
    }

    public IReadOnlyList<LiveEndpoint> Endpoints => _endpoints;

    public event Action? EndpointsChanged;

    /// <summary>Reads the sender list now (it may wait Spout's 67 ms on the list's mutex: never call it on the render thread).</summary>
    public void Refresh() => Discover();

    private void Discover()
    {
        if (_kernel is null || Volatile.Read(ref _disposed) != 0)
            return;
        var seen = SpoutDirectory.Read(_kernel);
        var now = _options.UnixMs();
        var next = seen.Where(s => s.Info is not null)
            .Select(s => new LiveEndpoint(LiveProtocol.Spout, LiveUri.Format(LiveProtocol.Spout, s.Name), s.Name, "", s.Name, "", now))
            .ToImmutableArray();
        var before = _endpoints;
        if (before.Length == next.Length && before.Select(e => e.Name).SequenceEqual(next.Select(e => e.Name), StringComparer.Ordinal))
            return;
        _endpoints = next;
        EndpointsChanged?.Invoke();
    }

    /// <summary>Spout's device, made on first use; null with the provider's words when it cannot be made.</summary>
    private ISpoutGpu? Gpu()
    {
        lock (_gate)
        {
            if (_gpu is not null || _gpuWords.Length > 0 || _makeGpu is null)
                return _gpu;
            try
            {
                _gpu = _makeGpu();
            }
            catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or COMException)
            {
                _gpuWords = "Spout has no Direct3D 11 device here: " + ex.Message;
            }
            return _gpu;
        }
    }

    private string Unavailable() => Status.Words is { Length: > 0 } w ? w : WindowsOnly;

    /// <summary>The published name of a send: "PlayMap &lt;node&gt; - &lt;name&gt;" unless the send names its own.</summary>
    public string PublishedName(string sendName, string published = "") =>
        published.Length > 0 ? published : SpoutNames.Default(_options.Node, sendName);

    public ILiveSource Open(string address, bool byUrl, LiveSettings settings, string key)
    {
        ArgumentNullException.ThrowIfNull(address);
        var source = new SpoutSource(key, settings, _clock, address);
        if (_kernel is null || Gpu() is not { } gpu)
        {
            source.Unavailable(Unavailable());
            return source;
        }
        source.Start(_kernel, gpu, _options.ReceivePollMs);
        return source;
    }

    public ISendSink Create(SendSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (_kernel is null || Gpu() is not { } gpu)
            throw new NotSupportedException(Unavailable());
        var name = PublishedName(spec.Name);
        var data = new SpoutDataWriter(_options.Node, spec.Id, "", $"{spec.FpsN}/{spec.FpsD}");
        var sender = new SpoutSender(_kernel, gpu, name, SpoutFormats.Bgra8, data);
        sender.Register(spec.Width, spec.Height);
        return new SpoutSink(spec, sender);
    }

    public IGpuSendTarget Create(VideoSend send, string publishedName, IGpuShare gpu)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(gpu);
        if (_kernel is null || Gpu() is not { } own)
            throw new NotSupportedException(Unavailable());
        // On the renderer's own Direct3D 11 device when it has one (the DXGI path), so it draws into the texture with no copy;
        // else Spout's device.
        var device = gpu.Api == GpuApi.D3D11 && gpu.D3D11Device != 0 && _shareGpu is not null ? _shareGpu(gpu) : own;
        var name = PublishedName(send.Name, publishedName);
        var data = new SpoutDataWriter(_options.Node, send.Id, send.Tap, send.Fps > 0 ? send.Fps.ToString(System.Globalization.CultureInfo.InvariantCulture) : "show");
        var sender = new SpoutSender(_kernel, device, name, SpoutFormats.Bgra8, data);
        sender.Register(Math.Max(1, send.Width > 0 ? send.Width : 1920), Math.Max(1, send.Height > 0 ? send.Height : 1080));
        return new SpoutGpuTarget(send.Id, sender, Math.Max(1, send.RateDivider), _clock, ReferenceEquals(device, own) ? null : device);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _discovery?.Dispose();
        lock (_gate)
        {
            _gpu?.Dispose();
            _gpu = null;
        }
    }
}

/// <summary>A Spout input: a receive thread steps a <see cref="SpoutReceiver"/> every few milliseconds (never blocking: a busy sender
/// is tried again at the next step), reads each copied frame back one step later without waiting for the GPU — a copy the GPU has not
/// finished is read at a later step before another copy is made — and publishes it into the frame pool as BGRA or RGBA.
/// <c>spout:*</c> follows the active sender. Spout has no tally.</summary>
public sealed class SpoutSource : LiveSource
{
    private readonly string _address;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Lock _stepGate = new();
    private SpoutReceiver? _receiver;
    private ISpoutKernel? _kernel;
    private ISpoutGpu? _gpu;
    private Thread? _thread;
    private int _pollMs;

    internal SpoutSource(string key, LiveSettings settings, IMonoClock clock, string address)
        : base(LiveProtocol.Spout, key, settings, clock)
    {
        _address = address;
    }

    /// <summary>The sender name asked ("*": the active sender).</summary>
    public string Address => _address;

    /// <summary>The receiver now (tests read its counters).</summary>
    public SpoutReceiver? Receiver => _receiver;

    internal void Unavailable(string words) => Fail(words);

    internal void Start(ISpoutKernel kernel, ISpoutGpu gpu, int pollMs)
    {
        _kernel = kernel;
        _gpu = gpu;
        _pollMs = pollMs;
        Waiting($"waiting for Spout sender '{_address}'");
        if (pollMs <= 0)
            return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "spout " + _address };
        _thread.Start();
    }

    /// <summary>How long the receive thread waits between steps while no sender of the name exists.</summary>
    public const int AbsentPollMs = 250;

    private void Loop()
    {
        var wait = _pollMs;
        while (!_stop.Wait(wait))
            wait = Poll() ? AbsentPollMs : _pollMs;
    }

    /// <summary>One step: the pending copy read back into the pool (or left for the next step), then the next frame copied; true when
    /// no sender of the name exists (the thread then backs off). The receive thread calls it; a test with no thread calls it lock-step.</summary>
    public bool Poll()
    {
        lock (_stepGate)
        {
            if (IsDisposed || _kernel is null || _gpu is null)
                return false;
            _receiver ??= new SpoutReceiver(_kernel, _gpu, Resolve());
            var r = _receiver;
            if (r.HasPending)
            {
                Publish(r);
                // The copy is still in flight: it is read back at the next step, never copied over (the newest frame is copied then).
                if (r.HasPending)
                    return false;
            }
            switch (r.Step())
            {
                case ReceiveStep.Absent:
                    Waiting(r.Words);
                    if (_address == "*")
                    {
                        // The active sender may have changed: ask again at the next step.
                        r.Dispose();
                        _receiver = null;
                    }
                    return true;
                case ReceiveStep.Failed:
                    CountDropped();
                    break;
            }
            return false;
        }
    }

    private string Resolve()
    {
        if (_address != "*")
            return _address;
        foreach (var s in SpoutDirectory.Read(_kernel!))
            if (s.Active)
                return s.Name;
        return "*";
    }

    private void Publish(SpoutReceiver r)
    {
        if (SpoutFormats.Layout(r.Format, out _) is not { } layout || !Pool.TryBeginWrite(layout, r.Width, r.Height, out var planes))
        {
            _ = r.ReadPending([], 0);
            CountDropped();
            return;
        }
        var plane = planes[0];
        StagingRead read;
        unsafe
        {
            read = r.ReadPending(new Span<byte>((void*)plane.Data, plane.Stride * plane.Height), plane.Stride);
        }
        if (read == StagingRead.Ready)
        {
            var now = Clock.NowNs;
            Pool.Publish(long.MinValue, now, ColorMatrix.Bt709, ColorRange.Full);
            Delivered(layout, r.Width, r.Height, (long)plane.Stride * plane.Height, now);
            return;
        }
        Pool.Abandon();
        if (read == StagingRead.Failed)
            CountDropped();
    }

    protected override void SendTally(bool program, bool preview)
    {
        // Spout carries no tally.
    }

    protected override void Close()
    {
        _stop.Set();
        // Joined to the end (a step never blocks; a spout:* step waits at most Spout's 67 ms on the list): nothing it uses is disposed
        // while it can still run.
        _thread?.Join();
        lock (_stepGate)
        {
            _receiver?.Dispose();
            _receiver = null;
        }
        _stop.Dispose();
    }
}

/// <summary>A Spout send fed by the send stage: each packed frame (the frame every sender of the tap is handed: blackout black, freeze
/// held, a pattern black, the master applied, a blind draft never in it) is converted to BGRA on the sender's worker and uploaded into
/// the shared texture; a receiver holding the texture past <see cref="BusyWaitMs"/> skips the frame (counted dropped). Spout shows no receivers, so the send counts
/// one connection — its shared texture — and sends whenever it is on. Stopping sends one black frame first (the base class).</summary>
internal sealed class SpoutSink : SendSink
{
    /// <summary>How long the send's worker waits for a receiver holding the texture (a receiver's copy takes microseconds).</summary>
    public const int BusyWaitMs = 20;

    private readonly SpoutSender _sender;
    private byte[] _bgra = [];

    public SpoutSink(SendSpec spec, SpoutSender sender)
        : base(spec)
    {
        _sender = sender;
        Connections = 1;
        Start();
    }

    public override string FullName => _sender.Name;

    public SpoutSender Sender => _sender;

    protected override unsafe bool Transmit(SendFrame frame)
    {
        var bytes = frame.Width * frame.Height * 4;
        if (_bgra.Length < bytes)
            _bgra = new byte[bytes];
        var source = new ReadOnlySpan<byte>((void*)frame.Data, frame.Bytes);
        // A frame that does not convert reaches no receiver: dropped with the words (the base class counts a frame whose transmit
        // throws), never counted sent, and never the shape of the stop's black frame.
        if (!SpoutPixels.ToBgra(source, frame.Stride, frame.Format, frame.Width, frame.Height, _bgra))
            throw new InvalidOperationException($"{frame.Format} frames do not convert for Spout: send BGRA8 or UYVY");
        if (_sender.Texture is { } t && (t.Width != frame.Width || t.Height != frame.Height))
            _sender.Resize(frame.Width, frame.Height);
        // The worker may wait a little for a receiver's copy (the stop's black frame among them); still held, the frame is skipped and
        // counted dropped (the base class counts a frame whose transmit throws).
        if (!_sender.Publish(_bgra.AsSpan(0, bytes), frame.Width * 4, frame.LeaderNs, BusyWaitMs))
            throw new InvalidOperationException($"a Spout receiver held {_sender.Name}'s texture past {BusyWaitMs} ms: the frame was skipped");
        Words = "";
        return false;
    }

    protected override void Refresh()
    {
        // Spout shows no receivers: the shared texture is the one connection.
        Connections = 1;
    }

    protected override void Close() => _sender.Dispose();
}

/// <summary>A Spout send the renderer draws into on the GPU, its texture on the renderer's own device: every <c>RateDivider</c>-th
/// frame, the access mutex tried at 0 ms (busy: skipped, counted), the texture handed as a <see cref="GpuSurface"/>, then counted,
/// released and announced. A frame of another size than the texture is skipped and counted (the texture is made at its send's size
/// on the control lane). Render thread.</summary>
internal sealed class SpoutGpuTarget(string key, SpoutSender sender, int divider, IMonoClock clock, ISpoutGpu? ownDevice) : IGpuSendTarget
{
    private readonly GpuSurface _surface = new() { Api = GpuApi.D3D11, Format = GpuSurfaceFormat.Bgra8, Persistent = true };
    private long _offered;
    private long _dropped;
    private long _firstNs = long.MinValue;
    private long _lastNs;

    public string Key => key;

    public SendHealth Health
    {
        get
        {
            var span = _firstNs == long.MinValue ? 0 : (_lastNs - _firstNs) / 1e9;
            var fps = span > 0 ? Math.Round((sender.Frames - 1) / span, 2) : 0;
            return new SendHealth(sender.Frames > 0 ? "sending" : "waiting", sender.Frames, _dropped, sender.Busy, fps, "", "");
        }
    }

    public bool TryBegin(int width, int height, SendFormat format, out GpuSurface target)
    {
        target = _surface;
        if (_offered++ % divider != 0)
            return false;
        if (format is not (SendFormat.Bgra8 or SendFormat.Auto) || width < 1 || height < 1)
        {
            _dropped++;
            return false;
        }
        // A new size is never made on the frame path (it waits on the info mutex and makes a texture): the frame is skipped, counted.
        if (sender.Texture is { } t && (t.Width != width || t.Height != height))
        {
            _dropped++;
            return false;
        }
        if (!sender.TryBegin(out var texture))
            return false;
        (_surface.Handle, _surface.Width, _surface.Height, _surface.CodedWidth, _surface.CodedHeight) = (texture.Native, width, height, width, height);
        return true;
    }

    public void End(bool drawn, in SendFrameInfo info)
    {
        sender.End(drawn, info.LeaderNs);
        if (!drawn)
            return;
        var now = clock.NowNs;
        if (_firstNs == long.MinValue)
            _firstNs = now;
        _lastNs = now;
    }

    public void Dispose()
    {
        sender.Dispose();
        ownDevice?.Dispose();
    }
}
