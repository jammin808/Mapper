using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Devices;
using PlayMap.Engine.Host;
using PlayMap.Media;
using PlayMap.Net;
using PlayMap.Net.Bus;
using PlayMap.Net.Fleet;
using PlayMap.Net.Links;
using PlayMap.Net.Media;
using PlayMap.Net.Tls;

namespace PlayMap.Engine.Fleet;

/// <summary>How the backup part paces itself (tests make it fast).</summary>
public sealed record BackupOptions
{
    /// <summary>How often the part looks at its mirror, its leader and the peers it hears.</summary>
    public TimeSpan WatchEvery { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>How long FLEET BACKUP waits for the primary to take the subscription before it answers.</summary>
    public TimeSpan JoinWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A route step's deadline (a projector that does not answer by then is "unreached").</summary>
    public TimeSpan RouteWait { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>A takeover or a hand-back as STATE's <c>fleet.last</c> and the journal tell it.</summary>
public sealed record TakeoverRecord(string Kind, string Id, long AtUnixMs, string Words);

/// <summary>A backup's mirror of its primary, as STATE's <c>fleet.mirror</c> tells it.</summary>
public sealed record MirrorView(string Of, string Name, string State, long Rev, long BehindRev, string Words);

/// <summary>The hot backup (wp2-backup, fleet's continuation), network and runtime side:
/// <list type="bullet">
/// <item>on a primary, the mirror feed (<c>SUBSCRIBE MIRROR</c>, paired only): the show's revision, every playlist's anchor and the air
/// lines the sync group's feed does not carry, each with its leader instant; the show and its files are pulled from <c>/mirror</c>
/// with the pairing proof over HTTPS pinned by the hello;</item>
/// <item>on a backup, the link to its primary: the show pulled when its revision moves and applied as one mirror edit, the
/// primary's sync group followed (its clock, its group's anchors and air), every other playlist's anchor and air line taken through
/// this node's own <c>MIRROR</c> verb (session "fleet"), the files and optimised versions the show names pulled by hash;</item>
/// <item>the takeover: <c>FAILOVER</c> commits the next epoch (kept in the node's settings before it serves), freezes the clock's
/// mapping into its own line (<c>NetworkClock.Promote</c>), leads the group, switches the routes; a node that hears a higher epoch
/// for the show it held fences itself (outputs held black, edits refused, its clock following the new primary); <c>FAILBACK</c>
/// hands the show back and the two swap roles; <c>FLEET PROMOTE</c> makes a follower its group's leader on the same time line, and
/// a follower whose leader went quiet takes its clock from the peer that leads the group under a higher epoch.</item>
/// </list>
/// The engine's lane does what changes the show; this part moves facts and lines beside it.</summary>
public sealed class BackupNode : IAsyncDisposable
{
    private readonly ExtensionSetup _setup;
    private readonly BackupOptions _o;
    private readonly ILogger _log;
    private readonly Func<PinStore> _pins;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly Lock _settingsGate = new();
    private readonly Lock _dueGate = new();
    private readonly Queue<(long AtLeaderNs, WireLine Line)> _due = new();
    private readonly Lock _airGate = new();
    private readonly HashSet<(string Feed, long Seq)> _airDone = [];
    private readonly Queue<(string Feed, long Seq)> _airOrder = new();
    private readonly ConcurrentDictionary<string, long> _acted = new(StringComparer.Ordinal);
    private readonly List<Task> _work = [];
    private volatile FleetSettings _settings;
    private volatile EngineCore? _engine;
#pragma warning disable CA2213 // Closed by StopMirrorAsync, which a role change and DisposeAsync both call.
    private volatile MirrorLink? _link;
    private volatile MirrorPuller? _puller;
#pragma warning restore CA2213
    private volatile Channel<string>? _lines;
    private volatile string _primaryId = "";
    private volatile string _primaryName = "";
    private volatile string _primaryAddress = "";
    private volatile string _group = "";
    private long _primaryEpoch;
    private long _announcedRev = -1;
    private long _appliedRev = -1;
    private volatile bool _pulling;
    private volatile string _mirrorWords = "";
    private volatile bool _subscribed;
    private long _estimatesBefore = long.MaxValue;
    private volatile TakeoverRecord? _last;
    private volatile string _handingBackTo = "";
    private long _lastPublishedRev = long.MinValue;
    private int _followTurns;
    private int _projectTurns;
    private CancellationTokenSource? _joining;
    private readonly SemaphoreSlim _pullGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _groupPeers = new(StringComparer.Ordinal);
    private volatile string _groupOf = "";
    private int _missingFiles;
    private int _disposed;

    /// <summary>Air lines remembered by (feed, seq) so a line handed again on a new subscription is not run twice.</summary>
    private const int AirRemembered = 1024;

    public BackupNode(ExtensionSetup setup, Func<PinStore> pins, BackupOptions? options = null)
    {
        _setup = setup ?? throw new ArgumentNullException(nameof(setup));
        _pins = pins ?? throw new ArgumentNullException(nameof(pins));
        _o = options ?? new BackupOptions();
        _settings = setup.Settings.Fleet;
        _log = setup.Logs?.CreateLogger("PlayMap.Engine.Fleet") ?? NullLogger.Instance;
        Gains = new FenceGains(null) { Held = _settings.Role == FleetRoles.Fenced };
        Feed.MembersChanged += OnMembersChanged;
    }

    /// <summary>The primary's side of the mirror.</summary>
    public MirrorFeed Feed { get; } = new();

    /// <summary>The pulls this node serves to its backups (<c>/mirror</c>).</summary>
    public MirrorWebModule? Web { get; internal set; }

    /// <summary>The gain a fenced node's outputs are held at (0) — every other node's outputs pass (1).</summary>
    public FenceGains Gains { get; }

    /// <summary>The role, the primary and the epoch as this node keeps them.</summary>
    public FleetSettings Settings => _settings;

    public string Role => _settings.Role;

    public long Epoch => _settings.Epoch;

    /// <summary>The last takeover, hand-back or promotion here (null before any).</summary>
    public TakeoverRecord? Last => _last;

    private NetworkClock Clock => _setup.Network.Clock;

    private string SelfId => _setup.Network.Settings.NodeId;

    private string SelfName => _setup.Network.Settings.Name;

    /// <summary>Attaches to the running engine: the watch starts, and a backup role kept from before a restart is entered again
    /// once its primary is heard.</summary>
    public void Attach(EngineCore engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Feed.Publish("hello", HelloLine());
        Background(WatchAsync(_stop.Token));
        if (_settings.Role == FleetRoles.Backup && _settings.Of.Length > 0)
            Background(RestoreAsync(_stop.Token));
    }

    // ---- The primary's feed -----------------------------------------------------------------------------------------------

    /// <summary>SUBSCRIBE MIRROR &lt;node&gt;: a backup joins the feed. Paired only — the feed is the whole show's motion — and only on a
    /// node that leads a sync group (the backup follows its clock there); a node that is itself a backup or fenced has no show of
    /// its own to mirror.</summary>
    public PushOpen OpenMirror(IReadOnlyList<string> args, SessionInfo session, IPushSink sink)
    {
        if (!session.Paired)
            return PushOpen.Refuse("SUBSCRIBE MIRROR carries the whole show: pair with this node's token first");
        if (args.Count != 1 || !IsIdWord(args[0]))
            return PushOpen.Refuse("SUBSCRIBE MIRROR <node>");
        if (_settings.Role is FleetRoles.Backup or FleetRoles.Fenced)
            return PushOpen.Refuse($"{SelfName} is {(_settings.Role == FleetRoles.Backup ? "itself a backup" : "fenced")}: it has no show of its own to mirror");
        var sync = _engine?.Services.GroupSync;
        if (sync is not { Leads: true } || sync.Settings.Group.Length == 0)
            return PushOpen.Refuse($"{SelfName} leads no sync group — SYNC LEAD <group> here first, so a backup can follow its clock");
        var member = new MirrorMember(session.SessionId, args[0], session.ClientName, session.RemoteAddress);
        Feed.Publish("hello", HelloLine());
        PublishProject(force: true);
        return PushOpen.Open(Feed.Subscribe(member, sink));
    }

    private void OnMembersChanged()
    {
        if (_engine is not { } engine)
            return;
        // A node a backup joined is the show's primary, kept for the next start, with the backups it has had: only they may fence it.
        var members = Feed.Members.Select(m => m.Node).ToList();
        if (members.Count > 0 && (_settings.Role == FleetRoles.Solo || members.Any(n => !_settings.Backups.Contains(n))))
        {
            Persist(s => s with
            {
                Role = s.Role == FleetRoles.Solo ? FleetRoles.Primary : s.Role,
                Of = s.Role == FleetRoles.Solo ? "" : s.Of,
                Backups = [.. s.Backups.Union(members, StringComparer.Ordinal)],
            });
        }
        engine.FactsChanged();
    }

    private string HelloLine()
    {
        var group = _engine?.Services.GroupSync?.Settings.Group ?? "";
        return "MIRROR HELLO " + EngineCore.JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("proto", 1);
            w.WriteString("node", SelfId);
            w.WriteString("name", SelfName);
            w.WriteNumber("epoch", _settings.Epoch);
            w.WriteString("group", group);
            w.WriteString("feed", Feed.Instance);
            w.WriteEndObject();
        });
    }

    /// <summary>The show's revision to the feed when it moved (the backup pulls the show itself).</summary>
    public void PublishProject(bool force = false)
    {
        if (_engine is not { } engine)
            return;
        var rev = engine.Store.Current.Revision;
        if (Interlocked.Exchange(ref _lastPublishedRev, rev) == rev && !force)
            return;
        Feed.Publish("project", "MIRROR PROJECT " + string.Create(CultureInfo.InvariantCulture, $"{{\"rev\":{rev}}}"));
    }

    /// <summary>A playlist's transport moved here: its anchor to the feed (every playlist, in a group or not).</summary>
    public void PublishAnchor(string playlistId, string anchorJson) =>
        Feed.Publish("anchor:" + playlistId, $"MIRROR ANCHOR {playlistId} {anchorJson}");

    /// <summary>One of this node's own air lines ran at <paramref name="leaderNs"/>: to the feed, unless the sync group's fan-out
    /// carries it to the backup already (then the feed's latest of that kind is the group's, and the mirror's is dropped).</summary>
    public void PublishAir(string line, long leaderNs, bool groupCarries)
    {
        if (!WireGrammar.TryParse(line, out var parsed, out _) || MirrorAir.KindOf(parsed) is not { } kind)
            return;
        if (groupCarries)
        {
            Feed.Drop("air:" + kind);
            return;
        }
        Feed.PublishAir(kind, seq => "MIRROR AIR " + EngineCore.JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("feed", Feed.Instance);
            w.WriteNumber("seq", seq);
            w.WriteString("line", line);
            w.WriteNumber("leaderNs", leaderNs);
            w.WriteEndObject();
        }));
    }

    // ---- The verbs --------------------------------------------------------------------------------------------------------

    /// <summary>FLEET BACKUP &lt;primary&gt; [HOT]: this node becomes a hot backup of a primary it hears; kept for the next start.</summary>
    public async Task<CommandReply> BackupAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count is < 1 or > 2 || (args.Count == 2 && !args[1].Equals("HOT", StringComparison.OrdinalIgnoreCase)))
            return CommandReply.Fail("FLEET BACKUP <primary node> [HOT]");
        await _changes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var peer = FindPeer(args[0]);
            if (peer is null)
                return CommandReply.Fail($"no PlayMap node '{args[0]}' is heard on the show network — NODES lists the ones that are");
            if (peer.Id == SelfId)
                return CommandReply.Fail("a node cannot back itself up — FLEET BACKUP <primary> runs on the machine that stands by");
            if (_settings.Role == FleetRoles.Backup && _settings.Of == peer.Id && _link is not null && _subscribed)
                return CommandReply.Fail($"already a hot backup of {peer.Name} ({peer.Id})");
            // A primary's backups go first; the node it hands its show back to (which mirrored it until now) is not one of them.
            var others = Feed.Members.Count(m => m.Node != peer.Id);
            if (_settings.Role == FleetRoles.Primary && others > 0)
                return CommandReply.Fail($"{SelfName} is a primary with {others} backup{(others == 1 ? "" : "s")} — FLEET SOLO on them first");
            if (peer.Liveness != PeerLiveness.Live)
                return CommandReply.Fail($"{peer.Name} has gone quiet on the show network — it can be backed up once it is heard again");
            var (joined, words) = await JoinAsync(peer, ct).ConfigureAwait(false);
            if (!joined)
                return CommandReply.Fail(words);
            var kept = Persist(s => s with { Role = FleetRoles.Backup, Of = peer.Id });
            Gains.Held = false;
            Feed.Forget();
            _engine?.FactsChanged();
            return CommandReply.Accepted($"joining {peer.Name} ({peer.Id}) as a hot backup: its show, clock, transports and air mirrored here{words}{kept}");
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <summary>FLEET SOLO: this node stops backing up and plays on alone, kept for the next start.</summary>
    public async Task<CommandReply> SoloAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count != 0)
            return CommandReply.Fail("FLEET SOLO takes no words after it");
        await _changes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var was = _settings;
            if (was.Role == FleetRoles.Solo)
                return CommandReply.Fail($"{SelfName} is solo already — it backs up no primary");
            if (was.Role == FleetRoles.Primary && Feed.Members.Count > 0)
                return CommandReply.Fail($"{SelfName} has {Feed.Members.Count} backup{(Feed.Members.Count == 1 ? "" : "s")} mirroring it — FLEET SOLO on them first");
            await StopMirrorAsync().ConfigureAwait(false);
            var kept = Persist(s => s with { Role = FleetRoles.Solo, Of = "" });
            Gains.Held = false;
            _engine?.FactsChanged();
            var words = was.Role switch
            {
                FleetRoles.Backup => $"{SelfName} no longer backs up {Label(was.Of)}: it plays the show it has, alone",
                FleetRoles.Fenced => $"{SelfName} is no longer fenced: its outputs show its own show again",
                _ => $"{SelfName} is no longer a primary",
            };
            return CommandReply.Success(effect: words + kept);
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <summary>FAILOVER [&lt;primary&gt;]: this backup takes the show from its primary now.</summary>
    public async Task<CommandReply> FailoverAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count > 1)
            return CommandReply.Fail("FAILOVER [<primary node>]");
        if (_settings.Role != FleetRoles.Backup || _settings.Of.Length == 0)
            return CommandReply.Fail(_settings.Role == FleetRoles.Fenced
                ? $"{SelfName} is fenced: {Label(_settings.Of)} has the show — FLEET BACKUP {Label(_settings.Of)} to mirror it"
                : $"{SelfName} backs up no primary — FAILOVER runs on a hot backup (FLEET BACKUP <primary> first)");
        if (args.Count == 1 && FindPeer(args[0])?.Id != _settings.Of && !args[0].Equals(_settings.Of, StringComparison.OrdinalIgnoreCase))
            return CommandReply.Fail($"{SelfName} backs up {Label(_settings.Of)}, not {args[0]}");
        await CancelJoinAsync().ConfigureAwait(false);
        return await TakeOverAsync("pressed", ct).ConfigureAwait(false);
    }

    /// <summary>FAILBACK CONFIRM: the show goes back to the node this one took it from, once that node mirrors this one; the two swap
    /// roles (this node becomes its backup).</summary>
    public CommandReply Failback(IReadOnlyList<string> args)
    {
        if (args.Count != 1 || !args[0].Equals("CONFIRM", StringComparison.OrdinalIgnoreCase))
            return CommandReply.Fail("FAILBACK CONFIRM — a person's press only: it moves the show back to the original primary");
        if (_settings.Role != FleetRoles.Primary || _settings.Of.Length == 0)
            return CommandReply.Fail($"{SelfName} took the show from no node — FAILBACK runs on the node that took over, to hand it back");
        var original = _settings.Of;
        if (!Feed.Members.Any(m => m.Node == original))
            return CommandReply.Fail($"{Label(original)} does not mirror {SelfName} — FLEET BACKUP {SelfName} on {Label(original)} first, then FAILBACK");
        var health = Peer(original)?.Health?.Facts;
        if (health is not { Role: FleetRoles.Backup } || health.Of != SelfId || health.Mirror != "identical")
            return CommandReply.Fail($"{Label(original)} is not identical to {SelfName} yet ({(health is null ? "its health is not heard" : $"its mirror is {(health.Mirror.Length > 0 ? health.Mirror : "not running")}")}) — FAILBACK waits for it");
        _handingBackTo = original;
        var line = "MIRROR HANDBACK " + EngineCore.JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("from", SelfId);
            w.WriteNumber("epoch", _settings.Epoch);
            w.WriteEndObject();
        });
        if (!Feed.SendTo(original, line))
        {
            _handingBackTo = "";
            return CommandReply.Fail($"{Label(original)}'s mirror closed before the hand-back could reach it");
        }
        _engine?.Journal.Add("fleet", $"handing the show back to {Label(original)}: it takes it under the next epoch, and {SelfName} mirrors it");
        return CommandReply.Accepted($"handing the show back to {Label(original)}: it takes the show and {SelfName} becomes its hot backup");
    }

    /// <summary>FLEET FENCE &lt;epoch&gt; &lt;node&gt; (node to node only): a peer took the show under a higher epoch — this node's outputs are
    /// held black, its edits refused, and its clock follows the new primary; kept for the next start.</summary>
    public async Task<CommandReply> FenceAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count != 2 || !long.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) || !IsIdWord(args[1]))
            return CommandReply.Fail("FLEET FENCE <epoch> <node>");
        await _changes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (epoch <= _settings.Epoch)
                return CommandReply.Fail(string.Create(CultureInfo.InvariantCulture, $"epoch {epoch} is not above this node's ({_settings.Epoch}): nothing to fence"));
            if (_settings.Role is FleetRoles.Fenced or FleetRoles.Backup)
                return CommandReply.Fail($"{SelfName} does not hold the show (it is {_settings.Role})");
            var by = args[1];
            var kept = Persist(s => s with { Role = FleetRoles.Fenced, Of = by, Epoch = epoch });
            Gains.Held = true;
            Feed.Forget();
            var at = _setup.Wall.UnixMs;
            var words = string.Create(CultureInfo.InvariantCulture, $"fenced: {Label(by)} has the show since {Clock24(at)} (epoch {epoch}) — outputs held black; FLEET BACKUP {Label(by)} to mirror it");
            _engine?.Journal.Add("fleet", words + kept, ok: false);
            _engine?.RaiseEvent("fleet.fenced", _engine.LeaderNowNs, w =>
            {
                w.WriteString("by", by);
                w.WriteNumber("epoch", epoch);
            });
            // The group's clock is the new primary's now: this node stops leading it, so its followers find the new leader.
            var group = _engine?.Services.GroupSync?.Settings.Group ?? "";
            if (group.Length > 0 && Peer(by) is not null && _engine is { } engine && await LeadsHeardAsync(by, ct).ConfigureAwait(false))
                await RunAsync(engine, $"SYNC FOLLOW {group} {by}", _stop.Token).ConfigureAwait(false);
            _engine?.FactsChanged();
            return CommandReply.Success(effect: words + kept);
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <summary>FLEET PROMOTE [&lt;group&gt;]: this follower leads its group now, its show clock continuing its leader's time line with no
    /// step, under the next epoch (so the group's other followers take their clock from it).</summary>
    public async Task<CommandReply> PromoteAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (_engine is not { } engine || engine.Services.GroupSync is not { } sync)
            return CommandReply.Fail("no sync part on this node: there is no group to lead");
        if (args.Count > 1)
            return CommandReply.Fail("FLEET PROMOTE [<group>]");
        var group = sync.Settings.Group;
        if (sync.Role != "follower" || group.Length == 0)
            return CommandReply.Fail(sync.Role == "leader" ? $"{SelfName} leads {group} already" : $"{SelfName} follows no group — FLEET PROMOTE runs on a follower");
        if (args.Count == 1 && !string.Equals(args[0], group, StringComparison.Ordinal))
            return CommandReply.Fail($"{SelfName} follows {group}, not {args[0]}");
        if (_settings.Role == FleetRoles.Backup)
            return CommandReply.Fail($"{SelfName} is a hot backup of {Label(_settings.Of)} — FAILOVER takes its show and its group");
        await _changes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var before = Clock.NowNs;
            var epoch = Takeover.NextEpoch(_settings.Epoch, HeardEpochs());
            var kept = Persist(s => s with { Epoch = epoch });
            Clock.Promote();
            var lead = await RunAsync(engine, "SYNC LEAD " + group, _stop.Token).ConfigureAwait(false);
            if (!lead.Ok)
                return CommandReply.Fail($"the clock is this node's own now, but it could not lead {group}: {lead.Payload}");
            var after = Clock.NowNs;
            var words = string.Create(CultureInfo.InvariantCulture, $"leading {group} under epoch {epoch}; the show clock continues the old leader's line ({(after - before) / 1e6:0.###} ms read across)");
            _last = new TakeoverRecord("promote", NewId(), _setup.Wall.UnixMs, words);
            engine.Journal.Add("fleet", words + kept);
            engine.RaiseEvent("sync.promoted", engine.LeaderNowNs, w =>
            {
                w.WriteString("group", group);
                w.WriteNumber("epoch", epoch);
            });
            engine.FactsChanged();
            return CommandReply.Success(effect: words + kept);
        }
        finally
        {
            _changes.Release();
        }
    }

    // ---- The takeover -----------------------------------------------------------------------------------------------------

    /// <summary>This backup takes its primary's show: the epoch committed and kept first, the clock's line made its own, the group led,
    /// the routes switched (each with its own receipt), the trail journalled. The old primary hears the epoch and fences itself.</summary>
    private async Task<CommandReply> TakeOverAsync(string why, CancellationToken ct)
    {
        if (_engine is not { } engine)
            return CommandReply.Fail("the engine is not running yet");
        await _changes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_settings.Role != FleetRoles.Backup)
                return CommandReply.Fail($"{SelfName} is not a backup any more ({_settings.Role})");
            var started = Clock.Local.NowNs;
            var id = NewId();
            var from = _settings.Of;
            var trail = new List<string>();
            var epoch = Takeover.NextEpoch(_settings.Epoch, [.. HeardEpochs(), Interlocked.Read(ref _primaryEpoch)]);
            var kept = Persist(s => s with { Role = FleetRoles.Primary, Of = from, Epoch = epoch });
            trail.Add(string.Create(CultureInfo.InvariantCulture, $"epoch {epoch} committed{kept}"));
            if (kept.Length > 0)
                engine.Journal.Add("fault", $"the takeover's epoch {epoch} is not kept for the next start{kept}: after a restart this node would not know it took the show", ok: false);
            var group = _group.Length > 0 ? _group : engine.Services.GroupSync?.Settings.Group ?? "";
            // The primary's air lines still waiting for their instants are the show's: they land on the lane's tick as planned.
            await StopMirrorAsync(keepDue: true).ConfigureAwait(false);
            var before = Clock.NowNs;
            Clock.Promote();
            if (group.Length > 0)
            {
                var lead = await RunAsync(engine, "SYNC LEAD " + group, _stop.Token).ConfigureAwait(false);
                trail.Add(lead.Ok ? $"leading {group} on the same time line" : $"could not lead {group}: {lead.Payload}");
            }
            trail.Add(string.Create(CultureInfo.InvariantCulture, $"show clock continuous ({(Clock.NowNs - before) / 1e6:0.###} ms read across the change)"));
            trail.AddRange(await RunRoutesAsync(engine, ct).ConfigureAwait(false));
            Gains.Held = false;
            Feed.Forget();
            Feed.Publish("hello", HelloLine());
            PublishProject(force: true);
            engine.PublishMirrorAnchors(this);
            trail.Add("serving");
            var ms = (Clock.Local.NowNs - started) / 1e6;
            var words = string.Create(CultureInfo.InvariantCulture, $"takeover {id} of {Label(from)} by {SelfName} ({why}) — {string.Join(" → ", trail)} → complete in {ms:0.#} ms");
            _last = new TakeoverRecord("takeover", id, _setup.Wall.UnixMs, words);
            engine.Journal.Add("fleet", words);
            engine.RaiseEvent("fleet.takeover", engine.LeaderNowNs, w =>
            {
                w.WriteString("id", id);
                w.WriteString("from", from);
                w.WriteNumber("epoch", epoch);
                w.WriteString("words", words);
            });
            engine.FactsChanged();
            return CommandReply.Success(EngineCore.JsonOut(w =>
            {
                w.WriteStartObject();
                w.WriteString("id", id);
                w.WriteNumber("epoch", epoch);
                w.WriteString("words", words);
                w.WriteEndObject();
            }), words);
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <summary>Every route of the show to this node's input: <c>DEVICE INPUT</c> through this node's own verb, each step's receipt in
    /// words. A route that does not answer is said and the takeover goes on (the operator switches by hand).</summary>
    private async Task<IReadOnlyList<string>> RunRoutesAsync(EngineCore engine, CancellationToken ct)
    {
        var routes = engine.Store.Project.Fleet.Routes;
        if (routes.IsDefaultOrEmpty)
            return ["no routes to switch"];
        var steps = routes.Select(async r =>
        {
            var output = engine.Store.Project.FindOutput(r.OutputId)?.Name ?? r.OutputId;
            if (r.DeviceId.Length == 0)
                return $"{output}: no route (switch by hand)";
            var device = engine.Store.Project.FindDevice(r.DeviceId)?.Name ?? r.DeviceId;
            var input = r.InputFor(SelfId);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_o.RouteWait);
            CommandReply reply;
            try
            {
                reply = await RunAsync(engine, $"DEVICE INPUT {r.DeviceId} {input}", deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                reply = CommandReply.Fail("no answer within " + _o.RouteWait.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s");
            }
            return reply.Ok
                ? $"{device} {input} {StageOf(reply)}"
                : $"{device} {input} unreached ({reply.Payload}) — switch it by hand";
        });
        return await Task.WhenAll(steps).ConfigureAwait(false);
    }

    private async Task CancelJoinAsync()
    {
        if (Volatile.Read(ref _joining) is { } joining)
        {
            try
            {
                await joining.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException ex)
            {
                // The join ended on its own meanwhile.
                _log.LogDebug(ex, "The join ended before it could be given up.");
            }
        }
    }

    /// <summary>"observed" when the projector read its new input back, else "accepted".</summary>
    internal static string StageOf(CommandReply reply)
    {
        if (reply.Payload is { Length: > 0 } p && p[0] == '{')
        {
            try
            {
                using var doc = JsonDocument.Parse(p);
                if (doc.RootElement.TryGetProperty("stage", out var s) && s.GetString() is { Length: > 0 } stage)
                    return stage;
            }
            catch (JsonException)
            {
                return reply.Applied ? "applied" : "accepted";
            }
        }
        return reply.Applied ? "applied" : "accepted";
    }

    // ---- The backup's link ------------------------------------------------------------------------------------------------

    /// <summary>Opens the mirror link to a primary and waits (bounded) for its answer to the subscription: joined, or the words of its
    /// refusal (the link is closed again then).</summary>
    private async Task<(bool Joined, string Words)> JoinAsync(ShowBusPeer peer, CancellationToken ct)
    {
        await StopMirrorAsync().ConfigureAwait(false);
        var wire = peer.Hello.Port("wire");
        var http = peer.Hello.Port("http");
        if (wire <= 0 || http <= 0)
            return (false, $"{peer.Name}'s hello names no {(wire <= 0 ? "wire" : "web")} port: its show cannot be mirrored");
        var token = Credential(peer.Id);
        _primaryId = peer.Id;
        _primaryName = peer.Name;
        _primaryAddress = peer.Address;
        Interlocked.Exchange(ref _announcedRev, -1);
        Interlocked.Exchange(ref _appliedRev, -1);
        Interlocked.Exchange(ref _estimatesBefore, long.MaxValue);
        _subscribed = false;
        _mirrorWords = $"joining {peer.Name}";
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = new MirrorLink(peer.Address, wire, SelfName, token, SelfId, _setup.Wall, _log);
        var lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        link.Line += l => lines.Writer.TryWrite(l);
        link.Subscribed += () =>
        {
            _subscribed = true;
            answer.TrySetResult("");
            _engine?.FactsChanged();
        };
        link.Refused += words =>
        {
            _subscribed = false;
            _mirrorWords = $"{peer.Name} refused the mirror: {words}";
            answer.TrySetResult(words);
            _engine?.FactsChanged();
        };
        link.StatusChanged += _ =>
        {
            if (link.Status != LinkStatus.Live)
                _subscribed = false;
            _engine?.FactsChanged();
        };
        var puller = new MirrorPuller(new MirrorSource(peer.Id, peer.Name, peer.Address, http, peer.Hello.TlsFingerprint ?? "", token), _pins(),
            () => _setup.Wall.UnixMs, _setup.Mono, _log);
        if (puller.Refusal.Length > 0)
        {
            puller.Dispose();
            await link.DisposeAsync().ConfigureAwait(false);
            return (false, puller.Refusal);
        }
        _puller = puller;
        _lines = lines;
        _link = link;
        Background(TakeLinesAsync(lines, _stop.Token));
        link.Start();
        string refused;
        // A pressed FAILOVER does not wait behind a join: it cancels it.
        using var joining = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        Volatile.Write(ref _joining, joining);
        try
        {
            refused = await answer.Task.WaitAsync(_o.JoinWait, joining.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            refused = $"{peer.Name} did not answer the mirror's subscription within {_o.JoinWait.TotalSeconds:0} s";
        }
        catch (OperationCanceledException)
        {
            refused = "the join was given up (a takeover was pressed, or the node stops)";
        }
        finally
        {
            Interlocked.CompareExchange(ref _joining, null, joining);
        }
        if (refused.Length == 0)
            return (true, token.Length > 0 ? " (paired by proof; pulls over " + (puller.Tls ? "HTTPS, pinned" : "plain HTTP on this machine") + ")" : "");
        await StopMirrorAsync().ConfigureAwait(false);
        var hint = link.Pairing is PairState.NeedsToken && token.Length == 0
            ? $" — CREDENTIAL SET node.{peer.Id} <{peer.Name}'s pairing token> here first"
            : "";
        return (false, $"{peer.Name} refused the mirror: {refused}{hint}");
    }

    /// <summary>The mirror's lines one at a time, in the order they came: the show first; an anchor or an air line only once this
    /// node's clock reads the primary's time line (the group followed, an estimate heard since, no step waiting).</summary>
    private async Task TakeLinesAsync(Channel<string> lines, CancellationToken ct)
    {
        await foreach (var text in lines.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (!ReferenceEquals(_lines, lines) || _engine is not { } engine)
                return;
            if (!WireGrammar.TryParse(text, out var line, out _))
                continue;
            var word = line.WordUpper(1);
            if (word is "ANCHOR" or "AIR")
            {
                while (!OnPrimaryLine())
                {
                    if (!ReferenceEquals(_lines, lines) || !await Waits.DelayAsync(_o.WatchEvery / 5, ct).ConfigureAwait(false))
                        return;
                }
            }
            try
            {
                var reply = await engine.HandleAsync(line, FleetSession, ct).ConfigureAwait(false);
                if (!reply.Ok)
                    _log.LogInformation("A mirror line was refused here: {Why}", reply.Payload);
                if (word == "PROJECT" && reply.Ok)
                    await FollowGroupAsync(engine).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // One line that faults is that line's; the mirror goes on with the next.
                _log.LogWarning(ex, "A mirror line faulted: {Word}", word);
            }
        }
    }

    /// <summary>True once this node's clock reads its primary's time line.</summary>
    private bool OnPrimaryLine() =>
        Interlocked.Read(ref _appliedRev) >= 0 && Clock.IsFollowing && !Clock.StepPending && Clock.Estimates > Interlocked.Read(ref _estimatesBefore);

    /// <summary>Follows the primary's sync group (its clock, its group's anchors and air) once its show is here, through this node's own
    /// SYNC FOLLOW.</summary>
    private async Task FollowGroupAsync(EngineCore engine)
    {
        if (engine.Services.GroupSync is not { } sync || _group.Length == 0)
            return;
        var of = _primaryId;
        if (sync.Role == "follower" && sync.Settings.Leader == of && sync.Settings.Group == _group && Interlocked.Read(ref _estimatesBefore) != long.MaxValue)
            return;
        // A primary that has just begun to lead names its clock port in its next hello: following before that would ask nothing.
        if (!await LeadsHeardAsync(of, _stop.Token).ConfigureAwait(false))
        {
            _mirrorWords = $"{_primaryName}'s hello names no clock port: it does not lead {_group} now";
            return;
        }
        Interlocked.Exchange(ref _estimatesBefore, Clock.Estimates);
        var reply = await RunAsync(engine, $"SYNC FOLLOW {_group} {of}", _stop.Token).ConfigureAwait(false);
        if (!reply.Ok && !(reply.Payload ?? "").StartsWith("already following", StringComparison.Ordinal))
        {
            _mirrorWords = $"could not follow {_primaryName}'s clock: {reply.Payload}";
            Interlocked.Exchange(ref _estimatesBefore, long.MaxValue);
        }
        else if (!reply.Ok)
        {
            Interlocked.Exchange(ref _estimatesBefore, -1);
        }
    }

    // ---- MIRROR, on the backup (session "fleet") --------------------------------------------------------------------------

    /// <summary>MIRROR HELLO|PROJECT|ANCHOR|AIR|HANDBACK …: a line of this node's primary, from its own mirror link only.</summary>
    public async Task<CommandReply> MirrorAsync(VerbCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (_engine is not { } engine)
            return CommandReply.Fail("the engine is not running yet");
        if (_link is null || !call.Session.SessionId.StartsWith("fleet:", StringComparison.Ordinal))
            return CommandReply.Fail("MIRROR lines come only from this node's own mirror link, on a hot backup");
        var word = call.Args.Count > 0 ? call.Args[0].ToUpperInvariant() : "";
        switch (word)
        {
            case "HELLO":
                return Hello(call.Line.RestAfter(2));
            case "PROJECT":
                return await ProjectAsync(engine, call.Line.RestAfter(2), call.Ct).ConfigureAwait(false);
            case "ANCHOR" when call.Args.Count >= 3:
                return await engine.AdoptMirrorAnchorAsync(call.Args[1], call.Line.RestAfter(3)).ConfigureAwait(false);
            case "AIR":
                return Air(call.Line.RestAfter(2));
            case "HANDBACK":
                return await HandbackAsync(call.Line.RestAfter(2), call.Ct).ConfigureAwait(false);
            default:
                return CommandReply.Fail("MIRROR HELLO|PROJECT|ANCHOR|AIR|HANDBACK <json>");
        }
    }

    private CommandReply Hello(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            _group = r.TryGetProperty("group", out var g) ? g.GetString() ?? "" : "";
            if (r.TryGetProperty("epoch", out var e) && e.TryGetInt64(out var epoch))
                Interlocked.Exchange(ref _primaryEpoch, epoch);
            if (r.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                _primaryName = name;
            return CommandReply.NoReply();
        }
        catch (JsonException ex)
        {
            return CommandReply.Fail("the mirror's hello is not one this node reads: " + ex.Message.Split('.')[0]);
        }
    }

    /// <summary>The primary's show moved: pulled (with proof, over pinned HTTPS) and applied as one mirror edit; then the files and
    /// versions it names that are not here are pulled beside the mirror.</summary>
    private async Task<CommandReply> ProjectAsync(EngineCore engine, string json, CancellationToken ct)
    {
        long rev;
        try
        {
            using var doc = JsonDocument.Parse(json);
            rev = doc.RootElement.GetProperty("rev").GetInt64();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return CommandReply.Fail("MIRROR PROJECT {\"rev\":<revision>}");
        }
        Interlocked.Exchange(ref _announcedRev, rev);
        if (rev == Interlocked.Read(ref _appliedRev))
            return CommandReply.NoReply();
        if (_puller is not { } puller)
            return CommandReply.Fail("the mirror's pull is closed");
        _pulling = true;
        engine.FactsChanged();
        try
        {
            var (servedRev, body) = await puller.ProjectAsync(ct).ConfigureAwait(false);
            var project = ProjectJson.Deserialize(body);
            var label = string.Create(CultureInfo.InvariantCulture, $"mirrored {_primaryName} rev {servedRev}");
            var result = await engine.MirrorEditAsync(label, _ => project).ConfigureAwait(false);
            if (!result.Changed && !result.Effect.EndsWith("nothing to change", StringComparison.Ordinal))
                return CommandReply.Fail($"{_primaryName}'s show (rev {servedRev}) could not be applied here: {result.Effect}");
            Interlocked.Exchange(ref _appliedRev, Math.Max(servedRev, rev));
            _mirrorWords = string.Create(CultureInfo.InvariantCulture, $"{_primaryName}'s show at rev {Math.Max(servedRev, rev)}");
            Background(PullFilesAsync(engine, _stop.Token));
            return CommandReply.Success(effect: label);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // A pull that fails is retried from the watch while the primary's revision is ahead of this node's.
            var why = puller.PinWords.Length > 0 ? puller.PinWords : ex.Message;
            _mirrorWords = $"could not pull {_primaryName}'s show: {why}";
            return CommandReply.Fail(_mirrorWords);
        }
        finally
        {
            _pulling = false;
            engine.FactsChanged();
        }
    }

    /// <summary>Every file the show names (media and their optimised versions, by hash) that this node does not hold, pulled from the
    /// primary one at a time and verified before it lands.</summary>
    private async Task PullFilesAsync(EngineCore engine, CancellationToken ct)
    {
        // One pull run at a time, each from the show as it is when it starts: two revisions never pull one file into one place at once.
        await _pullGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_puller is not { } puller || _setup.Media is not MediaBackend store)
                return;
            var project = engine.Store.Project;
            var wanted = project.Media.Where(m => m.IsFile && ContentHasher.IsValid(m.ContentHash))
                .Select(m => (Hash: m.ContentHash, Size: m.SizeBytes, Name: m.Location.Length > 0 ? m.Location : m.Name))
                .Concat(project.Media.SelectMany(m => m.Versions).Where(v => ContentHasher.IsValid(v.ContentHash))
                    .Select(v => (Hash: v.ContentHash, Size: v.SizeBytes, Name: v.Id + "." + v.To)))
                .Where(f => store.PathForHash(f.Hash) is null)
                .DistinctBy(f => f.Hash)
                .ToList();
            Interlocked.Exchange(ref _missingFiles, wanted.Count);
            foreach (var f in wanted)
            {
                var (ok, words) = await PullOneAsync(puller, _primaryName, store, f.Hash, f.Size, f.Name, ct).ConfigureAwait(false);
                engine.Journal.Add(ok ? "media" : "refused", words, ok: ok);
                Interlocked.Decrement(ref _missingFiles);
                engine.FactsChanged();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _missingFiles, 0);
            _pullGate.Release();
        }
    }

    private static async Task<(bool Ok, string Words)> PullOneAsync(MirrorPuller puller, string from, MediaBackend store, string hash, long size, string name, CancellationToken ct)
    {
        var hex = MediaBackend.Hex(hash);
        var destination = store.StorePath(hex, name);
        try
        {
            var result = await puller.FileAsync(hex, size, destination, ct).ConfigureAwait(false);
            if (result.Outcome is MediaPullOutcome.Verified or MediaPullOutcome.AlreadyHere)
            {
                store.AddVerified(hex, destination, Path.GetFileName(name));
                return (true, $"{Path.GetFileName(name)} pulled from {from} with proof{(puller.Tls ? " over HTTPS" : "")} and verified");
            }
            return (false, $"{Path.GetFileName(name)} could not be pulled from {from}: {result.Words}");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (false, $"{Path.GetFileName(name)} could not be pulled from {from}: {(puller.PinWords.Length > 0 ? puller.PinWords : ex.Message)}");
        }
    }

    /// <summary>MEDIA FETCH &lt;media&gt; VERSION &lt;version&gt; [FROM &lt;node&gt;]: one optimised version, by its hash, from a peer that holds it
    /// (the node named; else the primary this node backs up, then every other PlayMap node heard), with that peer's proof over its pinned
    /// HTTPS.</summary>
    public CommandReply FetchVersion(MediaItem item, MediaVersion version, string? from)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(version);
        if (!ContentHasher.IsValid(version.ContentHash))
            return CommandReply.Fail($"{version.Id} of {item.Name} has no content hash yet");
        if (_setup.Media.PathForHash(version.ContentHash) is not null)
            return CommandReply.Fail($"{version.Id} of {item.Name} is here already, verified");
        return Fetch([new PulledFile(version.ContentHash, version.SizeBytes, version.Id + "." + version.To)], $"{version.Id} of {item.Name} ({version.To})", from);
    }

    /// <summary>MEDIA FETCH &lt;media&gt;|ALL [FROM &lt;node&gt;] on a node with the fleet's part: each missing file by its hash, from the node
    /// named or else the first heard that holds it, with that peer's proof over its pinned HTTPS (never its token, never in clear).</summary>
    public CommandReply FetchMedia(IReadOnlyList<MediaItem> items, string? from)
    {
        ArgumentNullException.ThrowIfNull(items);
        var files = items.Where(m => ContentHasher.IsValid(m.ContentHash) && _setup.Media.PathForHash(m.ContentHash) is null)
            .Select(m => new PulledFile(m.ContentHash, m.SizeBytes, m.Location.Length > 0 ? m.Location : m.Name)).ToList();
        if (files.Count == 0)
            return CommandReply.Fail("nothing to fetch: every file asked for is unhashed or here already");
        return Fetch(files, files.Count == 1 ? Path.GetFileName(files[0].Name) : $"{files.Count} files", from);
    }

    private sealed record PulledFile(string Hash, long Size, string Name);

    private CommandReply Fetch(IReadOnlyList<PulledFile> files, string what, string? from)
    {
        if (_engine is not { } engine || _setup.Media is not MediaBackend store)
            return CommandReply.Fail("no media store on this node");
        List<ShowBusPeer> peers;
        if (from is not null)
        {
            if (FindPeer(from) is not { } named || named.Hello.Port("http") <= 0)
                return CommandReply.Fail($"no PlayMap node '{from}' with a web port is heard on the show network — NODES lists the ones that are");
            peers = [named];
        }
        else
        {
            peers = [.. (_setup.Network.Hello?.Peers.Snapshot() ?? [])
                .Where(p => p.Kind == ShowBusConstants.KindPlayMap && p.Id != SelfId && p.Liveness == PeerLiveness.Live && p.Hello.Port("http") > 0)
                .OrderBy(p => p.Id == _settings.Of ? 0 : 1)];
            if (peers.Count == 0)
                return CommandReply.Fail("no other PlayMap node is heard on the show network to fetch from");
        }
        Background(Task.Run(async () =>
        {
            foreach (var f in files)
            {
                var said = "";
                foreach (var peer in peers)
                {
                    using var puller = new MirrorPuller(new MirrorSource(peer.Id, peer.Name, peer.Address, peer.Hello.Port("http"), peer.Hello.TlsFingerprint ?? "",
                        Credential(peer.Id)), _pins(), () => _setup.Wall.UnixMs, _setup.Mono, _log);
                    try
                    {
                        var (ok, words) = await PullOneAsync(puller, peer.Name, store, f.Hash, f.Size, f.Name, _stop.Token).ConfigureAwait(false);
                        said = words;
                        if (ok)
                            break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                    {
                        // A pull that faults is that peer's: the next one is asked, and the journal says what happened.
                        said = $"{Path.GetFileName(f.Name)} could not be pulled from {peer.Name}: {ex.Message}";
                    }
                }
                var landed = store.PathForHash(f.Hash) is not null;
                engine.Journal.Add(landed ? "media" : "refused", said, ok: landed);
                engine.FactsChanged();
            }
        }, CancellationToken.None));
        var source = peers.Count == 1 ? peers[0].Name : string.Join(", ", peers.Select(p => p.Name));
        return CommandReply.Accepted($"fetching {what} from {source} by hash, with proof");
    }

    /// <summary>An air line of the primary's, kept once by (feed, seq) and run at its leader instant by the lane's tick.</summary>
    private CommandReply Air(string json)
    {
        string feed, text;
        long seq, at;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            feed = r.GetProperty("feed").GetString() ?? "";
            seq = r.GetProperty("seq").GetInt64();
            text = r.GetProperty("line").GetString() ?? "";
            at = r.GetProperty("leaderNs").GetInt64();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return CommandReply.Fail("MIRROR AIR {\"feed\",\"seq\",\"line\",\"leaderNs\"}");
        }
        if (!WireGrammar.TryParse(text, out var line, out _) || MirrorAir.KindOf(line) is null)
            return CommandReply.Fail($"'{text}' is not an air line a backup takes from its primary");
        if (!FirstTime(feed, seq))
            return CommandReply.NoReply();
        lock (_dueGate)
            _due.Enqueue((at, line));
        return CommandReply.NoReply();
    }

    private bool FirstTime(string feed, long seq)
    {
        lock (_airGate)
        {
            if (!_airDone.Add((feed, seq)))
                return false;
            _airOrder.Enqueue((feed, seq));
            while (_airOrder.Count > AirRemembered)
                _airDone.Remove(_airOrder.Dequeue());
            return true;
        }
    }

    /// <summary>On the lane, each tick: the primary's air lines whose instant has come, each pinned to it, in the order they came.</summary>
    internal void Tick(EngineCore engine, long leaderNs)
    {
        while (true)
        {
            (long AtLeaderNs, WireLine Line) d;
            lock (_dueGate)
            {
                if (!_due.TryPeek(out var head) || head.AtLeaderNs > leaderNs)
                    break;
                d = _due.Dequeue();
            }
            var (reply, _) = engine.RunAirLineOnLane(d.Line, d.AtLeaderNs, FleetSession);
            if (!reply.Ok)
                _log.LogInformation("The primary's air line {Line} was refused here: {Why}", d.Line.Raw, reply.Payload);
        }
    }

    private async Task<CommandReply> HandbackAsync(string json, CancellationToken ct)
    {
        string from;
        try
        {
            using var doc = JsonDocument.Parse(json);
            from = doc.RootElement.GetProperty("from").GetString() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return CommandReply.Fail("MIRROR HANDBACK {\"from\",\"epoch\"}");
        }
        if (from != _primaryId)
            return CommandReply.Fail($"a hand-back comes from the primary this node mirrors ({Label(_settings.Of)}), not {from}");
        // The takeover runs beside the mirror's line (it closes the link this line came on).
        Background(Task.Run(() => TakeOverAsync($"handed back by {Label(from)}", _stop.Token), CancellationToken.None));
        await Task.CompletedTask.ConfigureAwait(false);
        return CommandReply.Accepted($"taking the show back from {Label(from)}");
    }

    // ---- What this node hears ---------------------------------------------------------------------------------------------

    /// <summary>The peers' health moved: a primary that took this node's show under a higher epoch fences it (or, after a hand-back,
    /// this node joins it as its backup); a follower whose leader went quiet takes its clock from the group's new leader.</summary>
    private async Task LookAsync(EngineCore engine)
    {
        foreach (var peer in engine.Services.FleetPeers?.Peers() ?? [])
        {
            if (peer.Heard != FleetHeard.Live || peer.Health?.Facts is not { } f || peer.Id == SelfId)
                continue;
            // Only a node that joined this one's mirror (proved by its token), heard over its pinned certificate, can fence it.
            if (Takeover.FencedBy(SelfId, _settings.Role, _settings.Epoch, f.Role, f.Of, f.Epoch, _settings.Backups.Contains(peer.Id) && peer.Pin == "pinned")
                && Once("fence:" + peer.Id, f.Epoch))
            {
                if (_handingBackTo == peer.Id)
                {
                    _handingBackTo = "";
                    var joined = await RunAsync(engine, $"FLEET BACKUP {peer.Id}", _stop.Token).ConfigureAwait(false);
                    if (joined.Ok)
                    {
                        // The epoch it took the show under is this node's to know as its backup.
                        Persist(s => s with { Epoch = Math.Max(s.Epoch, f.Epoch) });
                    }
                    else
                    {
                        // A node that handed its show back and cannot mirror it does not keep serving beside it: it is fenced.
                        await RunAsync(engine, string.Create(CultureInfo.InvariantCulture, $"FLEET FENCE {f.Epoch} {peer.Id}"), _stop.Token).ConfigureAwait(false);
                    }
                    var words = joined.Ok ? $"{peer.Name} took the show back (epoch {f.Epoch}): {SelfName} is its hot backup again"
                        : $"{peer.Name} took the show back, but {SelfName} could not mirror it ({joined.Payload}): fenced, its outputs held";
                    _last = new TakeoverRecord("failback", NewId(), _setup.Wall.UnixMs, words);
                    engine.Journal.Add("fleet", words, ok: joined.Ok);
                }
                else
                {
                    await RunAsync(engine, string.Create(CultureInfo.InvariantCulture, $"FLEET FENCE {f.Epoch} {peer.Id}"), _stop.Token).ConfigureAwait(false);
                }
            }
        }
        await RetargetAsync(engine).ConfigureAwait(false);
    }

    /// <summary>A follower whose leader is silent (its clock free-wheels) follows the peer that leads its group under a higher epoch
    /// than any this node knows.</summary>
    private async Task RetargetAsync(EngineCore engine)
    {
        NoteGroup(engine);
        await StepDownAsync(engine).ConfigureAwait(false);
        if (engine.Services.GroupSync is not { } sync || sync.Role != "follower" || !Clock.IsHolding || _settings.Role == FleetRoles.Backup)
            return;
        var group = sync.Settings.Group;
        var leader = sync.Settings.Leader;
        var known = Math.Max(_settings.Epoch, (engine.Services.FleetPeers?.Peers() ?? []).FirstOrDefault(p => p.Id == leader)?.Health?.Facts.Epoch ?? 0);
        var next = (engine.Services.FleetPeers?.Peers() ?? [])
            .Where(p => p.Heard == FleetHeard.Live && Trusted(p) && p.Health?.Facts is { } f && Takeover.Retargets(group, leader, known, p.Id, f.SyncRole, f.SyncGroup, f.Epoch))
            .OrderByDescending(p => p.Health!.Facts.Epoch)
            .FirstOrDefault();
        if (next?.Health?.Facts.Epoch is not { } nextEpoch || !(FindPeer(next.Id)?.Hello.Port("sync") > 0) || !Once("retarget:" + next.Id, nextEpoch))
            return;
        var reply = await RunAsync(engine, $"SYNC FOLLOW {group} {next.Id}", _stop.Token).ConfigureAwait(false);
        if (reply.Ok)
        {
            Persist(s => s with { Epoch = Math.Max(s.Epoch, nextEpoch) });
            engine.Journal.Add("fleet", $"{Label(leader)} went quiet and {next.Name} leads {group} now (epoch {nextEpoch}): following it on the same time line");
        }
        else
        {
            _acted.TryRemove("retarget:" + next.Id, out _);
        }
    }

    /// <summary>The group's members this node has known while its group was whole: its leader, the leader's other followers (their
    /// HEALTH names the same leader and group), and a leader's own members. Only they may take the lead of it from this node's view.</summary>
    private void NoteGroup(EngineCore engine)
    {
        if (engine.Services.GroupSync is not { } sync || sync.Settings.Group.Length == 0)
            return;
        var group = sync.Settings.Group;
        if (!string.Equals(_groupOf, group, StringComparison.Ordinal))
        {
            _groupPeers.Clear();
            _groupOf = group;
        }
        if (sync.Role == "leader")
        {
            foreach (var m in sync.Members)
                _groupPeers.TryAdd(m, 0);
            return;
        }
        if (sync.Role != "follower" || Clock.IsHolding)
            return;
        var leader = sync.Settings.Leader;
        _groupPeers.TryAdd(leader, 0);
        foreach (var p in engine.Services.FleetPeers?.Peers() ?? [])
        {
            if (p.Heard == FleetHeard.Live && p.Health?.Facts is { } f && f.SyncLeader == leader && string.Equals(f.SyncGroup, group, StringComparison.Ordinal))
                _groupPeers.TryAdd(p.Id, 0);
        }
    }

    /// <summary>A peer this node may take its group's clock from: a member of its group it has known, heard over its pinned certificate.</summary>
    private bool Trusted(FleetPeer peer) => _groupPeers.ContainsKey(peer.Id) && peer.Pin == "pinned";

    /// <summary>A leader that hears a member of its group lead it under a higher epoch (a promotion or a takeover while this node was
    /// away or cut off) steps down and follows it: a group has one clock.</summary>
    private async Task StepDownAsync(EngineCore engine)
    {
        if (engine.Services.GroupSync is not { Role: "leader" } sync || _settings.Role is FleetRoles.Backup or FleetRoles.Fenced)
            return;
        var group = sync.Settings.Group;
        var next = (engine.Services.FleetPeers?.Peers() ?? [])
            .Where(p => p.Heard == FleetHeard.Live && p.Id != _handingBackTo && Trusted(p) && p.Health?.Facts is { SyncRole: "leader" } f
                && string.Equals(f.SyncGroup, group, StringComparison.Ordinal) && f.Epoch > _settings.Epoch)
            .OrderByDescending(p => p.Health!.Facts.Epoch)
            .FirstOrDefault();
        if (next?.Health?.Facts.Epoch is not { } epoch || !(FindPeer(next.Id)?.Hello.Port("sync") > 0) || !Once("stepdown:" + next.Id, epoch))
            return;
        var reply = await RunAsync(engine, $"SYNC FOLLOW {group} {next.Id}", _stop.Token).ConfigureAwait(false);
        if (reply.Ok)
        {
            Persist(s => s with { Epoch = Math.Max(s.Epoch, epoch) });
            engine.Journal.Add("fleet", $"{next.Name} leads {group} under epoch {epoch}, above this node's: {SelfName} steps down and follows it");
        }
        else
        {
            _acted.TryRemove("stepdown:" + next.Id, out _);
        }
    }

    /// <summary>True once the node's hello names the clock port it answers on (a node that has just begun to lead says so in its next
    /// hello); false when it does not within two seconds.</summary>
    private async Task<bool> LeadsHeardAsync(string nodeId, CancellationToken ct)
    {
        for (var i = 0; i < 100; i++)
        {
            if (FindPeer(nodeId)?.Hello.Port("sync") > 0)
                return true;
            if (!await Waits.DelayAsync(TimeSpan.FromMilliseconds(20), ct).ConfigureAwait(false))
                return false;
        }
        return false;
    }

    private bool Once(string key, long epoch)
    {
        var seen = _acted.GetOrAdd(key, long.MinValue);
        return seen < epoch && _acted.TryUpdate(key, epoch, seen);
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        var lastMirror = "";
        while (await Waits.DelayAsync(_o.WatchEvery, ct).ConfigureAwait(false))
        {
            try
            {
                if (_engine is not { } engine)
                    continue;
                await LookAsync(engine).ConfigureAwait(false);
                await KeepFollowingAsync(engine).ConfigureAwait(false);
                await RepullAsync(engine).ConfigureAwait(false);
                var mirror = Mirror()?.State ?? "";
                if (mirror != lastMirror)
                {
                    if (mirror == "identical")
                        engine.Journal.Add("fleet", $"{SelfName} mirrors {_primaryName}: identical (rev {Interlocked.Read(ref _appliedRev)})");
                    lastMirror = mirror;
                    engine.FactsChanged();
                    // The primary reads its backup's state in the backup's HEALTH: told at once.
                    engine.Fleet.Publish(due: true);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // A watch that faults says nothing this time round; the next one looks again.
                _log.LogWarning(ex, "The backup watch faulted.");
            }
        }
    }

    /// <summary>A backup whose show is here and whose mirror is live but whose sync part does not follow the primary's group (a follow
    /// that found no clock port yet, a group that moved) follows it again, about once a second (the watch's own task alone counts its turns).</summary>
    private async Task KeepFollowingAsync(EngineCore engine)
    {
        if (_settings.Role != FleetRoles.Backup || !_subscribed || Interlocked.Read(ref _appliedRev) < 0 || _group.Length == 0
            || engine.Services.GroupSync is not { } sync)
            return;
        if (sync.Role == "follower" && sync.Settings.Leader == _primaryId && string.Equals(sync.Settings.Group, _group, StringComparison.Ordinal))
            return;
        // Paced in the watch's own turns (real time, whatever the show clock does): about once a second.
        if (++_followTurns < Math.Max(1, (int)(TimeSpan.FromSeconds(1) / _o.WatchEvery)))
            return;
        _followTurns = 0;
        Interlocked.Exchange(ref _estimatesBefore, long.MaxValue);
        await FollowGroupAsync(engine).ConfigureAwait(false);
    }

    /// <summary>A pull of the primary's show that failed (or a revision announced while one ran) is asked again about every two seconds
    /// while the primary's revision is ahead of this node's; the lines waiting behind it then go on.</summary>
    private async Task RepullAsync(EngineCore engine)
    {
        var announced = Interlocked.Read(ref _announcedRev);
        if (_link is null || !_subscribed || _pulling || announced < 0 || announced == Interlocked.Read(ref _appliedRev))
        {
            _projectTurns = 0;
            return;
        }
        if (++_projectTurns < Math.Max(1, (int)(TimeSpan.FromSeconds(2) / _o.WatchEvery)))
            return;
        _projectTurns = 0;
        var text = "MIRROR PROJECT " + string.Create(CultureInfo.InvariantCulture, $"{{\"rev\":{announced}}}");
        if (WireGrammar.TryParse(text, out var line, out _))
            await engine.HandleAsync(line, FleetSession, _stop.Token).ConfigureAwait(false);
    }

    /// <summary>A backup role kept from before a restart: the mirror is joined again once the primary is heard on the show network.</summary>
    private async Task RestoreAsync(CancellationToken ct)
    {
        var of = _settings.Of;
        _mirrorWords = $"waiting to hear {of} on the show network";
        do
        {
            if (_settings.Role != FleetRoles.Backup || _settings.Of != of || _link is not null)
                return;
            if (FindPeer(of) is { Liveness: PeerLiveness.Live } peer)
            {
                await _changes.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (_settings.Role != FleetRoles.Backup || _settings.Of != of || _link is not null)
                        return;
                    var (joined, words) = await JoinAsync(peer, ct).ConfigureAwait(false);
                    _engine?.Journal.Add("fleet", joined ? $"mirroring {peer.Name} again (kept from before the restart){words}" : words, ok: joined);
                    if (joined)
                        return;
                }
                finally
                {
                    _changes.Release();
                }
            }
        }
        while (await Waits.DelayAsync(_o.WatchEvery * 5, ct).ConfigureAwait(false));
    }

    // ---- What STATE reads -------------------------------------------------------------------------------------------------

    /// <summary>The backup's mirror now (null on a node that mirrors none): identical once the primary's show is here at its revision,
    /// every file it names is here, the group's clock is followed and the feed is live.</summary>
    public MirrorView? Mirror()
    {
        if (_settings.Role != FleetRoles.Backup)
            return null;
        var announced = Interlocked.Read(ref _announcedRev);
        var applied = Interlocked.Read(ref _appliedRev);
        var behind = announced < 0 || applied < 0 ? 0 : Math.Max(0, announced - applied);
        string state, words;
        if (_link is not { } link)
            (state, words) = ("joining", _mirrorWords);
        else if (link.Status != LinkStatus.Live || !_subscribed)
            (state, words) = (applied < 0 ? "joining" : "lost", _mirrorWords.Length > 0 && link.RefusedWords.Length > 0 ? _mirrorWords : $"{_primaryName}'s mirror is {(link.Status == LinkStatus.Live ? "not subscribed" : "down")} — the last show and air hold here");
        else if (applied < 0)
            (state, words) = ("joining", _mirrorWords);
        else if (_pulling || behind > 0)
            (state, words) = ("resyncing", string.Create(CultureInfo.InvariantCulture, $"pulling {_primaryName}'s show (rev {announced})"));
        else if (MissingHere() is var missing && missing > 0)
            (state, words) = Volatile.Read(ref _missingFiles) > 0
                ? ("resyncing", string.Create(CultureInfo.InvariantCulture, $"pulling {missing} file{(missing == 1 ? "" : "s")} from {_primaryName}"))
                : ("different", string.Create(CultureInfo.InvariantCulture, $"{missing} file{(missing == 1 ? "" : "s")} the show names could not be pulled from {_primaryName} — PREFLIGHT names the line that fetches {(missing == 1 ? "it" : "them")}"));
        else if (!OnPrimaryLine())
            (state, words) = ("joining", $"waiting for this node's clock to read {_primaryName}'s time line");
        else if (_engine?.Services.GroupSync is not { FeedWord: "live" })
            (state, words) = ("joining", $"waiting for {_primaryName}'s group feed ({_engine?.Services.GroupSync?.FeedWord ?? "no sync part"})");
        else
            (state, words) = ("identical", string.Create(CultureInfo.InvariantCulture, $"same show (rev {applied}), clock, transports and air as {_primaryName}"));
        return new MirrorView(_settings.Of, _primaryName, state, applied, behind, words);
    }

    /// <summary>The files and optimised versions the show names that this node does not hold (verified, by hash).</summary>
    private int MissingHere()
    {
        if (_engine is not { } engine || engine.Services.Media is not { } store)
            return 0;
        var n = 0;
        foreach (var m in engine.Store.Project.Media)
        {
            if (m.IsFile && ContentHasher.IsValid(m.ContentHash) && store.PathForHash(m.ContentHash) is null)
                n++;
            foreach (var v in m.Versions)
            {
                if (ContentHasher.IsValid(v.ContentHash) && store.PathForHash(v.ContentHash) is null)
                    n++;
            }
        }
        return n;
    }

    /// <summary>The words a client's line meets on a backup or a fenced node, or null when it may run (<see cref="BackupGate"/>).</summary>
    public string? Refused(VerbSpec spec, WireLine line, SessionInfo session)
    {
        var role = _settings.Role;
        if (role is not (FleetRoles.Backup or FleetRoles.Fenced))
            return null;
        var words = role == FleetRoles.Backup
            ? $"{SelfName} mirrors {Label(_settings.Of)} — send it to {Label(_settings.Of)}{(_primaryAddress.Length > 0 ? " (" + _primaryAddress + ")" : "")}, or FAILOVER to make this node the show"
            : $"fenced: {Label(_settings.Of)} has the show (epoch {_settings.Epoch}) — FLEET BACKUP {Label(_settings.Of)} to mirror it";
        return BackupGate.Refused(spec, line, session.Transport, role, words);
    }

    // ---- Small things -----------------------------------------------------------------------------------------------------

    /// <summary>Who the primary's lines run as here: a fleet peer, named after the primary.</summary>
    private SessionInfo FleetSession => new("fleet:" + _primaryId, "fleet", _primaryAddress, false, $"{(_primaryName.Length > 0 ? _primaryName : "the primary")} (mirror)", true);

    private static async Task<CommandReply> RunAsync(EngineCore engine, string text, CancellationToken ct)
    {
        if (!WireGrammar.TryParse(text, out var line, out var error))
            return CommandReply.Fail(error?.Reason ?? "not a line");
        return await engine.HandleAsync(line, EngineCore.SystemSession, ct).ConfigureAwait(false);
    }

    /// <summary>The epochs this node hears from its peers' health.</summary>
    private IEnumerable<long> HeardEpochs() =>
        (_engine?.Services.FleetPeers?.Peers() ?? []).Select(p => p.Health?.Facts.Epoch ?? 0);

    private string Credential(string nodeId)
    {
        var reference = "node." + nodeId;
        var store = _engine?.Services.Devices?.Credentials ?? _engine?.Services.Assistant?.Credentials;
        var found = store?.Find(reference) ?? new NodeCredentialStore(_setup.Home.KeysFolder).Find(reference);
        return found?.Secret ?? "";
    }

    private ShowBusPeer? FindPeer(string idOrName)
    {
        var peers = (_setup.Network.Hello?.Peers.Snapshot() ?? []).Where(p => p.Kind == ShowBusConstants.KindPlayMap).ToList();
        return peers.FirstOrDefault(p => p.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
            ?? peers.FirstOrDefault(p => p.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
    }

    private FleetPeer? Peer(string id) => _engine?.Services.FleetPeers?.Peers().FirstOrDefault(p => p.Id == id);

    private string Label(string nodeId)
    {
        if (nodeId.Length == 0)
            return "no node";
        var name = Peer(nodeId)?.Name ?? FindPeer(nodeId)?.Name ?? (nodeId == _settings.Of && _primaryName.Length > 0 ? _primaryName : "");
        return name.Length > 0 ? name : nodeId;
    }

    private static string Clock24(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    private static string NewId() => "tk" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));

    private static bool IsIdWord(string s) => s.Length is > 0 and <= 64 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Keeps a change of the fleet settings for the next start, through the network host (the settings file's one writer);
    /// answers "" or the words of a failure (the change holds for this run either way).</summary>
    private string Persist(Func<FleetSettings, FleetSettings> edit)
    {
        lock (_settingsGate)
        {
            var next = edit(_settings);
            _settings = next;
            var failed = _setup.Network.PersistSettings(s => s with { Fleet = next });
            return failed.Length == 0 ? "" : " — not kept for the next start (" + failed + ")";
        }
    }

    private async Task StopMirrorAsync(bool keepDue = false)
    {
        Interlocked.Exchange(ref _lines, null)?.Writer.TryComplete();
        var link = Interlocked.Exchange(ref _link, null);
        if (link is not null)
            await link.DisposeAsync().ConfigureAwait(false);
        Interlocked.Exchange(ref _puller, null)?.Dispose();
        _subscribed = false;
        if (keepDue)
            return;
        lock (_dueGate)
            _due.Clear();
    }

    private void Background(Task task)
    {
        // A part that has begun to stop takes no new work (its own close waits only for what it already holds).
        if (Volatile.Read(ref _disposed) != 0)
            return;
        _ = task.ContinueWith(t => _log.LogWarning(t.Exception, "A backup task faulted."),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        lock (_work)
        {
            _work.RemoveAll(t => t.IsCompleted);
            _work.Add(task);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stop.CancelAsync().ConfigureAwait(false);
        var held = await _changes.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopMirrorAsync().ConfigureAwait(false);
        }
        finally
        {
            if (held)
                _changes.Release();
        }
        Task[] work;
        lock (_work)
            work = [.. _work.Select(t => t.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default))];
        try
        {
            await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            // Work that does not end within five seconds is left to the process: the node's close goes on.
            _log.LogWarning(ex, "The backup's work did not end within five seconds of its close.");
        }
        // A verb still holding the changes releases them itself; they are let go only when this close held them.
        if (held)
        {
            _changes.Dispose();
            _pullGate.Dispose();
        }
        _stop.Dispose();
    }
}

/// <summary>The fence on the air: a fenced node's outputs at gain 0 from the next frame (read by the render thread every frame, with
/// no allocation), every output at 1 otherwise; any other degrade gain is multiplied in.</summary>
public sealed class FenceGains(IOutputGains? inner) : IOutputGains
{
    private volatile bool _held;

    /// <summary>The degrade gain another part supplies (multiplied in), set once when the node is composed.</summary>
    public IOutputGains? Inner { get; set; } = inner;

    public bool Held
    {
        get => _held;
        set => _held = value;
    }

    public float GainOf(string outputId) => _held ? 0f : Inner?.GainOf(outputId) ?? 1f;
}
