using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;
using PlayMap.Engine.Host;

namespace PlayMap.Engine.Wall;

/// <summary>The ledger the services carry before an engine exists: the first engine built from them binds its own, and every call
/// after goes there. Before that, records and accepts are refused with words and a score reads as none. A caller cannot tell two
/// engines built from the same services apart, so once a second engine binds, every call is refused (fail closed) rather than
/// recorded in the wrong engine's project.</summary>
public sealed class LateCalibrationLedger : ICalibrationLedger
{
    private volatile ICalibrationLedger? _bound;
    private volatile bool _shared;

    /// <summary>Binds the engine's ledger once; false when one is bound already (another engine's makes the late ledger shared).</summary>
    public bool Bind(ICalibrationLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        if (Interlocked.CompareExchange(ref _bound, ledger, null) is not { } prior)
            return true;
        if (!ReferenceEquals(prior, ledger))
            _shared = true;
        return false;
    }

    private ICalibrationLedger? Bound => _shared ? null : _bound;

    private EditResult Refused => new(false, _shared
        ? "two engines were built from these services — the calibration ledger cannot tell which one a result is for"
        : "the calibration ledger has no engine yet", 0);

    public Task<(EditResult Result, string SnapshotId)> RecordAsync(SessionInfo session, CalibrationSnapshot snapshot, bool apply, CancellationToken ct) =>
        Bound?.RecordAsync(session, snapshot, apply, ct) ?? Task.FromResult((Refused, ""));

    public Task<EditResult> AcceptAsync(SessionInfo session, string targetOrSnapshotId, CancellationToken ct) =>
        Bound?.AcceptAsync(session, targetOrSnapshotId, ct) ?? Task.FromResult(Refused);

    public Task<EditResult> FallbackAsync(SessionInfo session, string targetId, CancellationToken ct) =>
        Bound?.FallbackAsync(session, targetId, ct) ?? Task.FromResult(Refused);

    public QualityScore ScoreOf(Project project, string targetId) => Bound?.ScoreOf(project, targetId) ?? QualityScore.None;
}

/// <summary>Preflight's calibration rows: each projector an output shows, its grade and whether it has a fallback to return to.
/// F is red with the line that returns to the fallback; C, a staged result waiting or no fallback is amber (no fallback: the line
/// that makes the current calibration one); A and B with a fallback are green. A projector never calibrated is amber.</summary>
internal sealed class WallPreflight : IPreflightCheck
{
    public string Section => "calibration";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(2);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = context.Engine;
        var p = engine.Store.Project;
        var node = engine.Services.Settings.NodeId;
        var rows = new List<PreflightRow>();
        foreach (var pj in p.Stage.Projectors)
        {
            if (!p.Outputs.Any(o => o.Enabled && o.Mode == OutputMode.Projector && string.Equals(o.ProjectorId, pj.Id, StringComparison.Ordinal)))
                continue;
            var current = pj.CalibrationId is { } cid ? p.FindCalibration(cid) : null;
            var grade = current?.Score.Grade ?? "";
            var value = current is null ? "none" : string.Create(CultureInfo.InvariantCulture, $"{grade} {current.Score.Value:0.0}");
            var (light, note, fix) =
                current is null ? (Light.Amber, $"{pj.Name} has no calibration result to return to", (string?)$"CALIBRATION ACCEPT {pj.Id}")
                : grade == "F" ? (Light.Red, current.Score.Words, pj.FallbackId is not null && pj.FallbackId != pj.CalibrationId ? $"CALIBRATION FALLBACK {pj.Id}" : $"PROJECTOR SOLVE {pj.Id}")
                : pj.StagedId is { } staged ? (Light.Amber, $"a result waits for a person: {staged}", $"CALIBRATION ACCEPT {staged}")
                : pj.FallbackId is null ? (Light.Amber, $"{current.Score.Words} — no fallback to return to", $"CALIBRATION ACCEPT {pj.Id}")
                : grade == "C" ? (Light.Amber, current.Score.Words, current.Score.Terms.Skip(1).Where(t => t.Penalty > 0 && t.Fix.Length > 0).OrderByDescending(t => t.Penalty).Select(t => t.Fix).FirstOrDefault()
                    ?? $"CALIBRATION RESOLVE {pj.Id}")
                : (Light.Green, current.Score.Words, null);
            rows.Add(new PreflightRow("calibration:" + pj.Id, node, Section, pj.Name, light, value, note, fix));
        }
        if (rows.Count == 0)
            rows.Add(new PreflightRow("calibration", node, Section, "Calibration", Light.Green, "none", "no output shows a projector", null));
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }
}

/// <summary>wp2-wall's part of the node: its preflight check and the display a camera re-solve shows its dots on, in the services;
/// nothing to start.</summary>
internal sealed class WallExtension(IScanDisplay? display = null) : IEngineExtension
{
    public string Name => "wall";

    public EngineServices Configure(EngineServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services with { WallDisplay = display ?? services.WallDisplay, PreflightChecks = [.. services.PreflightChecks, new WallPreflight()] };
    }

    public Task StartAsync(EngineCore engine, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>INDEX section "calibration": one row per projector with its grade as the light and its score as the state, and a preset
/// per projector — re-solve, back to the fallback, the history.</summary>
internal sealed class CalibrationIndex(EngineCore engine) : IIndexContributor
{
    public string Section => "calibration";

    public IndexPresetHint Hint { get; } = new("Calibration", "projector",
    [
        new IndexKey("Re-solve", "CALIBRATION RESOLVE $(id)", "calibration.grade", "instant"),
        new IndexKey("Fallback", "CALIBRATION FALLBACK $(id)", "calibration.fallback", "hold"),
        new IndexKey("History", "CALIBRATION HISTORY $(id)", null, "instant"),
    ]);

    public void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(w);
        var ledger = engine.CalibrationLedgerService;
        w.WriteStartArray();
        foreach (var pj in project.Stage.Projectors)
        {
            var score = ledger.ScoreOf(project, pj.Id);
            w.WriteStartObject();
            w.WriteString("id", pj.Id);
            w.WriteString("kind", "projector");
            w.WriteString("label", pj.Name);
            w.WriteString("parent", "");
            w.WriteString("light", score.Grade switch
            {
                "A" or "B" => pj.StagedId is null ? "green" : "amber",
                "C" => "amber",
                "F" => "red",
                _ => "grey",
            });
            w.WriteString("state", score.Grade.Length == 0 ? "none" : string.Create(CultureInfo.InvariantCulture, $"{score.Grade} {score.Value:0.0}"));
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
