using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Playback;
using PlayMap.Net;

namespace PlayMap.Engine;

/// <summary>One line of a package's verb as its handler receives it: the matched row, the words after the verb's own, the whole
/// line and who sent it.</summary>
public sealed record VerbCall(VerbSpec Spec, List<string> Args, WireLine Line, SessionInfo Session, CancellationToken Ct);

/// <summary>What opening a topic gave: a subscription to dispose on OFF or close, or the refusal words.</summary>
public readonly record struct PushOpen(IDisposable? Sub, string? Refused)
{
    public static PushOpen Refuse(string words) => new(null, words);

    public static PushOpen Open(IDisposable sub) => new(sub, null);
}

/// <summary>A show event for automations: its order on this node, its kind, when it happened on the show clock and on the wall
/// clock, and its fields as one JSON object.</summary>
public sealed record ShowEvent(long Seq, string Kind, long AtLeaderNs, long AtUnixMs, string FieldsJson);

/// <summary>The packages' verbs and where each is answered: on the control lane (the default — what it changes is the lane's), or
/// off it (work that waits on a device, a peer, the GPU, a file or a person; what it changes in the project still goes through
/// the lane with <see cref="EngineCore.EditAsync"/>). Built once in the engine's constructor; a row routed twice, or a name
/// that is not a row's own name, throws there. A later round's row is available exactly when it is routed.</summary>
public sealed class VerbRoutes : IVerbAvailability
{
    private readonly Dictionary<string, Func<VerbCall, CommandReply>> _lane = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<VerbCall, Task<CommandReply>>> _offLane = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<IReadOnlyList<string>, SessionInfo, IPushSink, PushOpen>> _topics =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IIndexContributor> _index = new(StringComparer.Ordinal);

    public void Lane(string verb, Func<VerbCall, CommandReply> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Check(verb);
        _lane.Add(verb, handler);
    }

    public void OffLane(string verb, Func<VerbCall, Task<CommandReply>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Check(verb);
        _offLane.Add(verb, handler);
    }

    public bool TryLane(string verb, [MaybeNullWhen(false)] out Func<VerbCall, CommandReply> handler) => _lane.TryGetValue(verb, out handler);

    public bool TryOffLane(string verb, [MaybeNullWhen(false)] out Func<VerbCall, Task<CommandReply>> handler) => _offLane.TryGetValue(verb, out handler);

    public bool IsRouted(string verb) => _lane.ContainsKey(verb) || _offLane.ContainsKey(verb);

    /// <summary>Every row of the first round, and a later round's row once a package routed it.</summary>
    public bool IsAvailable(VerbSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.Round == 1 || IsRouted(spec.Name);
    }

    /// <summary>A SUBSCRIBE topic a package serves (STATE, CONTEXT, PROJECT and OFF stay the session's own).</summary>
    public void Topic(string name, Func<IReadOnlyList<string>, SessionInfo, IPushSink, PushOpen> open)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(open);
        if (name.ToUpperInvariant() is "STATE" or "CONTEXT" or "PROJECT" or "OFF")
            throw new ArgumentException($"{name} is the session's own topic", nameof(name));
        if (name.Contains(' ', StringComparison.Ordinal))
            throw new ArgumentException($"a topic is one word, not '{name}'", nameof(name));
        _topics.Add(name, open);
    }

    public bool TryTopic(string name, [MaybeNullWhen(false)] out Func<IReadOnlyList<string>, SessionInfo, IPushSink, PushOpen> open) =>
        _topics.TryGetValue(name, out open);

    public IEnumerable<string> Topics => _topics.Keys;

    /// <summary>A package's INDEX section: its rows and a preset hint, as data. One contributor per section name; a second
    /// throws here, at construction.</summary>
    public void Index(IIndexContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(contributor);
        if (!_index.TryAdd(contributor.Section, contributor))
            throw new InvalidOperationException($"the INDEX section {contributor.Section} is contributed twice");
    }

    public IEnumerable<IIndexContributor> IndexSections => _index.Values;

    private void Check(string verb)
    {
        if (VerbCatalog.Find(verb) is not { } spec || !string.Equals(spec.Name, verb, StringComparison.Ordinal))
            throw new ArgumentException($"{verb} is not a row of the catalogue", nameof(verb));
        if (IsRouted(verb))
            throw new InvalidOperationException($"{verb} is routed twice");
    }
}

