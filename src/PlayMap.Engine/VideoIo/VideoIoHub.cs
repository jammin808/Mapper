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

    /// <summary>The protocol the sender was made for (a send moved to another protocol gets a new sender).</summary>
    public SendProtocol MadeFor { get; set; }

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
    private const long ColdGraceNs = 1_000_000_000;

    private readonly EngineCore _engine;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LiveInput> _inputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SendSlot> _sends = new(StringComparer.Ordinal);
    private readonly AutoResetEvent _wake = new(false);
    private readonly Func<long> _mono;
    private readonly VideoIoSettings _running;
    private SendPlanEntry[] _plan = [];
    private Project? _reconciled;
    private VideoIoSettings _settings;
    private Thread? _thread;
    private volatile bool _stopping;
    private long _nextDiscoveryNs = long.MinValue;
    private long _signature;
    private int _inputCount;
    private int _disposed;
    private (string Interface, NicInfo Nic, long AtNs)? _nic;
    private readonly Lock _nicGate = new();

    public VideoIoHub(EngineCore engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _settings = engine.Services.Settings.VideoIo;
        _running = _settings;
        _mono = () => _engine.Services.Mono.NowNs;
        foreach (var provider in engine.Services.LiveInputs)
            provider.EndpointsChanged += Wake;
    }

    /// <summary>This node's video I/O settings as they stand (VIDEOIO SET changes them; the runtimes read some only at start).</summary>
    public VideoIoSettings Settings => Volatile.Read(ref _settings);

    /// <summary>The settings this engine started with: which runtimes were loaded and how they discover. A protocol turned on or off
    /// since applies at the next start.</summary>
    public VideoIoSettings Running => _running;

    /// <summary>Keeps changed settings on the machine (set by the exe's part; null in tests, which keep them in memory).</summary>
    public Action<VideoIoSettings>? Save { get; set; }

    /// <summary>The network interface sends go out of and its link, read at most every two seconds (an edit that checks the bandwidth
    /// guard never lists the machine's interfaces under the store's write lock more often than that).</summary>
    internal NicInfo Nic()
    {
        var now = _mono();
        var name = Settings.Interface;
        lock (_nicGate)
        {
            if (_nic is { } known && known.Interface == name && now - known.AtNs < 2_000_000_000)
                return known.Nic;
            var nic = NicInfo.Read(name);
            _nic = (name, nic, now);
            return nic;
        }
    }

    /// <summary>Whether any live input is kept (the render thread asks once a frame, without locking).</summary>
    public bool HasInputs => Volatile.Read(ref _inputCount) > 0;

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

    /// <summary>Why a protocol cannot be used on this node (off since this engine started, no provider, or its runtime is not
    /// ready), or null. <paramref name="adding"/> asks for a new input or send, which is refused too when the protocol is off from
    /// the next start: what runs keeps running until then.</summary>
    public string? Unavailable(LiveProtocol protocol, bool sending, bool adding = false)
    {
        var running = _running;
        var saved = Settings;
        if (protocol == LiveProtocol.Ndi && !running.NdiEnabled)
            return saved.NdiEnabled
                ? "NDI turns on at the next engine start (it was off when this one started)"
                : "NDI is off on this node — VIDEOIO SET NDI ON (applies at the next engine start)";
        if (protocol == LiveProtocol.Omt && !running.OmtEnabled)
            return saved.OmtEnabled
                ? "OMT turns on at the next engine start (it was off when this one started)"
                : "OMT is off on this node — VIDEOIO SET OMT ON (applies at the next engine start)";
        if (adding && OffAtNextStart(protocol) is { } off)
            return off;
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

    /// <summary>The words when a protocol runs now but is off from the next engine start (VIDEOIO SET … OFF since this one started),
    /// else null.</summary>
    public string? OffAtNextStart(LiveProtocol protocol) => protocol switch
    {
        LiveProtocol.Ndi when _running.NdiEnabled && !Settings.NdiEnabled => "NDI is off from the next engine start — VIDEOIO SET NDI ON keeps it",
        LiveProtocol.Omt when _running.OmtEnabled && !Settings.OmtEnabled => "OMT is off from the next engine start — VIDEOIO SET OMT ON keeps it",
        _ => null,
    };

    /// <summary>Whether a live input uses a protocol, for its primary or its backup.</summary>
    internal static bool Uses(LiveInput input, LiveProtocol protocol) =>
        input.Protocol == protocol || (input.Item.Live is { Backup.Length: > 0 } live && LiveUri.TryParse(live.Backup, out var p, out _, out _) && p == protocol);

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
            else if (!input.InProject && Reopens(input.Item, item))
            {
                // A camera rig's feed (not a library item, so no reconcile sees it) moved to another source: the new consumer reads
                // the new one, and the old consumer's handle reads it too until it lets go.
                CloseInput(input);
                ResetFeed(input);
                input.Item = item;
                input.Protocol = protocol;
                input.Address = address;
                input.ByUrl = byUrl;
            }
            input.AddHandle();
            OpenInput(input);
            Volatile.Write(ref _inputCount, _inputs.Count);
            return new LiveHandle(input, _mono);
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

    /// <summary>Closes an input's receivers and opens them again (INPUT RECONNECT); false when the input left the hub meanwhile.</summary>
    internal bool Reconnect(LiveInput input)
    {
        lock (_gate)
        {
            if (!_inputs.TryGetValue(input.Id, out var known) || !ReferenceEquals(known, input))
                return false;
            CloseInput(input);
            OpenInput(input);
        }
        Wake();
        return true;
    }

    /// <summary>Opens what of the input is closed: its primary, and its backup when it names one (warm beside the primary).</summary>
    private void OpenInput(LiveInput input)
    {
        var settings = input.Item.Live ?? LiveSettings.Default;
        if (input.Primary is null)
        {
            input.Primary = OpenFeed(input.Protocol, input.Address, input.ByUrl, settings, input.Id, input.Item.Name, out var problem);
            input.Problem = problem;
            input.OpenedNs = input.Primary is null ? long.MinValue : _mono();
            input.Tally = "none";
        }
        if (input.Backup is not null || settings.Backup.Length == 0)
            return;
        input.BackupUri = settings.Backup;
        if (!LiveUri.TryParse(settings.Backup, out var protocol, out var address, out var byUrl) || protocol is LiveProtocol.Ffmpeg)
        {
            input.BackupProblem = $"the backup {settings.Backup} is no NDI, OMT or Spout source";
            return;
        }
        input.Backup = OpenFeed(protocol, address, byUrl, settings with { Backup = "" }, input.Id + "/backup", input.Item.Name + "'s backup",
            out var why);
        input.BackupProblem = why;
        input.BackupOpenedNs = input.Backup is null ? long.MinValue : _mono();
    }

    /// <summary>A receiver for one feed, or null with the words why.</summary>
    private ILiveSource? OpenFeed(LiveProtocol protocol, string address, bool byUrl, LiveSettings settings, string key, string name, out string problem)
    {
        problem = "";
        var why = Unavailable(protocol, sending: false);
        if (why is not null || InputProvider(protocol) is not { } provider)
        {
            problem = why ?? NoProvider(protocol);
            return null;
        }
        try
        {
            return provider.Open(address, byUrl, settings, key);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or NotSupportedException or System.Net.Sockets.SocketException)
        {
            problem = $"{name} would not open: {ex.Message}";
            return null;
        }
    }

    /// <summary>Closes both feeds' receivers (which feed is used stays: a reconnect is not a switch).</summary>
    private static void CloseInput(LiveInput input)
    {
        CloseBackup(input);
        if (input.Primary is { } source)
        {
            input.Primary = null;
            input.OpenedNs = long.MinValue;
            source.Dispose();
        }
    }

    private static void CloseBackup(LiveInput input)
    {
        input.BackupProblem = "";
        input.BackupUri = "";
        if (input.Backup is { } backup)
        {
            input.Backup = null;
            input.BackupOpenedNs = long.MinValue;
            backup.Dispose();
        }
    }

    /// <summary>A cold input nothing shows: both receivers closed, and nothing of its last connection kept (its feed, its loss, why it
    /// would not open) — it is not received, so it is neither lost nor failed until something shows it.</summary>
    private static void CloseCold(LiveInput input)
    {
        CloseInput(input);
        ResetFeed(input);
        input.Problem = "";
        input.BackupProblem = "";
    }

    /// <summary>Back to the primary with no loss held (a new location, the backup gone).</summary>
    private static void ResetFeed(LiveInput input)
    {
        input.Use(backup: false);
        input.LossRoute = null;
        input.Loss = "";
        input.SteadySinceNs = long.MinValue;
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
            // The store's project as it stands under the lock: a tick that read an older one never undoes the lane's newer reconcile.
            if (_engine.Store.Project is { } latest && !ReferenceEquals(latest, project))
                project = latest;
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
                var reopen = Reopens(input.Item, item);
                input.Item = item;
                if (reopen)
                {
                    CloseInput(input);
                    ResetFeed(input);
                    input.Protocol = protocol;
                    input.Address = address;
                    input.ByUrl = byUrl;
                }
                else
                {
                    if (!string.Equals(before.Backup, settings.Backup, StringComparison.Ordinal))
                    {
                        // The backup changed or went: a feed on the old one returns to the primary.
                        CloseBackup(input);
                        if (input.Using == "backup")
                            ResetFeed(input);
                    }
                    if (input.Primary is PlayMap.Media.Video.LiveSource primary)
                        primary.Update(settings);
                    if (input.Backup is PlayMap.Media.Video.LiveSource backup)
                        backup.Update(settings with { Backup = "" });
                }
            }
            else
            {
                input = new LiveInput(item, protocol, address, byUrl);
                _inputs[item.Id] = input;
            }
            input.InProject = true;
            // A cold input nothing holds is closed by the sweep, after its grace.
            if (settings.Warm || input.Handles > 0)
                OpenInput(input);
        }
        foreach (var input in _inputs.Values)
            if (!seen.Contains(input.Id))
                input.InProject = false;
        SweepInputs();
    }

    /// <summary>Whether an item's change needs its receivers made again (another location, proxy or depth).</summary>
    private static bool Reopens(MediaItem before, MediaItem after)
    {
        var a = before.Live ?? LiveSettings.Default;
        var b = after.Live ?? LiveSettings.Default;
        return !string.Equals(before.Location, after.Location, StringComparison.Ordinal) || a.Proxy != b.Proxy || a.Depth != b.Depth;
    }

    /// <summary>Closes and forgets the inputs no longer in the project that no consumer holds (a camera rig's feed lives while its
    /// consumer holds it; a removed item's goes once the renderer lets go of it), and closes a cold input nothing has shown for
    /// <see cref="ColdGraceNs"/>.</summary>
    private void SweepInputs()
    {
        List<string>? gone = null;
        var now = _mono();
        foreach (var (id, input) in _inputs)
        {
            if (input.Handles > 0)
            {
                input.UnheldSinceNs = long.MinValue;
                continue;
            }
            if (input.UnheldSinceNs == long.MinValue)
                input.UnheldSinceNs = now;
            if (!input.InProject)
                (gone ??= []).Add(id);
            else if (!(input.Item.Live ?? LiveSettings.Default).Warm && now - input.UnheldSinceNs >= ColdGraceNs
                     && (input.Primary is not null || input.Backup is not null || input.Problem.Length > 0 || input.BackupProblem.Length > 0))
            {
                // A cold input is received only while something shows it (a second after the last consumer let go).
                CloseCold(input);
            }
        }
        if (gone is not null)
        {
            foreach (var id in gone)
            {
                CloseInput(_inputs[id]);
                _inputs.Remove(id);
            }
        }
        Volatile.Write(ref _inputCount, _inputs.Count);
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
                send.Quality, send.Groups, "", shape.Format is SendFormat.Uyva or SendFormat.Pa16);
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
                // A protocol whose senders draw on the GPU (Spout) registers a GPU provider and draws through its own stage, which this
                // hub does not drive: no read-back plan, and the send says why it sends nothing.
                CloseSink(slot);
                slot.Problem = GpuSendProvider(send.Protocol) is not null
                    ? $"{Name(protocol)} sends are drawn by {Name(protocol)}'s own GPU stage, which this node does not run"
                    : NoProvider(protocol);
                slot.Plan = null;
                continue;
            }
            if (slot.Sink is not null && (slot.MadeFor != send.Protocol || slot.Spec is { } old
                && (old.Name != spec.Name || old.Format != spec.Format || old.Width != spec.Width || old.Height != spec.Height)))
            {
                // The same name on the same protocol goes on at once from the new sender: its receivers get no black between.
                CloseSink(slot, black: slot.MadeFor != send.Protocol || slot.Spec?.Name != spec.Name);
            }
            if (slot.Sink is null)
            {
                try
                {
                    slot.Sink = provider.Create(spec);
                    slot.MadeFor = send.Protocol;
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

    /// <summary>Stops a send's sender: the render thread offers it nothing more, and a network sender sends one black frame before
    /// it closes (<paramref name="black"/>), as a stopped output goes dark.</summary>
    private static void CloseSink(SendSlot slot, bool black = true)
    {
        if (slot.Plan is { } entry)
            entry.Sink = null;
        if (slot.Sink is { } sink)
        {
            slot.Sink = null;
            if (sink is PlayMap.Media.Video.SendSink network)
                network.BlackOnStop = black;
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
        List<LossNews>? news = null;
        lock (_gate)
        {
            SweepInputs();
            foreach (var input in _inputs.Values)
            {
                if (Loss(input, project, now) is { } said)
                    (news ??= []).Add(said);
                if (input.Source is not { } source)
                    continue;
                // Program: drawn into the air (an output or a send) in the window and not blacked out; preview: drawn otherwise.
                var drawn = input.LastDrawnNs != long.MinValue && now - input.LastDrawnNs <= TallyWindowNs;
                var program = !black && input.LastAirNs != long.MinValue && now - input.LastAirNs <= TallyWindowNs;
                var preview = drawn && !program;
                source.SetTally(program, preview);
                // The feed not in use is on nothing (a camera left by a takeover goes dark, not red).
                (ReferenceEquals(source, input.Primary) ? input.Backup : input.Primary)?.SetTally(false, false);
                input.Tally = program ? "program" : preview ? "preview" : "none";
            }
        }
        if (news is not null)
            Tell(news);
        if (_nextDiscoveryNs == long.MinValue || now >= _nextDiscoveryNs)
        {
            _nextDiscoveryNs = now + DiscoveryEveryNs;
            Refresh();
        }
        var signature = Signature(project);
        if (Interlocked.Exchange(ref _signature, signature) != signature)
            _engine.FactsChanged();
    }

    // ---- the loss policy ---------------------------------------------------------------------------------------------------

    /// <summary>What a tick saw happen to an input's feed, said after the hub's lock is let go.</summary>
    private readonly record struct LossNews(string Kind, string Id, string Name, string Words, string Shows, string Fallback = "");

    /// <summary>One input's loss policy at a tick. The feed in use is lost when its receiver says lost or failed, when it could not
    /// open, or when it has given no frame <see cref="LiveSettings.LostMs"/> after it opened. On a loss the other feed takes over when
    /// it delivers (the backup, or the primary while the backup is in use: a switch only ever leaves a lost feed, so it never flaps);
    /// else the input holds its last picture, shows black or shows its fallback, once per loss. A held picture is left at the first
    /// steady frame; black and a fallback after <see cref="LiveSettings.RecoverMs"/> of steady frames.</summary>
    private LossNews? Loss(LiveInput input, Project project, long now)
    {
        if (input.Primary is null && input.Backup is null && input.Problem.Length == 0)
            return null;
        var settings = input.Item.Live ?? LiveSettings.Default;
        var onBackup = input.Using == "backup";
        if (!Lost(onBackup ? input.Backup : input.Primary, onBackup ? input.BackupProblem : input.Problem,
                onBackup ? input.BackupOpenedNs : input.OpenedNs, settings, now, out var words))
        {
            if (input.Loss.Length == 0)
                return null;
            if ((onBackup ? input.Backup : input.Primary)?.Live.State is not LiveState.Ok)
            {
                input.SteadySinceNs = long.MinValue;
                return null;
            }
            if (input.SteadySinceNs == long.MinValue)
                input.SteadySinceNs = now;
            var steadyNs = now - input.SteadySinceNs;
            if (input.Loss != "hold" && steadyNs < settings.RecoverMs * 1_000_000L)
                return null;
            var was = input.Loss;
            input.LossRoute = null;
            input.Loss = "";
            input.SteadySinceNs = long.MinValue;
            return new LossNews("input.back", input.Id, input.Item.Name, was == "hold" ? "frames again"
                : string.Create(CultureInfo.InvariantCulture, $"steady for {steadyNs / 1e9:0.0} s"), onBackup ? "backup" : "primary");
        }
        input.SteadySinceNs = long.MinValue;
        var other = onBackup ? input.Primary : input.Backup;
        if (other is not null && other.Live.State is LiveState.Ok)
        {
            input.Use(!onBackup);
            input.LossRoute = null;
            input.Loss = "";
            return new LossNews("input.switched", input.Id, input.Item.Name, words, onBackup ? "primary" : "backup");
        }
        // The route is worked out again only when the policy or the project moved (a long outage costs nothing per tick).
        SourceRef? route;
        if (input.Loss.Length > 0 && ReferenceEquals(input.LossFor, settings) && ReferenceEquals(input.LossIn, project))
        {
            route = input.LossRoute;
        }
        else
        {
            route = settings.OnLoss switch
            {
                LiveLoss.Black => SourceRef.None,
                // A fallback that went from the project, or came to show a live input, is black: never a dangling or live picture.
                LiveLoss.Fallback when SourceRef.TryParse(settings.Fallback, out var fallback) && Fallbacks.Fits(project, fallback) is null => fallback,
                LiveLoss.Fallback => SourceRef.None,
                _ => null,
            };
            input.LossFor = settings;
            input.LossIn = project;
        }
        var shows = route is null ? "hold" : route.IsNone ? "black" : "fallback";
        if (input.Loss == shows && Equals(input.LossRoute, route))
            return null;
        var first = input.Loss.Length == 0;
        input.LossRoute = route;
        input.Loss = shows;
        return first ? new LossNews("input.lost", input.Id, input.Item.Name, words, shows, route is { IsNone: false } ? Describe(project, route) : "") : null;
    }

    /// <summary>Whether a feed is lost, with its words.</summary>
    private static bool Lost(ILiveSource? feed, string problem, long openedNs, LiveSettings settings, long now, out string words)
    {
        words = problem;
        if (feed is null)
            return problem.Length > 0;
        var live = feed.Live;
        words = live.Words;
        if (live.State is LiveState.Lost or LiveState.Failed or LiveState.Absent)
            return true;
        if (live.State is not LiveState.Connecting || openedNs == long.MinValue || now - openedNs <= settings.LostMs * 1_000_000L)
            return false;
        words = string.Create(CultureInfo.InvariantCulture, $"no frame in the {(now - openedNs) / 1e9:0.0} s since it opened");
        return true;
    }

    /// <summary>A fallback in words: the name of what it shows.</summary>
    private static string Describe(Project project, SourceRef source) => source.Kind switch
    {
        SourceKind.Media => project.FindMedia(source.Id)?.Name ?? source.ToString(),
        SourceKind.Generator => project.FindGenerator(source.Id)?.Name ?? source.ToString(),
        SourceKind.Canvas => project.FindCanvas(source.Id)?.Name ?? source.ToString(),
        SourceKind.Playlist => project.FindPlaylist(source.Id)?.Name ?? source.ToString(),
        SourceKind.Color => "colour " + source.Id,
        _ => source.ToString(),
    };

    /// <summary>The journal and the show events for what the tick saw (off the hub's lock).</summary>
    private void Tell(List<LossNews> news)
    {
        foreach (var n in news)
        {
            var input = InputOf(n.Id);
            var text = n.Kind switch
            {
                "input.lost" => $"{n.Name} lost ({n.Words}) — " + n.Shows switch
                {
                    "hold" => "holding its last picture",
                    "black" => "showing black",
                    _ => "showing its fallback " + n.Fallback,
                },
                "input.switched" => n.Shows == "backup"
                    ? $"{n.Name} lost ({n.Words}) — showing its backup {input?.BackupUri}"
                    : $"{n.Name}'s backup lost ({n.Words}) — showing its primary {input?.Item.Location}",
                _ => $"{n.Name} is back ({n.Words}) — showing its {n.Shows}",
            };
            _engine.Journal.Add("videoio", text, ok: n.Kind == "input.back");
            _engine.RaiseEvent(n.Kind, _engine.LeaderNowNs, w =>
            {
                w.WriteString("input", n.Id);
                w.WriteString("name", n.Name);
                w.WriteString("words", n.Words);
                w.WriteString("shows", n.Shows);
                if (n.Fallback.Length > 0)
                    w.WriteString("fallback", n.Fallback);
            });
        }
    }

    /// <summary>INPUT SWITCH: makes consumers read the primary or the backup from their next acquire; a loss held on the feed left
    /// is let go (the feed switched to is the person's word). Null when done, else why not (the input left, or the feed it checked
    /// went meanwhile).</summary>
    internal string? Switch(LiveInput input, bool backup)
    {
        lock (_gate)
        {
            if (!_inputs.TryGetValue(input.Id, out var known) || !ReferenceEquals(known, input))
                return $"{input.Item.Name} left the show meanwhile — nothing to switch";
            if ((backup ? input.Backup : input.Primary) is null)
                return $"{input.Item.Name}'s {(backup ? "backup" : "primary")} went meanwhile — nothing to switch to";
            input.Use(backup);
            input.LossRoute = null;
            input.Loss = "";
            input.SteadySinceNs = long.MinValue;
        }
        Wake();
        return null;
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
                h.Add(input.Loss);
                h.Add(input.BackupProblem);
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
                h.Add(slot.Plan?.Refused);
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
            Volatile.Write(ref _inputCount, 0);
            foreach (var slot in _sends.Values)
                CloseSink(slot);
            _sends.Clear();
            Volatile.Write(ref _plan, []);
        }
        _wake.Dispose();
    }
}

/// <summary>What a live input may fall back to: a picture this node always has — a library item that is not a stream, a generator,
/// a canvas, a playlist, a colour, black — and never one that shows a live input itself, however deep (a playlist's items, a
/// canvas's layers).</summary>
internal static class Fallbacks
{
    private const int MaxDepth = 8;

    /// <summary>Why a source cannot be a fallback in this project, or null.</summary>
    public static string? Fits(Project project, SourceRef source)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);
        if (source.IsNone || source.Kind is SourceKind.Color)
            return null;
        if (!Exists(project, source))
            return $"no {source.Kind.ToString().ToLowerInvariant()} {source.Id} in the project";
        return ShowsLive(project, source, 0, []) switch
        {
            null => null,
            Deep => $"{source} nests more than {MaxDepth} deep: a fallback is a picture this node always has, and this one cannot be shown to be",
            var live => $"{source} shows the live input {live}: a fallback is a picture this node always has",
        };
    }

    /// <summary>Whether a source names something in the project (a colour, black and the timer always do; a live ref names a stream
    /// item or a camera rig).</summary>
    public static bool Exists(Project project, SourceRef source) => source.Kind switch
    {
        SourceKind.Media => project.FindMedia(source.Id) is not null,
        SourceKind.Generator => project.FindGenerator(source.Id) is not null,
        SourceKind.Canvas => project.FindCanvas(source.Id) is not null,
        SourceKind.Playlist => project.FindPlaylist(source.Id) is not null,
        SourceKind.Live => project.FindMedia(source.Id) is not null || project.Stage.Cameras.Find(source.Id) is not null,
        _ => true,
    };

    private const string Deep = "\0deep";

    /// <summary>The first live input a source shows, as its ref; <see cref="Deep"/> when it nests past <see cref="MaxDepth"/> (fails
    /// closed: what cannot be searched is not taken as clear); null when it shows none. A node already searched (a loop) is clear.</summary>
    private static string? ShowsLive(Project project, SourceRef source, int depth, HashSet<string> seen)
    {
        if (source.Kind is SourceKind.Live || source.Kind is SourceKind.Media && project.FindMedia(source.Id) is { Kind: MediaKind.Stream })
            return source.ToString();
        if (source.Kind is not (SourceKind.Playlist or SourceKind.Canvas))
            return null;
        if (depth >= MaxDepth)
            return Deep;
        if (!seen.Add(source.ToString()))
            return null;
        switch (source.Kind)
        {
            case SourceKind.Playlist when project.FindPlaylist(source.Id) is { } list:
                foreach (var item in list.Items)
                    if (ShowsLive(project, item.Source, depth + 1, seen) is { } found)
                        return found;
                return null;
            case SourceKind.Canvas when project.FindCanvas(source.Id) is { } canvas:
                foreach (var layer in canvas.Layers)
                    if (ShowsLive(project, layer.Source, depth + 1, seen) is { } found)
                        return found;
                return null;
            default:
                return null;
        }
    }
}

