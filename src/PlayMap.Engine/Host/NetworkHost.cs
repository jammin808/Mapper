using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Bus;
using PlayMap.Core.Machine;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Core.Storage;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Devices.Model;
using PlayMap.Engine.Devices;
using PlayMap.Net;
using PlayMap.Net.Bus;
using PlayMap.Net.Http;
using PlayMap.Net.Links;
using PlayMap.Net.Media;
using PlayMap.Net.Sync;
using PlayMap.Net.Tls;
using PlayMap.Net.Wire;

namespace PlayMap.Engine.Host;

/// <summary>When this node's pairing token now in force last came in clear from another machine.</summary>
public sealed record TokenSighting(ClearSighting Seen, long AtUnixMs);

/// <summary>A network view that knows when the node's pairing token was seen in clear (preflight's links section reads it).</summary>
public interface ITokenSightings
{
    /// <summary>The latest time the token now in force came in clear from another machine; null when it has not since it was made.</summary>
    TokenSighting? TokenSeenInClear { get; }
}

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

    /// <summary>How the node's media pulls ask their peers (the quiet a chunk's body is given before its try is called stalled, the
    /// tries per chunk), this host's and the fleet part's alike; null = the transfer's own.</summary>
    public MediaTransferOptions? Pulls { get; init; }
}

/// <summary>The network edge as the engine sees it, over PlayMap.Net: the line wire and the web port (HTTP, HTTPS,
/// WebSocket, MCP, media) with the engine as their endpoint; the hello and the peers it hears; the Patterns and
/// Stagewatch links; the fine clock sync (answering when this node leads, following when it does not); media pulls from
/// peers. It is made before the engine (its clock is the engine's leader clock, its views and controls are engine
/// services) and opened once the engine exists. A door that cannot open stays closed and says so in STATE's faults;
/// the node stays up on the others, and only a node with neither the wire nor the web port refuses to start.
/// <para>The engine calls the controls on its control lane: each one leaves the lane at once and does its work
/// beside it, and one link or sync change runs at a time.</para>
/// <para>The Stagewatch key lives in the link keys' store (<see cref="NodeCredentialStore.LinkKeysFileName"/>), this host its one
/// writer; the settings file holds none (an older file's key is moved across at start, and journalled once the settings file is
/// written without it). The node's own token arriving in clear from another machine changes no gate's answer: it is journalled once
/// per address and carrier, and marked for preflight (<see cref="ITokenSightings"/>) until a new token is made.</para></summary>
public sealed class NetworkHost : INetworkView, INetworkControl, INodeFaults, IMediaTransfers, INodePairing, IAiDoor, ITokenSightings, IAsyncDisposable
{
    private static readonly TimeSpan WatchEvery = TimeSpan.FromMilliseconds(500);

    /// <summary>At most this many addresses and carriers are journalled for one token seen in clear; the mark keeps the latest.</summary>
    private const int ClearRowsPerToken = 32;

    private readonly NetworkHostOptions _o;
    private readonly ILogger _log;
    private readonly ClockSyncClient _sync;
    private readonly ConcurrentDictionary<string, NodeFault> _faults = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MediaTransferView> _transfers = new(StringComparer.Ordinal);