/// <summary>The packages' hooks: each package fills its own in its stub file (EngineCore.&lt;Word&gt;.cs), so the router,
/// STATE, the context, the tick, validation and recovery stay one file each and no package edits another's.</summary>
public sealed partial class EngineCore : IVerbAvailability, IPushTopics
{
    /// <summary>Who the node's own background work is (a scan's record, a recovery): it owns no blind draft.</summary>
    public static readonly SessionInfo SystemSession = new("system", "system", "127.0.0.1", true, "PlayMap", true);

    private readonly VerbRoutes _routes = new();
    private long _eventSeq;

    /// <summary>The verbs, topics and INDEX sections the packages routed from their Route hooks in the constructor; fixed from then
    /// on (VERBS, the prompt and the context are made from it once), so it is not the public surface's to change.</summary>
    internal VerbRoutes Routes => _routes;

    /// <summary>A show event, raised on the thread that saw it.</summary>
    public event Action<ShowEvent>? EventRaised;

    public bool IsAvailable(VerbSpec spec) => _routes.IsAvailable(spec);

    /// <summary>Raises a show event for automations; <paramref name="atLeaderNs"/> is when it happened on the show clock (an item's
    /// end is its scheduled instant, not when the lane noticed).</summary>
    public void RaiseEvent(string kind, long atLeaderNs, Action<Utf8JsonWriter>? fields = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (EventRaised is null)
            return;
        var json = JsonOut(w =>
        {
            w.WriteStartObject();
            fields?.Invoke(w);
            w.WriteEndObject();
        });
        EventRaised?.Invoke(new ShowEvent(Interlocked.Increment(ref _eventSeq), kind, atLeaderNs, Services.Wall.UnixMs, json));
    }

    /// <summary>item.end and item.start for each playlist whose item changed between two playback snapshots (a transport verb on the
    /// same item is not a new item): a scheduled switch at its own instant, any other change at the leader's now.</summary>
    private void RaiseItemEvents(PlaybackSnapshot before, PlaybackSnapshot after)
    {
        if (EventRaised is null)
            return;
        var now = LeaderNowNs;
        foreach (var (id, lane) in after.Playlists)
        {
            before.Playlists.TryGetValue(id, out var old);
            var oldItem = old is { IsIdle: false } ? old.ItemId : null;
            var newItem = lane.IsIdle ? null : lane.ItemId;
            if (string.Equals(oldItem, newItem, StringComparison.Ordinal))
                continue;
            var at = old is { NextItemId: { } next, SwitchAtLeaderNs: var sw } && sw != long.MaxValue
                && string.Equals(next, newItem, StringComparison.Ordinal) && sw <= now ? sw : now;
            if (oldItem is not null)
                RaiseEvent("item.end", at, w => { w.WriteString("playlist", id); w.WriteString("item", oldItem); });
            if (newItem is not null)
                RaiseEvent("item.start", at, w => { w.WriteString("playlist", id); w.WriteString("item", newItem); });
        }
        foreach (var (id, old) in before.Playlists)
            if (!after.Playlists.ContainsKey(id) && !old.IsIdle && old.ItemId is { } gone)
                RaiseEvent("item.end", now, w => { w.WriteString("playlist", id); w.WriteString("item", gone); });
    }

    /// <summary>air.blackout and air.freeze when the air's switches move.</summary>
    private void RaiseAirEvents(AirState before, AirState after)
    {
        if (EventRaised is null)
            return;
        var now = LeaderNowNs;
        if (before.EffectiveBlackout != after.EffectiveBlackout)
            RaiseEvent("air.blackout", now, w => w.WriteBoolean("on", after.EffectiveBlackout));
        if (before.Frozen != after.Frozen)
            RaiseEvent("air.freeze", now, w => w.WriteBoolean("on", after.Frozen));
    }

    IReadOnlyCollection<string> IPushTopics.Topics => [.. _routes.Topics];

