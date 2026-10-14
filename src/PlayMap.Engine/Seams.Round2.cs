using System.Collections.Immutable;
using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;

namespace PlayMap.Engine;

/// <summary>Show Mode as every caller reads it: the router's gate (wp2-show), MCP and the assistant (proposal instead of refusal),
/// Companion's WHOAMI (wp2-companion), optimise and heavy work (refused while armed), the edit overlay (off while armed).</summary>
public interface IShowGate
{
    bool Armed { get; }

    string ArmedBy { get; }

    /// <summary>The session that owns the blind draft, or null.</summary>
    string? BlindOwner { get; }

    int MinGoMs { get; }

    ShowRole RoleOf(SessionInfo session);

    /// <summary>What the show's table says about this line from this session now (pure; published state only).</summary>
    ShowDecision Decide(VerbSpec spec, WireLine line, SessionInfo session);

    /// <summary>The verbs this session may run now (null = every verb) — for greying keys before they are pressed.</summary>
    IReadOnlyCollection<string>? Allowed(SessionInfo session);
}

/// <summary>Rehearsal, everything allowed: the gate before wp2-show lands and on a bare core.</summary>
public sealed class OpenShowGate : IShowGate
{
    public static readonly OpenShowGate Instance = new();

    private OpenShowGate()
    {
    }

    public bool Armed => false;

    public string ArmedBy => "";

    public string? BlindOwner => null;

    public int MinGoMs => 0;

    public ShowRole RoleOf(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Transport == "desk" ? ShowRole.Operator : ShowRole.Remote;
    }

    public ShowDecision Decide(VerbSpec spec, WireLine line, SessionInfo session) => new(ShowVerdict.Allow, null);

    public IReadOnlyCollection<string>? Allowed(SessionInfo session) => null;
}

/// <summary>A degrade gain per output that the air multiplies in (a stack that lost a member dims its blend group); 1 = none.
/// Read on the render thread: lock-free.</summary>
public interface IOutputGains
{
    float GainOf(string outputId);
}

/// <summary>The calibration ledger (wp2-wall) as the packages that make results use it: align and stacks stage and accept output
/// results; PROJECTOR SOLVE and SCAN APPLY record projector results. Every call is an edit on the lane.</summary>
public interface ICalibrationLedger
{
    /// <summary>Records a result and makes it current (applied) or staged; answers its id. The edit that applies the projector's
    /// pose or the output's correction is the same edit (one undo step).</summary>
    Task<(EditResult Result, string SnapshotId)> RecordAsync(SessionInfo session, CalibrationSnapshot snapshot, bool apply, CancellationToken ct);

    Task<EditResult> AcceptAsync(SessionInfo session, string targetOrSnapshotId, CancellationToken ct);

    Task<EditResult> FallbackAsync(SessionInfo session, string targetId, CancellationToken ct);

    QualityScore ScoreOf(Project project, string targetId);
}

/// <summary>One preflight section's checks (machine, outputs, media, mapping, devices, sync, links, safety — wp2-show; video I/O,
/// decode, fleet, control … — their packages), run with a time budget; an overrun is a grey row "did not finish in 5 s".</summary>
public interface IPreflightCheck
{
    string Section { get; }

    PreflightDepth MinDepth { get; }

    TimeSpan Budget { get; }

    Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct);
}

/// <summary>What a preflight check reads: the engine (published state only), how deep the run goes and whether the show is armed.</summary>
public sealed record PreflightContext(EngineCore Engine, PreflightDepth Depth, bool Armed);

/// <summary>Posters by content hash (wp2-optimise makes them; wp2-companion's THUMB reads them first, decoding one frame when
/// absent).</summary>
public interface IPosterStore
{
    bool TryGet(string contentHash, int maxWidth, int maxHeight, out ReadOnlyMemory<byte> jpeg, out int width, out int height);
}

/// <summary>The group's air across machines (wp2-sync implements it; wp2-show and wp2-blind call it). A line sent here runs on
/// every follower and backup of the group this node leads, at the given leader instant, through each member's own gate.</summary>
public interface IFleetFanout
{
    /// <summary>The members a line would reach now (followers and backups, by node id), empty when this node leads no group.</summary>
    IReadOnlyList<string> Members { get; }

    /// <summary>Sends an air line (PANIC…, PANIC CLEAR…, BLACKOUT…, MASTER…, SHOW ARM|DISARM, PREFLIGHT) as an anchored air line;
    /// answers each member's receipt (accepted, applied with its leader instant, or refused with its words; silent members time
    /// out with words).</summary>
    Task<IReadOnlyList<FanoutReceipt>> SendAsync(string line, long atLeaderNs, CancellationToken ct);
}

/// <summary>One member's receipt for a fanned-out air line: who, its stage, the leader instant it applied at, and its words.</summary>
public sealed record FanoutReceipt(string Node, string Name, string Stage, long AppliedLeaderNs, string Words);

/// <summary>A package's section of INDEX (wp2-companion writes INDEX): the entities a controller can act on, and how a deck should
/// lay them out — data, never code. Registered with <see cref="VerbRoutes.Index"/> from the package's Route hook.</summary>
public interface IIndexContributor
{
    /// <summary>The section's key ("fx", "led", "video", "calibration", "eye", "scan", …); one per package section.</summary>
    string Section { get; }

    /// <summary>The preset section the Companion module builds for these rows.</summary>
    IndexPresetHint Hint { get; }

    /// <summary>Writes the section's rows as a JSON array of {"id","kind","label","parent","light","state"} from published state
    /// only (any thread; no lane work).</summary>
    void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w);
}

/// <summary>One key template per row: its label, the line its press sends ("$(id)" is the row's id), the feedback that lights it,
/// and the press shape ("instant", "hold", "double").</summary>
public sealed record IndexKey(string Label, string DownLine, string? Feedback, string Press);

/// <summary>How a deck lays out one INDEX section: its title, the kind of row it shows and a key template per row.</summary>
public sealed record IndexPresetHint(string Title, string RowKind, ImmutableArray<IndexKey> Keys);
