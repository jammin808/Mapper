using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Core.Bus;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Render.Engine;
using PlayMap.Render.Passes;

namespace PlayMap.Engine.VideoIo;

/// <summary>A send as the hub keeps it: the project's record, the render thread's entry, and the sender once made.</summary>
internal sealed class SendSlot
{
    public required VideoSend Send { get; set; }

    public SendPlanEntry? Plan { get; set; }

    public ISendSink? Sink { get; set; }

    public SendSpec? Spec { get; set; }

    /// <summary>Why it sends nothing (no runtime for its protocol, a refused sender, a tap with nothing behind it), or "".</summary>
    public string Problem { get; set; } = "";

    /// <summary>The size or rate change the hub made to fit the format ("1918 → 1916 …"), or "".</summary>
    public string Fitted { get; set; } = "";
}

/// <summary>The node's network video: every live input of the project received once (a warm receiver per input, lent to the renderer,
/// scan cameras and thumbnails through non-owning handles), every enabled send's sender, and the plan the render thread packs and
/// reads back from. The providers are the services' lists (NDI and OMT here; Spout's registered after, by its own package), each
/// looked up by protocol, so a protocol with no provider refuses with words and the node runs on.
///
/// <para><see cref="Reconcile"/> brings receivers and senders to what the project names (warm inputs open when they enter the
/// project, cold ones at their first consumer; an input that leaves is closed; a send's sender is made, changed in place or made
/// again, and closed when the send is disabled or removed) and publishes the send plan whole. <see cref="Tick"/> (10 times a
/// second on its own thread once <see cref="Start"/>ed, or by a test) reconciles when the project moved, tells each sender whether
/// its input is on air, refreshes discovery each second, and tells the engine when what STATE shows has moved. Receivers and
/// senders are made off the render thread; the render thread reads only the published plan and the handles.</para></summary>
public sealed class VideoIoHub : IDisposable
{
    private const long TallyWindowNs = 250_000_000;
    private const long DiscoveryEveryNs = 1_000_000_000;

    private readonly EngineCore _engine;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LiveInput> _inputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SendSlot> _sends = new(StringComparer.Ordinal);
    private readonly AutoResetEvent _wake = new(false);
    private readonly Func<long> _mono;
    private readonly Func<bool> _blackout;
    private SendPlanEntry[] _plan = [];
    private Project? _reconciled;
    private VideoIoSettings _settings;
    private Thread? _thread;
    private volatile bool _stopping;
    private long _nextDiscoveryNs = long.MinValue;
    private long _signature;
    private int _disposed;

    public VideoIoHub(EngineCore engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _settings = engine.Services.Settings.VideoIo;
        _mono = () => _engine.Services.Mono.NowNs;
        _blackout = () => _engine.Snapshot.Air.EffectiveBlackout;
        foreach (var provider in engine.Services.LiveInputs)
            provider.EndpointsChanged += Wake;
    }

    /// <summary>This node's video I/O settings as they stand (VIDEOIO SET changes them; the runtimes read some only at start).</summary>
    public VideoIoSettings Settings => Volatile.Read(ref _settings);

    /// <summary>Keeps changed settings on the machine (set by the exe's part; null in tests, which keep them in memory).</summary>
    public Action<VideoIoSettings>? Save { get; set; }

    /// <summary>The send plan the render thread runs (published whole).</summary>
    public ImmutableArray<SendPlanEntry> Plan => System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(Volatile.Read(ref _plan));

    public ILiveInputProvider? InputProvider(LiveProtocol protocol)
    {
        foreach (var p in _engine.Services.LiveInputs)
            if (p.Protocol == protocol)
                return p;
        return null;
    }

    public ISendProvider? SendProvider(LiveProtocol protocol)
    {
        foreach (var p in _engine.Services.SendProviders)
            if (p.Protocol == protocol)
                return p;
        return null;
    }

    public ISendSinkProvider? GpuSendProvider(SendProtocol protocol)
    {
        foreach (var p in _engine.Services.GpuSendProviders)
            if (p.Protocol == protocol)
                return p;
        return null;
    }

    /// <summary>The live protocol of a send's (NDI, OMT, Spout, Syphon).</summary>
    public static LiveProtocol ProtocolOf(SendProtocol protocol) => protocol switch
    {
        SendProtocol.Ndi => LiveProtocol.Ndi,
        SendProtocol.Omt => LiveProtocol.Omt,
        SendProtocol.Spout => LiveProtocol.Spout,
        _ => LiveProtocol.Syphon,
    };