    bool IPushTopics.TryOpen(string topic, IReadOnlyList<string> args, SessionInfo session, IPushSink sink,
        out IDisposable? subscription, out string words)
    {
        subscription = null;
        if (!_routes.TryTopic(topic, out var open))
        {
            words = $"{topic} is not a topic ({string.Join(", ", new[] { "STATE", "CONTEXT", "PROJECT", "OFF" }.Concat(_routes.Topics))})";
            return false;
        }
        var opened = open(args, session, sink);
        subscription = opened.Sub;
        words = opened.Refused ?? "";
        return opened.Refused is null;
    }

    /// <summary>Every package's verbs, in this order (called once from the constructor).</summary>
    private void RouteModules()
    {
        RouteShow(_routes); RouteFleet(_routes); RouteSync(_routes); RouteOutput(_routes); RouteDecode(_routes); RouteOptimise(_routes);
        RouteVideoIo(_routes); RouteControl(_routes); RouteCompanion(_routes); RouteFx(_routes); RouteAudio(_routes);
        RouteContent(_routes); RouteLed(_routes); RouteSite(_routes); RouteAlign(_routes); RoutePhone(_routes); RouteWall(_routes);
        // Round 3's, by hook word (docs/round3/PLAN.md §2.3).
        RouteMachine(_routes); RouteGpu(_routes); RouteObserve(_routes); RouteShaders(_routes); RouteUndo(_routes); RouteHandles(_routes);
        RouteCanvas(_routes); RouteGuide(_routes); RouteCapture(_routes);
    }

    /// <summary>Every package's lane work each 10 ms tick, after the playlists' own tick, with the leader's now.</summary>
    private void TickModules(long leaderNs)
    {
        TickShow(leaderNs); TickFleet(leaderNs); TickSync(leaderNs); TickOutput(leaderNs); TickControl(leaderNs); TickAudio(leaderNs);
        TickContent(leaderNs); TickLed(leaderNs); TickWall(leaderNs);
    }

    /// <summary>STATE's new top-level keys, in this order, after "cameras": round 2's, then round 3's (render, observe, previews,
    /// guides). STATE is written on whichever thread asks for it and cached by <see cref="StateKey"/>: every State, Air, Output …
    /// hook reads published state only (never the lane's working fields), a package whose facts move outside the project, the
    /// playback and the air calls <see cref="FactsChanged"/>, and one whose shown values move with time on their own calls
    /// <see cref="Moved"/> when they move.</summary>
    internal void WriteModuleState(Utf8JsonWriter w)
    {
        StateShow(w); StateFleet(w); StateDecode(w); StateVideoIo(w); StateControl(w); StateCompanion(w); StateFx(w);
        StateAudio(w); StateContent(w); StateLed(w); StateWall(w); StateAlign(w); StatePhone(w);
        StateGpu(w); StateObserve(w); StatePreview(w); StateGuide(w);
    }

    /// <summary>Keys added at the end of existing STATE objects, in package order (round 2's, then round 3's).</summary>
    internal void WriteAirExtras(Utf8JsonWriter w) { AirShow(w); AirWall(w); }

    /// <summary>Keys at the end of air.overlay, after wp2-wall's own (handleP95Ms).</summary>
    internal void WriteOverlayExtras(Utf8JsonWriter w) { OverlayHandles(w); }

    /// <summary>Keys at the end of STATE's render block, after wp3-d3d12-core's own (graph): wp3-d3d12-core's StateGpu calls it last
    /// inside the block it writes.</summary>
    internal void WriteRenderExtras(Utf8JsonWriter w) { RenderPlan(w); }

    /// <summary>Keys at the end of fx.compile, after wp2-fx's own (worker), and at the end of the fx block (quarantine).</summary>
    internal void WriteFxCompileExtras(Utf8JsonWriter w) { FxCompileShaders(w); }

    internal void WriteFxExtras(Utf8JsonWriter w) { FxShaders(w); }

    /// <summary>Keys at the end of each output, after the round-2 packages' own: measured (wp2-align), then observed, showing, hand,
    /// passThrough.</summary>
    internal void WriteOutputExtras(Utf8JsonWriter w, Output o)
    {
        OutputOutput(w, o); OutputFx(w, o); OutputAlign(w, o);
        OutputObserve(w, o); OutputShowing(w, o); OutputCanvas(w, o); OutputCapture(w, o);
    }