    /// <summary>Each pull on its way to a peer, by its file's hash: its fetch's journal rows, and whether a failed try of it at that peer
    /// has been said yet. A pull says its first failed try at each peer and its end; the tries between are its row's note and the
    /// log's.</summary>
    private readonly ConcurrentDictionary<string, PullWords> _pulling = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly Lock _settingsGate = new();
    private readonly Lock _backgroundGate = new();
    private readonly List<Task> _background = [];
    private readonly List<IWebModule> _modules = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly HttpClient _http;
    private readonly MediaTransfer _transfer;
    private readonly NodeCredentialStore _linkKeys;
    private volatile bool _keyLeftInSettings;
    private readonly ConcurrentDictionary<string, byte> _clearSaid = new(StringComparer.Ordinal);
    private volatile TokenSighting? _seenInClear;
    private volatile NodeSettings _stored;
    private volatile NodeSettings _running;
    private volatile EngineCore? _engine;
    private WireServer? _wire;
    private HttpHost? _web;
    private volatile HelloService? _hello;
    private volatile ClockSyncResponder? _responder;
    private volatile IPAddress? _wireAt;
    private volatile IPAddress? _webAt;
    private volatile IPAddress? _syncAt;
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
        _transfer = new MediaTransfer(_http, new TokenBucket(0, options.Mono), options.Pulls, options.Logs.CreateLogger("PlayMap.Net.Media"), options.Mono);
        _transfer.Progress += OnPullProgress;
        _transfer.Trouble += OnPullTrouble;
        _linkKeys = new NodeCredentialStore(options.Home.KeysFolder, NodeCredentialStore.LinkKeysFileName);
    }

    public TokenSighting? TokenSeenInClear => _seenInClear;

    /// <summary>How the node's media pulls ask their peers (<see cref="NetworkHostOptions.Pulls"/>), for the fleet part's pulls.</summary>
    internal MediaTransferOptions? PullOptions => _o.Pulls;

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

    /// <summary>The show doors open now, each with the address it is bound to: the line wire and the web port (the phone page and MCP
    /// ride it), and the clock sync while this node leads. A door binds the settings' bind address as it opens, exactly, or stays
    /// closed, so an open door is bound to the address the settings named then, and to every interface (<see cref="IPAddress.Any"/>)
    /// where they named none. The hello, which hears every interface to find its peers, is not a show door.</summary>
    public ImmutableArray<ShowDoor> Doors
    {
        get
        {
            var doors = ImmutableArray.CreateBuilder<ShowDoor>(3);
            if (_wire is not null && _wireAt is { } wire)
                doors.Add(new ShowDoor("wire", wire));
            if (_web is not null && _webAt is { } web)
                doors.Add(new ShowDoor("web", web));
            if (_responder is not null && _syncAt is { } sync)
                doors.Add(new ShowDoor("sync", sync));
            return doors.ToImmutable();
        }
    }

    /// <summary>The address a door opened with <paramref name="settings"/> is bound to: their bind address, or every interface.</summary>
    private static IPAddress BoundTo(NodeSettings settings) =>
        IPAddress.TryParse(settings.Bind.Trim(), out var address) ? NetAddress.Normal(address) : IPAddress.Any;

    /// <summary>The hello, once started: its peer table, and its targets (which a test points at another node).</summary>
    public HelloService? Hello => _hello;

    /// <summary>The Stagewatch view this node shows as its timer (the operator's last redirect; "" = the default).</summary>
    public string StagewatchView => _stagewatchView;

    /// <summary>A part that serves pages and sockets under its own path of the web port (the phone's /scan). Added before
    /// <see cref="StartAsync"/>; one module per path.</summary>
    public void AddWebModule(IWebModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (_web is not null)
            throw new InvalidOperationException($"{module.Name} came after the web port opened — web modules are added before the node starts");
        if (HttpHost.WhyNotAModulePath(module.PathPrefix) is { } why)
            throw new ArgumentException($"{module.Name}: {why}", nameof(module));
        if (_modules.Exists(m => string.Equals(m.PathPrefix, module.PathPrefix, StringComparison.Ordinal)))
            throw new InvalidOperationException($"{module.PathPrefix} is served by another web module already");
        _modules.Add(module);
    }

    /// <summary>Opens the doors with the engine as their endpoint, then the hello and the links the settings name. A
    /// door that is refused stays closed and becomes a fault; with neither the wire nor the web port open there is no
    /// node to reach, and this throws <see cref="BindRefusedException"/> with both reasons.</summary>
    public async Task StartAsync(EngineCore engine, CancellationToken ct)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        var logs = _o.Logs;
        var running = _running;
        var wire = new WireServer(running, engine, _o.Wall, new WireServerOptions { SeenInClear = OnTokenSeenInClear }, logs.CreateLogger("PlayMap.Net.Wire"));
        try
        {
            wire.Start();
            _wireAt = BoundTo(running);
            _wire = wire;
        }
        catch (BindRefusedException ex)
        {
            await wire.DisposeAsync();
            Fault("wire", "the line wire stays closed: " + ex.Message);
        }

        var web = new HttpHost(running, engine, _o.Wall, new HttpHostOptions
        {
            Tls = _o.Tls,
            Media = _o.Media,
            Approvals = _o.Approvals.Policy,
            LoggerFactory = logs,
            Version = engine.Services.Build.Informational,
            Modules = [.. _modules],
            SeenInClear = OnTokenSeenInClear,
        });
        try
        {
            await web.StartAsync(ct);
            _webAt = BoundTo(running);
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
        MoveStagewatchKeyOutOfSettings();
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
        var live = link.Status == LinkStatus.Live;
        string detail;
        if (link.Pairing == PairState.Refused)
            detail = "wrong token — LINK PATTERNS with the desk's pairing token";
        else if (link.Pairing == PairState.Unprovable)
            detail = "it asked for a proof with a NONCE that is not 32 hex — no proof is sent and nothing it says is followed (another machine at its address?)";
        else if (link.Pairing == PairState.NeedsToken)
            detail = "it asks for a pairing token and this node holds none — LINK PATTERNS with the desk's pairing token";
        else if (live && !link.Paired && link.Pairing == PairState.NotNeeded)
            detail = "it asks for no proof, so it cannot show it is the desk — the token stays on this machine and nothing it says is followed. "
                + $"A desk that asks for no token is followed open by LINK PATTERNS {link.Address} with no token; otherwise it is a Patterns "
                + "older than Show Bus 1.1, or another machine at its address";
        else if (live && !link.Paired)
            detail = "pairing — nothing it says is followed until it pairs";
        else if (link.Video is { } v)
            detail = v.RemainingMs is { } left ? $"{Label(v)} {ClipTransport.Format((long)(left * 1e6))}" : Label(v);
        else
            detail = live ? "no clip on air" : "";
        return new LinkView(StatusWord(link.Status), link.Address, link.Paired, detail);

        static string Label(PatternsVideo v) => v.Tag.Length > 0 ? v.Tag : v.File;
    }

    public LinkView? Stagewatch()
    {
        if (_stagewatch is not { } link)
            return null;
        var view = _stagewatchView;
        var detail = link.Words + (view.Length > 0 ? (link.Words.Length > 0 ? " · " : "") + "showing " + view : "");
        // A link that holds its key back (no TLS off this machine) reads the timer, and is not paired.
        return new LinkView(StatusWord(link.Status), link.Address, link.Status == LinkStatus.Live && !link.KeyHeldBack, detail);
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
                KeepStagewatchKey("");
                if (Persist(s => s with { StagewatchEnabled = false, StagewatchHost = "", StagewatchKey = "" }))
                    SettingsHoldNoKey();
                return "stopped following Stagewatch";
            }
            var keep = key ?? (string.Equals(host, _stored.StagewatchHost, StringComparison.OrdinalIgnoreCase) ? StagewatchKeyNow() : "");
            if (host.Trim().Length == 0)
            {
                // No address: the first Stagewatch heard on the show network, now or when one is.
                await StopStagewatchAsync();
                KeepStagewatchKey(keep);
                if (Persist(s => s with { StagewatchEnabled = true, StagewatchHost = "", StagewatchKey = "" }))
                    SettingsHoldNoKey();
                StartStagewatchFromSettings();
                return _stagewatch is { } found
                    ? $"following the Stagewatch heard at {found.Address} as a screen"
                    : "following the first Stagewatch heard — none is heard yet";
            }
            var address = PatternsLinkOptions.ParseAddress(host, ShowBusConstants.StagewatchHttpPort)
                ?? throw new ArgumentException($"'{host}' is not an address — LINK STAGEWATCH <host>[:<port>] [<key>]");
            await StopStagewatchAsync();
            KeepStagewatchKey(keep);
            StartStagewatch(StagewatchOptions(address.Host, address.Port, keep));
            if (Persist(s => s with { StagewatchEnabled = true, StagewatchHost = host, StagewatchKey = "" }))
                SettingsHoldNoKey();
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
                var running = _running;
                var responder = new ClockSyncResponder(running, _sync.Clock, running.SyncPort, _o.Logs.CreateLogger("PlayMap.Net.Sync"));
                try
                {
                    responder.Start();
                    _syncAt = BoundTo(running);
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

    /// <summary>Where this node's clock asks its leader now, as "address:port" ("" unless it follows).</summary>
    public string FollowedClock => _role == "follower" ? _sync.Snapshot.Leader : "";

    /// <summary>Where a follow of <paramref name="nodeId"/> would ask its clock now, by the clock port its latest hello names, as
    /// "address:port" ("" when it is not heard).</summary>
    public string ClockOf(string nodeId) =>
        FindPeer(nodeId) is { } peer && IPAddress.TryParse(peer.Address, out var ip)
            ? new IPEndPoint(ip, peer.Hello.Port("sync") is > 0 and var port ? port : ShowBusConstants.PlayMapSyncPort).ToString()
            : "";

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
    /// when given), journals what was queued (the fetch's accepted row, before any row of its pulls) and answers it; the pulls run
    /// beside the engine — each verified against the item's SHA-256 before it lands in the media store. STATE's row says where each
    /// stands (its stage, bytes and peer, its last failed try's words) and how it ended when it failed; the journal holds each pull's
    /// first failed try at each peer and its end, at most
    /// <see cref="FetchJournal.Most"/> rows a fetch, and one that counts the rest when there were more (<see cref="FetchJournal"/>).
    /// This is the node's own pull, over a peer's <c>/api/media</c>; a node with the fleet's part pulls MEDIA FETCH's files with the
    /// peer's proof instead (<c>BackupNode.FetchMedia</c>).</summary>
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
        var engine = _engine ?? throw new InvalidOperationException("the engine is not running yet");
        var wanted = new List<(MediaItem Item, string Hex)>();
        foreach (var item in items)
        {
            var hex = MediaBackend.Hex(item.ContentHash);
            if (hex.Length == 0 || _transfers.TryGetValue(hex, out var t) && t.Moving)
                continue;
            _transfers[hex] = new MediaTransferView(hex, item.Id, item.Name, "", 0, item.SizeBytes, "queued") { SinceUnixMs = _o.Wall.UnixMs };
            wanted.Add((item, hex));
        }
        if (wanted.Count == 0)
            throw new InvalidOperationException("nothing to fetch: every file asked for is unhashed or already on its way");
        Facts();
        var from = string.Join(", ", peers.Select(p => p.Name));
        var what = string.Create(CultureInfo.InvariantCulture, $"{wanted.Count} file{(wanted.Count == 1 ? "" : "s")} from {from}");
        // What was queued is the fetch's accepted row, written before the pulls begin: a peer can end them all at once, and every
        // row of theirs follows it.
        var accepted = "fetching " + what;
        engine.Journal.Add("media", accepted);
        Background(PullAllAsync(wanted, peers, new FetchJournal(engine.Journal, _log, "the fetch of " + what), _stop.Token));
        return accepted;
    }

    /// <summary>PAIRING NEW: a new token written to the node's settings first (a token that could not be kept is not
    /// taken), then handed to the wire and the web port, which compare every verb against it — so a peer paired with the
    /// old one is unpaired at its next verb. The words say where the token is read, never the token.</summary>
    public string NewToken()
    {
        using (EnterSettings())
        {
            var token = Pairing.NewToken();
            // Read fresh: another part's keys kept since this run began (a sync role, the fleet's epoch) are written back with it.
            var stored = Fresh() with { Token = token };
            try
            {
                WriteSettingsLocked(stored);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new EditRefusedException("the new pairing token could not be written, so the old one stands: " + ex.Message);
            }
            _stored = stored;
            _running = _running with { Token = token };
            _wire?.UpdateSettings(_running);
            _web?.UpdateSettings(_running);
            // The token seen in clear is gone with it.
            _seenInClear = null;
            _clearSaid.Clear();
        }
        Journal("network", "a new pairing token: every paired peer pairs again");
        return $"a new pairing token is in this node's settings ({_o.Home.SettingsPath}) and its Network drawer — every paired peer pairs again with it";
    }

    /// <summary>Keeps a change of the node's settings for the next start, as the one writer of its settings file: the edit is made
    /// to the stored and the running settings under one lock and written, so a later link or token change writes it back with its
    /// own. Answers "" or the words of a failure (the running settings hold the change either way).</summary>
    public string PersistSettings(Func<NodeSettings, NodeSettings> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        using (EnterSettings())
        {
            _running = edit(_running);
            try
            {
                _stored = edit(Fresh());
                WriteSettingsLocked(_stored);
                return "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "the node's settings could not be written: " + ex.Message;
            }
        }
    }

    /// <summary>Keeps a change of the node's settings, all or nothing, as the one writer of its settings file: the file is read fresh
    /// under the writer's lock, the edit made to it and written, and only then made to the stored and the running settings. A write
    /// that fails throws its <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> and nothing changes, in the file or
    /// here (a verb that keeps a setting refuses with the words: PRESENT PATH, DECODE POLICY); a write that landed but whose folder did
    /// not flush is the file's content, and is taken (<see cref="WriteSettingsLocked"/>).</summary>
    public void KeepSettings(Func<NodeSettings, NodeSettings> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        using (EnterSettings())
        {
            var next = edit(Fresh());
            WriteSettingsLocked(next);
            _stored = next;
            _running = edit(_running);
        }
    }

    /// <summary>Raised on the editor's thread when an edit of the settings file finds the writer's lock held by another edit and waits
    /// for it (a test holds one edit inside the lock and sees the next one wait at it).</summary>
    internal event Action? SettingsEditWaits;

    /// <summary>The writer's lock: every read-modify-write of the settings file runs inside it, one at a time.</summary>
    private SettingsScope EnterSettings()
    {
        if (!_settingsGate.TryEnter())
        {
            SettingsEditWaits?.Invoke();
            _settingsGate.Enter();
        }
        return new SettingsScope(_settingsGate);
    }

    /// <summary>The writer's lock held, let go when disposed.</summary>
    private readonly record struct SettingsScope(Lock Gate) : IDisposable
    {
        public void Dispose() => Gate.Exit();
    }

    /// <summary>The fault a settings write that landed without its folder's flush leaves, until a write flushes whole.</summary>
    private const string FlushFault = "settings-flush";

    /// <summary>The settings file's one write (durable: flushed before it is renamed into place). Inside <see cref="EnterSettings"/>. A
    /// write that landed (the file renamed into place) but whose folder could not be flushed after is the file's content all the same:
    /// it counts as written (its caller takes the change, as a later start will), and the node says in its faults that a power cut may
    /// bring the earlier file back; a later write that flushes whole makes it durable with its own, and the fault goes.</summary>
    private void WriteSettingsLocked(NodeSettings settings)
    {
        try
        {
            _o.Home.SaveSettings(settings);
        }
        catch (NotFlushedException ex)
        {
            Fault(FlushFault, "the node's settings file holds the last change, but its folder could not be flushed to the disk — a power cut "
                + "may bring the earlier settings back until the next change is written: " + ex.InnerException?.Message);
            return;
        }
        ClearFault(FlushFault);
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

    /// <summary>The Stagewatch key this node holds: the link keys' store's, or — while an older settings file's key could not be moved
    /// into the store — the file's.</summary>
    private string StagewatchKeyNow() =>
        _linkKeys.Find(NodeCredentialStore.StagewatchKey)?.Secret is { Length: > 0 } key ? key : _stored.StagewatchKey;

    /// <summary>Keeps the Stagewatch key in the link keys' store ("" forgets it); a store that cannot be written holds it for this run
    /// and says so in the faults. The settings file is never where it goes. The fault clears only once the store is written and the
    /// settings file is known to hold no key.</summary>
    private void KeepStagewatchKey(string key)
    {
        if (key.Length > 0)
            _linkKeys.Set(NodeCredentialStore.StagewatchKey, new Credential("", key));
        else
            _linkKeys.Remove(NodeCredentialStore.StagewatchKey);
        if (_linkKeys.Problem.Length > 0)
            Fault("stagewatch-key", "the Stagewatch key is held for this run only: " + _linkKeys.Problem);
        else if (!_keyLeftInSettings)
            ClearFault("stagewatch-key");
    }

    /// <summary>The settings file was written with no Stagewatch key: a key an older file held is out of it now.</summary>
    private void SettingsHoldNoKey()
    {
        _keyLeftInSettings = false;
        if (_linkKeys.Problem.Length == 0)
            ClearFault("stagewatch-key");
    }

    /// <summary>A settings file an older node wrote may hold the Stagewatch key in the clear. It is moved into the link keys' store
    /// and taken out of the settings file only after the store holds it, and the move is journalled only once the settings file is
    /// written without it. A store that cannot be written leaves the key where it was; a settings file that cannot be written keeps
    /// it in the clear beside the store's copy (the next start, or LINK STAGEWATCH, takes it out). Each says so as the fault
    /// <c>stagewatch-key</c>.</summary>
    private void MoveStagewatchKeyOutOfSettings()
    {
        var plain = _stored.StagewatchKey;
        if (plain.Length == 0)
            return;
        _keyLeftInSettings = true;
        _linkKeys.Set(NodeCredentialStore.StagewatchKey, new Credential("", plain));
        if (_linkKeys.Problem.Length > 0)
        {
            Fault("stagewatch-key", "the Stagewatch key is still in the node's settings file in the clear — it could not be moved into the "
                + "credential store: " + _linkKeys.Problem);
            return;
        }
        if (!Persist(s => s with { StagewatchKey = "" }))
        {
            Fault("stagewatch-key", "the Stagewatch key is in the credential store and still in the node's settings file in the clear — the "
                + "settings file could not be written without it; the next start, or LINK STAGEWATCH, takes it out");
            return;
        }
        _keyLeftInSettings = false;
        Journal("network", "the Stagewatch key moved from the node's settings file into its credential store — the settings file holds no key now");
    }

    /// <summary>The node's own token came in clear from another machine (the wire's and the web port's gates report it): marked for
    /// preflight, and journalled once per address and carrier for this token (a client that sends it with every request writes one
    /// row).</summary>
    internal void OnTokenSeenInClear(ClearSighting seen)
    {
        ArgumentNullException.ThrowIfNull(seen);
        _seenInClear = new TokenSighting(seen, _o.Wall.UnixMs);
        var said = seen.From + " " + seen.Carrier;
        if (_clearSaid.ContainsKey(said) || _clearSaid.Count >= ClearRowsPerToken || !_clearSaid.TryAdd(said, 0))
            return;
        var words = $"this node's pairing token came in clear from {seen.From} ({seen.How}) — anyone on the network between could have read it; "
            + "PAIRING NEW makes a new one";
        _log.LogWarning("The pairing token came in clear from {From} ({How}).", seen.From, seen.How);
        Journal("network", words, ok: false);
    }

    private StagewatchLinkOptions StagewatchOptions(string host, int port, string key)
    {
        // A Stagewatch heard at that address is met as itself (pinned by its id, TLS or nothing when its hello names a certificate,
        // approvals when it is a desk); a typed address no hello names is pinned by the address and tries TLS first.
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
                StartStagewatch(StagewatchOptions(a.Host, a.Port, StagewatchKeyNow()));
            else
                Fault("stagewatch", $"the Stagewatch address '{s.StagewatchHost}' in the node's settings is not an address — LINK STAGEWATCH <host>[:<port>]");
            return;
        }
        if (_hello?.Peers.Live(ShowBusConstants.KindStagewatch) is { Count: > 0 } heard)
            StartStagewatch(StagewatchLinkOptions.FromPeer(heard[0], StagewatchKeyNow(), s.NodeId, s.Name));
    }

    private void StartStagewatch(StagewatchLinkOptions options)
    {
        var engine = _engine ?? throw new InvalidOperationException("the engine is not running yet");
        var link = new StagewatchLink(options, engine, _o.Wall, _o.Pins, _o.Mono, _o.Logs.CreateLogger("PlayMap.Net.Stagewatch"));
        link.StatusChanged += _ => Facts();
        link.FrameReceived += _ => Facts();
        link.ScreenCommand += command => OnScreenCommand(command, options.DisconnectHold);
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

    /// <summary>Stagewatch's operator, from its Screens page, through <see cref="StagewatchScreenCommands"/>: redirect picks the
    /// view this node shows as its timer; each of the four is one journal row naming Stagewatch as who asked (the link redials by
    /// itself after a disconnect). A screen command runs no line on the engine: this node's outputs are on the air, and no screen
    /// command is Stagewatch's to flash them with.</summary>
    private void OnScreenCommand(StagewatchScreenCommand command, TimeSpan disconnectHold)
    {
        if (StagewatchScreenCommands.Plan(command, disconnectHold) is not { } plan)
            return;
        if (plan.View is { } view)
            _stagewatchView = view;
        Journal("network", plan.Row, who: StagewatchScreenCommands.Who);
        Facts();
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

    /// <summary>Each file in turn from the peers that offer it, until one lands verified. Every pull ends in its fact or in a row
    /// that says why not: a pull that fails any other way than the stop's own cancelling still ends failed with its words, so its
    /// transfer never stays on its way and a later fetch of the file is taken; once <paramref name="ct"/> is cancelled, whatever a
    /// pull met ends the fetch as that cancel. The fetch's journal rows are bounded (<see cref="FetchJournal"/>).</summary>
    internal async Task PullAllAsync(List<(MediaItem Item, string Hex)> wanted, List<ShowBusPeer> peers, FetchJournal rows, CancellationToken ct)
    {
        var offers = new Dictionary<string, PeerOffer>(StringComparer.Ordinal);
        foreach (var (item, hex) in wanted)
        {
            string why;
            try
            {
                why = await PullOneAsync(item, hex, peers, offers, rows, ct);
            }
            catch (Exception ex)
            {
                // Once the stop is taken, whatever the pull met is the stop's (a refusal, a reset or a list cut short as well as the
                // cancel): the fetch ends with the node, as its cancel.
                ct.ThrowIfCancellationRequested();
                // Not one of a pull's own failures (those are words already): logged with its trace, and said as the pull's end.
                _log.LogWarning(ex, "The pull of {Name} faulted.", item.Name);
                why = ex.Message;
            }
            if (why.Length > 0)
            {
                _transfers[hex] = new MediaTransferView(hex, item.Id, item.Name, "", 0, item.SizeBytes, "failed: " + why) { SinceUnixMs = _o.Wall.UnixMs };
                rows.Failed($"{item.Name} could not be fetched: {why}");
            }
            Facts();
        }
        rows.End();
    }

    /// <summary>A pull on its way to a peer: its fetch's journal rows, and whether its first failed try there has been said.</summary>
    private sealed class PullWords(FetchJournal rows)
    {
        private int _trySaid;

        public FetchJournal Rows => rows;

        /// <summary>True for the first failed try at this peer only.</summary>
        public bool FirstTry() => Interlocked.Exchange(ref _trySaid, 1) == 0;
    }

    /// <summary>What one peer's list offered a fetch: its files by hash, or the words why the list could not be read (asked once a
    /// fetch, so each later file of the fetch carries them).</summary>
    private sealed record PeerOffer(Dictionary<string, MediaManifestEntry> Files, string Why);

    /// <summary>One file from the first peer that offers it and gives it whole: "" once it landed verified, else why not. A pull at a
    /// later peer keeps the row's bytes until its own start says the chunks the part file keeps.</summary>
    private async Task<string> PullOneAsync(MediaItem item, string hex, List<ShowBusPeer> peers, Dictionary<string, PeerOffer> offers, FetchJournal rows,
        CancellationToken ct)
    {
        var why = "no heard node holds it";
        foreach (var peer in peers)
        {
            if (PeerUri(peer) is not { } uri)
                continue;
            if (!offers.TryGetValue(peer.Id, out var offered))
            {
                Update(hex, t => t with { From = peer.Name, At = uri.Authority, Stage = "asking", SinceUnixMs = _o.Wall.UnixMs });
                Facts();
                var files = new Dictionary<string, MediaManifestEntry>(StringComparer.Ordinal);
                var unread = "";
                try
                {
                    // A list that names a file twice offers its first row.
                    foreach (var entry in await _transfer.ManifestAsync(uri, ct))
                        files.TryAdd(entry.Hash, entry);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or IOException or System.Text.Json.JsonException)
                {
                    // Once the stop is taken, whatever the ask met is the stop's: it ends the fetch as the stop's cancel.
                    ct.ThrowIfCancellationRequested();
                    _log.LogInformation(ex, "{Peer}'s media list could not be read.", peer.Name);
                    unread = $"{peer.Name}'s media list could not be read: {ex.Message}";
                }
                offered = new PeerOffer(files, unread);
                offers[peer.Id] = offered;
            }
            if (offered.Why.Length > 0)
            {
                why = offered.Why;
                continue;
            }
            if (!offered.Files.TryGetValue(hex, out var offer))
                continue;
            var now = _o.Wall.UnixMs;
            Update(hex, t => new MediaTransferView(hex, item.Id, item.Name, peer.Name, t.BytesDone, offer.Size, "pulling")
            {
                At = uri.Authority,
                Stage = "receiving",
                SinceUnixMs = now,
            });
            Facts();
            var destination = _o.Media.StorePath(hex, item.Location.Length > 0 ? item.Location : item.Name);
            MediaPullResult result;
            _pulling[hex] = new PullWords(rows);
            try
            {
                result = await _transfer.PullAsync(uri, hex, offer.Size, destination, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
            {
                // Once the stop is taken, whatever the pull met is the stop's: it ends the fetch as the stop's cancel.
                ct.ThrowIfCancellationRequested();
                why = $"{peer.Name}: {ex.Message}";
                continue;
            }
            finally
            {
                _pulling.TryRemove(hex, out _);
            }
            if (result.Outcome is MediaPullOutcome.Verified or MediaPullOutcome.AlreadyHere)
            {
                _o.Media.AddVerified(hex, destination, Path.GetFileName(item.Location.Length > 0 ? item.Location : item.Name));
                _transfers.TryRemove(hex, out _);
                rows.Landed(string.Create(CultureInfo.InvariantCulture,
                    $"{item.Name} pulled from {peer.Name} and verified ({result.BytesFetched / 1024 / 1024} MiB, {result.ChunksFetched} chunk{(result.ChunksFetched == 1 ? "" : "s")}{(result.ChunksResumed > 0 ? $", {result.ChunksResumed} resumed" : "")})"));
                return "";
            }
            why = $"{peer.Name}: {result.Words}";
        }
        return why;
    }

    /// <summary>A transfer's row changed in place (a stage, its bytes, a try's words), whoever else changes it meanwhile.</summary>
    private void Update(string hex, Func<MediaTransferView, MediaTransferView> change)
    {
        while (_transfers.TryGetValue(hex, out var t) && !_transfers.TryUpdate(hex, change(t), t))
        {
            // Another change came between the read and the write: this one is made again over it.
        }
    }

    private void OnPullProgress(MediaProgress progress)
    {
        var stage = progress.Stage == MediaPullStage.Verifying ? "verifying" : "receiving";
        Update(progress.Hash, t => t with
        {
            BytesDone = progress.BytesDone,
            Size = progress.Size,
            Stage = stage,
            SinceUnixMs = t.Stage == stage ? t.SinceUnixMs : _o.Wall.UnixMs,
        });
        Facts();
    }

    /// <summary>A try at a chunk failed: the row carries its words; the fetch's journal rows take the pull's first failed try at this
    /// peer while the pull tries again, and the tries after it go to the row and the log only (the last try's words are the refused
    /// row's).</summary>
    private void OnPullTrouble(MediaTrouble trouble)
    {
        if (!_transfers.TryGetValue(trouble.Hash, out var t))
            return;
        Update(trouble.Hash, v => v with { Note = trouble.Words });
        if (trouble.Attempt < trouble.Attempts && _pulling.TryGetValue(trouble.Hash, out var words) && words.FirstTry())
            words.Rows.Try(string.Create(CultureInfo.InvariantCulture,
                $"{t.Name} from {t.From}: {trouble.Words} — trying again ({trouble.Attempt + 1} of {trouble.Attempts})"));
        Facts();
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
        return HelloService.PlayMapHello(advertised, _o.Wall.UnixMs, _web is not null ? _o.Tls?.Fingerprint : null, build: _engine?.Services.Build);
    }

    private ShowBusPeer? FindPeer(string idOrName) =>
        (_hello?.Peers.Snapshot() ?? [])
            .Where(p => p.Kind == ShowBusConstants.KindPlayMap)
            .FirstOrDefault(p => p.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
        ?? (_hello?.Peers.Snapshot() ?? [])
            .Where(p => p.Kind == ShowBusConstants.KindPlayMap)
            .FirstOrDefault(p => p.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>Makes a link's change to the running settings and keeps it in the settings file; answers whether the file holds it now
    /// (a write that fails leaves the change running and says so as the fault <c>settings</c>).</summary>
    private bool Persist(Func<NodeSettings, NodeSettings> edit)
    {
        using (EnterSettings())
        {
            _running = edit(_running);
            try
            {
                _stored = edit(Fresh());
                WriteSettingsLocked(_stored);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fault("settings", "the node's settings could not be written — the link holds until the node restarts: " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>The settings as the file holds them now (another part may have kept its own keys since this run began), or the stored
    /// ones when the file is gone. Under the settings lock.</summary>
    private NodeSettings Fresh() => File.Exists(_o.Home.SettingsPath) ? _o.Home.LoadSettings(_stored.Name) : _stored;

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

    private void Journal(string kind, string text, bool ok = true, string? who = null) => _engine?.Journal.Add(kind, text, who, ok: ok);

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
