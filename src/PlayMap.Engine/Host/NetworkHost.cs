using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Net;
using PlayMap.Net.Bus;
using PlayMap.Net.Http;
using PlayMap.Net.Links;
using PlayMap.Net.Media;
using PlayMap.Net.Sync;
using PlayMap.Net.Tls;
using PlayMap.Net.Wire;

namespace PlayMap.Engine.Host;

/// <summary>What the network host is made from.</summary>
public sealed record NetworkHostOptions
{
    public required NodeHome Home { get; init; }

    /// <summary>The settings as the node's file holds them: what a LINK verb changes and writes back.</summary>
    public required NodeSettings Stored { get; init; }

    /// <summary>The settings this run uses: the stored ones with the command line's name and ports.</summary>
    public required NodeSettings Running { get; init; }

    public required IMonoClock Mono { get; init; }

    public required ShowWallClock Wall { get; init; }

    public required ILoggerFactory Logs { get; init; }

    public required MediaBackend Media { get; init; }

    public required ApprovalHook Approvals { get; init; }

    /// <summary>The node's certificate (HTTPS on the web port, the hello's fingerprint); null serves plain only.</summary>
    public TlsIdentity? Tls { get; init; }

    /// <summary>The certificates of peers this node has met (Stagewatch over TLS).</summary>
    public PinStore Pins { get; init; } = new();

    /// <summary>The hello's ports and pace; null = the contract's (UDP 9928, every 2 s).</summary>
    public HelloOptions? Hello { get; init; }

    /// <summary>A follower's exchange with its leader; the contract's is every 250 ms.</summary>
    public TimeSpan SyncEvery { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Media pulls while a show plays (bytes a second); unlimited while none does.</summary>
    public long PullRateDuringShow { get; init; } = 20L * 1024 * 1024;
}

/// <summary>The network edge as the engine sees it, over PlayMap.Net: the line wire and the web port (HTTP, HTTPS,
/// WebSocket, MCP, media) with the engine as their endpoint; the hello and the peers it hears; the Patterns and
/// Stagewatch links; the fine clock sync (answering when this node leads, following when it does not); media pulls from
/// peers. It is made before the engine (its clock is the engine's leader clock, its views and controls are engine
/// services) and opened once the engine exists. A door that cannot open stays closed and says so in STATE's faults;
/// the node stays up on the others, and only a node with neither the wire nor the web port refuses to start.
/// <para>The engine calls the controls on its control lane: each one leaves the lane at once and does its work
/// beside it, and one link or sync change runs at a time.</para></summary>
public sealed class NetworkHost : INetworkView, INetworkControl, INodeFaults, IMediaTransfers, INodePairing, IAiDoor, IAsyncDisposable
{
    private static readonly TimeSpan WatchEvery = TimeSpan.FromMilliseconds(500);

    private readonly NetworkHostOptions _o;
    private readonly ILogger _log;
    private readonly ClockSyncClient _sync;
    private readonly ConcurrentDictionary<string, NodeFault> _faults = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MediaTransferView> _transfers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly Lock _settingsGate = new();
    private readonly Lock _backgroundGate = new();
    private readonly List<Task> _background = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly HttpClient _http;
    private readonly MediaTransfer _transfer;
    private volatile NodeSettings _stored;
    private volatile NodeSettings _running;
    private volatile EngineCore? _engine;
    private WireServer? _wire;
    private HttpHost? _web;
    private volatile HelloService? _hello;
    private volatile ClockSyncResponder? _responder;
#pragma warning disable CA2213 // Closed by StopPatternsAsync and StopStagewatchAsync, which a link change and DisposeAsync both call.
    private volatile PatternsLink? _patterns;
    private volatile StagewatchLink? _stagewatch;
#pragma warning restore CA2213
    private volatile string _stagewatchView = "";
    private volatile string _role = "solo";
    private volatile string? _leader;
    private bool _patternsBlackedUs;
    private SyncQuality _lastQuality = SyncQuality.None;
    private int _lastClients;
    private (LinkView?, LinkView?, string?) _lastLinks;
    private int _disposed;

    public NetworkHost(NetworkHostOptions options)
    {
        _o = options ?? throw new ArgumentNullException(nameof(options));
        _stored = options.Stored;
        _running = options.Running;
        _log = options.Logs.CreateLogger("PlayMap.Engine.Network");
        _sync = new ClockSyncClient(options.Mono, options.SyncEvery, options.Logs.CreateLogger("PlayMap.Net.Sync"));
        _http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = TimeSpan.FromSeconds(60) };
        _transfer = new MediaTransfer(_http, new TokenBucket(0, options.Mono), log: options.Logs.CreateLogger("PlayMap.Net.Media"));
        _transfer.Progress += OnPullProgress;
    }