    internal void WritePlaylistExtras(Utf8JsonWriter w, Playlist list) { PlaylistControl(w, list); }

    /// <summary>STATE.sync's package keys, then <c>light</c> last: the group's light as the sync part lights it, read with its verdict
    /// (absent on a node without the sync part).</summary>
    internal void WriteSyncExtras(Utf8JsonWriter w)
    {
        string? light = null;
        SyncSync(w, ref light);
        SyncOutput(w);
        SyncFleet(w);
        if (light is not null)
            w.WriteString("light", light);
    }

    internal void WriteMediaExtras(Utf8JsonWriter w) { MediaOptimise(w); MediaShowing(w); }

    /// <summary>Keys at the end of each of videoio's inputs, after wp2-videoio's own (capture, latency); in STATE and INPUT LIST
    /// alike.</summary>
    internal void WriteInputExtras(Utf8JsonWriter w, VideoIo.LiveInput input) { InputCapture(w, input); }

    internal void WriteStageExtras(Utf8JsonWriter w) { StageSite(w); }

    internal void WriteProjectorExtras(Utf8JsonWriter w, Projector pj) { ProjectorSite(w, pj); ProjectorWall(w, pj); }

    /// <summary>Keys at the end of the machine block: round 2's, then the identity, capabilities, platform and locked keys, then
    /// install.</summary>
    internal void WriteMachineExtras(Utf8JsonWriter w) { MachineOutput(w); MachineDecode(w); MachineMachine(w); MachineInstall(w); }

    internal void WriteProjectExtras(Utf8JsonWriter w) { ProjectCompanion(w); }

    /// <summary>Every package's context items and links (Eye verbs only), then the fleet package's shaping for a 12-thing window. Built
    /// on whichever thread asks (with STATE, or for CONTEXT): each hook reads published state only.</summary>
    internal void AddModuleContext(List<ContextItem> items, List<ContextLink> links)
    {
        ContextShow(items, links); ContextFleet(items, links); ContextSync(items, links); ContextOutput(items, links); ContextDecode(items, links);
        ContextOptimise(items, links); ContextVideoIo(items, links); ContextControl(items, links); ContextCompanion(items, links);
        ContextFx(items, links); ContextAudio(items, links); ContextContent(items, links); ContextLed(items, links);
        ContextSite(items, links); ContextAlign(items, links); ContextPhone(items, links); ContextWall(items, links);
        ContextMachine(items, links); ContextGpu(items, links); ContextObserve(items, links); ContextGuide(items, links);
        ShapeContext(items, links);
    }

    /// <summary>Words the show and fleet packages put in front of and after the headline.</summary>
    internal string ModuleHeadline(string headline)
    {
        HeadlineShow(ref headline);
        HeadlineFleet(ref headline);
        return headline;
    }

    /// <summary>A test's stand-in for a package's Validate hook (null in a running node), so the spine's test sees the generic check
    /// reach the packages' checks and their words refuse the edit.</summary>
    internal Func<Project, object, string?>? ValidateForTests { get; set; }

    /// <summary>A field the air does not follow yet, refused by its package's check until the package draws it: an edit that moves it
    /// from the stored value (<paramref name="before"/>, absent for an added entity) to anything but its default is answered with
    /// the words of a verb not available yet, so the generic SET and ADD never report as done what the air never follows. A stored
    /// value (a project opened with it) is kept through other edits, and setting the field back to its default is taken.</summary>
    internal static string? NotDrawnYet<T>(string field, T after, T before, T fallback, bool added) =>
        SameValue(after, fallback) || (!added && SameValue(after, before)) ? null : VerbCatalog.NotAvailableYet(field);

    /// <summary>Two values as the project file holds them (a list read back is a new array with the same items).</summary>
    private static bool SameValue<T>(T a, T b) =>
        EqualityComparer<T>.Default.Equals(a, b) || (a is not null && b is not null
            && string.Equals(Core.Model.ProjectJson.SerializeEntity(a), Core.Model.ProjectJson.SerializeEntity(b), StringComparison.Ordinal));

