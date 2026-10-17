using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
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

    /// <summary>How long the leader waits for each member's report (<c>PREFLIGHT … WAIT</c>): a member checks itself under its own
    /// checks' budgets (up to 5 s each), so its report is not an air line's receipt.</summary>
    public TimeSpan ReportWait { get; init; } = TimeSpan.FromSeconds(10);

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
    private readonly ShowWallClock _wall;
    private readonly ILogger _log;
    private readonly SyncNodeOptions _o;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly ConcurrentDictionary<string, uint> _seq = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (Core.Sync.LockState State, long AtWatch)> _lastSent = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _adopted = new(StringComparer.Ordinal);
    private readonly HashSet<(string Group, string Playlist)> _leaderLists = [];
    private readonly ConcurrentDictionary<(string Group, string Playlist), byte> _unmatched = new();
    private readonly Lock _airGate = new();
    private readonly HashSet<(string Feed, long Seq)> _airDone = [];
    private readonly Queue<(string Feed, long Seq)> _airOrder = new();
    private readonly Dictionary<string, (string Feed, long Seq)> _airLatest = new(StringComparer.Ordinal);
    private readonly Lock _dueGate = new();
    private readonly Lock _persistGate = new();

    /// <summary>A leader's watch offering its own outputs, a lead's restart of the board and a member's naming, one at a time.</summary>
    private readonly Lock _boardGate = new();
    private readonly Queue<DueAir> _due = new();
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
    private volatile string _joining = "";
    private volatile Channel<(string Group, string Key, string Json)>? _lines;

    /// <summary>Feed lines a follower holds while its clock comes to the leader's time line or its lane is busy.</summary>
    private const int LinesHeld = 4096;
    private IVblankSource? _source;
    private bool _holdNoted;
    private (string Verdict, Light Light, int Heard, bool Mixed) _lastPicture = ("", Light.Grey, -1, false);
    private (bool Slewing, bool Holding, long HoldingS) _lastClock;
    private string _lastLock = "";
    private LockWish? _lockWish;
    private long _refused;
    private long _watches;
    private long _estimatesBefore;
    private int _disposed;

    /// <summary>The air lines whose sequence a follower remembers (so a line handed again is not run twice).</summary>
    private const int AirRemembered = 1024;

    /// <summary>An air line waiting for its instant on this node's show clock.</summary>
    private sealed record DueAir(long AtLeaderNs, WireLine Line, TaskCompletionSource<(CommandReply Reply, long RanAtLeaderNs)> Done);

    /// <summary>A lock record a member's armed show holds back, asked again on each watch.</summary>
    private sealed record LockWish(LockRecord Record);

    public SyncNode(NetworkHost network, ShowWallClock wall, SyncSettings settings, ILogger? log = null, SyncNodeOptions? options = null)
    {
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _wall = wall ?? throw new ArgumentNullException(nameof(wall));
        _settings = settings ?? SyncSettings.Default;
        _log = log ?? NullLogger.Instance;
        _o = options ?? new SyncNodeOptions();
        Feed.MembersChanged += () => _engine?.FactsChanged();
        Feed.LateReceipt += (line, r) => _engine?.Journal.Add(r.Stage == "refused" ? "refused" : "fleet",
            $"{line} on {r.Name}: {r.Stage} at its instant{(r.Words.Length > 0 ? " — " + r.Words : "")}", ok: r.Stage != "refused");
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

    /// <summary>The leader this node is following or about to follow (its node id; "" when it follows none): set before the clock's
    /// first answer from it, so the step that answer brings is said as following it.</summary>
    public string Joining => _joining;

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
    /// time line already: a step that brings the clock onto that line leaves them as they are).</summary>
    public IReadOnlyCollection<string> Adopted => [.. _adopted.Keys];

    /// <summary>A playlist takes its leader's anchor: called on the engine's lane in the action that applies it, before the anchor
    /// lands (so its journal row and every later action see it adopted); answers whether it had taken one before.</summary>
    internal bool TookLeaderAnchor(string playlistId) => !_adopted.TryAdd(playlistId, 0);

    /// <summary>The anchor marked by <see cref="TookLeaderAnchor"/> was refused, and the playlist had taken none before.</summary>
    internal void ForgetLeaderAnchor(string playlistId) => _adopted.TryRemove(playlistId, out _);

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
            _adopted.Clear();
            _joining = "";
            ResetBoard();
            // The group's latest lines start again: every synced playlist's anchor as it is now (a member that joins after a GO is
            // put on the playing item), no air or lock from before.
            Feed.Forget();
            _engine?.PublishAnchors(Feed);
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
            // Following the same leader again is a no-op while it is heard, its feed is live and the clock asks the port its hello
            // names; a leader that went quiet, or whose clock answers on another port now, is followed afresh.
            if (Role == "follower" && peer is not null && peer.Id == _settings.Leader && string.Equals(group, _settings.Group, StringComparison.Ordinal)
                && !Clock.IsHolding && FeedWord == "live" && string.Equals(_network.FollowedClock, _network.ClockOf(peer.Id), StringComparison.Ordinal))
                return CommandReply.Fail($"already following {peer.Name} ({peer.Id}) for {group}");
            string words;
            var was = _joining;
            _joining = peer?.Id ?? args[1];
            try
            {
                words = await _network.FollowAsync(group, args[1], ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                _joining = was;
                return CommandReply.Fail(ex.Message);
            }
            // The clock follows now: the peer the network host followed is the one heard just before (looked up again only when it was
            // not heard yet then), and the role is kept whatever the link can do.
            peer ??= FindPeer(args[1]);
            if (peer is null)
            {
                var kept0 = Persist(s => s with { Role = "follow", Group = group, Leader = args[1] });
                return CommandReply.Success(effect: words + $"; its anchors and air are not linked yet ({args[1]} is not heard now) — SYNC FOLLOW again once it is" + kept0);
            }
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
        // A node is named only by itself: a paired session, or a PlayMap node the show network hears saying hello from the address
        // this session comes from. A reader that names no node is open, as every subscription is.
        if (node.Length > 0 && !session.Paired && !(FindPeer(node) is { } peer && peer.Id == node && SameAddress(peer.Address, NormalText(session.RemoteAddress))))
            return PushOpen.Refuse($"SUBSCRIBE ANCHORS {node}: no PlayMap node {node} is heard from {session.RemoteAddress} — name no node to read the feed");
        var outputs = args.Skip(1).ToImmutableArray();
        var member = new AnchorMember(session.SessionId, node, session.ClientName, session.RemoteAddress, outputs);
        var sub = Feed.Subscribe(member, sink);
        NameMember(node);
        return PushOpen.Open(new Ended(() =>
        {
            sub.Dispose();
            NameMember(node);
        }));
    }

    /// <summary>The board's names for a member node are its subscription's (the latest of that node's, when it has two), none once it
    /// has none. Read from the feed after the subscription changed, under the board's gate, so the last of a node's opens and ends
    /// to take the gate leaves the names of the subscriptions the feed holds, whatever order they came in.</summary>
    private void NameMember(string node)
    {
        if (node.Length == 0)
            return;
        lock (_boardGate)
        {
            if (Feed.Members.LastOrDefault(m => m.Node == node) is { } member)
                Board.Name(node, member.Outputs);
            else
                Board.Unname(node);
        }
    }

    private sealed class Ended(Action end) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                end();
        }
    }

    private static string NormalText(string address) =>
        IPAddress.TryParse(address, out var a) ? NetAddress.Normal(a).ToString() : address;

    // ---- The fan-out ------------------------------------------------------------------------------------------------------

    public IReadOnlyList<string> Members => Leads ? [.. Feed.Members.Select(m => m.Node)] : [];

    public async Task<IReadOnlyList<FanoutReceipt>> SendAsync(string line, long atLeaderNs, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        if (!Leads || _settings.Group.Length == 0)
            return [];
        var wait = WireGrammar.TryParse(line, out var parsed, out _) && AsksForReport(parsed) ? _o.ReportWait : _o.ReceiptWait;
        var receipts = await Feed.SendAirAsync(_settings.Group, KindOf(line), line, atLeaderNs, wait, ct).ConfigureAwait(false);
        return [.. receipts.Select(r => new FanoutReceipt(r.Node, r.Name, r.Stage, r.AppliedLeaderNs, r.Words))];
    }

    /// <summary>A line a member answers with its finished report rather than an effect: <c>PREFLIGHT … WAIT</c>.</summary>
    internal static bool AsksForReport(WireLine line) =>
        VerbCatalog.Match(line) is { Spec.Name: "PREFLIGHT" } && line.Words.Skip(1).Any(w => w.Equals("WAIT", StringComparison.OrdinalIgnoreCase));

    /// <summary>The longest report a receipt carries: one wire line, less room for the receipt's own words.</summary>
    private const int ReportBytes = WireGrammar.MaxLineBytes - 256;

    /// <summary>What a member's receipt says: a refusal's words; for a line that asks for the member's report (<c>PREFLIGHT …
    /// WAIT</c>), the finished report itself (compact JSON, one word to the end of the receipt's line), or why it does not fit in
    /// one; else what the line did here.</summary>
    internal static string ReceiptWords(WireLine line, CommandReply reply)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(reply);
        if (!reply.Ok)
            return reply.Payload ?? "refused";
        if (!reply.Applied || !AsksForReport(line) || reply.Payload is not { Length: > 0 } report || report[0] != '{')
            return reply.Effect;
        return System.Text.Encoding.UTF8.GetByteCount(report) <= ReportBytes ? report
            : "its report is longer than one line — PREFLIGHT REPORT on that node reads it";
    }

    /// <summary>The air line a leader's own line becomes for its members (BLACKOUT TOGGLE as the state it left; a panic's AT &lt;unix ms&gt;
    /// as AT +&lt;ms&gt; from the line's instant, read on this node's wall clock <paramref name="wallUnixMs"/>, so every member lands on the
    /// leader's instant whatever its own wall clock says; a panic's AT carries this node's <paramref name="panicLeadMs"/> too, which a
    /// member adds to no lead of its own, so the group fades on the leader's frames), or null when it stays here: not an air verb,
    /// HERE, a line that names outputs (ON …: they are this node's outputs), or a malformed line.</summary>
    public static string? FanoutLine(string text, bool blackoutNow, long wallUnixMs, int panicLeadMs = 0)
    {
        if (!WireGrammar.TryParse(text, out var line, out _) || VerbCatalog.Match(line) is not { } m || !AirVerbs.Contains(m.Spec.Name))
            return null;
        var words = line.Words.ToList();
        if (words.Any(w => w.Equals("HERE", StringComparison.OrdinalIgnoreCase))
            || (m.Spec.Name is "PANIC" or "PANIC CLEAR" && words.Any(w => w.Equals("ON", StringComparison.OrdinalIgnoreCase))))
            return null;
        if (m.Spec.Name == "BLACKOUT")
            return blackoutNow ? "BLACKOUT ON" : "BLACKOUT OFF";
        var lead = m.Spec.Name == "PANIC" ? Math.Max(0, panicLeadMs) : 0;
        var at = false;
        for (var i = 0; i < words.Count - 1; i++)
        {
            if (!words[i].Equals("AT", StringComparison.OrdinalIgnoreCase))
                continue;
            if (long.TryParse(words[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
            {
                words[i + 1] = "+" + (Math.Max(0, unix - wallUnixMs) + lead).ToString(CultureInfo.InvariantCulture);
                at = true;
            }
            else if (words[i + 1].StartsWith('+') && double.TryParse(words[i + 1].AsSpan(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
            {
                words[i + 1] = "+" + (ms + lead).ToString("0.######", CultureInfo.InvariantCulture);
                at = true;
            }
        }
        if (lead > 0 && !at)
            words.AddRange(["AT", "+" + lead.ToString(CultureInfo.InvariantCulture)]);
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

    /// <summary>A line from the leader's feed, queued in arrival order for <see cref="TakeLinesAsync"/>. A feed that comes faster than
    /// this node takes it is redialled: its new subscription hands the latest of every key again.</summary>
    private void OnAnchor(string group, string key, string json)
    {
        if (_lines is not { } lines || lines.Writer.TryWrite((group, key, json)))
            return;
        _log.LogWarning("The leader's feed came faster than this node takes it: a line was dropped; the link is dialled again.");
        _engine?.Journal.Add("sync", "the leader's feed came faster than this node takes it — dialling it again for the latest", ok: false);
        _follower?.Drop();
    }

    /// <summary>The feed's lines, one at a time in the order they came: anchors adopted, air lines run (or scheduled), the lock taken.
    /// They are on the leader's time line, so none is taken until this node's clock reads it.</summary>
    private async Task TakeLinesAsync(Channel<(string Group, string Key, string Json)> lines, AnchorFollower link, CancellationToken ct)
    {
        await foreach (var (group, key, json) in lines.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            // A follow that ended (or moved to another leader) takes nothing more of its old feed.
            // Taken only once the clock reads this follow's leader: an estimate heard since the follow (or the feed's connection) began,
            // and no step still waiting to be taken.
            while (!Clock.IsFollowing || Clock.Estimates <= Interlocked.Read(ref _estimatesBefore) || Clock.StepPending)
            {
                if (!ReferenceEquals(_lines, lines) || !await Waits.DelayAsync(_o.WatchEvery, ct).ConfigureAwait(false))
                    return;
            }
            if (!ReferenceEquals(_lines, lines))
                return;
            // The air and the lock are the followed group's; any other '@' key is a line this node does not know.
            if (key.StartsWith('@') && (!string.Equals(group, _settings.Group, StringComparison.Ordinal) || key is not (AnchorFeed.Air or LockRecord.Key)))
            {
                _log.LogDebug("A feed line for {Group} {Key} is not this node's to take.", group, key);
                continue;
            }
            try
            {
                if (key == AnchorFeed.Air)
                    await RunAirAsync(json, link).ConfigureAwait(false);
                else if (key == LockRecord.Key)
                    await TakeLockAsync(json).ConfigureAwait(false);
                else
                    await AdoptAsync(group, key, json).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // One line that faults is that line's; the feed goes on with the next.
                _log.LogWarning(ex, "A line from the leader's feed faulted: {Key}", key);
            }
        }
    }

    /// <summary>The leader's frame lock, taken here (on the lane); a join the armed show holds back is asked again on each watch.</summary>
    private Task TakeLockAsync(string json) =>
        LockRecord.TryParse(json, out var record) ? TakeLockAsync(new LockWish(record), fresh: true) : Task.CompletedTask;

    /// <summary>Takes the leader's lock record on the lane while it is still the newest wish: a retry queued before a newer record never
    /// acts after it. A join the armed show holds back stays the wish.</summary>
    private async Task TakeLockAsync(LockWish wish, bool fresh)
    {
        if (_engine is not { } engine)
            return;
        if (fresh)
            Volatile.Write(ref _lockWish, wish);
        var (taken, _) = await engine.AdoptLeaderLockAsync(wish.Record, () => ReferenceEquals(Volatile.Read(ref _lockWish), wish)).ConfigureAwait(false);
        if (taken)
            Interlocked.CompareExchange(ref _lockWish, null, wish);
    }

    private async Task AdoptAsync(string group, string playlistId, string json)
    {
        if (_engine is not { } engine)
            return;
        bool oneOnLeader;
        lock (_leaderLists)
        {
            _leaderLists.Add((group, playlistId));
            oneOnLeader = _leaderLists.Count(k => string.Equals(k.Group, group, StringComparison.Ordinal)) == 1;
        }
        var (_, unmatched) = await engine.AdoptLeaderAnchorAsync(group, playlistId, json, oneOnLeader, LeaderSession).ConfigureAwait(false);
        // Said once per playlist of the leader's: its anchors keep coming.
        if (unmatched.Length > 0 && _unmatched.TryAdd((group, playlistId), 0))
            engine.Journal.Add("sync", $"{LeaderLabel}'s {playlistId} for {group} is not taken here: {unmatched}", ok: false);
    }

    /// <summary>One air line: refused with words unless a member runs it from its leader; run now, pinned to the leader's instant,
    /// when that instant has come; else accepted now and run at its instant by the lane's tick (its receipt follows then, beside the
    /// feed, so later lines are not held up).</summary>
    private async Task RunAirAsync(string json, AnchorFollower link)
    {
        if (_engine is not { } engine)
            return;
        if (!AnchorFeed.TryReadAir(json, out var feed, out var seq, out var text, out var at))
            return;
        if (!FirstTime(feed, seq, KindOf(text)))
            return;   // the latest of its kind, handed again on a new subscription: it ran here already
        CommandReply reply;
        if (!WireGrammar.TryParse(text, out var line, out var error) || VerbCatalog.Match(line) is not { } m || !MemberVerbs.Contains(m.Spec.Name))
        {
            reply = CommandReply.Fail($"'{text}' is not an air line a member runs from its leader{(error is not null ? " (" + error.Reason + ")" : "")}");
        }
        else if (engine.IsLaneVerb(m.Spec) && Queued(at, line) is { } due)
        {
            await link.ReceiptAsync(seq, "accepted", at, "", _stop.Token).ConfigureAwait(false);
            Background(ReceiptWhenRunAsync(due, seq, link));
            return;
        }
        else if (m.Spec.Name == "PREFLIGHT")
        {
            // A preflight takes its checks' time and moves nothing on the air: it runs beside the feed, so a later air line (a
            // PANIC) is never held behind it; its receipt follows when it ends.
            Background(RunBesideAsync(engine, line, m.Spec, at, seq, link));
            return;
        }
        else
        {
            (reply, var ran) = await engine.RunAirLineAsync(line, m.Spec, at, LeaderSession).ConfigureAwait(false);
            await Receipt(link, seq, ran, line, reply).ConfigureAwait(false);
            return;
        }
        await Receipt(link, seq, at, line, reply).ConfigureAwait(false);
    }

    private async Task RunBesideAsync(EngineCore engine, WireLine line, VerbSpec spec, long at, long seq, AnchorFollower link)
    {
        var (reply, ran) = await engine.RunAirLineAsync(line, spec, at, LeaderSession).ConfigureAwait(false);
        await Receipt(link, seq, ran, line, reply).ConfigureAwait(false);
    }

    /// <summary>A lane line whose instant is still to come, or that arrives behind lines still waiting, joins the queue the lane's tick
    /// runs in arrival order (so a line never runs before one the leader ran first); null when it may run now.</summary>
    private DueAir? Queued(long at, WireLine line)
    {
        lock (_dueGate)
        {
            if (_due.Count == 0 && at <= Clock.NowNs)
                return null;
            var due = new DueAir(at, line, new TaskCompletionSource<(CommandReply, long)>(TaskCreationOptions.RunContinuationsAsynchronously));
            _due.Enqueue(due);
            return due;
        }
    }

    /// <summary>True the first time this node takes (feed, seq) — across follows too, as a feed's instance is its leader process's.
    /// Remembered in arrival order and the oldest forgotten first, never the latest line of a kind (the one each new subscription
    /// hands again).</summary>
    private bool FirstTime(string feed, long seq, string? kind)
    {
        lock (_airGate)
        {
            var key = (feed, seq);
            if (!_airDone.Add(key))
                return false;
            _airOrder.Enqueue(key);
            if (kind is not null)
                _airLatest[kind] = key;
            while (_airOrder.Count > AirRemembered)
            {
                var old = _airOrder.Dequeue();
                if (_airLatest.ContainsValue(old))
                    _airOrder.Enqueue(old);   // at most one per kind: the next one out is forgotten
                else
                    _airDone.Remove(old);
            }
            return true;
        }
    }

    private async Task ReceiptWhenRunAsync(DueAir due, long seq, AnchorFollower link)
    {
        var (reply, ran) = await due.Done.Task.WaitAsync(_stop.Token).ConfigureAwait(false);
        await Receipt(link, seq, ran, due.Line, reply).ConfigureAwait(false);
    }

    /// <summary>The member's answer: applied (with the leader instant it took effect here) only when the line says it applied;
    /// accepted when it only started (PREFLIGHT without WAIT); refused with its words (<see cref="ReceiptWords"/>).</summary>
    private Task<bool> Receipt(AnchorFollower link, long seq, long ran, WireLine line, CommandReply reply) =>
        link.ReceiptAsync(seq, !reply.Ok ? "refused" : reply.Applied ? "applied" : "accepted", ran, ReceiptWords(line, reply), _stop.Token);

    /// <summary>On the lane, each tick: queued air lines run from the head while their instant has come, each pinned to it, in the
    /// order they arrived; a silent leader is free-wheeled on.</summary>
    internal void Tick(EngineCore engine, long leaderNs)
    {
        while (true)
        {
            DueAir d;
            lock (_dueGate)
            {
                if (!_due.TryPeek(out var head) || head.AtLeaderNs > leaderNs)
                    break;
                d = _due.Dequeue();
            }
            d.Done.TrySetResult(engine.RunAirLineOnLane(d.Line, d.AtLeaderNs, LeaderSession));
        }
        WatchLeader(engine);
    }

    /// <summary>A follower whose leader has been silent longer than the settings' timeout free-wheels on its last estimate: its
    /// playlists keep switching their own items at the anchored instants; the journal and the context say so, once.</summary>
    private void WatchLeader(EngineCore engine)
    {
        if (_follower is null || !Clock.IsFollowing || Role != "follower")
            return;
        var heard = Clock.HeardLocalNs;
        var silent = Clock.Local.NowNs - heard > _settings.LeaderTimeoutMs * Durations.NsPerMs;
        if (silent && Clock.Hold(heard))
        {
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
    private SessionInfo LeaderSession => new("sync:" + _settings.Leader, EngineCore.LeaderTransport, _leaderAddress, false, $"{(_leaderName.Length > 0 ? _leaderName : "the leader")} (sync leader)", true);

    // ---- The board ---------------------------------------------------------------------------------------------------------

    public void Offer(in VblankReport report, IPAddress from)
    {
        // Only the group's members are heard: the report's node must be a member of this node's feed, subscribed from that address,
        // and a PlayMap node the show network hears saying hello from it.
        var node = report.NodeId;
        var address = NetAddress.Normal(from).ToString();
        var member = Leads && Feed.Members.Any(m => VblankReport.NodeNumber(m.Node) == node && SameAddress(m.Address, address)
            && FindPeer(m.Node) is { } peer && peer.Id == m.Node && SameAddress(peer.Address, address));
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
                if (Volatile.Read(ref _lockWish) is { } wish && Role == "follower")
                    await TakeLockAsync(wish, fresh: false).ConfigureAwait(false);
                if (_engine is { } engine && Leads)
                {
                    // The group's frame lock to the members, whenever it changes (and to each member that joins, as the latest).
                    if (engine.LockRecordNow() is { } record && record.ToJson() is var lockJson && lockJson != _lastLock && _settings.Group.Length > 0)
                    {
                        _lastLock = lockJson;
                        Feed.Publish(_settings.Group, LockRecord.Key, lockJson);
                    }
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
        // A follower's reports are on its leader's time line: none until its clock reads it.
        var reporter = Role == "follower" && Clock.IsFollowing ? _reporter : null;
        if (!leads && reporter is null)
            return;
        var mono = Clock.Local.NowNs;
        var me = _network.Settings.NodeId;
        var node = VblankReport.NodeNumber(me);
        var watch = Interlocked.Increment(ref _watches);
        var every = Math.Max(1, (long)Math.Round(_o.ReportEvery / _o.WatchEvery));
        // Every number on the leader's time base: the period measured on this node's clock scaled by the leader's rate against it
        // (the estimate's drift, not a slew's), and the uncertainty widened by any correction still being slewed.
        var rate = 1 + Clock.Drift;
        var uncertaintyUs = (uint)Math.Clamp(Math.Round((Clock.UncertaintyNs + Clock.SlewingNs) / 1000), 0, VblankReport.MaxClockUncertaintyUs);
        var outputs = source.Read();
        if (leads)
        {
            lock (_boardGate)
            {
                // The leader's own outputs are named in its project's order before any is offered, whatever order the source lists
                // them in: the board's rows and its reference (the first heard) follow that order, never the reports' arrival.
                Board.Name(me, InProjectOrder(outputs));
                foreach (var v in outputs)
                {
                    if (Due(v) is { } report)
                        Board.Offer(report, mono, local: true);
                }
            }
            return;
        }
        foreach (var v in outputs)
        {
            if (Due(v) is { } report)
                reporter!.Send(report);
        }

        VblankReport? Due(OutputVblank v)
        {
            if (_lastSent.TryGetValue(v.OutputId, out var last) && watch - last.AtWatch < every && last.State == v.State)
                return null;
            var seq = _seq.AddOrUpdate(v.OutputId, 1, (_, s) => s + 1);
            var periodPs = Math.Clamp((long)Math.Round(v.PeriodPs * rate), VblankReport.MinPeriodPs, VblankReport.MaxPeriodPs);
            _lastSent[v.OutputId] = (v.State, watch);
            return new VblankReport(v.State, (byte)(v.Settled ? VblankReport.FlagSettled : 0), v.Path, node, VblankReport.HashId(v.OutputId), seq,
                v.FrameCount & 0xFFFFFF, periodPs, Math.Max(0, Clock.ToLeader(v.LastVblankLocalNs)),
                v.RefreshIndex, v.Missed60s, 0, v.AirLatencyUs, uncertaintyUs);
        }
    }

    /// <summary>The ids of this node's reported outputs in its project's order (any the project does not hold after them, as listed).</summary>
    private List<string> InProjectOrder(IReadOnlyList<OutputVblank> outputs)
    {
        var project = _engine?.Snapshot.Project.Project.Outputs ?? [];
        int Index(string id)
        {
            for (var i = 0; i < project.Length; i++)
            {
                if (string.Equals(project[i].Id, id, StringComparison.Ordinal))
                    return i;
            }
            return int.MaxValue;
        }
        return [.. outputs.Select(v => v.OutputId).OrderBy(Index)];
    }

    /// <summary>The board starts again (a lead): the reports it heard go, the names its nodes gave stay, and this node's own outputs
    /// are offered afresh on the next watch. Under the gate a watch's pass offers under, so a pass is wholly before the restart or
    /// wholly after it.</summary>
    private void ResetBoard()
    {
        lock (_boardGate)
        {
            Board.Restart();
            _lastSent.Clear();
        }
    }

    // ---- Following and the role kept --------------------------------------------------------------------------------------

    /// <summary>Opens the link to the leader's feed and the reports to its sync port; answers what it could not open, in words.</summary>
    private async Task<string> StartFollowingAsync(string group, ShowBusPeer peer)
    {
        await StopFollowingAsync().ConfigureAwait(false);
        if (Volatile.Read(ref _disposed) != 0)
            return "; this node is closing";
        // The network host has just moved the exchange to this leader: its lines wait for an estimate heard from here on.
        Interlocked.Exchange(ref _estimatesBefore, Clock.Estimates);
        _adopted.Clear();
        _unmatched.Clear();
        lock (_leaderLists)
            _leaderLists.Clear();
        _leaderName = peer.Name;
        _leaderAddress = peer.Address;
        var said = "";
        var me = _network.Settings;
        var outputs = _engine?.Snapshot.Project.Project.Outputs.Where(o => o.Enabled).Select(o => o.Id) ?? [];
        var wire = peer.Hello.Port("wire");
        if (wire > 0)
        {
            var link = new AnchorFollower(peer.Address, wire, me.Name, string.Join(' ', new[] { me.NodeId }.Concat(outputs)), _wall, _log);
            var lines = Channel.CreateBounded<(string Group, string Key, string Json)>(new BoundedChannelOptions(LinesHeld)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
            _lines = lines;
            Background(TakeLinesAsync(lines, link, _stop.Token));
            link.Subscribing += () => Interlocked.Exchange(ref _estimatesBefore, Clock.Estimates);
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
        Interlocked.Exchange(ref _lines, null)?.Writer.TryComplete();
        _lastSent.Clear();
        lock (_dueGate)
        {
            foreach (var d in _due)
                d.Done.TrySetResult((CommandReply.Fail("this node stopped following before the line's instant"), d.AtLeaderNs));
            _due.Clear();
        }
        Volatile.Write(ref _lockWish, null);
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
                if (_settings.Role != "lead" || !string.Equals(_settings.Group, s.Group, StringComparison.Ordinal) || Leads)
                    return;   // a verb that held the changes chose otherwise
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
                    if (_settings.Role != "follow" || _settings.Leader != s.Leader || Role == "follower")
                        return;   // a verb that held the changes chose otherwise
                    _joining = peer.Id;
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

    /// <summary>Keeps the sync settings for the next start through the network host, the settings file's one writer (it reads the
    /// file fresh under its own lock, so a link, the pairing token or the fleet's role and epoch written beside this change are never
    /// lost, nor is this change by theirs); answers "" or the words of a failure (the change holds for this run either way).</summary>
    private string Persist(Func<SyncSettings, SyncSettings> edit)
    {
        lock (_persistGate)
        {
            var next = edit(_settings);
            _settings = next;
            var failed = _network.PersistSettings(s => s with { Sync = next });
            return failed.Length == 0 ? "" : " — not kept for the next start (" + failed + ")";
        }
    }

    /// <summary>Keeps the "follow Patterns' blackout" switch for the next start (the file is written beside the lane; a failure is
    /// journalled).</summary>
    public void PersistFollowBlackout(bool on) => Background(Task.Run(() =>
    {
        var kept = Persist(s => s with { FollowPatternsBlackout = on });
        if (kept.Length > 0)
            _engine?.Journal.Add("fault", "the follow-blackout switch" + kept, ok: false);
    }, CancellationToken.None));

    private ShowBusPeer? FindPeer(string idOrName)
    {
        var peers = (_network.Hello?.Peers.Snapshot() ?? []).Where(p => p.Kind == ShowBusConstants.KindPlayMap).ToList();
        return peers.FirstOrDefault(p => p.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
            ?? peers.FirstOrDefault(p => p.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SameAddress(string text, string normal) =>
        IPAddress.TryParse(text, out var a) && string.Equals(NetAddress.Normal(a).ToString(), normal, StringComparison.Ordinal);

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
        // A role change in flight finishes first (it may be opening a link this close must then close); bounded, so a stuck one
        // never holds the engine's stop.
        var held = await _changes.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopFollowingAsync().ConfigureAwait(false);
        }
        finally
        {
            if (held)
                _changes.Release();
        }
        Task[] work;
        lock (_work)
            work = [.. _work.Select(t => t.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default))];
        await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        _stop.Dispose();
        _changes.Dispose();
    }
}