    /// <summary>The leader's clock as this node reads it — the engine's leader clock. It is the sync client's own: the
    /// client publishes each exchange's estimate into it, so a follower's offset reaches every reader of leader time
    /// with no copy in between; a leading or solo node reads its own monotonic clock through it.</summary>
    public NetworkClock Clock => _sync.Clock;

    /// <summary>The settings this run uses (the command line's name and ports over the stored ones).</summary>
    public NodeSettings Settings => _running;

    /// <summary>The ports the doors listen on now (0 when a door is closed).</summary>
    public int WirePort => _wire?.Port ?? 0;

    public int HttpPort => _web?.Port ?? 0;

    public int SyncPort => _responder?.Port ?? 0;

    public int HelloPort => _hello?.ListenPort ?? 0;

    /// <summary>The hello, once started: its peer table, and its targets (which a test points at another node).</summary>
    public HelloService? Hello => _hello;

    /// <summary>The Stagewatch view this node shows as its timer (the operator's last redirect; "" = the default).</summary>
    public string StagewatchView => _stagewatchView;

    /// <summary>Opens the doors with the engine as their endpoint, then the hello and the links the settings name. A
    /// door that is refused stays closed and becomes a fault; with neither the wire nor the web port open there is no
    /// node to reach, and this throws <see cref="BindRefusedException"/> with both reasons.</summary>
    public async Task StartAsync(EngineCore engine, CancellationToken ct)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        var logs = _o.Logs;
        var wire = new WireServer(_running, engine, _o.Wall, new WireServerOptions(), logs.CreateLogger("PlayMap.Net.Wire"));
        try
        {
            wire.Start();
            _wire = wire;
        }
        catch (BindRefusedException ex)
        {
            await wire.DisposeAsync();
            Fault("wire", "the line wire stays closed: " + ex.Message);
        }

        var web = new HttpHost(_running, engine, _o.Wall, new HttpHostOptions
        {
            Tls = _o.Tls,
            Media = _o.Media,
            Approvals = _o.Approvals.Policy,
            LoggerFactory = logs,
            Version = StateWriter.Version,
        });
        try
        {
            await web.StartAsync(ct);
            _web = web;
        }
        catch (BindRefusedException ex)
        {
            await web.DisposeAsync();
            Fault("web", "the web port stays closed: " + ex.Message);
        }
        if (_wire is null && _web is null)
            throw new BindRefusedException("neither the line wire nor the web port could listen, so nothing can reach this node — "
                + string.Join("; ", _faults.Values.Select(f => f.Words)));

        var hello = new HelloService(_running, SayHello, _o.Mono, _o.Hello, logs.CreateLogger("PlayMap.Net.Bus"));
        try
        {
            hello.Peers.Changed += OnPeer;
            hello.Start();
            _hello = hello;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or BindRefusedException)
        {
            await hello.DisposeAsync();
            Fault("hello", "the Show Bus hello is off — other nodes will not find this one and it will not hear them: " + ex.Message);
        }

