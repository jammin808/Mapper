using System.Globalization;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Host;

namespace PlayMap.Engine.Show;

/// <summary>Mapping, through the calibration ledger: each projector an enabled output shows, with its current result's grade and a
/// fallback to return to. Never calibrated, or failed with nothing to return to, is red with PROJECTOR SOLVE; failed with a fallback is
/// red with CALIBRATION FALLBACK; C, or no fallback, is amber; A and B with a fallback are green. A node that runs the calibration
/// ledger's own check (wp2-wall's "calibration" rows, one per projector) answers through those rows alone, so one report never
/// gives a projector two verdicts; show-ready's mapping step reads either.</summary>
public sealed class MappingCheck : IPreflightCheck
{
    public string Section => "mapping";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(2);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var e = context.Engine;
        if (e.PreflightChecks.Any(c => c.Section == "calibration"))
            return Task.FromResult<IReadOnlyList<PreflightRow>>([]);
        var node = e.Services.Settings.Name;
        var p = e.Store.Project;
        var ledger = e.CalibrationLedgerService;
        var rows = new List<PreflightRow>();
        foreach (var pj in p.Stage.Projectors)
        {
            if (!p.Outputs.Any(o => o.Enabled && o.Mode == OutputMode.Projector && string.Equals(o.ProjectorId, pj.Id, StringComparison.Ordinal)))
                continue;
            var score = ledger.ScoreOf(p, pj.Id);
            var id = "mapping:" + pj.Id;
            var hasFallback = pj.FallbackId is { } f && f != pj.CalibrationId && p.FindCalibration(f) is not null;
            var value = score.Grade.Length == 0 ? "not solved" : string.Create(CultureInfo.InvariantCulture, $"{score.Grade} {score.Value:0.0} {score.Unit}");
            rows.Add(score.Grade switch
            {
                "" => new PreflightRow(id, node, Section, pj.Name, Light.Red, value, $"{pj.Name} has no solved calibration — its picture lands where its guess says", $"PROJECTOR SOLVE {pj.Id}"),
                "F" when hasFallback => new PreflightRow(id, node, Section, pj.Name, Light.Red, value, $"{score.Words} — return to the last good result", $"CALIBRATION FALLBACK {pj.Id}"),
                "F" => new PreflightRow(id, node, Section, pj.Name, Light.Red, value, score.Words, $"PROJECTOR SOLVE {pj.Id}"),
                "C" => new PreflightRow(id, node, Section, pj.Name, Light.Amber, value, score.Words, $"PROJECTOR SOLVE {pj.Id}"),
                _ when pj.FallbackId is null => new PreflightRow(id, node, Section, pj.Name, Light.Amber, value, $"{score.Words} — no fallback to return to", $"CALIBRATION ACCEPT {pj.Id}"),
                _ => new PreflightRow(id, node, Section, pj.Name, Light.Green, value, score.Words.Length > 0 ? score.Words : "solved, with a fallback", null),
            });
        }
        if (rows.Count == 0)
            rows.Add(new PreflightRow("mapping:none", node, Section, "Mapping", Light.Grey, "none", "no output shows a projector", null));
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }
}

