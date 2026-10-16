using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Host;

namespace PlayMap.Engine.Site;

/// <summary>Preflight's site rows: each stack's alignment (<see cref="StackRows"/>), and the model measured against the control points now (SITE CHECK's measure, run fresh on the stage
/// as it stands). Green when every enabled point is within the site's tolerance (max(2σ, 2 cm)); amber or red as the worst point,
/// with the line that takes the worst point out of the solve when it alone disagrees, else SITE CHECK (which names every point). No
/// control points, or no model yet: a green row that says so.</summary>
internal sealed class SitePreflight : IPreflightCheck
{
    public string Section => "site";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(5);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = context.Engine;
        var p = engine.Store.Project;
        var node = engine.Services.Settings.NodeId;
        var stacks = StackRows(p, node);
        if (p.Stage.Controls.Length == 0)
            return Task.FromResult<IReadOnlyList<PreflightRow>>(
                [new PreflightRow("site", node, Section, "Site", Light.Green, "no control points", "nothing to measure the model against", null), .. stacks]);
        return Task.Run<IReadOnlyList<PreflightRow>>(() =>
        {
            var (result, why) = engine.RunSiteCheck(p, null);
            if (result is null)
                return [new PreflightRow("site", node, Section, "Site", Light.Green, "no model", why ?? "", null), .. stacks];
            var bad = result.Points.Where(r => r.Light != Light.Green).OrderByDescending(r => r.DistanceM).ToList();
            var fix = bad.Count == 0 ? null : bad.Count == 1 ? $"SET {bad[0].Id} enabled false" : "SITE CHECK";
            var worst = result.Points.Where(r => double.IsFinite(r.DistanceM)).Select(r => r.DistanceM).DefaultIfEmpty(0).Max();
            return [new PreflightRow("site:check", node, Section, "Model against control points", result.Light,
                string.Create(CultureInfo.InvariantCulture, $"worst {worst * 100:0.#} cm over {result.Points.Count} points"), result.Words, fix), .. stacks];
        }, ct);
    }

    /// <summary>One row per stack: green when every member is aligned to the master within 0.5 px; amber when a member is not aligned or
    /// lands farther, with the line that aligns it from the newest scan of all its members.</summary>
    internal static List<PreflightRow> StackRows(Project p, string node)
    {
        var rows = new List<PreflightRow>();
        foreach (var sk in p.Stage.Stacks)
        {
            var (aligned, residual, _) = EngineCore.StackFacts(p, sk);
            var scan = p.Stage.Scans.LastOrDefault(s => sk.ProjectorIds.All(id => s.ProjectorIds.Contains(id, StringComparer.Ordinal)));
            var fix = scan is null ? $"SCAN START <camera> {string.Join(' ', sk.ProjectorIds)}" : $"STACK ALIGN {sk.Id} FROM {scan.Id}";
            var ok = aligned && residual <= EngineCore.StackResidualMaxPx;
            var words = !aligned ? $"{sk.Name}'s members are not aligned to {p.FindProjector(sk.MasterId)?.Name ?? sk.MasterId}"
                : string.Create(CultureInfo.InvariantCulture, $"{sk.Name}'s worst member lands {residual:0.00} px from the master (within {EngineCore.StackResidualMaxPx:0.0} px is ready)");
            rows.Add(new PreflightRow("stack:" + sk.Id, node, "site", sk.Name, ok ? Light.Green : Light.Amber,
                !aligned ? "not aligned" : string.Create(CultureInfo.InvariantCulture, $"{residual:0.00} px"), words, ok ? null : fix));
        }
        return rows;
    }
}

/// <summary>INDEX's site section: per stack, ALIGN and MATCH keys (from the newest scan of all its members), and the facade guide's
/// next step. Rows from published state only.</summary>
internal sealed class SiteIndex(EngineCore engine) : IIndexContributor
{
    public string Section => "site";

    public IndexPresetHint Hint { get; } = new("Site", "stack",
    [
        new IndexKey("$(label)", "$(id)", "$(light)", "instant"),
    ]);

    public void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartArray();
        foreach (var sk in project.Stage.Stacks)
        {
            var (aligned, residual, matched) = EngineCore.StackFacts(project, sk);
            var scan = project.Stage.Scans.LastOrDefault(s => sk.ProjectorIds.All(id => s.ProjectorIds.Contains(id, StringComparer.Ordinal)));
            var from = scan?.Id ?? "<scan>";
            var alignLight = !aligned ? "amber" : residual > EngineCore.StackResidualMaxPx ? "amber" : "green";
            Row(w, $"STACK ALIGN {sk.Id} FROM {from}", "align", $"ALIGN {sk.Name}", alignLight,
                aligned ? string.Create(CultureInfo.InvariantCulture, $"{residual:0.00} px") : "not aligned");
            Row(w, $"STACK MATCH {sk.Id} FROM {from}", "match", $"MATCH {sk.Name}", matched ? "green" : "grey", matched ? "matched" : "not matched");
        }
        var next = Core.Site.SiteGuide.Next(engine.GuideFor(project));
        Row(w, "SITE GUIDE", "guide", "NEXT", next is null ? "green" : "amber", next is null ? "through" : $"{next.Step}: {next.Words}");
        w.WriteEndArray();
    }

    private static void Row(Utf8JsonWriter w, string id, string kind, string label, string light, string state)
    {
        w.WriteStartObject();
        w.WriteString("id", id);
        w.WriteString("kind", kind);
        w.WriteString("label", label);
        w.WriteString("parent", "site");
        w.WriteString("light", light);
        w.WriteString("state", state);
        w.WriteEndObject();
    }
}

/// <summary>wp2-facade's part of the node: its preflight check in the services; nothing to start.</summary>
internal sealed class SiteExtension : IEngineExtension
{
    public string Name => "site";

    public EngineServices Configure(EngineServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services with { PreflightChecks = [.. services.PreflightChecks, new SitePreflight()] };
    }

    public Task StartAsync(EngineCore engine, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
