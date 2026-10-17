using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace PlayMap.App.State;

/// <summary>STATE.air: what the audience sees, beyond the content.</summary>
public sealed record AirState(bool Blackout, bool Frozen, string? Pattern, bool Identify)
{
    public static readonly AirState Normal = new(false, false, null, false);
}

/// <summary>Patterns' video row: the focused playlist's current item.</summary>
public sealed record VideoRow(string File, string Tag, long PositionMs, long LengthMs, long RemainingMs, bool Playing, bool Paused,
    bool Ended, bool Loops, double Rate, string Chip, string Text, long Seq);

public sealed record ItemRef(string Id, string Label, string Source);

/// <summary>One playlist's transport as STATE says it.</summary>
public sealed record PlaylistState(string Id, string Name, bool Focused, string State, int Index, ItemRef? Item, long PositionMs,
    long LengthMs, long RemainingMs, double Rate, bool Loop, string? SyncGroup, string? Leader, long Seq, ItemRef? Next)
{
    public bool IsPlaying => State == "playing";

    public bool IsPaused => State == "paused";

    public bool IsIdle => State is "empty" or "idle" || Item is null;
}

/// <summary>One output as STATE says it. <see cref="Dropped"/> is its late presents in the last 10 s once they pass 0.5 % of its
/// refreshes (0 within that budget); <see cref="LatencyMs"/> its display's input lag as the node holds it (-1 unknown);
/// <see cref="PresentPath"/> how its frames reach the display (outputs[].present.path: "gl", "dxgi", "offscreen", …).</summary>
public sealed record OutputState(string Id, string Name, string Mode, string Display, int Width, int Height, double RefreshHz, bool Open,
    double Fps, long Dropped, double FrameMsP95, string? Pattern, string Light)
{
    public double LatencyMs { get; init; } = -1;

    public string PresentPath { get; init; } = "";
}

public sealed record TransferState(string MediaId, string Name, double Progress, string From, string State);

public sealed record MediaState(int Items, int Missing, bool Ready, ImmutableArray<TransferState> Transfers);

public sealed record ProjectorState(string Id, string Name, bool Solved, double ResidualPx);

public sealed record StageState(int Surfaces, int Meshes, ImmutableArray<ProjectorState> Projectors, int Scans);

/// <summary>A plane or object the scan found, in smart-scan pixels (normalised 0..1), for click-to-surface.</summary>
public sealed record ScanRegion(string Id, string Label, double X, double Y, double W, double H, double Confidence);

public sealed record ScanProjectorProgress(string Id, string Name, double Progress, string State);

/// <summary>STATE.scan: the running (or last) scan. Null when none ran this session.</summary>
public sealed record ScanState(string Id, string State, string Stage, int Pattern, int Patterns, double Progress, double Quality,
    string QualityWords, string Message, ImmutableArray<ScanProjectorProgress> Projectors, ImmutableArray<ScanRegion> Regions)
{
    public bool IsRunning => PlayMap.Core.Calibration.PatternTasks.ScanRunning(State);

    public bool IsDone => State == "done";
}

/// <summary>STATE.sync's clock part. <see cref="OffsetMs"/> is how far this node's reading of its leader's clock can be off (half the
/// best exchange's round trip): an uncertainty, never a measured offset between two machines.</summary>
public sealed record SyncState(string Role, string? Leader, double OffsetMs, double RttMs, double DriftPpm, ImmutableArray<string> Groups)
{
    /// <summary>"± 0.21 ms": the leader-time uncertainty as the desk shows it.</summary>
    public string UncertaintyText => string.Create(CultureInfo.InvariantCulture, $"± {OffsetMs:0.00} ms");

    public static readonly SyncState None = new("solo", null, 0, 0, 0, []);
}

public sealed record TimerState(string State, string Text, double Seconds, string Phase, string Color, string Title, string Message);

public sealed record LinkState(string State, string Host, bool Paired, string Clip, TimerState? Timer)
{
    public static readonly LinkState Off = new("off", "", false, "", null);
}

public sealed record PeerState(string Kind, string Id, string Name, string Light, bool Live, string Headline);

public sealed record ProjectState(string Id, string Name, long Revision, string Path, bool Dirty);

/// <summary>STATE.ai (the assistant's door): whether MCP is served, who is on it, and how many proposals wait for
/// a human.</summary>
public sealed record AiState(bool Mcp, int Clients, int Proposals)
{
    public static readonly AiState Off = new(false, 0, 0);
}

/// <summary>One preview view the node shows now (STATE <c>machine.views[]</c>): its id, what it shows, and the file of the ring its
/// pictures go to ("" while it names none).</summary>
public sealed record ShownView(string Id, string What, string Ring);

