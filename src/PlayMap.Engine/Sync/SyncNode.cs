using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Host;
using PlayMap.Net;
using PlayMap.Net.Bus;
using PlayMap.Net.Sync;

namespace PlayMap.Engine.Sync;

/// <summary>How the sync package paces itself; the defaults are the contract's (a report a second, a receipt within 2 s).</summary>
public sealed record SyncNodeOptions
{
    /// <summary>Each output's vblank report at least this often (and at once when its lock state changes).</summary>
    public TimeSpan ReportEvery { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How often the package looks at its outputs, the board and a follower's way back to its leader.</summary>
    public TimeSpan WatchEvery { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>How long the leader waits for each member's receipt of an air line.</summary>
    public TimeSpan ReceiptWait { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Where this node's outputs' vblanks are read (null: the render host's numbers).</summary>
    public Func<EngineCore, IVblankSource>? Vblanks { get; init; }
}

/// <summary>The group's sync, network side: the role kept in the node's settings and re-entered at start; a leader's anchor feed
/// (<c>SUBSCRIBE ANCHORS</c>), its sync board fed by its own outputs and its members' vblank reports, and its air fanned out to
/// every member (<see cref="IFleetFanout"/>); a follower's link to its leader, which adopts the leader's anchors and air lines through
/// this node's own verbs, and its vblank reports to the leader. The engine's lane does what changes the show; this part moves
/// facts and lines beside it.</summary>
public sealed class SyncNode : IFleetFanout, IVblankSink, IAsyncDisposable
{
    /// <summary>Air lines a leader fans out to its group when run here (unless HERE keeps them local).</summary>
    private static readonly FrozenSet<string> AirVerbs = new[] { "PANIC", "PANIC CLEAR", "BLACKOUT", "MASTER" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Air lines a member runs from its leader; anything else is refused with words (fail closed).</summary>
    private static readonly FrozenSet<string> MemberVerbs =
        new[] { "PANIC", "PANIC CLEAR", "BLACKOUT", "MASTER", "SHOW ARM", "SHOW DISARM", "PREFLIGHT" }.ToFrozenSet(StringComparer.Ordinal);

    private readonly NetworkHost _network;
    private readonly NodeHome _home;
    private readonly ShowWallClock _wall;
    private readonly ILogger _log;
    private readonly SyncNodeOptions _o;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly ConcurrentDictionary<string, uint> _seq = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (Core.Sync.LockState State, long AtWatch)> _lastSent = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _adopted = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, byte> _airDone = new();
    private readonly Lock _pendingGate = new();
    private readonly List<(string Group, string Key, string Json)> _pending = [];
    private readonly List<DueAir> _due = [];
    private readonly List<Task> _work = [];
    private volatile EngineCore? _engine;
    private volatile SyncSettings _settings;
#pragma warning disable CA2213 // Closed by StopFollowingAsync, which a role change and DisposeAsync both call.
    private volatile AnchorFollower? _follower;
    private volatile VblankReporter? _reporter;
#pragma warning restore CA2213
    private volatile string _words = "";
    private volatile string _leaderName = "";
    private volatile string _leaderAddress = "";
    private IVblankSource? _source;
    private bool _holdNoted;
    private (string Verdict, Light Light, int Heard, bool Mixed) _lastPicture = ("", Light.Grey, -1, false);
    private (bool Slewing, bool Holding, long HoldingS) _lastClock;
    private long _refused;
    private long _watches;
    private int _disposed;

    /// <summary>An air line waiting for its instant on this node's show clock.</summary>
    private sealed record DueAir(long AtLeaderNs, WireLine Line, TaskCompletionSource<CommandReply> Done);

    public SyncNode(NetworkHost network, NodeHome home, ShowWallClock wall, SyncSettings settings, ILogger? log = null, SyncNodeOptions? options = null)
    {
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _home = home ?? throw new ArgumentNullException(nameof(home));
        _wall = wall ?? throw new ArgumentNullException(nameof(wall));
        _settings = settings ?? SyncSettings.Default;
        _log = log ?? NullLogger.Instance;
        _o = options ?? new SyncNodeOptions();
        Feed.MembersChanged += () => _engine?.FactsChanged();
    }

    /// <summary>The leader's feed of anchors and air lines.</summary>
    public AnchorFeed Feed { get; } = new();

    /// <summary>The leader's board of vblank reports.</summary>
    public SyncBoard Board { get; } = new();

    /// <summary>The role, group and leader as kept in the node's settings.</summary>
    public SyncSettings Settings => _settings;

    /// <summary>The clock role now: "leader", "follower" or "solo".</summary>
    public string Role => _network.Sync().Role;

    public bool Leads => Role == "leader";

    public NetworkClock Clock => _network.Clock;

    /// <summary>What the node says about its place in the group now.</summary>
    public string Words => _words;

    /// <summary>Reports refused from a sender that is not a member of this node's group.</summary>
    public long RefusedReports => Interlocked.Read(ref _refused);

    /// <summary>The feed's state: a leader "live" (serving its members); a follower "live", "connecting" or "lost"; solo "off".</summary>
    public string FeedWord => Role switch
    {
        "leader" => "live",
        "follower" => _follower?.Status switch
        {
            LinkStatus.Live => "live",
            LinkStatus.Stale or LinkStatus.Connecting => "connecting",
            null => "lost",
            _ => "lost",
        },
        _ => _settings.Role == "follow" ? "connecting" : "off",
    };

    /// <summary>The playlists that took their leader's anchor since this node began following (their anchors are in the leader's
    /// time line already: a step of the clock leaves them as they are). Empty unless following.</summary>
    public IReadOnlyCollection<string> Adopted => Role == "follower" ? [.. _adopted.Keys] : [];

    /// <summary>Attaches to the running engine: the board hears its clock's responder, the watch starts, and the role kept in the
    /// settings is entered again (a lead at once, a follow once its leader is heard on the show network).</summary>
    public void Attach(EngineCore engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _source = _o.Vblanks?.Invoke(engine) ?? new PresentVblankSource(engine);
        Clock.SlewPpm = _settings.SlewPpm;
        VblankSinks.Attach(Clock, this);
        Background(WatchAsync(_stop.Token));
        Background(RestoreAsync(_stop.Token));
    }

    // ---- The verbs --------------------------------------------------------------------------------------------------------

    /// <summary>SYNC LEAD &lt;group&gt;: this node's clock becomes the group's, kept for the next start.</summary>
    public async Task<CommandReply> LeadAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count != 1)
            return CommandReply.Fail("SYNC LEAD <group>");
        var group = args[0];
        if (!IsGroupWord(group))
            return CommandReply.Fail("a sync group is one word of letters, digits, - and _ (up to 32)");
        await _changes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string words;
            if (Leads)
            {
                if (string.Equals(_settings.Group, group, StringComparison.Ordinal))
                    return CommandReply.Fail(string.Create(CultureInfo.InvariantCulture,
                        $"already leading {group} — followers take this node's clock on UDP {_network.SyncPort}"));
                words = $"leading {group} (was {(_settings.Group.Length > 0 ? _settings.Group : "no group")})";
            }
            else
            {
                try
                {
                    words = await _network.LeadAsync(group, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    return CommandReply.Fail(ex.Message);
                }
            }
            await StopFollowingAsync().ConfigureAwait(false);
            Board.Clear();
            Feed.Forget();
            var kept = Persist(s => s with { Role = "lead", Group = group, Leader = "" });
            _words = $"leading {group}";
            _engine?.FactsChanged();
            return CommandReply.Success(effect: words + kept);
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <summary>SYNC FOLLOW &lt;group&gt; &lt;node&gt;: this node takes the leader's clock, its anchors and its air, kept for the next start.</summary>
    public async Task<CommandReply> FollowAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count != 2)
            return CommandReply.Fail("SYNC FOLLOW <group> <node>");
        var group = args[0];
        if (!IsGroupWord(group))
            return CommandReply.Fail("a sync group is one word of letters, digits, - and _ (up to 32)");
        await _changes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var peer = FindPeer(args[1]);
            // Following the same leader again is a no-op while it is heard and its feed is live; a leader that went quiet (or came
            // back on another port) is followed afresh.
            if (Role == "follower" && peer is not null && peer.Id == _settings.Leader && string.Equals(group, _settings.Group, StringComparison.Ordinal)
                && !Clock.IsHolding && FeedWord == "live")
                return CommandReply.Fail($"already following {peer.Name} ({peer.Id}) for {group}");
            string words;
            try
            {
                words = await _network.FollowAsync(group, args[1], ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return CommandReply.Fail(ex.Message);
            }
            peer = FindPeer(args[1]);
            if (peer is null)
                return CommandReply.Fail($"{args[1]} went quiet while this node began following it — SYNC FOLLOW again when it is heard");
            var link = await StartFollowingAsync(group, peer).ConfigureAwait(false);
            var kept = Persist(s => s with { Role = "follow", Group = group, Leader = peer.Id });
            return CommandReply.Success(effect: words + link + kept);
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <summary>SYNC RECEIPT &lt;seq&gt; ACCEPTED|APPLIED|REFUSED &lt;leader ns&gt; [&lt;words&gt;]: a member's answer to an air line, from the session that
    /// subscribed to this node's feed.</summary>
    public CommandReply Receipt(IReadOnlyList<string> args, SessionInfo session)
    {
        const string Syntax = "SYNC RECEIPT <seq> ACCEPTED|APPLIED|REFUSED <leader ns> [<words>]";
        if (args.Count < 3 || !long.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seq)
            || !long.TryParse(args[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var at))
            return CommandReply.Fail(Syntax);
        var stage = args[1].ToUpperInvariant() switch
        {
            "ACCEPTED" => "accepted",
            "APPLIED" => "applied",
            "REFUSED" => "refused",
            _ => null,
        };
        if (stage is null)
            return CommandReply.Fail(Syntax);
        var words = string.Join(' ', args.Skip(3));
        return Feed.Receipt(session.SessionId, seq, stage, at, words)
            ? CommandReply.Success(effect: "")
            : CommandReply.Fail($"no air line {seq} waits for a receipt from this session");
    }

    /// <summary>SUBSCRIBE ANCHORS [&lt;node&gt; [&lt;output&gt;…]]: a member joins the feed, naming itself and its outputs (so the board names
    /// the outputs it reports). Open like every subscription: anchors are transport facts already in STATE.</summary>
    public PushOpen OpenAnchors(IReadOnlyList<string> args, SessionInfo session, IPushSink sink)
    {
        if (args.Count > 65 || args.Any(a => !IsIdWord(a)))
            return PushOpen.Refuse("SUBSCRIBE ANCHORS [<node> [<output>…]] — ids are letters, digits, - and _ (up to 64 outputs)");
        var node = args.Count > 0 ? args[0] : "";
        var outputs = args.Skip(1).ToImmutableArray();
        foreach (var o in outputs)
            Board.Name(node, o);
        var member = new AnchorMember(session.SessionId, node, session.ClientName, session.RemoteAddress, outputs);
        return PushOpen.Open(Feed.Subscribe(member, sink));
    }

    // ---- The fan-out ------------------------------------------------------------------------------------------------------

    public IReadOnlyList<string> Members => Leads ? [.. Feed.Members.Select(m => m.Node.Length > 0 ? m.Node : m.Name)] : [];

    public async Task<IReadOnlyList<FanoutReceipt>> SendAsync(string line, long atLeaderNs, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        if (!Leads || _settings.Group.Length == 0)
            return [];
        var receipts = await Feed.SendAirAsync(_settings.Group, KindOf(line), line, atLeaderNs, _o.ReceiptWait, ct).ConfigureAwait(false);
        return [.. receipts.Select(r => new FanoutReceipt(r.Node, r.Name, r.Stage, r.AppliedLeaderNs, r.Words))];
    }

    /// <summary>The air line a leader's own line becomes for its members (BLACKOUT TOGGLE as the state it left), or null when it
    /// stays here: not an air verb, HERE, or a malformed line.</summary>
    public static string? FanoutLine(string text, bool blackoutNow)
    {
        if (!WireGrammar.TryParse(text, out var line, out _) || VerbCatalog.Match(line) is not { } m || !AirVerbs.Contains(m.Spec.Name))
            return null;
        var words = line.Words;
        if (words.Any(w => w.Equals("HERE", StringComparison.OrdinalIgnoreCase)))
            return null;
        if (m.Spec.Name == "BLACKOUT")
            return blackoutNow ? "BLACKOUT ON" : "BLACKOUT OFF";
        return string.Join(' ', words);
    }

    /// <summary>The kind a member that joins later is handed the latest of: one blackout, one master, one panic (or its clear).</summary>
    private static string? KindOf(string line)
    {
        if (!WireGrammar.TryParse(line, out var parsed, out _) || VerbCatalog.Match(parsed) is not { } m)
            return null;
        return m.Spec.Name switch
        {
            "BLACKOUT" => "blackout",
            "MASTER" => "master",
            "PANIC" or "PANIC CLEAR" => "panic",
            _ => null,
        };
    }

    // ---- What the follower's link brings ----------------------------------------------------------------------------------

    private void OnAnchor(string group, string key, string json)
    {
        if (_engine is null)
            return;
        if (!Clock.IsFollowing)
        {
            // Anchors and air lines are on the leader's time line: they wait until this node's clock reads it.
            lock (_pendingGate)
            {
                if (key != AnchorFeed.Air)
                    _pending.RemoveAll(p => p.Group == group && p.Key == key);
                _pending.Add((group, key, json));
            }
            return;
        }
        Take(group, key, json);
    }

    private void Take(string group, string key, string json)
    {
        if (key == AnchorFeed.Air)
            Background(RunAirAsync(json));
        else
            Background(AdoptAsync(group, key, json));
    }

    private async Task AdoptAsync(string group, string playlistId, string json)
    {
        if (_engine is not { } engine)
            return;
        foreach (var id in await engine.AdoptLeaderAnchorAsync(group, playlistId, json, LeaderSession).ConfigureAwait(false))
            _adopted.TryAdd(id, 0);
    }

    private async Task RunAirAsync(string json)
    {
        if (_engine is not { } engine || _follower is not { } link)
            return;
        if (!AnchorFeed.TryReadAir(json, out var seq, out var text, out var at))
            return;
        if (!_airDone.TryAdd(seq, 0))
            return;   // the latest of its kind, handed again on a new subscription: it ran here already
        CommandReply reply;
        if (!WireGrammar.TryParse(text, out var line, out var error) || VerbCatalog.Match(line) is not { } m || !MemberVerbs.Contains(m.Spec.Name))
        {
            reply = CommandReply.Fail($"'{text}' is not an air line a member runs from its leader{(error is not null ? " (" + error.Reason + ")" : "")}");
        }
        else if (at > Clock.NowNs && engine.IsLaneVerb(m.Spec))
        {
            await link.ReceiptAsync(seq, "accepted", at, "", _stop.Token).ConfigureAwait(false);
            var due = new DueAir(at, line, new TaskCompletionSource<CommandReply>(TaskCreationOptions.RunContinuationsAsynchronously));
            lock (_pendingGate)
                _due.Add(due);
            reply = await due.Done.Task.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        else
        {
            reply = await engine.RunAirLineAsync(line, m.Spec, at, LeaderSession).ConfigureAwait(false);
        }
        var words = reply.Ok ? reply.Effect : reply.Payload ?? "refused";
        await link.ReceiptAsync(seq, reply.Ok ? "applied" : "refused", at, words, _stop.Token).ConfigureAwait(false);
    }

    /// <summary>On the lane, each tick: air lines whose instant came run (pinned to it); what waited for the clock is taken once it
    /// follows; a silent leader is free-wheeled on.</summary>
    internal void Tick(EngineCore engine, long leaderNs)
    {
        List<DueAir>? due = null;
        List<(string Group, string Key, string Json)>? pending = null;
        lock (_pendingGate)
        {
            if (_due.Count > 0)
            {
                for (var i = _due.Count - 1; i >= 0; i--)
                {
                    if (_due[i].AtLeaderNs > leaderNs)
                        continue;
                    (due ??= []).Add(_due[i]);
                    _due.RemoveAt(i);
                }
            }
            if (_pending.Count > 0 && Clock.IsFollowing)
            {
                pending = [.. _pending];
                _pending.Clear();
            }
        }
        if (due is not null)
        {
            foreach (var d in due.OrderBy(d => d.AtLeaderNs))
                d.Done.TrySetResult(engine.RunAirLineOnLane(d.Line, d.AtLeaderNs, LeaderSession));
        }
        if (pending is not null)
        {
            foreach (var (g, k, j) in pending)
                Take(g, k, j);
        }
        WatchLeader(engine);
    }

    /// <summary>A follower whose leader has been silent longer than the settings' timeout free-wheels on its last estimate: its
    /// playlists keep switching their own items at the anchored instants; the journal and the context say so, once.</summary>
    private void WatchLeader(EngineCore engine)
    {
        if (Role != "follower" || !Clock.IsFollowing)
            return;
        var silent = Clock.Local.NowNs - Clock.HeardLocalNs > _settings.LeaderTimeoutMs * Durations.NsPerMs;
        if (silent && !Clock.IsHolding)
        {
            Clock.Hold();
            _holdNoted = true;
            var who = LeaderLabel;
            engine.Journal.Add("sync", $"{who} went quiet: free-wheeling on the last estimate; playlists keep their anchored switches", ok: false);
            engine.RaiseEvent("sync.leader.lost", engine.LeaderNowNs, w => w.WriteString("leader", _settings.Leader));
            _words = $"free-wheeling: {who} is not heard";
            engine.FactsChanged();
        }
        else if (!silent && _holdNoted && !Clock.IsHolding)
        {
            _holdNoted = false;
            engine.Journal.Add("sync", $"{LeaderLabel} is heard again");
            _words = $"following {LeaderLabel} for {_settings.Group}";
            engine.FactsChanged();
        }
    }

    private string LeaderLabel => _leaderName.Length > 0 ? $"{_leaderName} ({_settings.Leader})" : _settings.Leader;

    /// <summary>Who the leader's lines run as here: a peer of the show (its own gate decides), named after the leader.</summary>
    private SessionInfo LeaderSession => new("sync:" + _settings.Leader, "sync", _leaderAddress, false, $"{(_leaderName.Length > 0 ? _leaderName : "the leader")} (sync leader)", true);

    // ---- The board ---------------------------------------------------------------------------------------------------------

    public void Offer(in VblankReport report, IPAddress from)
    {
        // Only the group's members are heard: the report's node must be a subscriber of this node's feed, from that address.
        var node = report.NodeId;
        var address = NetAddress.Normal(from).ToString();
        var member = Leads && Feed.Members.Any(m => m.Node.Length > 0 && VblankReport.NodeNumber(m.Node) == node
            && string.Equals(NetAddress.Normal(IPAddress.TryParse(m.Address, out var a) ? a : IPAddress.None).ToString(), address, StringComparison.Ordinal));
        if (!member || !Board.Offer(report, Clock.Local.NowNs))
            Interlocked.Increment(ref _refused);
    }

    /// <summary>The picture the leader's board gives now (empty on a node that does not lead).</summary>
    public SyncPicture Picture(bool playing) => Leads ? Board.Picture(Clock.NowNs, Clock.Local.NowNs, playing) : SyncPicture.Empty;

    /// <summary>This node's own outputs as rows (a follower's view; the board is the leader's).</summary>
    public IReadOnlyList<SyncOutputRow> OwnRows()
    {
        var me = _network.Settings.NodeId;
        var u = Clock.UncertaintyNs / 1000;
        return [.. (_source?.Read() ?? []).Select(v => new SyncOutputRow(me, v.OutputId, Core.Sync.FrameLockMachine.Word(v.State),
            v.Path switch { VblankReport.PathGl => "gl", VblankReport.PathDxgiFlip => "dxgi-flip", _ => "offscreen" },
            Math.Round(1e12 / v.PeriodPs, 6), Math.Round((v.PeriodPs / 1000.0 - 1e9 / SyncBoard.Nominal(1e12 / v.PeriodPs)) / (1e9 / SyncBoard.Nominal(1e12 / v.PeriodPs)) * 1e6, 2),
            0, Math.Round(u, 3), null, v.Missed60s, v.FrameCount, true, true, v.AirLatencyUs / 1000.0))];
    }

    // ---- The watch -------------------------------------------------------------------------------------------------------

    private async Task WatchAsync(CancellationToken ct)
    {
        while (await Waits.DelayAsync(_o.WatchEvery, ct).ConfigureAwait(false))
        {
            try
            {
                Report();
                // The clock's slew and free-wheel are facts STATE shows; they move without an edit.
                var c = Clock;
                var clockShape = (c.IsSlewing, c.IsHolding, c.HoldingNs / Durations.NsPerSecond);
                if (clockShape != _lastClock)
                {
                    _lastClock = clockShape;
                    _engine?.FactsChanged();
                }
                if (_engine is { } engine && Leads)
                {
                    var p = Picture(engine.IsAnimating);
                    var shape = (p.Verdict, p.Light, p.Outputs.Count(o => o.Heard), p.MixedRate);
                    if (shape != _lastPicture)
                    {
                        if (p.MixedRate && !_lastPicture.Mixed)
                            engine.RaiseEvent("sync.mixedrate", engine.LeaderNowNs, w => w.WriteString("words", p.Words));
                        _lastPicture = shape;
                        engine.FactsChanged();
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // A watch that faults says nothing this time round; the next one looks again.
                _log.LogWarning(ex, "The sync watch faulted.");
            }
        }
    }

    /// <summary>Each output's report once a period and at once on a change of its lock state: to the leader's board here, or to the
    /// leader over UDP from a follower. The period is counted in the watch's own turns (a send is paced in real time, whatever the
    /// show clock does); the report's numbers are read on the show clock.</summary>
    private void Report()
    {
        if (_source is not { } source)
            return;
        var leads = Leads;
        var reporter = Role == "follower" ? _reporter : null;
        if (!leads && reporter is null)
            return;
        var mono = Clock.Local.NowNs;
        var me = _network.Settings.NodeId;
        var node = VblankReport.NodeNumber(me);
        var watch = Interlocked.Increment(ref _watches);
        var every = Math.Max(1, (long)Math.Round(_o.ReportEvery / _o.WatchEvery));
        foreach (var v in source.Read())
        {
            if (_lastSent.TryGetValue(v.OutputId, out var last) && watch - last.AtWatch < every && last.State == v.State)
                continue;
            var seq = _seq.AddOrUpdate(v.OutputId, 1, (_, s) => s + 1);
            var report = new VblankReport(v.State, (byte)(v.Settled ? VblankReport.FlagSettled : 0), v.Path, node, VblankReport.HashId(v.OutputId), seq,
                v.FrameCount & 0xFFFFFF, Math.Clamp(v.PeriodPs, VblankReport.MinPeriodPs, VblankReport.MaxPeriodPs), Math.Max(0, Clock.ToLeader(v.LastVblankLocalNs)),
                v.RefreshIndex, v.Missed60s, 0, v.AirLatencyUs, (uint)Math.Clamp(Math.Round(Clock.UncertaintyNs / 1000), 0, VblankReport.MaxClockUncertaintyUs));
            if (leads)
            {
                Board.Name(me, v.OutputId);
                Board.Offer(report, mono, local: true);
            }
            else
            {
                reporter!.Send(report);
            }
            _lastSent[v.OutputId] = (v.State, watch);
        }
    }

    // ---- Following and the role kept --------------------------------------------------------------------------------------

    /// <summary>Opens the link to the leader's feed and the reports to its sync port; answers what it could not open, in words.</summary>
    private async Task<string> StartFollowingAsync(string group, ShowBusPeer peer)
    {
        await StopFollowingAsync().ConfigureAwait(false);
        _adopted.Clear();
        _airDone.Clear();
        _leaderName = peer.Name;
        _leaderAddress = peer.Address;
        var said = "";
        var me = _network.Settings;
        var outputs = _engine?.Snapshot.Project.Project.Outputs.Where(o => o.Enabled).Select(o => o.Id) ?? [];
        var wire = peer.Hello.Port("wire");
        if (wire > 0)
        {
            var link = new AnchorFollower(peer.Address, wire, me.Name, string.Join(' ', new[] { me.NodeId }.Concat(outputs)), _wall, _log);
            link.Anchor += OnAnchor;
            link.StatusChanged += _ => _engine?.FactsChanged();
            _follower = link;
            link.Start();
        }
        else
        {
            said = $"; {peer.Name}'s hello names no wire port, so its anchors and air cannot reach this node";
        }
        if (IPAddress.TryParse(peer.Address, out var ip))
        {
            var port = peer.Hello.Port("sync") is > 0 and var p ? p : ShowBusConstants.PlayMapSyncPort;
            _reporter = new VblankReporter(new IPEndPoint(ip, port), _log);
        }
        _words = $"following {peer.Name} ({peer.Id}) for {group}";
        _engine?.FactsChanged();
        return said;
    }

    private async Task StopFollowingAsync()
    {
        var link = Interlocked.Exchange(ref _follower, null);
        if (link is not null)
        {
            link.Anchor -= OnAnchor;
            await link.DisposeAsync().ConfigureAwait(false);
        }
        Interlocked.Exchange(ref _reporter, null)?.Dispose();
        _lastSent.Clear();
        lock (_pendingGate)
        {
            _pending.Clear();
            foreach (var d in _due)
                d.Done.TrySetResult(CommandReply.Fail("this node stopped following before the line's instant"));
            _due.Clear();
        }
        _holdNoted = false;
    }

    /// <summary>The role kept in the settings, entered again at start: a lead binds at once; a follow waits until its leader is heard
    /// on the show network ("waiting to hear …"), then follows.</summary>
    private async Task RestoreAsync(CancellationToken ct)
    {
        var s = _settings;
        if (s.Role == "lead" && s.Group.Length > 0)
        {
            await _changes.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var words = await _network.LeadAsync(s.Group, ct).ConfigureAwait(false);
                _words = $"leading {s.Group}";
                _engine?.Journal.Add("sync", words + " (kept from before the restart)");
            }
            catch (InvalidOperationException ex)
            {
                _words = ex.Message;
                _engine?.Journal.Add("sync", "could not lead again after the restart: " + ex.Message, ok: false);
            }
            finally
            {
                _changes.Release();
            }
            _engine?.FactsChanged();
            return;
        }
        if (s.Role != "follow" || s.Leader.Length == 0)
            return;
        _words = $"waiting to hear {s.Leader} on the show network";
        _engine?.FactsChanged();
        do
        {
            if (_settings.Role != "follow" || _settings.Leader != s.Leader || Role == "follower")
                return;   // the operator chose otherwise meanwhile
            if (FindPeer(s.Leader) is { Liveness: PeerLiveness.Live } peer && peer.Hello.Port("sync") > 0)
            {
                await _changes.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var words = await _network.FollowAsync(s.Group, peer.Id, ct).ConfigureAwait(false);
                    var said = await StartFollowingAsync(s.Group, peer).ConfigureAwait(false);
                    _engine?.Journal.Add("sync", words + said + " (kept from before the restart)");
                    return;
                }
                catch (InvalidOperationException ex)
                {
                    _words = $"waiting to hear {s.Leader}: {ex.Message}";
                }
                finally
                {
                    _changes.Release();
                }
            }
        }
        while (await Waits.DelayAsync(_o.WatchEvery, ct).ConfigureAwait(false));
    }

    /// <summary>Writes the sync settings into the node's file (kept for the next start); answers "" or the words of a failure.</summary>
    private string Persist(Func<SyncSettings, SyncSettings> edit)
    {
        var next = edit(_settings);
        _settings = next;
        try
        {
            var stored = _home.LoadSettings(_network.Settings.Name);
            _home.SaveSettings(stored with { Sync = next });
            return "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return " — not kept for the next start (the node's settings could not be written: " + ex.Message + ")";
        }
    }

    /// <summary>Keeps the "follow Patterns' blackout" switch for the next start.</summary>
    public string PersistFollowBlackout(bool on) => Persist(s => s with { FollowPatternsBlackout = on });

    private ShowBusPeer? FindPeer(string idOrName)
    {
        var peers = (_network.Hello?.Peers.Snapshot() ?? []).Where(p => p.Kind == ShowBusConstants.KindPlayMap).ToList();
        return peers.FirstOrDefault(p => p.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
            ?? peers.FirstOrDefault(p => p.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGroupWord(string s) => s.Length is > 0 and <= 32 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool IsIdWord(string s) => s.Length is > 0 and <= 64 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private void Background(Task task)
    {
        _ = task.ContinueWith(t => _log.LogWarning(t.Exception, "A sync task faulted."),
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
        VblankSinks.Detach(Clock, this);
        await StopFollowingAsync().ConfigureAwait(false);
        Task[] work;
        lock (_work)
            work = [.. _work.Select(t => t.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default))];
        await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        _stop.Dispose();
        _changes.Dispose();
    }
}