    /// <summary>Why a protocol cannot be used on this node (no provider, or its runtime is not ready), or null.</summary>
    public string? Unavailable(LiveProtocol protocol, bool sending)
    {
        var settings = Settings;
        if (protocol == LiveProtocol.Ndi && !settings.NdiEnabled)
            return "NDI is off on this node — VIDEOIO SET NDI ON (applies at the next engine start)";
        if (protocol == LiveProtocol.Omt && !settings.OmtEnabled)
            return "OMT is off on this node — VIDEOIO SET OMT ON (applies at the next engine start)";
        ProviderStatus? status = sending
            ? SendProvider(protocol)?.Status
            : InputProvider(protocol)?.Status;
        if (status is null && protocol is LiveProtocol.Spout && sending)
            status = GpuSendProvider(SendProtocol.Spout)?.Status;
        if (status is null)
            return NoProvider(protocol);
        if (status.State != "ready")
            return status.Words.Length > 0 ? status.Words : $"{Name(protocol)} is {status.State} on this node";
        return null;
    }

    /// <summary>The words for a protocol nobody provides here.</summary>
    public static string NoProvider(LiveProtocol protocol) => protocol switch
    {
        LiveProtocol.Spout when !OperatingSystem.IsWindows() => "Spout is Windows only",
        LiveProtocol.Syphon when !OperatingSystem.IsMacOS() => "Syphon is macOS only",
        LiveProtocol.Ndi => "NDI is not available on this node: no NDI runtime was loaded — install the NDI 6 runtime (https://ndi.video) and set PLAYMAP_NDI_DIR to its folder",
        _ => $"{Name(protocol)} is not available on this node",
    };

    /// <summary>What STATE says of a protocol no provider registered, in the same words on every machine (a refusal names this
    /// machine's own reason, <see cref="NoProvider"/>).</summary>
    public static string Unregistered(LiveProtocol protocol) => protocol switch
    {
        LiveProtocol.Spout => "no Spout provider on this node (Spout is Windows only)",
        LiveProtocol.Syphon => "no Syphon provider on this node (Syphon is macOS only)",
        _ => NoProvider(protocol),
    };

    public static string Name(LiveProtocol protocol) => protocol switch
    {
        LiveProtocol.Ndi => "NDI",
        LiveProtocol.Omt => "OMT",
        LiveProtocol.Spout => "Spout",
        LiveProtocol.Syphon => "Syphon",
        LiveProtocol.DeckLink => "DeckLink",
        _ => "FFmpeg",
    };

    // ---- inputs ------------------------------------------------------------------------------------------------------------

