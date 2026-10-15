using System.Collections.Immutable;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Show;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Playback;

namespace PlayMap.Engine;

/// <summary>What the audience-facing switches say. Checked before any content code runs: blackout wins over
/// everything, then a pattern override, then the show.</summary>
public sealed record AirState(
    bool Blackout,
    bool Frozen,
    /// <summary>A test pattern over outputs ("pattern.grid"); null = the show.</summary>
    string? PatternKind,
    /// <summary>The outputs the pattern covers; empty = all.</summary>
    ImmutableArray<string> PatternOutputs,
    /// <summary>Show each output's name and number until this leader instant (0 = off).</summary>
    long IdentifyUntilLeaderNs,
    /// <summary>A Patterns desk's blackout fact (from its Interactive fact lines or its STATE).</summary>
    bool PatternsBlackout,
    bool FollowPatternsBlackout,
    long Revision)
{
    public static readonly AirState Initial = new(false, false, null, [], 0, false, true, 0);

    public bool EffectiveBlackout => Blackout || (FollowPatternsBlackout && PatternsBlackout);

    public bool PatternCovers(string outputId) =>
        PatternKind is not null && (PatternOutputs.IsDefaultOrEmpty || PatternOutputs.Contains(outputId, StringComparer.Ordinal));

    /// <summary>The grand master over every output (wp2-show).</summary>
    public AirFade Master { get; init; } = AirFade.Full;

    /// <summary>A panic in force, or null (wp2-show).</summary>
    public PanicAir? Panic { get; init; }

    /// <summary>The edit overlay on the wall (wp2-wall).</summary>
    public OverlayState Overlay { get; init; } = OverlayState.Off;
}

/// <summary>Everything the render thread needs for one frame, published as one immutable value.</summary>
public sealed record EngineSnapshot(ProjectSnapshot Project, PlaybackSnapshot Playback, AirState Air)
{
    /// <summary>The blind draft, for the desk's draft view only (never an output) (wp2-blind).</summary>
    public ProjectSnapshot? Draft { get; init; }

    /// <summary>A blind commit scheduled at a leader instant: the render thread draws its root from that frame on (wp2-blind).</summary>
    public PendingCommit? Pending { get; init; }
}

/// <summary>A blind commit waiting for its boundary: the root it publishes, the leader instant it takes effect, its label and how
/// many changes it carries.</summary>
public sealed record PendingCommit(Project Root, long EffectiveLeaderNs, string Label, int Changes);

/// <summary>Actions the engine asks of the network edge (verbs that change links and sync).</summary>
public interface INetworkControl
{
    Task<string> LinkPatternsAsync(string? host, string? token, CancellationToken ct);

    Task<string> LinkStagewatchAsync(string? host, string? key, CancellationToken ct);

    Task<string> LeadAsync(string group, CancellationToken ct);

    Task<string> FollowAsync(string group, string nodeId, CancellationToken ct);

    /// <summary>Starts pulling missing media from peers; answers what was queued.</summary>
    Task<string> FetchAsync(IReadOnlyList<MediaItem> items, string? fromNode, CancellationToken ct);
}

/// <summary>The services an engine is built from. Every edge is a seam: a headless node or a test passes
/// fakes, the real exe passes the modules. Partial: a module's later services are init properties in their
/// own file, so adding one never moves the positional ones.</summary>
public sealed partial record EngineServices(
    NodeSettings Settings,
    IMonoClock Mono,
    ShowWallClock Wall,
    NetworkClock Clock,
    IMediaBackend? Media,
    IOutputBackend Outputs,
    IScanBackend Scan,
    INetworkView Network,
    INetworkControl? NetworkControl,
    string? JournalPath,
    string? ProjectFolder)
{
    public static EngineServices ForTests(IMonoClock? mono = null)
    {
        var m = mono ?? new ManualClock(1_000_000_000);
        var wall = new ShowWallClock(m, 1_760_000_000_000);
        return new EngineServices(NodeSettings.CreateDefault("Test node") with { NodeId = "0badc0de", Token = "" }, m, wall,
            new NetworkClock(m), null, NullBackends.Instance, NullBackends.Instance, NullBackends.Instance, null, null, null);
    }
}

/// <summary>The node. One instance per engine process: the project's single writer, the playlists'
/// transports, the air, the journal — and the <see cref="IWireEndpoint"/> every transport hands its lines to.
/// All state is written on the <see cref="ControlLane"/>; readers take <see cref="Snapshot"/>.</summary>
public sealed partial class EngineCore : IWireEndpoint, IAsyncDisposable
{
    private readonly ControlLane _lane = new();
    private readonly Timer _ticker;
    private readonly Lock _stateGate = new();
    private volatile EngineSnapshot _snapshot;
    private AirState _air = AirState.Initial;
    private long _factsRevision;
    private (long Key, string Json) _stateCache = (-1, "");
    private string? _projectPath;
    private bool _dirty;
    private int _stopping;

