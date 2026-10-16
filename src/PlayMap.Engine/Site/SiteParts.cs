using System.Globalization;
using PlayMap.Core.Bus;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Host;

namespace PlayMap.Engine.Site;

/// <summary>Preflight's site rows: the model measured against the control points now (SITE CHECK's measure, run fresh on the stage
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
        if (p.Stage.Controls.Length == 0)
            return Done(new PreflightRow("site", node, Section, "Site", Light.Green, "no control points", "nothing to measure the model against", null));
        return Task.Run<IReadOnlyList<PreflightRow>>(() =>
        {
            var (result, why) = engine.RunSiteCheck(p, null);
            if (result is null)
                return [new PreflightRow("site", node, Section, "Site", Light.Green, "no model", why ?? "", null)];
            var bad = result.Points.Where(r => r.Light != Light.Green).OrderByDescending(r => r.DistanceM).ToList();
            var fix = bad.Count == 0 ? null : bad.Count == 1 ? $"SET {bad[0].Id} enabled false" : "SITE CHECK";
            var worst = result.Points.Where(r => double.IsFinite(r.DistanceM)).Select(r => r.DistanceM).DefaultIfEmpty(0).Max();
            return [new PreflightRow("site:check", node, Section, "Model against control points", result.Light,
                string.Create(CultureInfo.InvariantCulture, $"worst {worst * 100:0.#} cm over {result.Points.Count} points"), result.Words, fix)];
        }, ct);
    }

    private static Task<IReadOnlyList<PreflightRow>> Done(PreflightRow row) => Task.FromResult<IReadOnlyList<PreflightRow>>([row]);
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