/// <summary>The show clock: this node's role and, following, the uncertainty of its leader time (under 1 ms green, under 4 ms amber,
/// else red); a playlist that follows a sync group while this node leads or follows none is amber with the line that leads it. On
/// a node that runs the sync part's own check (its "sync" rows judge a follower's leader time against the group's bound), a
/// follower's clock is those rows' alone, so one report never gives the clock two lights.</summary>
public sealed class ClockCheck : IPreflightCheck
{
    public string Section => "clock";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(1);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var e = context.Engine;
        var node = e.Services.Settings.Name;
        var sync = e.Services.Network.Sync();
        if (sync.Role == "follower" && e.PreflightChecks.Any(c => c.Section == "sync"))
            return Task.FromResult<IReadOnlyList<PreflightRow>>([]);
        var groups = e.Store.Project.Playlists.Select(l => l.SyncGroup).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var ms = sync.OffsetMs.ToString("0.00", CultureInfo.InvariantCulture);
        PreflightRow row = sync.Role switch
        {
            "follower" when sync.OffsetMs < 1 => new("clock:sync", node, Section, "Show clock", Light.Green, $"± {ms} ms", $"follows {sync.Leader}", null),
            "follower" when sync.OffsetMs < 4 => new("clock:sync", node, Section, "Show clock", Light.Amber, $"± {ms} ms",
                $"follows {sync.Leader} with {ms} ms of uncertainty — a wired network to the leader lowers it", null),
            "follower" => new("clock:sync", node, Section, "Show clock", Light.Red, $"± {ms} ms",
                $"the leader's time is known to {ms} ms only — mirrors will not land on the same frame",
                groups.Count > 0 && sync.Leader is { } leader ? $"SYNC FOLLOW {groups[0]} {leader}" : null),
            "leader" => new("clock:sync", node, Section, "Show clock", Light.Green, "leads", "this node's clock is the group's", null),
            _ when groups.Count > 0 => new("clock:sync", node, Section, "Show clock", Light.Amber, "solo",
                $"{groups.Count} sync group{(groups.Count == 1 ? "" : "s")} named by playlists ({string.Join(", ", groups)}) but this node leads or follows none",
                $"SYNC LEAD {groups[0]}"),
            _ => new("clock:sync", node, Section, "Show clock", Light.Grey, "solo", "this node keeps its own clock", null),
        };
        return Task.FromResult<IReadOnlyList<PreflightRow>>([row]);
    }
}

/// <summary>The links a show listens to: Patterns and Stagewatch heard and paired (not linked is grey; linked but not heard is red
/// with the line that links it again; heard but not paired is amber — its token is a secret, so no line carries it). This node's own
/// pairing token seen in clear from another machine since it was made is one more row, amber, with PAIRING NEW: a sighting refuses
/// nothing, and whoever was on the network between could have read the token.</summary>
public sealed class LinksCheck : IPreflightCheck
{
    public string Section => "links";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(1);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var e = context.Engine;
        var node = e.Services.Settings.Name;
        var net = e.Services.Network;
        List<PreflightRow> rows = [Row(node, "patterns", "Patterns", net.Patterns(), "LINK PATTERNS"), Row(node, "stagewatch", "Stagewatch", net.Stagewatch(), "LINK STAGEWATCH")];
        if (net is ITokenSightings { TokenSeenInClear: { } seen })
            rows.Add(new PreflightRow("links:token", node, Section, "Pairing token", Light.Amber, "seen in clear",
                $"this node's pairing token came in clear from {seen.Seen.From} ({seen.Seen.How}) — anyone on the network between could have read it",
                "PAIRING NEW"));
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }

    private PreflightRow Row(string node, string key, string name, LinkView? link, string verb)
    {
        var id = "links:" + key;
        if (link is null)
            return new PreflightRow(id, node, Section, name, Light.Grey, "not linked", $"this node follows no {name}", null);
        var at = link.Host.Length > 0 ? " at " + link.Host : "";
        return link.State switch
        {
            "live" when link.Paired => new PreflightRow(id, node, Section, name, Light.Green, "live", $"heard{at}{(link.Detail.Length > 0 ? " · " + link.Detail : "")}", null),
            "live" => new PreflightRow(id, node, Section, name, Light.Amber, "not paired",
                $"heard{at} but not paired{(link.Detail.Length > 0 ? " — " + link.Detail : "")}", null),
            _ => new PreflightRow(id, node, Section, name, Light.Red, link.State, $"{name}{at} is not heard ({link.State})",
                link.Host.Length > 0 ? $"{verb} {link.Host}" : null),
        };
    }
}