    /// <summary>The store this line's document edits go to while it runs on the lane (a session's blind draft), else null.</summary>
    private ProjectStore? _lineStore;

    public EngineCore(EngineServices services, Project? project = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
        Store = new ProjectStore(project ?? Project.CreateDefault(services.Settings.Name));
        Journal = new Journal(services.JournalPath, () => services.Wall.UnixMs);
        Playback = new PlaybackLanes(MediaDurationNs);
        Playback.Reconcile(Store.Project);
        _snapshot = new EngineSnapshot(Store.Current, Playback.Snapshot, _air);
        Store.Changed += OnProjectChanged;
        var lastPlayback = Playback.Snapshot;
        Playback.Changed += snap =>
        {
            var before = lastPlayback;
            lastPlayback = snap;
            Republish();
            PublishAnchors(before, snap);
            RaiseItemEvents(before, snap);
        };
        _lane.Faulted += ex => Journal.Add("fault", Journal.Describe(ex), ok: false);
        RouteModules();
        _ticker = new Timer(_ => _lane.Post(Tick), null, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10));
    }

    public EngineServices Services { get; }

    public ProjectStore Store { get; }

    /// <summary>Where this line's document edits go: the session's blind draft while it edits blind, else the live store. Set and
    /// cleared inside the router on the lane only; transport, air, show state and runtime facts use <see cref="Store"/>.</summary>
    internal ProjectStore Edits => _lineStore ?? Store;

    /// <summary>An off-lane verb's document edit: posted to the lane, applied to the session's blind draft or the live store, answered
    /// with the store's own result (refusals included).</summary>
    public Task<EditResult> EditAsync(SessionInfo session, string label, Func<Project, Project> edit, string? coalesceKey = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _lane.RunAsync(() => EditsFor(session).Apply(label, edit, coalesceKey));
    }

    /// <summary>On the lane, for a line answered off it: the store its document edits go to (the session's blind draft, else live).</summary>
    private ProjectStore EditsFor(SessionInfo session) => DraftOf(session) ?? Store;

    /// <summary>The blind draft a session's document edits go to, or null for the live store: the show's (wp2-blind), or a test's
    /// stand-in for it.</summary>
    private ProjectStore? DraftOf(SessionInfo session) => DraftForTests?.Invoke(session) ?? BlindStoreFor(session);

    /// <summary>A test's stand-in for the show's blind draft: the store a session's document edits go to, on the lane and off it,
    /// through the same router (null in a running node, where the show's gate decides).</summary>
    internal Func<SessionInfo, ProjectStore?>? DraftForTests { get; set; }

    /// <summary>On the lane, an off-lane verb's document edit with <see cref="Edits"/> on the session's store for its length, so the
    /// handler reads what it checks and writes its edit in one store (the session's blind draft, else live).</summary>
    private T EditsInto<T>(SessionInfo session, Func<T> handler)
    {
        _lineStore = DraftOf(session);
        try
        {
            return handler();
        }
        finally
        {
            _lineStore = null;
        }
    }

    public PlaybackLanes Playback { get; }

    public Journal Journal { get; }

    public ControlLane Lane => _lane;

    /// <summary>The frame's inputs for the render thread: lock-free, never torn.</summary>
    public EngineSnapshot Snapshot => _snapshot;

    public AirState Air => _air;

    /// <summary>The leader's clock now (this node's own when it leads nothing).</summary>
    public long LeaderNowNs => Services.Clock.NowNs;

    public string? ProjectPath => _projectPath;

    public bool Dirty => _dirty;

    public event Action<long>? StateChanged;

    public event Action<long>? ProjectChanged;

    /// <summary>True while any playlist plays (the wire pushes STATE at least every second then).</summary>
    public bool IsAnimating => Playback.Snapshot.Playlists.Values.Any(p => p.Clip.State == Core.Playback.TransportState.Playing);

    /// <summary>What every line hears once the node has begun to stop (<see cref="BeginStop"/>).</summary>
    internal const string StoppingWords = "the node is stopping — it takes no more lines";

    /// <summary>True once the node has begun to stop: no line from outside the node runs on it from then on.</summary>
    internal bool Stopping => Volatile.Read(ref _stopping) != 0;

    /// <summary>The node stops taking lines. From this call on, every line that enters — through <see cref="HandleAsync"/> (the wire,
    /// the web port, MCP, OSC, the assistant, an approval) or fired by a binding (<see cref="RunBindingLineAsync"/>) — and every line
    /// still waiting for the lane is refused with <see cref="StoppingWords"/>. The host calls it as it begins to stop, before it closes
    /// any door or part, so no line source it closes later (a binding, an OSC socket, a link) can start work on a part it has closed
    /// already. The node's own work still runs: its own lines (<see cref="SystemSession"/>: a panic's shutters and effects, sent on
    /// the panic's instant while the projector links are still open — a panic put back after a restart takes its shutters as
    /// closed), and its last work (the recovery record, the journal), which goes to the lane and the journal directly, never as a
    /// line. Thread-safe; a second call changes nothing.</summary>
    internal void BeginStop() => Interlocked.Exchange(ref _stopping, 1);

    /// <summary>True when a line from this session is refused because the node has begun to stop: any line but the node's own.</summary>
    private bool RefusedAsStopping(SessionInfo session) =>
        Stopping && !string.Equals(session.Transport, SystemSession.Transport, StringComparison.Ordinal);

    /// <summary>A line that arrived once the node began to stop: refused with the words, and journalled as any refusal of its verb is
    /// (when <paramref name="journal"/>: a binding's gesture is journalled by its owner, once) — an operations line with every
    /// secret the node holds masked, as <see cref="RunOpsAsync"/> keeps it.</summary>
    private CommandReply RefusedStopping(WireLine line, SessionInfo session, bool journal)
    {
        if (journal && VerbCatalog.Match(line) is { } m && IsJournalled(m.Spec))
        {
            var shown = IsOpsVerb(m.Spec) && !_routes.TryOffLane(m.Spec.Name, out _) ? OpsShown(m.Spec, line) : VerbCatalog.Redacted(line);
            Journal.Add("refused", StoppingWords, session.ClientName, shown, false);
        }
        return CommandReply.Fail(StoppingWords);
    }

    /// <summary>Network facts and output health changed (the edges call this; STATE's revision moves).</summary>
    public void FactsChanged()
    {
        Interlocked.Increment(ref _factsRevision);
        StateChanged?.Invoke(StateKey());
    }

    public ValueTask<CommandReply> HandleAsync(WireLine line, SessionInfo session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(session);
        if (ct.IsCancellationRequested)
            return ValueTask.FromResult(CommandReply.Fail("cancelled"));
        if (RefusedAsStopping(session))
            return ValueTask.FromResult(RefusedStopping(line, session, journal: true));
        // A shown copy (its secret words replaced by the marker) stands for a line kept elsewhere: it never runs.
        if (VerbCatalog.IsShownCopy(line))
            return ValueTask.FromResult(CommandReply.Fail("this is a shown copy — its secret words were kept back where it was shown; "
                + "the line runs only from the record that holds them (APPROVE its proposal)"));
        if (TryOffLane(line, session, ct, out var offLane))
            return new ValueTask<CommandReply>(offLane);
        if (TryOps(line, session, ct, out var ops))
            return new ValueTask<CommandReply>(ops);
        return new ValueTask<CommandReply>(HandleOnLaneAsync(line, session, ct));
    }

    /// <summary>True with the answer under way when a package answers the line's verb off the lane.</summary>
    private bool TryOffLane(WireLine line, SessionInfo session, CancellationToken ct, out Task<CommandReply> reply)
    {
        if (VerbCatalog.Match(line) is not { } m || !_routes.TryOffLane(m.Spec.Name, out var handler))
        {
            reply = Task.FromResult(CommandReply.Fail("not answered off the lane"));
            return false;
        }
        reply = RunOffLaneAsync(m.Spec, handler, line.Words.Skip(m.Words).ToList(), line, session, ct);
        return true;
    }

    /// <summary>A package's off-lane verb: pairing, the node-to-node rule and the show's gate (reading published state) first, then
    /// its handler; faults and the journal follow the router's rules. Its document edits go through <see cref="EditAsync"/>.</summary>
    private async Task<CommandReply> RunOffLaneAsync(VerbSpec spec, Func<VerbCall, Task<CommandReply>> handler, List<string> args, WireLine line,
        SessionInfo session, CancellationToken ct)
    {
        CommandReply reply;
        try
        {
            reply = Admission(spec, session) ?? Gate(spec, line, session) ?? await handler(new VerbCall(spec, args, line, session, ct)).ConfigureAwait(false);
        }
        catch (EditRefusedException refused)
        {
            reply = CommandReply.Fail(refused.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            reply = CommandReply.Fail("cancelled — whether it took effect is not known; check STATE");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Journal.Add("fault", $"{spec.Name}: {Journal.Describe(ex)}", session.ClientName, VerbCatalog.Redacted(line), false);
            return CommandReply.Fail($"the node faulted on this line ({ex.GetType().Name}) — logged");
        }
        if (!reply.Silent && IsJournalled(spec))
            Journal.Add(reply.Ok ? "verb" : "refused", reply.Ok ? (reply.Effect.Length > 0 ? reply.Effect : VerbCatalog.Redacted(line)) : reply.Payload ?? "refused",
                session.ClientName, VerbCatalog.Redacted(line), reply.Ok);
        return reply;
    }

    /// <summary>The show's gate for a line answered off the lane: its refusal, or null to run it (an off-lane handler sends its edits to
    /// the session's draft itself, through <see cref="EditAsync"/>).</summary>
    private CommandReply? Gate(VerbSpec spec, WireLine line, SessionInfo session)
    {
        CommandReply? refused = null;
        ProjectStore? lineStore = null;
        GateLine(spec, line, session, ref refused, ref lineStore);
        return refused;
    }

    private async Task<CommandReply> HandleOnLaneAsync(WireLine line, SessionInfo session, CancellationToken ct)
    {
        CommandReply reply;
        try
        {
            reply = await _lane.RunAsync(() => Route(line, session, ct)).ConfigureAwait(false);
        }
        catch (EditRefusedException refused)
        {
            // A handler refusing a word it cannot take: a refusal like any other, not a fault.
            reply = CommandReply.Fail(refused.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Journal.Add("fault", $"{line.Verb}: {Journal.Describe(ex)}", session.ClientName, VerbCatalog.Redacted(line), false);
            return CommandReply.Fail($"the node faulted on this line ({ex.GetType().Name}) — logged");
        }
        if (!reply.Silent && IsJournalled(line))
            Journal.Add(reply.Ok ? "verb" : "refused", reply.Ok ? (reply.Effect.Length > 0 ? reply.Effect : VerbCatalog.Redacted(line)) : reply.Payload ?? "refused",
                session.ClientName, VerbCatalog.Redacted(line), reply.Ok);
        return reply;
    }

    private static bool IsJournalled(WireLine line) =>
        VerbCatalog.Match(line) is { } m && IsJournalled(m.Spec);

    /// <summary>A verb that changes something is journalled, except an ephemeral one (cursors, focus, selection); a gesture is
    /// journalled by its owner once per gesture.</summary>
    private static bool IsJournalled(VerbSpec spec) => spec.Mutates && !spec.Ephemeral;

    public (string Json, long Revision) State()
    {
        var key = StateKey();
        // Positions move with time while anything plays: the cache then also keys on a 20 ms quantum of the
        // leader's clock, so a reader never gets a position older than one frame at 50 Hz.
        var cacheKey = IsAnimating || _air.IdentifyUntilLeaderNs != 0 ? HashCode.Combine(key, LeaderNowNs / 20_000_000) : key;
        lock (_stateGate)
        {
            if (_stateCache.Key != cacheKey)
                _stateCache = (cacheKey, StateWriter.Write(this));
            return (_stateCache.Json, key);
        }
    }

    public string Context() => ContextBuilder.Build(this).ToJson();

    public (string Json, long Revision) ProjectJson()
    {
        var snap = Store.Current;
        return (Core.Model.ProjectJson.SerializeEntity(snap.Project), snap.Revision);
    }

    /// <summary>One number that moves when anything STATE shows moves.</summary>
    public long StateKey() =>
        Store.Current.Revision * 1_000_003 + Playback.Snapshot.Revision * 10_007 + _air.Revision * 101 + Interlocked.Read(ref _factsRevision);

    private void OnProjectChanged(ProjectSnapshot snap)
    {
        // Runs on whichever thread applied the edit — always the lane, the store's only caller.
        _dirty = true;
        Playback.Reconcile(snap.Project);
        Republish();
        ProjectChanged?.Invoke(snap.Revision);
    }

    private void SetAir(Func<AirState, AirState> edit)
    {
        var next = edit(_air);
        if (next == _air)
            return;
        var before = _air;
        _air = next with { Revision = _air.Revision + 1 };
        Republish();
        RaiseAirEvents(before, _air);
    }

    private void Republish()
    {
        var snapshot = new EngineSnapshot(Store.Current, Playback.Snapshot, _air);
        SnapshotShow(ref snapshot);
        _snapshot = snapshot;
        StateChanged?.Invoke(StateKey());
    }

    private void Tick()
    {
        var now = LeaderNowNs;
        Playback.Tick(Store.Project, now);
        if (_air.IdentifyUntilLeaderNs != 0 && now >= _air.IdentifyUntilLeaderNs)
            SetAir(a => a with { IdentifyUntilLeaderNs = 0 });
        ExpireViews();
        TickModules(now);
    }

    private long MediaDurationNs(string mediaId) => Store.Project.FindMedia(mediaId)?.Info.DurationNs ?? 0;

    public async ValueTask DisposeAsync()
    {
        BeginStop();
        StopSolving();
        await _ticker.DisposeAsync().ConfigureAwait(false);
        _lane.Dispose();
        await Journal.DisposeAsync().ConfigureAwait(false);
    }
}
