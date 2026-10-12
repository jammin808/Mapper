using System.Collections.Immutable;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
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
}

/// <summary>Everything the render thread needs for one frame, published as one immutable value.</summary>
public sealed record EngineSnapshot(ProjectSnapshot Project, PlaybackSnapshot Playback, AirState Air);

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
        };
        _lane.Faulted += ex => Journal.Add("fault", Journal.Describe(ex), ok: false);
        _ticker = new Timer(_ => _lane.Post(Tick), null, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10));
    }

    public EngineServices Services { get; }

    public ProjectStore Store { get; }

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
        return new ValueTask<CommandReply>(HandleOnLaneAsync(line, session, ct));
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
            Journal.Add("fault", $"{line.Verb}: {Journal.Describe(ex)}", session.ClientName, line.Raw, false);
            return CommandReply.Fail($"the node faulted on this line ({ex.GetType().Name}) — logged");
        }
        if (!reply.Silent && IsJournalled(line))
            Journal.Add(reply.Ok ? "verb" : "refused", reply.Ok ? (reply.Effect.Length > 0 ? reply.Effect : line.Raw) : reply.Payload ?? "refused",
                session.ClientName, line.Raw, reply.Ok);
        return reply;
    }

    private static bool IsJournalled(WireLine line) =>
        VerbCatalog.Match(line) is { } m && m.Spec.Mutates;

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
        _air = next with { Revision = _air.Revision + 1 };
        Republish();
    }

    private void Republish()
    {
        _snapshot = new EngineSnapshot(Store.Current, Playback.Snapshot, _air);
        StateChanged?.Invoke(StateKey());
    }

    private void Tick()
    {
        var now = LeaderNowNs;
        Playback.Tick(Store.Project, now);
        if (_air.IdentifyUntilLeaderNs != 0 && now >= _air.IdentifyUntilLeaderNs)
            SetAir(a => a with { IdentifyUntilLeaderNs = 0 });
    }

    private long MediaDurationNs(string mediaId) => Store.Project.FindMedia(mediaId)?.Info.DurationNs ?? 0;

    public async ValueTask DisposeAsync()
    {
        await _ticker.DisposeAsync().ConfigureAwait(false);
        _lane.Dispose();
        await Journal.DisposeAsync().ConfigureAwait(false);
    }
}