    /// <summary>A consumer's handle on a live input of the library (NDI, OMT, Spout, …), or null for a location FFmpeg reads (an RTSP
    /// URL, a capture device). A cold input starts receiving here. Any thread; never blocks on the network.</summary>
    public IVideoSource? Open(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!LiveUri.TryParse(item.Location, out var protocol, out var address, out var byUrl) || protocol is LiveProtocol.Ffmpeg)
            return null;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return null;
            if (!_inputs.TryGetValue(item.Id, out var input))
            {
                input = new LiveInput(item, protocol, address, byUrl);
                _inputs[item.Id] = input;
            }
            input.AddHandle();
            if (input.Source is null)
                OpenInput(input);
            return new LiveHandle(input, _mono, _blackout);
        }
    }

    /// <summary>The live inputs of the project as they stand, in library order.</summary>
    internal IReadOnlyList<LiveInput> InputsOf(Project project)
    {
        var list = new List<LiveInput>();
        lock (_gate)
        {
            foreach (var m in project.Media)
                if (_inputs.TryGetValue(m.Id, out var input))
                    list.Add(input);
        }
        return list;
    }

    internal LiveInput? InputOf(string id)
    {
        lock (_gate)
            return _inputs.GetValueOrDefault(id);
    }

    /// <summary>Closes an input's receiver and opens it again (INPUT RECONNECT).</summary>
    internal void Reconnect(LiveInput input)
    {
        lock (_gate)
        {
            CloseInput(input);
            OpenInput(input);
        }
        Wake();
    }

    private void OpenInput(LiveInput input)
    {
        var why = Unavailable(input.Protocol, sending: false);
        if (why is not null || InputProvider(input.Protocol) is not { } provider)
        {
            input.Problem = why ?? NoProvider(input.Protocol);
            return;
        }
        try
        {
            input.Source = provider.Open(input.Address, input.ByUrl, input.Item.Live ?? LiveSettings.Default, input.Id);
            input.Problem = "";
            input.Tally = "none";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or NotSupportedException or System.Net.Sockets.SocketException)
        {
            input.Problem = $"{input.Item.Name} would not open: {ex.Message}";
        }
    }

    private static void CloseInput(LiveInput input)
    {
        if (input.Source is { } source)
        {
            input.Source = null;
            source.Dispose();
        }
    }

    // ---- reconcile ---------------------------------------------------------------------------------------------------------

    /// <summary>Brings receivers and senders to what the project names and publishes the send plan. Any thread (the lane after an
    /// edit, the hub's own tick); senders are made here, never on the render thread.</summary>
    public void Reconcile(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            _reconciled = project;
            ReconcileInputs(project);
            ReconcileSends(project);
        }
        Wake();
    }

    private void ReconcileInputs(Project project)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in project.Media)
        {
            if (item.Kind != MediaKind.Stream || !LiveUri.TryParse(item.Location, out var protocol, out var address, out var byUrl)
                || protocol is LiveProtocol.Ffmpeg)
                continue;
            seen.Add(item.Id);
            var settings = item.Live ?? LiveSettings.Default;
            if (_inputs.TryGetValue(item.Id, out var input))
            {
                var before = input.Item.Live ?? LiveSettings.Default;
                var reopen = !string.Equals(input.Item.Location, item.Location, StringComparison.Ordinal)
                    || before.Proxy != settings.Proxy || before.Depth != settings.Depth;
                input.Item = item;
                if (reopen)
                {
                    CloseInput(input);
                    input.Protocol = protocol;
                    input.Address = address;
                    input.ByUrl = byUrl;
                }
                else if (input.Source is PlayMap.Media.Video.LiveSource source)
                {
                    source.Update(settings);
                }
            }
            else
            {
                input = new LiveInput(item, protocol, address, byUrl);
                _inputs[item.Id] = input;
            }
            if (input.Source is null && (settings.Warm || input.Handles > 0))
                OpenInput(input);
            else if (input.Source is not null && !settings.Warm && input.Handles <= 0)
                CloseInput(input);
        }
        foreach (var (id, input) in _inputs.ToList())
        {
            if (seen.Contains(id))
                continue;
            CloseInput(input);
            _inputs.Remove(id);
        }
    }

    private void ReconcileSends(Project project)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var plan = new List<SendPlanEntry>();
        foreach (var send in project.Sends)
        {
            seen.Add(send.Id);
            if (!_sends.TryGetValue(send.Id, out var slot))
            {
                slot = new SendSlot { Send = send };
                _sends[send.Id] = slot;
            }
            slot.Send = send;
            if (!send.Enabled)
            {
                CloseSink(slot);
                slot.Problem = "";
                slot.Plan = null;
                continue;
            }
            if (SendShape.Resolve(project, send, out var fitted) is not { } shape)
            {
                CloseSink(slot);
                slot.Problem = fitted;
                slot.Plan = null;
                continue;
            }
            slot.Fitted = fitted;
            var spec = new SendSpec(send.Id, send.Name, shape.Width, shape.Height, shape.FpsN, shape.FpsD, shape.Format,
                send.Quality, send.Groups, "", shape.Format == SendFormat.Uyva);
            var protocol = ProtocolOf(send.Protocol);
            if (Unavailable(protocol, sending: true) is { } why)
            {
                CloseSink(slot);
                slot.Problem = why;
                slot.Plan = null;
                continue;
            }
            if (SendProvider(protocol) is not { } provider)
            {
                // A protocol whose senders draw on the GPU (Spout) registers its own provider and stage; none here.
                CloseSink(slot);
                slot.Problem = NoProvider(protocol);
                slot.Plan = null;
                continue;
            }
            if (slot.Sink is not null && slot.Spec is { } old
                && (old.Name != spec.Name || old.Format != spec.Format || old.Width != spec.Width || old.Height != spec.Height))
                CloseSink(slot);
            if (slot.Sink is null)
            {
                try
                {
                    slot.Sink = provider.Create(spec);
                    slot.Problem = "";
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or NotSupportedException or System.Net.Sockets.SocketException)
                {
                    slot.Problem = $"{send.Name} could not start: {ex.Message}";
                    slot.Plan = null;
                    continue;
                }
            }
            else if (slot.Spec != spec)
            {
                slot.Sink.Update(spec);
            }
            slot.Spec = spec;
            if (slot.Plan is not { } entry || !SameShape(entry, send, shape))
            {
                entry = new SendPlanEntry
                {
                    Id = send.Id,
                    Name = send.Name,
                    Kind = shape.Kind,
                    Target = shape.Target,
                    Source = shape.Source,
                    Width = shape.Width,
                    Height = shape.Height,
                    Format = shape.Format,
                    FpsN = shape.FpsN,
                    FpsD = shape.FpsD,
                    Fit = send.Fit,
                    OnlyWhenWatched = send.OnlyWhenWatched,
                    FollowAir = send.FollowAir,
                };
                slot.Plan = entry;
            }
            entry.Sink = slot.Sink;
            plan.Add(entry);
        }
        foreach (var (id, slot) in _sends.ToList())
        {
            if (seen.Contains(id))
                continue;
            CloseSink(slot);
            _sends.Remove(id);
        }
        Volatile.Write(ref _plan, plan.ToArray());
    }

    private static bool SameShape(SendPlanEntry e, VideoSend send, SendShape shape) =>
        e.Kind == shape.Kind && e.Target == shape.Target && Equals(e.Source, shape.Source) && e.Width == shape.Width && e.Height == shape.Height
        && e.Format == shape.Format && e.FpsN == shape.FpsN && e.FpsD == shape.FpsD && e.Fit == send.Fit
        && e.OnlyWhenWatched == send.OnlyWhenWatched && e.FollowAir == send.FollowAir && e.Name == send.Name;

    private static void CloseSink(SendSlot slot)
    {
        if (slot.Plan is { } entry)
            entry.Sink = null;
        if (slot.Sink is { } sink)
        {
            slot.Sink = null;
            sink.Dispose();
        }
        slot.Spec = null;
    }

    internal IReadOnlyList<SendSlot> SendsOf(Project project)
    {
        var list = new List<SendSlot>();
        lock (_gate)
        {
            foreach (var s in project.Sends)
                if (_sends.TryGetValue(s.Id, out var slot))
                    list.Add(slot);
        }
        return list;
    }

    internal SendSlot? SendOf(string id)
    {
        lock (_gate)
            return _sends.GetValueOrDefault(id);
    }

    // ---- discovery ---------------------------------------------------------------------------------------------------------

    /// <summary>Every source the providers see now, each marked with the library item that names it.</summary>
    public IReadOnlyList<LiveEndpoint> Discovered(Project project, LiveProtocol? only = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var list = new List<LiveEndpoint>();
        foreach (var provider in _engine.Services.LiveInputs)
        {
            if (only is { } o && provider.Protocol != o)
                continue;
            foreach (var e in provider.Endpoints)
            {
                var added = "";
                foreach (var m in project.Media)
                {
                    if (m.Kind == MediaKind.Stream && Names(m.Location, e))
                    {
                        added = m.Id;
                        break;
                    }
                }
                list.Add(e with { InLibrary = added });
            }
        }
        list.Sort(static (a, b) => string.CompareOrdinal(a.Uri, b.Uri));
        return list;
    }

    private static bool Names(string location, LiveEndpoint e)
    {
        if (!LiveUri.TryParse(location, out var protocol, out var address, out var byUrl) || protocol != e.Protocol)
            return false;
        return byUrl ? string.Equals(address, e.Address, StringComparison.OrdinalIgnoreCase) : LiveUri.Matches(address, e.FullName);
    }

    /// <summary>Asks the providers to look again (INPUT DISCOVER).</summary>
    public void Refresh(LiveProtocol? only = null)
    {
        foreach (var provider in _engine.Services.LiveInputs)
            if (only is null || provider.Protocol == only)
                provider.Refresh();
    }

    // ---- settings ----------------------------------------------------------------------------------------------------------

    /// <summary>Changes the settings (VIDEOIO SET) and keeps them on the machine.</summary>
    internal void SetSettings(VideoIoSettings settings)
    {
        Volatile.Write(ref _settings, settings);
        Save?.Invoke(settings);
        Wake();
    }

    // ---- the tick ----------------------------------------------------------------------------------------------------------

    /// <summary>Starts ticking 10 times a second on a thread of its own. Once.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null || Volatile.Read(ref _disposed) != 0)
                return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "videoio" };
            _thread.Start();
        }
    }

    internal void Wake()
    {
        if (Volatile.Read(ref _disposed) == 0)
            _wake.Set();
    }

    private void Loop()
    {
        while (!_stopping)
        {
            _wake.WaitOne(100);
            if (_stopping)
                return;
            try
            {
                Tick();
            }
            // The hub's own thread: a fault is journalled and the next tick runs; the node plays on.
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or ObjectDisposedException)
            {
                _engine.Journal.Add("fault", $"video I/O: {ex.GetType().Name}: {ex.Message}", ok: false);
            }
        }
    }

    /// <summary>One tick: reconcile when the project moved, tally to every sender, discovery each second, and the engine told when
    /// what STATE shows moved.</summary>
    public void Tick()
    {
        var project = _engine.Store.Project;
        if (!ReferenceEquals(project, _reconciled))
            Reconcile(project);
        var now = _mono();
        var black = _engine.Snapshot.Air.EffectiveBlackout;
        lock (_gate)
        {
            foreach (var input in _inputs.Values)
            {
                if (input.Source is not { } source)
                    continue;
                var drawn = input.LastDrawnNs != long.MinValue && now - input.LastDrawnNs <= TallyWindowNs;
                var program = drawn && !black && !input.DrawnInBlack;
                var preview = drawn && !program;
                source.SetTally(program, preview);
                input.Tally = program ? "program" : preview ? "preview" : "none";
            }
        }
        if (_nextDiscoveryNs == long.MinValue || now >= _nextDiscoveryNs)
        {
            _nextDiscoveryNs = now + DiscoveryEveryNs;
            Refresh();
        }
        var signature = Signature(project);
        if (Interlocked.Exchange(ref _signature, signature) != signature)
            _engine.FactsChanged();
    }

    /// <summary>A number that moves when a word STATE shows moves (states, receivers, tally, problems, discovered count) — not the
    /// counters, which ride the next push.</summary>
    private long Signature(Project project)
    {
        var h = new HashCode();
        lock (_gate)
        {
            foreach (var input in _inputs.Values)
            {
                h.Add(input.Id);
                h.Add(input.Problem);
                h.Add(input.Tally);
                h.Add(input.Using);
                if (input.Source is { } s)
                {
                    var live = s.Live;
                    h.Add(live.State);
                    h.Add(live.Width);
                    h.Add(live.Height);
                }
            }
            foreach (var slot in _sends.Values)
            {
                h.Add(slot.Send.Id);
                h.Add(slot.Problem);
                if (slot.Sink is { } sink)
                {
                    h.Add(sink.Connections);
                    h.Add(sink.Tally);
                    h.Add(sink.Words);
                    h.Add(sink.FullName);
                }
            }
        }
        h.Add(Discovered(project).Count);
        return h.ToHashCode();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stopping = true;
        _wake.Set();
        _thread?.Join();
        foreach (var provider in _engine.Services.LiveInputs)
            provider.EndpointsChanged -= Wake;
        lock (_gate)
        {
            foreach (var input in _inputs.Values)
                CloseInput(input);
            _inputs.Clear();
            foreach (var slot in _sends.Values)
                CloseSink(slot);
            _sends.Clear();
            Volatile.Write(ref _plan, []);
        }
        _wake.Dispose();
    }
}