    /// <summary>Each package's check of an entity it owns, at the end of the generic Validate (null = fine).</summary>
    internal string? ValidateModules(Project p, object entity)
    {
        var why = ValidateForTests?.Invoke(p, entity);
        ValidateShow(p, entity, ref why); ValidateFleet(p, entity, ref why); ValidateOutput(p, entity, ref why);
        ValidateDecode(p, entity, ref why); ValidateOptimise(p, entity, ref why); ValidateVideoIo(p, entity, ref why);
        ValidateControl(p, entity, ref why); ValidateFx(p, entity, ref why); ValidateAudio(p, entity, ref why);
        ValidateContent(p, entity, ref why); ValidateLed(p, entity, ref why); ValidateSite(p, entity, ref why);
        ValidateAlign(p, entity, ref why); ValidatePhone(p, entity, ref why); ValidateWall(p, entity, ref why);
        ValidateObserve(p, entity, ref why); ValidateCanvas(p, entity, ref why); ValidateGuide(p, entity, ref why);
        ValidateCapture(p, entity, ref why);
        return why;
    }

    /// <summary>Runtime facts a package keeps across a restart, as one JSON object per package key in the recovery record.</summary>
    internal ImmutableSortedDictionary<string, string> SaveModuleRecovery()
    {
        var into = new SortedDictionary<string, string>(StringComparer.Ordinal);
        RecoverySaveShow(into); RecoverySaveFleet(into); RecoverySaveSync(into); RecoverySaveControl(into); RecoverySaveLed(into);
        return into.ToImmutableSortedDictionary(StringComparer.Ordinal);
    }

    internal void LoadModuleRecovery(IReadOnlyDictionary<string, string> from)
    {
        RecoveryLoadShow(from); RecoveryLoadFleet(from); RecoveryLoadSync(from); RecoveryLoadControl(from); RecoveryLoadLed(from);
    }

    /// <summary>The show's door for a line about to run — on the lane, or at the start of an off-lane verb (reading only published
    /// state there). Sets <paramref name="refused"/> to refuse it with words, or <paramref name="lineStore"/> to send the line's edits
    /// to a blind draft. wp2-show implements it (wp2-blind adds the redirection); its stub lets every line pass.</summary>
    partial void GateLine(VerbSpec spec, WireLine line, SessionInfo session, ref CommandReply? refused, ref ProjectStore? lineStore);

    /// <summary>The fleet's door before the show's: a hot backup or a fenced node refuses a client's line that would change the show it
    /// mirrors, with the way out (wp2-backup). Same places as <see cref="GateLine"/>.</summary>
    partial void GateFleet(VerbSpec spec, WireLine line, SessionInfo session, ref CommandReply? refused);

    /// <summary>The blind draft a session's off-lane edits go to (null = the live store). wp2-blind (show's continuation) fills it in
    /// show's stub.</summary>
    private partial ProjectStore? BlindStoreFor(SessionInfo session);

    /// <summary>What the show package adds to each snapshot the lane publishes: the blind draft for the desk's draft view and a commit
    /// waiting for its boundary (<see cref="EngineSnapshot.Draft"/>, <see cref="EngineSnapshot.Pending"/>). On the lane.</summary>
    partial void SnapshotShow(ref EngineSnapshot snapshot);

    // The hooks. Every one is implemented in its package's stub file with an empty body that holds one comment line, so the stubs
    // land with the spine and each package fills its own.
    partial void RouteShow(VerbRoutes routes);
    partial void RouteFleet(VerbRoutes routes);
    partial void RouteSync(VerbRoutes routes);
    partial void RouteOutput(VerbRoutes routes);
    partial void RouteDecode(VerbRoutes routes);
    partial void RouteOptimise(VerbRoutes routes);
    partial void RouteVideoIo(VerbRoutes routes);
    partial void RouteControl(VerbRoutes routes);
    partial void RouteCompanion(VerbRoutes routes);
    partial void RouteFx(VerbRoutes routes);
    partial void RouteAudio(VerbRoutes routes);
    partial void RouteContent(VerbRoutes routes);
    partial void RouteLed(VerbRoutes routes);
    partial void RouteSite(VerbRoutes routes);
    partial void RouteAlign(VerbRoutes routes);
    partial void RoutePhone(VerbRoutes routes);
    partial void RouteWall(VerbRoutes routes);