        Background(WatchAsync(_stop.Token));
        var s = _running;
        if (s.PatternsHost.Length > 0)
        {
            try
            {
                StartPatterns(s.PatternsHost, s.PatternsToken);
            }
            catch (ArgumentException ex)
            {
                Fault("patterns", ex.Message);
            }
        }
        if (s.StagewatchEnabled)
            StartStagewatchFromSettings();
        Facts();
    }

    // ---- What STATE reads (any thread). ----

    public IReadOnlyList<PeerView> Peers()
    {
        if (_hello is not { } hello)
            return [];
        var patterns = _patterns;
        var patternsContext = patterns?.ContextJson is { } json ? ShowContext.Parse(json) : null;
        return hello.Peers.Snapshot().Select(p =>
        {
            var live = p.Liveness == PeerLiveness.Live;
            if (patternsContext is not null && p.Kind == ShowBusConstants.KindPatterns && p.Id == patternsContext.NodeId)
                return new PeerView(p.Kind, p.Id, p.Name, ShowContext.LightWord(patternsContext.Light), patternsContext.Headline, live);
            var words = p.Liveness switch
            {
                PeerLiveness.Live => $"heard at {p.Address}",
                PeerLiveness.Stale => $"quiet at {p.Address}",
                _ => $"gone from {p.Address}",
            };
            return new PeerView(p.Kind, p.Id, p.Name, live ? "grey" : "amber", words, live);
        }).ToList();
    }

    public LinkView? Patterns()
    {
        if (_patterns is not { } link)
            return null;
        string detail;
        if (link.Pairing == PairState.Refused)
            detail = "wrong token — LINK PATTERNS with the desk's pairing token";
        else if (link.Video is { } v)
            detail = v.RemainingMs is { } left ? $"{Label(v)} {ClipTransport.Format((long)(left * 1e6))}" : Label(v);
        else
            detail = link.Status == LinkStatus.Live ? "no clip on air" : "";
        return new LinkView(StatusWord(link.Status), link.Address, link.Paired, detail);

        static string Label(PatternsVideo v) => v.Tag.Length > 0 ? v.Tag : v.File;
    }

    public LinkView? Stagewatch()
    {
        if (_stagewatch is not { } link)
            return null;
        var view = _stagewatchView;
        var detail = link.Words + (view.Length > 0 ? (link.Words.Length > 0 ? " · " : "") + "showing " + view : "");
        return new LinkView(StatusWord(link.Status), link.Address, link.Status == LinkStatus.Live, detail);
    }

    public string? StagewatchTimerJson()
    {
        if (_stagewatch is not { } link)
            return null;
        var reading = link.Reading;
        return EngineCore.JsonOut(w =>
        {
            w.WriteStartObject();
            var t = reading.Timer;
            w.WriteString("state", reading.Trusted ? t?.State ?? "idle" : "unknown");
            w.WriteString("text", reading.Text);
            if (reading.Trusted && t is not null)
                w.WriteNumber("seconds", Math.Round(t.Seconds, 1));
            else
                w.WriteNull("seconds");
            w.WriteString("phase", t?.Phase ?? "");
            w.WriteString("color", t?.Color ?? "");
            w.WriteString("title", t?.Title ?? "");
            w.WriteString("message", t?.Message ?? "");
            w.WriteBoolean("trusted", reading.Trusted);
            w.WriteString("why", reading.Why);
            w.WriteEndObject();
        });
    }

    /// <summary>The clock sync. A follower's offset is the uncertainty of its leader time (half the best exchange's
    /// round trip): the raw difference between two machines' monotonic clocks says nothing about the show.</summary>
    public SyncView Sync()
    {
        switch (_role)
        {
            case "follower":
                var s = _sync.Snapshot;
                return new SyncView("follower", _leader, s.RoundTripMs / 2, s.RoundTripMs, s.DriftPpm);
            case "leader":
                return new SyncView("leader", _running.NodeId, 0, 0, 0);
            default:
                return new SyncView("solo", null, 0, 0, 0);
        }
    }

    public IReadOnlyList<NodeFault> Faults() => _faults.Values.OrderBy(f => f.Part, StringComparer.Ordinal).ToList();

    public IReadOnlyList<MediaTransferView> Transfers() => _transfers.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();

    // ---- What the engine's verbs ask (called on the control lane; each leaves it at once). ----

    public async Task<string> LinkPatternsAsync(string? host, string? token, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.Yield();   // off the control lane: a link change dials and writes a file
        await _changes.WaitAsync(_stop.Token);
        try
        {
            if (host is null)
            {
                if (_patterns is null)
                    throw new InvalidOperationException("this node follows no Patterns desk");
                await StopPatternsAsync();
                Persist(s => s with { PatternsHost = "", PatternsToken = "" });
                ClearFault("patterns");
                return "stopped following Patterns";
            }
            var address = PatternsLinkOptions.ParseAddress(host)
                ?? throw new ArgumentException($"'{host}' is not an address — LINK PATTERNS <host>[:<port>] [<token>]");
            var keep = token ?? (string.Equals(host, _stored.PatternsHost, StringComparison.OrdinalIgnoreCase) ? _stored.PatternsToken : "");
            await StopPatternsAsync();
            StartPatterns(host, keep);
            Persist(s => s with { PatternsHost = host, PatternsToken = keep });
            ClearFault("patterns");
            return string.Create(CultureInfo.InvariantCulture, $"following Patterns at {address.Host}:{address.Port}{(keep.Length > 0 ? ", pairing by proof" : "")}");
        }
        finally
        {
            _changes.Release();
        }
    }

    public async Task<string> LinkStagewatchAsync(string? host, string? key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.Yield();   // off the control lane: a link change dials and writes a file
        await _changes.WaitAsync(_stop.Token);
        try
        {
            if (host is null)
            {
                if (_stagewatch is null && !_stored.StagewatchEnabled)
                    throw new InvalidOperationException("this node follows no Stagewatch");
                await StopStagewatchAsync();
                Persist(s => s with { StagewatchEnabled = false, StagewatchHost = "", StagewatchKey = "" });
                return "stopped following Stagewatch";
            }
            var keep = key ?? (string.Equals(host, _stored.StagewatchHost, StringComparison.OrdinalIgnoreCase) ? _stored.StagewatchKey : "");
            if (host.Trim().Length == 0)
            {
                // No address: the first Stagewatch heard on the show network, now or when one is.
                await StopStagewatchAsync();
                Persist(s => s with { StagewatchEnabled = true, StagewatchHost = "", StagewatchKey = keep });
                StartStagewatchFromSettings();
                return _stagewatch is { } found
                    ? $"following the Stagewatch heard at {found.Address} as a screen"
                    : "following the first Stagewatch heard — none is heard yet";
            }
            var address = PatternsLinkOptions.ParseAddress(host, ShowBusConstants.StagewatchHttpPort)
                ?? throw new ArgumentException($"'{host}' is not an address — LINK STAGEWATCH <host>[:<port>] [<key>]");
            await StopStagewatchAsync();
            StartStagewatch(StagewatchOptions(address.Host, address.Port, keep));
            Persist(s => s with { StagewatchEnabled = true, StagewatchHost = host, StagewatchKey = keep });
            return string.Create(CultureInfo.InvariantCulture, $"following Stagewatch at {address.Host}:{address.Port} as a screen");
        }
        finally
        {
            _changes.Release();
        }
    }

    public async Task<string> LeadAsync(string group, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.Yield();   // off the control lane: binding a port and stopping an exchange take their time
        await _changes.WaitAsync(_stop.Token);
        try
        {
            if (_role == "leader" && _responder is { } leading)
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"already leading — {group} takes this node's clock on UDP {leading.Port}"));
            await _sync.StopAsync();
            if (_responder is null)
            {
                var responder = new ClockSyncResponder(_running, _sync.Clock, _running.SyncPort, _o.Logs.CreateLogger("PlayMap.Net.Sync"));
                try
                {
                    responder.Start();
                }
                catch (BindRefusedException ex)
                {
                    await responder.DisposeAsync();
                    _role = "solo";
                    _leader = null;
                    Fault("sync", "this node cannot lead: " + ex.Message);
                    throw new InvalidOperationException("this node cannot lead: " + ex.Message, ex);
                }
                _responder = responder;
            }
            ClearFault("sync");
            _role = "leader";
            _leader = _running.NodeId;
            _lastQuality = SyncQuality.None;
            Facts();
            return string.Create(CultureInfo.InvariantCulture, $"leading {group}: followers take this node's clock on UDP {_responder.Port}");
        }
        finally
        {
            _changes.Release();
        }
    }

    public async Task<string> FollowAsync(string group, string nodeId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.Yield();   // off the control lane: the exchange starts on a socket of its own
        await _changes.WaitAsync(_stop.Token);
        try
        {
            var peer = FindPeer(nodeId)
                ?? throw new InvalidOperationException($"no PlayMap node '{nodeId}' is heard on the show network — NODES lists the ones that are");
            if (peer.Id == _running.NodeId)
                throw new InvalidOperationException($"a node cannot follow itself — SYNC LEAD {group} makes it the leader");
            if (peer.Liveness != PeerLiveness.Live)
                throw new InvalidOperationException($"{peer.Name} has gone quiet on the show network — it cannot lead until it is heard again");
            if (!IPAddress.TryParse(peer.Address, out var ip))
                throw new InvalidOperationException($"{peer.Name}'s hello came from '{peer.Address}', which is not an address");
            var port = peer.Hello.Port("sync") is > 0 and var p ? p : ShowBusConstants.PlayMapSyncPort;
            if (_responder is { } answering)
            {
                // A follower is not the group's clock: it stops answering before it starts asking.
                _responder = null;
                await answering.DisposeAsync();
            }
            await _sync.FollowAsync(new IPEndPoint(ip, port));
            ClearFault("sync");
            _role = "follower";
            _leader = peer.Id;
            _lastQuality = SyncQuality.None;
            Facts();
            return string.Create(CultureInfo.InvariantCulture, $"following {peer.Name} ({peer.Id}) for {group}: its clock over UDP {peer.Address}:{port}");
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <summary>Queues a pull of each item from the heard PlayMap nodes (only <paramref name="fromNode"/>, by id or name,
    /// when given) and answers what was queued; the pulls run beside the engine — each verified against the item's
    /// SHA-256 before it lands in the media store — and their outcome reaches the journal and STATE.</summary>
    public async Task<string> FetchAsync(IReadOnlyList<MediaItem> items, string? fromNode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);
        ct.ThrowIfCancellationRequested();
        await Task.Yield();   // off the control lane: the pulls ask peers for their manifests
        var peers = (_hello?.Peers.Snapshot() ?? [])
            .Where(p => p.Kind == ShowBusConstants.KindPlayMap && p.Id != _running.NodeId && p.Liveness == PeerLiveness.Live && p.Hello.Port("http") > 0)
            .Where(p => fromNode is null || p.Id.Equals(fromNode, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(fromNode, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (peers.Count == 0)
            throw new InvalidOperationException(fromNode is null
                ? "no other PlayMap node is heard on the show network to fetch from"
                : $"no PlayMap node '{fromNode}' is heard on the show network — NODES lists the ones that are");
        var wanted = new List<(MediaItem Item, string Hex)>();
        foreach (var item in items)
        {
            var hex = MediaBackend.Hex(item.ContentHash);
            if (hex.Length == 0 || _transfers.TryGetValue(hex, out var t) && t.Moving)
                continue;
            _transfers[hex] = new MediaTransferView(hex, item.Id, item.Name, "", 0, item.SizeBytes, "queued");
            wanted.Add((item, hex));
        }
        if (wanted.Count == 0)
            throw new InvalidOperationException("nothing to fetch: every file asked for is unhashed or already on its way");
        Facts();
        Background(PullAllAsync(wanted, peers, _stop.Token));
        var from = string.Join(", ", peers.Select(p => p.Name));
        return string.Create(CultureInfo.InvariantCulture, $"fetching {wanted.Count} file{(wanted.Count == 1 ? "" : "s")} from {from}");
    }

    /// <summary>PAIRING NEW: a new token written to the node's settings first (a token that could not be kept is not
    /// taken), then handed to the wire and the web port, which compare every verb against it — so a peer paired with the
    /// old one is unpaired at its next verb. The words say where the token is read, never the token.</summary>
    public string NewToken()
    {
        lock (_settingsGate)
        {
            var token = Pairing.NewToken();
            var stored = _stored with { Token = token };
            try
            {
                _o.Home.SaveSettings(stored);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new EditRefusedException("the new pairing token could not be written, so the old one stands: " + ex.Message);
            }
            _stored = stored;
            _running = _running with { Token = token };
            _wire?.UpdateSettings(_running);
            _web?.UpdateSettings(_running);
        }
        Journal("network", "a new pairing token: every paired peer pairs again");
        return $"a new pairing token is in this node's settings ({_o.Home.SettingsPath}) and its Network drawer — every paired peer pairs again with it";
    }

    public bool Mcp => _web?.ServesMcp ?? false;

    public int Clients => _web is { } web ? web.McpSessions + web.WebSockets : 0;

    // ---- The links. ----

    private void StartPatterns(string host, string token)
    {
        var (h, port) = PatternsLinkOptions.ParseAddress(host)
            ?? throw new ArgumentException($"the Patterns address '{host}' in the node's settings is not an address — LINK PATTERNS <host>[:<port>]");
        var link = new PatternsLink(new PatternsLinkOptions { Host = h, Port = port, Token = token }, _o.Wall, _o.Mono, _o.Logs.CreateLogger("PlayMap.Net.Patterns"));
        link.BlackoutChanged += OnPatternsBlackout;
        link.VideoChanged += _ => Facts();
        link.StatusChanged += _ => Facts();
        _patterns = link;
        link.Start();
    }

    /// <summary>The desk's blackout, taken in through the engine's own path for Patterns' blackout facts (the line a
    /// Patterns "Lines" device sends): the air follows it while "Follow Patterns' blackout" is on.</summary>
    private void OnPatternsBlackout(bool on)
    {
        _patternsBlackedUs = on;
        Fact(on ? "BLACKOUT 1" : "BLACKOUT 0");
    }

    private async Task StopPatternsAsync()
    {
        if (_patterns is not { } link)
            return;
        _patterns = null;
        link.BlackoutChanged -= OnPatternsBlackout;
        await link.DisposeAsync();
        if (_patternsBlackedUs)
        {
            // A desk this node no longer follows does not hold its air.
            _patternsBlackedUs = false;
            Fact("BLACKOUT 0");
        }
        Facts();
    }

    private StagewatchLinkOptions StagewatchOptions(string host, int port, string key)
    {
        // A Stagewatch heard at that address is met as itself (TLS pinned by its id, approvals when it is a desk);
        // a typed address no hello names is plain, pinned by the address.
        var heard = (_hello?.Peers.Live(ShowBusConstants.KindStagewatch) ?? [])
            .FirstOrDefault(p => string.Equals(p.Address, host, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Hello.Host, host, StringComparison.OrdinalIgnoreCase));
        if (heard is not null)
            return StagewatchLinkOptions.FromPeer(heard, key, _running.NodeId, _running.Name);
        return new StagewatchLinkOptions
        {
            Host = host,
            Port = port,
            Key = key,
            PinKey = PinStore.AddressKey(host, port),
            SelfId = _running.NodeId,
            SelfName = _running.Name,
        };
    }

    /// <summary>The settings' Stagewatch: the typed address, or — with none typed — the first Stagewatch heard (now,
    /// or when one is).</summary>
    private void StartStagewatchFromSettings()
    {
        var s = _running;
        if (s.StagewatchHost.Length > 0)
        {
            if (PatternsLinkOptions.ParseAddress(s.StagewatchHost, ShowBusConstants.StagewatchHttpPort) is { } a)
                StartStagewatch(StagewatchOptions(a.Host, a.Port, s.StagewatchKey));
            else
                Fault("stagewatch", $"the Stagewatch address '{s.StagewatchHost}' in the node's settings is not an address — LINK STAGEWATCH <host>[:<port>]");
            return;
        }
        if (_hello?.Peers.Live(ShowBusConstants.KindStagewatch) is { Count: > 0 } heard)
            StartStagewatch(StagewatchLinkOptions.FromPeer(heard[0], s.StagewatchKey, s.NodeId, s.Name));
    }

    private void StartStagewatch(StagewatchLinkOptions options)
    {
        var engine = _engine ?? throw new InvalidOperationException("the engine is not running yet");
        var link = new StagewatchLink(options, engine, _o.Wall, _o.Pins, _o.Mono, _o.Logs.CreateLogger("PlayMap.Net.Stagewatch"));
        link.StatusChanged += _ => Facts();
        link.FrameReceived += _ => Facts();
        link.ScreenCommand += OnScreenCommand;
        _stagewatch = link;
        link.Start();
        Facts();
    }

    private async Task StopStagewatchAsync()
    {
        if (_stagewatch is not { } link)
            return;
        _stagewatch = null;
        _stagewatchView = "";
        await link.DisposeAsync();
        Facts();
    }

    /// <summary>Stagewatch's operator, from its Screens page: identify flashes this node's outputs' names, redirect
    /// picks the view this node shows as its timer, reload and disconnect are noted (the link redials by itself).</summary>
    private void OnScreenCommand(StagewatchScreenCommand command)
    {
        switch (command.Type)
        {
            case "identify":
                Run("OUTPUT IDENTIFY ALL 5", StagewatchSession);
                break;
            case "redirect":
                _stagewatchView = command.To;
                Journal("network", $"Stagewatch's operator shows {(command.To.Length > 0 ? command.To : "the default view")} on this node's timer");
                Facts();
                break;
            case "reload":
                Journal("network", "Stagewatch's operator asked this screen to reload: its timer is read afresh from the next frame");
                Facts();
                break;
            case "disconnect":
                Journal("network", "Stagewatch's operator disconnected this screen; the link dials again in 30 s");
                Facts();
                break;
        }
    }

    private void OnPeer(PeerChange change)
    {
        Facts();
        if (change.Peer.Kind == ShowBusConstants.KindStagewatch && change.Kind is PeerChangeKind.Added && _stagewatch is null
            && _running.StagewatchEnabled && _running.StagewatchHost.Length == 0 && _engine is not null)
            Background(AutoStagewatchAsync(_stop.Token));
    }

    private async Task AutoStagewatchAsync(CancellationToken ct)
    {
        await _changes.WaitAsync(ct);
        try
        {
            if (_stagewatch is null && _running.StagewatchEnabled && _running.StagewatchHost.Length == 0)
                StartStagewatchFromSettings();
        }
        finally
        {
            _changes.Release();
        }
    }

    // ---- Media pulls. ----

    private async Task PullAllAsync(List<(MediaItem Item, string Hex)> wanted, List<ShowBusPeer> peers, CancellationToken ct)
    {
        var offers = new Dictionary<string, Dictionary<string, MediaManifestEntry>>(StringComparer.Ordinal);
        foreach (var (item, hex) in wanted)
        {
            var landed = false;
            var why = "no heard node holds it";
            foreach (var peer in peers)
            {
                if (PeerUri(peer) is not { } uri)
                    continue;
                if (!offers.TryGetValue(peer.Id, out var offered))
                {
                    try
                    {
                        offered = (await _transfer.ManifestAsync(uri, ct)).ToDictionary(e => e.Hash, StringComparer.Ordinal);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
                    {
                        _log.LogInformation(ex, "{Peer}'s media list could not be read.", peer.Name);
                        offered = [];
                    }
                    offers[peer.Id] = offered;
                }
                if (!offered.TryGetValue(hex, out var entry))
                    continue;
                _transfers[hex] = new MediaTransferView(hex, item.Id, item.Name, peer.Name, 0, entry.Size, "pulling");
                Facts();
                var destination = _o.Media.StorePath(hex, item.Location.Length > 0 ? item.Location : item.Name);
                MediaPullResult result;
                try
                {
                    result = await _transfer.PullAsync(uri, hex, entry.Size, destination, ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException && !ct.IsCancellationRequested)
                {
                    why = $"{peer.Name}: {ex.Message}";
                    continue;
                }
                if (result.Outcome is MediaPullOutcome.Verified or MediaPullOutcome.AlreadyHere)
                {
                    _o.Media.AddVerified(hex, destination, Path.GetFileName(item.Location.Length > 0 ? item.Location : item.Name));
                    _transfers.TryRemove(hex, out _);
                    Journal("media", string.Create(CultureInfo.InvariantCulture,
                        $"{item.Name} pulled from {peer.Name} and verified ({result.BytesFetched / 1024 / 1024} MiB, {result.ChunksFetched} chunk{(result.ChunksFetched == 1 ? "" : "s")}{(result.ChunksResumed > 0 ? $", {result.ChunksResumed} resumed" : "")})"));
                    landed = true;
                    break;
                }
                why = $"{peer.Name}: {result.Words}";
            }
            if (!landed)
            {
                _transfers[hex] = new MediaTransferView(hex, item.Id, item.Name, "", 0, item.SizeBytes, "failed: " + why);
                Journal("refused", $"{item.Name} could not be fetched: {why}", ok: false);
            }
            Facts();
        }
    }

    private void OnPullProgress(MediaProgress progress)
    {
        if (_transfers.TryGetValue(progress.Hash, out var t))
        {
            _transfers[progress.Hash] = t with { BytesDone = progress.BytesDone, Size = progress.Size };
            Facts();
        }
    }

    private static Uri? PeerUri(ShowBusPeer peer)
    {
        var port = peer.Hello.Port("http");
        if (port <= 0 || !IPAddress.TryParse(peer.Address, out var ip))
            return null;
        var host = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[" + ip + "]" : ip.ToString();
        return new Uri(string.Create(CultureInfo.InvariantCulture, $"http://{host}:{port}/"));
    }

    // ---- The watch: the pull rate while a show plays, the assistant's door, the sync's quality. ----

    private async Task WatchAsync(CancellationToken ct)
    {
        while (await Waits.DelayAsync(WatchEvery, ct))
        {
            try
            {
                if (_engine is { } engine)
                    _transfer.Bandwidth.SetRate(engine.IsAnimating ? _o.PullRateDuringShow : 0);
                var clients = Clients;
                if (clients != _lastClients)
                {
                    // Who is on the assistant's door is a fact STATE shows.
                    _lastClients = clients;
                    Facts();
                }
                // A link's view moves without an event of its own: pairing completes, a clip counts down, a timer is
                // counted on locally, a frame goes stale. What STATE shows of the links is looked at here.
                var links = (Patterns(), Stagewatch(), StagewatchTimerJson());
                if (links != _lastLinks)
                {
                    _lastLinks = links;
                    Facts();
                }
                if (_role != "follower")
                    continue;
                var quality = _sync.Snapshot.Quality;
                if (quality == _lastQuality)
                    continue;
                var was = _lastQuality;
                _lastQuality = quality;
                if (quality == SyncQuality.Lost)
                    Journal("network", "the sync leader has not answered for two seconds — the last estimate holds", ok: false);
                else if (was == SyncQuality.Lost)
                    Journal("network", "the sync leader answers again");
                Facts();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A watch that faults says nothing this half second; the next one looks again.
                _log.LogWarning(ex, "The network watch faulted.");
            }
        }
    }

    // ---- Small things. ----

    private ShowBusHello SayHello()
    {
        var s = _running;
        var advertised = s with
        {
            HttpPort = _web?.Port ?? 0,
            WirePort = _wire?.Port ?? 0,
            SyncPort = _responder?.Port ?? s.SyncPort,
        };
        return HelloService.PlayMapHello(advertised, _o.Wall.UnixMs, _web is not null ? _o.Tls?.Fingerprint : null);
    }

    private ShowBusPeer? FindPeer(string idOrName) =>
        (_hello?.Peers.Snapshot() ?? [])
            .Where(p => p.Kind == ShowBusConstants.KindPlayMap)
            .FirstOrDefault(p => p.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
        ?? (_hello?.Peers.Snapshot() ?? [])
            .Where(p => p.Kind == ShowBusConstants.KindPlayMap)
            .FirstOrDefault(p => p.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    private SessionInfo StagewatchSession => new("link-stagewatch", "stagewatch", _stagewatch?.Address ?? "", false, "Stagewatch", Paired: true);

    private SessionInfo PatternsSession => new("link-patterns", "patterns", _patterns?.Address ?? "", false, "Patterns", Paired: true);

    /// <summary>A Patterns fact line ("BLACKOUT 1") through the engine's command path, which takes it in silently.</summary>
    private void Fact(string line) => Run(line, PatternsSession);

    private void Run(string text, SessionInfo session)
    {
        if (_engine is not { } engine || !WireGrammar.TryParse(text, out var line, out _))
            return;
        Background(RunAsync(engine, line, session));
    }

    private async Task RunAsync(EngineCore engine, WireLine line, SessionInfo session)
    {
        var reply = await engine.HandleAsync(line, session, _stop.Token);
        if (!reply.Ok)
            _log.LogInformation("The engine refused {Verb} from the {Transport} link: {Why}", line.Verb, session.Transport, reply.Payload);
    }

    private void Persist(Func<NodeSettings, NodeSettings> edit)
    {
        lock (_settingsGate)
        {
            _stored = edit(_stored);
            _running = edit(_running);
            try
            {
                _o.Home.SaveSettings(_stored);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fault("settings", "the node's settings could not be written — the link holds until the node restarts: " + ex.Message);
            }
        }
    }

    private void Fault(string part, string words)
    {
        _faults[part] = new NodeFault(part, words);
        _log.LogWarning("{Part}: {Words}", part, words);
        Journal("fault", words, ok: false);
        Facts();
    }

    private void ClearFault(string part)
    {
        if (_faults.TryRemove(part, out _))
            Facts();
    }

    private void Journal(string kind, string text, bool ok = true) => _engine?.Journal.Add(kind, text, ok: ok);

    private void Facts() => _engine?.FactsChanged();

    private static string StatusWord(LinkStatus status) => status switch
    {
        LinkStatus.Live => "live",
        LinkStatus.Stale => "stale",
        LinkStatus.Connecting => "connecting",
        _ => "down",
    };

    /// <summary>Keeps a task beside the host so shutdown waits for it; a fault in it is logged, never lost.</summary>
    private void Background(Task task)
    {
        _ = task.ContinueWith(t => _log.LogWarning(t.Exception, "A network task faulted."),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        lock (_backgroundGate)
        {
            _background.RemoveAll(t => t.IsCompleted);
            _background.Add(task);
        }
    }

    /// <summary>Closes in reverse order of opening: the links, the clock sync, the hello, the web port, the wire, then
    /// what was running beside them. Every part is closed even when one before it throws.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stop.CancelAsync();
        await Quietly("the Stagewatch link", StopStagewatchAsync);
        await Quietly("the Patterns link", StopPatternsAsync);
        await Quietly("the clock sync", async () =>
        {
            if (_responder is not null)
                await _responder.DisposeAsync();
            await _sync.DisposeAsync();
        });
        await Quietly("the hello", async () =>
        {
            if (_hello is not null)
                await _hello.DisposeAsync();
        });
        await Quietly("the web port", async () =>
        {
            if (_web is not null)
                await _web.DisposeAsync();
        });
        await Quietly("the line wire", async () =>
        {
            if (_wire is not null)
                await _wire.DisposeAsync();
        });
        Task[] background;
        lock (_backgroundGate)
            background = [.. _background.Select(t => t.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default))];
        // Their faults were logged as they happened; here they are only waited for, five seconds at most.
        await Quietly("the network tasks", () => Task.WhenAll(background).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        _http.Dispose();
        _changes.Dispose();
        _stop.Dispose();
    }

    private async Task Quietly(string what, Func<Task> close)
    {
        try
        {
            await close();
        }
        catch (Exception ex)
        {
            // Shutdown goes on: the next part is closed whatever this one did.
            _log.LogWarning(ex, "Closing {What} faulted.", what);
        }
    }
}