/// <summary>What a send's tap resolves to on this project: the point it reads, its size fitted to its format, its rate as a
/// rational, and the format the render thread packs.</summary>
internal sealed record SendShape(SendTapKind Kind, string Target, SourceRef? Source, int Width, int Height, int FpsN, int FpsD, SendFormat Format)
{
    /// <summary>The shape, or null with the words why (a tap that names nothing); <paramref name="words"/> says a size the format
    /// changed ("1918 → 1916 …") when it resolves.</summary>
    public static SendShape? Resolve(Project project, VideoSend send, out string words)
    {
        words = "";
        if (!TryTap(send.Tap, out var kind, out var target, out var source, out var tapWhy))
        {
            words = tapWhy;
            return null;
        }
        int w, h;
        double fps;
        switch (kind)
        {
            case SendTapKind.Output:
                if (project.Outputs.Find(target) is not { } output)
                {
                    words = $"{send.Name} taps output {target}, which is not in the project";
                    return null;
                }
                (w, h, fps) = (output.Width, output.Height, output.RefreshHz);
                break;
            case SendTapKind.Canvas:
                if (project.FindCanvas(target) is not { } canvas)
                {
                    words = $"{send.Name} taps canvas {target}, which is not in the project";
                    return null;
                }
                (w, h, fps) = (canvas.Width, canvas.Height, 0);
                break;
            default:
                (w, h, fps) = (1920, 1080, 0);
                break;
        }
        if (send.Width > 0 && send.Height > 0)
            (w, h) = (send.Width, send.Height);
        if (send.Fps > 0)
            fps = send.Fps;
        if (fps <= 0)
            fps = RenderEngine.FrameRateOf(project.Settings.FrameRate);
        var format = send.Format switch
        {
            SendFormat.Auto or SendFormat.Uyvy => SendFormat.Uyvy,
            SendFormat.Uyva => SendFormat.Uyva,
            SendFormat.Bgra8 => SendFormat.Bgra8,
            _ => SendFormat.Auto,
        };
        if (format == SendFormat.Auto)
        {
            words = $"{send.Name}: {Word(send.Format)} sends are next round on this node — UYVY, UYVA or BGRA8";
            return null;
        }
        var multiple = format == SendFormat.Uyva ? 4 : format == SendFormat.Uyvy ? 2 : 1;
        var fitted = w / multiple * multiple;
        if (fitted < multiple || h < 1)
        {
            words = string.Create(CultureInfo.InvariantCulture, $"{send.Name}: {w}×{h} is too small to send");
            return null;
        }
        if (fitted != w)
            words = string.Create(CultureInfo.InvariantCulture, $"{w} → {fitted} ({Word(format)} needs a multiple of {multiple})");
        var (n, d) = Rational(fps);
        return new SendShape(kind, target, source, fitted, h, n, d, format);
    }