    partial void TickShow(long leaderNs);
    partial void TickFleet(long leaderNs);
    partial void TickSync(long leaderNs);
    partial void TickOutput(long leaderNs);
    partial void TickControl(long leaderNs);
    partial void TickAudio(long leaderNs);
    partial void TickContent(long leaderNs);
    partial void TickLed(long leaderNs);
    partial void TickWall(long leaderNs);

    partial void StateShow(Utf8JsonWriter w);
    partial void StateFleet(Utf8JsonWriter w);
    partial void StateDecode(Utf8JsonWriter w);
    partial void StateVideoIo(Utf8JsonWriter w);
    partial void StateControl(Utf8JsonWriter w);
    partial void StateCompanion(Utf8JsonWriter w);
    partial void StateFx(Utf8JsonWriter w);
    partial void StateAudio(Utf8JsonWriter w);
    partial void StateContent(Utf8JsonWriter w);
    partial void StateLed(Utf8JsonWriter w);
    partial void StateWall(Utf8JsonWriter w);
    partial void StateAlign(Utf8JsonWriter w);
    partial void StatePhone(Utf8JsonWriter w);

    partial void AirShow(Utf8JsonWriter w);
    partial void AirWall(Utf8JsonWriter w);
    partial void OutputOutput(Utf8JsonWriter w, Output o);
    partial void OutputFx(Utf8JsonWriter w, Output o);
    partial void OutputAlign(Utf8JsonWriter w, Output o);
    partial void PlaylistControl(Utf8JsonWriter w, Playlist list);
    partial void SyncSync(Utf8JsonWriter w, ref string? light);
    partial void SyncOutput(Utf8JsonWriter w);
    partial void SyncFleet(Utf8JsonWriter w);
    partial void MediaOptimise(Utf8JsonWriter w);
    partial void StageSite(Utf8JsonWriter w);
    partial void ProjectorSite(Utf8JsonWriter w, Projector pj);
    partial void ProjectorWall(Utf8JsonWriter w, Projector pj);
    partial void MachineOutput(Utf8JsonWriter w);
    partial void MachineDecode(Utf8JsonWriter w);
    partial void ProjectCompanion(Utf8JsonWriter w);

    partial void ContextShow(List<ContextItem> items, List<ContextLink> links);
    partial void ContextFleet(List<ContextItem> items, List<ContextLink> links);
    partial void ContextSync(List<ContextItem> items, List<ContextLink> links);
    partial void ContextOutput(List<ContextItem> items, List<ContextLink> links);
    partial void ContextDecode(List<ContextItem> items, List<ContextLink> links);
    partial void ContextOptimise(List<ContextItem> items, List<ContextLink> links);
    partial void ContextVideoIo(List<ContextItem> items, List<ContextLink> links);
    partial void ContextControl(List<ContextItem> items, List<ContextLink> links);
    partial void ContextCompanion(List<ContextItem> items, List<ContextLink> links);
    partial void ContextFx(List<ContextItem> items, List<ContextLink> links);
    partial void ContextAudio(List<ContextItem> items, List<ContextLink> links);
    partial void ContextContent(List<ContextItem> items, List<ContextLink> links);
    partial void ContextLed(List<ContextItem> items, List<ContextLink> links);
    partial void ContextSite(List<ContextItem> items, List<ContextLink> links);
    partial void ContextAlign(List<ContextItem> items, List<ContextLink> links);
    partial void ContextPhone(List<ContextItem> items, List<ContextLink> links);
    partial void ContextWall(List<ContextItem> items, List<ContextLink> links);
    partial void ShapeContext(List<ContextItem> items, List<ContextLink> links);

    partial void HeadlineShow(ref string headline);
    partial void HeadlineFleet(ref string headline);

    partial void ValidateShow(Project p, object entity, ref string? why);
    partial void ValidateFleet(Project p, object entity, ref string? why);
    partial void ValidateOutput(Project p, object entity, ref string? why);
    partial void ValidateDecode(Project p, object entity, ref string? why);
    partial void ValidateOptimise(Project p, object entity, ref string? why);
    partial void ValidateVideoIo(Project p, object entity, ref string? why);
    partial void ValidateControl(Project p, object entity, ref string? why);
    partial void ValidateFx(Project p, object entity, ref string? why);
    partial void ValidateAudio(Project p, object entity, ref string? why);
    partial void ValidateContent(Project p, object entity, ref string? why);
    partial void ValidateLed(Project p, object entity, ref string? why);
    partial void ValidateSite(Project p, object entity, ref string? why);
    partial void ValidateAlign(Project p, object entity, ref string? why);
    partial void ValidatePhone(Project p, object entity, ref string? why);
    partial void ValidateWall(Project p, object entity, ref string? why);