/// <summary>The node's STATE, read into small immutable records. Tolerant: a missing key reads as its
/// neutral value, an unknown key is ignored (STATE grows at the end of its objects).</summary>
public sealed record StateSnapshot(
    long Seq,
    string NodeId,
    string Name,
    string Light,
    string Headline,
    AirState Air,
    VideoRow? Video,
    ImmutableArray<PlaylistState> Playlists,
    ImmutableArray<OutputState> Outputs,
    MediaState Media,
    StageState Stage,
    ScanState? Scan,
    SyncState Sync,
    LinkState Patterns,
    LinkState Stagewatch,
    ImmutableArray<PeerState> Peers,
    ProjectState Project,
    AiState Ai)
{
    public static readonly StateSnapshot Empty = new(0, "", "", "grey", "", AirState.Normal, null, [], [],
        new MediaState(0, 0, true, []), new StageState(0, 0, [], 0), null, SyncState.None, LinkState.Off, LinkState.Off, [],
        new ProjectState("", "", 0, "", false), AiState.Off);

    private readonly Dictionary<(string Key, Type Type), object?> _sections = [];

    /// <summary>The whole STATE object as read (a detached copy), for the sections round-2 packages read
    /// themselves; default for the empty snapshot.</summary>
    public JsonElement Root { get; init; }

    /// <summary>STATE <c>machine.views</c>: the preview views the node shows now, each with the ring it names.</summary>
    public ImmutableArray<ShownView> Views { get; init; } = [];

    /// <summary>True when this STATE carries <c>machine.views</c> at all (a node that does not names its rings in its replies only).</summary>
    public bool ListsViews { get; init; }

    /// <summary>A top-level STATE object read once per snapshot and reader type by its package's reader (null element
    /// when the node does not publish it: the package's feature is absent there, so its controls hide). A null result is
    /// kept too.</summary>
    public T Section<T>(string key, Func<JsonElement?, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_sections)
        {
            if (_sections.TryGetValue((key, typeof(T)), out var cached))
                return (T)cached!;
            var value = read(Root.ValueKind == JsonValueKind.Object && Root.TryGetProperty(key, out var e) ? e : null);
            _sections[(key, typeof(T))] = value;
            return value;
        }
    }

    public PlaylistState? FocusedPlaylist
    {
        get
        {
            foreach (var p in Playlists)
                if (p.Focused)
                    return p;
            return Playlists.IsDefaultOrEmpty ? null : Playlists[0];
        }
    }

    public PlaylistState? FindPlaylist(string? id)
    {
        foreach (var p in Playlists)
            if (string.Equals(p.Id, id, StringComparison.Ordinal))
                return p;
        return null;
    }

    public OutputState? FindOutput(string? id)
    {
        foreach (var o in Outputs)
            if (string.Equals(o.Id, id, StringComparison.Ordinal))
                return o;
        return null;
    }

    public static StateSnapshot Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Empty;
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object)
            return Empty;
        var links = r.Obj("links");
        var machine = r.Obj("machine");
        return new StateSnapshot(
            r.Long("seq"),
            r.Str("id"),
            r.Str("name"),
            r.Str("light", "grey"),
            r.Str("headline"),
            ReadAir(r.Obj("air")),
            ReadVideo(r.Obj("video")),
            r.Arr("playlists").Select(ReadPlaylist).ToImmutableArray(),
            r.Arr("outputs").Select(ReadOutput).ToImmutableArray(),
            ReadMedia(r.Obj("media")),
            ReadStage(r.Obj("stage")),
            ReadScan(r.Obj("scan")),
            ReadSync(r.Obj("sync")),
            ReadLink(links?.Obj("patterns")),
            ReadLink(links?.Obj("stagewatch")),
            r.Arr("peers").Select(p => new PeerState(p.Str("t"), p.Str("id"), p.Str("name"), p.Str("light", "grey"), p.Bool("live"), p.Str("headline"))).ToImmutableArray(),
            ReadProject(r.Obj("project")),
            ReadAi(r.Obj("ai")))
        {
            Root = r.Clone(),
            ListsViews = machine is { } m && m.TryGetProperty("views", out var views) && views.ValueKind == JsonValueKind.Array,
            Views = machine is { } listed
                ? listed.Arr("views").Select(v => new ShownView(v.Str("id"), v.Str("what"), v.Str("ring"))).Where(v => v.Id.Length > 0).ToImmutableArray()
                : [],
        };
    }

    private static AirState ReadAir(JsonElement? a) => a is { } e
        ? new AirState(e.Bool("blackout"), e.Bool("frozen"), e.Obj("pattern")?.StrOrNull("kind") ?? e.StrOrNull("pattern"), e.Bool("identify"))
        : AirState.Normal;

    private static VideoRow? ReadVideo(JsonElement? v) => v is { } e
        ? new VideoRow(e.Str("file"), e.Str("tag"), e.Long("positionMs"), e.Long("lengthMs"), e.Long("remainingMs"), e.Bool("playing"),
            e.Bool("paused"), e.Bool("ended"), e.Bool("loops"), e.Num("rate", 1), e.Str("chip"), e.Str("text"), e.Long("seq"))
        : null;

    private static ItemRef? ReadItemRef(JsonElement? i) => i is { } e ? new ItemRef(e.Str("id"), e.Str("label"), e.Str("source")) : null;

    private static PlaylistState ReadPlaylist(JsonElement p) => new(
        p.Str("id"), p.Str("name"), p.Bool("focused"), p.Str("state", "empty"), (int)p.Long("index", -1), ReadItemRef(p.Obj("item")),
        p.Long("positionMs"), p.Long("lengthMs"), p.Long("remainingMs"), p.Num("rate", 1), p.Bool("loop"), p.StrOrNull("syncGroup"),
        p.StrOrNull("leader"), p.Long("seq"), ReadItemRef(p.Obj("next")));

    private static OutputState ReadOutput(JsonElement o) => new(
        o.Str("id"), o.Str("name"), o.Str("mode"), o.Str("display"), (int)o.Long("width"), (int)o.Long("height"), o.Num("refreshHz", 60),
        o.Bool("open"), o.Num("fps"), o.Long("dropped"), o.Num("frameMsP95"), o.StrOrNull("pattern"), o.Str("light", "grey"))
    {
        LatencyMs = o.Num("latencyMs", -1),
        PresentPath = o.Obj("present")?.Str("path") ?? "",
    };

    private static MediaState ReadMedia(JsonElement? m) => m is { } e
        ? new MediaState((int)e.Long("items"), (int)e.Long("missing"), e.Bool("ready", true),
            e.Arr("transfers").Select(t => new TransferState(t.Str("media"), t.Str("name"), t.Num("progress"), t.Str("from"), t.Str("state"))).ToImmutableArray())
        : new MediaState(0, 0, true, []);

    private static StageState ReadStage(JsonElement? s) => s is { } e
        ? new StageState((int)e.Long("surfaces"), (int)e.Long("meshes"),
            e.Arr("projectors").Select(p => new ProjectorState(p.Str("id"), p.Str("name"), p.Bool("solved"), p.Num("residualPx"))).ToImmutableArray(),
            (int)e.Long("scans"))
        : new StageState(0, 0, [], 0);

    private static ScanState? ReadScan(JsonElement? s) => s is { } e
        ? new ScanState(e.Str("id"), e.Str("state"), e.Str("stage"), (int)e.Long("pattern"), (int)e.Long("patterns"), e.Num("progress"),
            e.Num("quality"), e.Str("qualityWords"), e.Str("message"),
            e.Arr("projectors").Select(p => new ScanProjectorProgress(p.Str("id"), p.Str("name"), p.Num("progress"), p.Str("state"))).ToImmutableArray(),
            e.Arr("regions").Select(ReadRegion).ToImmutableArray())
        : null;

    private static ScanRegion ReadRegion(JsonElement g)
    {
        var rect = g.Arr("rect").Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0).ToArray();
        return new ScanRegion(g.Str("id"), g.Str("label"), rect.ElementAtOrDefault(0), rect.ElementAtOrDefault(1),
            rect.ElementAtOrDefault(2), rect.ElementAtOrDefault(3), g.Num("confidence"));
    }

    private static SyncState ReadSync(JsonElement? s) => s is { } e
        ? new SyncState(e.Str("role", "solo"), e.StrOrNull("leader"), e.Num("offsetMs"), e.Num("rttMs"), e.Num("driftPpm"),
            e.Arr("groups").Select(g => g.ValueKind == JsonValueKind.String ? g.GetString() ?? "" : "").ToImmutableArray())
        : SyncState.None;

    private static LinkState ReadLink(JsonElement? l)
    {
        if (l is not { } e)
            return LinkState.Off;
        TimerState? timer = e.Obj("timer") is { } t
            ? new TimerState(t.Str("state"), t.Str("text"), t.Num("seconds"), t.Str("phase"), t.Str("color"), t.Str("title"), t.Str("message"))
            : null;
        return new LinkState(e.Str("state", "off"), e.Str("host"), e.Bool("paired"), e.Str("clip"), timer);
    }

    private static ProjectState ReadProject(JsonElement? p) => p is { } e
        ? new ProjectState(e.Str("id"), e.Str("name"), e.Long("revision"), e.Str("path"), e.Bool("dirty"))
        : new ProjectState("", "", 0, "", false);

    private static AiState ReadAi(JsonElement? a) => a is { } e
        ? new AiState(e.Bool("mcp"), (int)e.Long("clients"), (int)e.Long("proposals"))
        : AiState.Off;
}

/// <summary>Lenient readers over a STATE element: a missing or mistyped key reads as the fallback.</summary>
internal static class JsonRead
{
    public static JsonElement? Obj(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().ToArray()
            : [];

    public static string Str(this JsonElement e, string name, string fallback = "") => StrOrNull(e, name) ?? fallback;

    public static string? StrOrNull(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static long Long(this JsonElement e, string name, long fallback = 0)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return fallback;
        if (v.ValueKind == JsonValueKind.Number)
            return v.TryGetInt64(out var l) ? l : (long)Math.Round(v.GetDouble());
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
            return s;
        return fallback;
    }

    public static double Num(this JsonElement e, string name, double fallback = 0)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return fallback;
        if (v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            return s;
        return fallback;
    }

    public static bool Bool(this JsonElement e, string name, bool fallback = false)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.GetDouble() != 0,
            _ => fallback,
        };
    }
}