    /// <summary>"output:&lt;id&gt;", "canvas:&lt;id&gt;" or "source:&lt;sourceref&gt;".</summary>
    public static bool TryTap(string tap, out SendTapKind kind, out string target, out SourceRef? source, out string words)
    {
        kind = SendTapKind.Output;
        target = "";
        source = null;
        words = "";
        var colon = tap.IndexOf(':', StringComparison.Ordinal);
        var head = colon > 0 ? tap[..colon].ToLowerInvariant() : "";
        var rest = colon > 0 ? tap[(colon + 1)..].Trim() : "";
        switch (head)
        {
            case "output" when rest.Length > 0:
                target = rest;
                return true;
            case "canvas" when rest.Length > 0:
                kind = SendTapKind.Canvas;
                target = rest;
                return true;
            case "source" when SourceRef.TryParse(rest, out var parsed) && !parsed.IsNone:
                kind = SendTapKind.Source;
                source = parsed;
                return true;
            case "prewarp":
                words = "prewarp taps are next round on this node — tap output:<id>, canvas:<id> or source:<ref>";
                return false;
            default:
                words = $"a tap is output:<id>, canvas:<id> or source:<ref>, not \"{tap}\"";
                return false;
        }
    }

    /// <summary>A rate as a rational: the NTSC rates exactly, any other to a thousandth.</summary>
    public static (int N, int D) Rational(double fps) => Math.Round(fps, 3) switch
    {
        23.976 or 23.98 => (24000, 1001),
        29.97 => (30000, 1001),
        59.94 => (60000, 1001),
        119.88 => (120000, 1001),
        var f when Math.Abs(f - Math.Round(f)) < 1e-9 => ((int)Math.Round(f), 1),
        var f => ((int)Math.Round(f * 1000), 1000),
    };

    public static string Word(SendFormat format) => format switch
    {
        SendFormat.Uyvy => "UYVY",
        SendFormat.Uyva => "UYVA",
        SendFormat.P216 => "P216",
        SendFormat.Pa16 => "PA16",
        SendFormat.Bgra8 => "BGRA8",
        SendFormat.Rgba8 => "RGBA8",
        SendFormat.Rgb10A2 => "RGB10A2",
        SendFormat.Rgba16F => "RGBA16F",
        _ => "auto",
    };
}