/// <summary>What a send's tap resolves to on this project: the point it reads, its size fitted to its format, its rate as a
/// rational, and the format the render thread packs.</summary>
internal sealed record SendShape(SendTapKind Kind, string Target, SourceRef? Source, int Width, int Height, int FpsN, int FpsD, SendFormat Format)
{
    /// <summary>The tallest (and widest) packed target a send makes; a larger send is refused with words here rather than failing on
    /// the render thread when the target is made.</summary>
    public const int MaxTargetRows = 16384;

    private static int MaxRows(SendFormat format) => format switch
    {
        SendFormat.Uyva => MaxTargetRows * 2 / 3,
        SendFormat.P216 => MaxTargetRows / 2,
        SendFormat.Pa16 => MaxTargetRows / 3,
        _ => MaxTargetRows,
    };

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
                if (source is { } tapped && !Fallbacks.Exists(project, tapped))
                {
                    words = $"{send.Name} taps {tapped}, which is not in the project";
                    return null;
                }
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
            SendFormat.P216 => SendFormat.P216,
            SendFormat.Pa16 => SendFormat.Pa16,
            SendFormat.Bgra8 => SendFormat.Bgra8,
            _ => SendFormat.Auto,
        };
        if (format == SendFormat.Auto)
        {
            words = $"{send.Name}: {Word(send.Format)} sends are next round on this node — UYVY, UYVA, P216, PA16 or BGRA8";
            return null;
        }
        var multiple = format switch { SendFormat.Uyva => 4, SendFormat.Uyvy or SendFormat.P216 or SendFormat.Pa16 => 2, _ => 1 };
        var fitted = w / multiple * multiple;
        if (fitted < multiple || h < 1)
        {
            words = string.Create(CultureInfo.InvariantCulture, $"{send.Name}: {w}×{h} is too small to send");
            return null;
        }
        // The packed target stacks a layout's planes in rows (UYVA's alpha below its picture, P216's and PA16's planes one under
        // another); it is a texture, so it holds at most MaxTargetRows rows.
        var rows = format switch { SendFormat.Uyva => h + (h + 1) / 2, SendFormat.P216 => 2 * h, SendFormat.Pa16 => 3 * h, _ => h };
        if (rows > MaxTargetRows || fitted > MaxTargetRows)
        {
            words = string.Create(CultureInfo.InvariantCulture,
                $"{send.Name}: {Word(format)} at {fitted}×{h} packs into {rows} rows — at most {MaxTargetRows} (a {Word(format)} send is at most {MaxRows(format)} rows tall)");
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