    partial void RecoverySaveShow(IDictionary<string, string> into);
    partial void RecoverySaveFleet(IDictionary<string, string> into);
    partial void RecoverySaveSync(IDictionary<string, string> into);
    partial void RecoverySaveControl(IDictionary<string, string> into);
    partial void RecoverySaveLed(IDictionary<string, string> into);
    partial void RecoveryLoadShow(IReadOnlyDictionary<string, string> from);
    partial void RecoveryLoadFleet(IReadOnlyDictionary<string, string> from);
    partial void RecoveryLoadSync(IReadOnlyDictionary<string, string> from);
    partial void RecoveryLoadControl(IReadOnlyDictionary<string, string> from);
    partial void RecoveryLoadLed(IReadOnlyDictionary<string, string> from);

    // Round 3's hooks, by hook word (docs/round3/PLAN.md §2.3), each implemented in its package's stub file
    // (EngineCore.<Word>.cs; wp3-installer's in Install/EngineCore.Install.cs) with an empty body holding one comment line.
    partial void RouteMachine(VerbRoutes routes);
    partial void RouteGpu(VerbRoutes routes);
    partial void RouteObserve(VerbRoutes routes);
    partial void RouteShaders(VerbRoutes routes);
    partial void RouteUndo(VerbRoutes routes);
    partial void RouteHandles(VerbRoutes routes);
    partial void RouteCanvas(VerbRoutes routes);
    partial void RouteGuide(VerbRoutes routes);
    partial void RouteCapture(VerbRoutes routes);

    partial void StateGpu(Utf8JsonWriter w);
    partial void StateObserve(Utf8JsonWriter w);
    partial void StatePreview(Utf8JsonWriter w);
    partial void StateGuide(Utf8JsonWriter w);

    partial void OverlayHandles(Utf8JsonWriter w);
    partial void RenderPlan(Utf8JsonWriter w);
    partial void FxCompileShaders(Utf8JsonWriter w);
    partial void FxShaders(Utf8JsonWriter w);
    partial void OutputObserve(Utf8JsonWriter w, Output o);
    partial void OutputShowing(Utf8JsonWriter w, Output o);
    partial void OutputCanvas(Utf8JsonWriter w, Output o);
    partial void OutputCapture(Utf8JsonWriter w, Output o);
    partial void MediaShowing(Utf8JsonWriter w);
    partial void InputCapture(Utf8JsonWriter w, VideoIo.LiveInput input);
    partial void MachineMachine(Utf8JsonWriter w);
    partial void MachineInstall(Utf8JsonWriter w);

    partial void ContextMachine(List<ContextItem> items, List<ContextLink> links);
    partial void ContextGpu(List<ContextItem> items, List<ContextLink> links);
    partial void ContextObserve(List<ContextItem> items, List<ContextLink> links);
    partial void ContextGuide(List<ContextItem> items, List<ContextLink> links);

    partial void ValidateObserve(Project p, object entity, ref string? why);
    partial void ValidateCanvas(Project p, object entity, ref string? why);
    partial void ValidateGuide(Project p, object entity, ref string? why);
    partial void ValidateCapture(Project p, object entity, ref string? why);

    /// <summary>VIEW … SHARED &lt;pid&gt;: wp3-desk-gpu answers it (EngineCore.Preview.cs); while <paramref name="reply"/> stays null the
    /// spine refuses it with the words of a form not available yet.</summary>
    partial void ViewShared(List<string> args, SessionInfo session, ref CommandReply? reply);

    /// <summary>What an output shows now, in the words its context item carries after its own ("Front — Intro.mov via Show"), or ""
    /// when nothing is said: wp3-desk-map fills it (EngineCore.Showing.cs). Read on whichever thread builds the context, from published
    /// state only.</summary>
    internal partial string ShowingWords(Output output);
}
