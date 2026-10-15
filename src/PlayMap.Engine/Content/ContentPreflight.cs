using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Content;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Host;

namespace PlayMap.Engine.Content;

/// <summary>Preflight's content rows: every generator on the air within its GPU budget. A generator whose measured 95th percentile is
/// over the node's content budget is amber, over twice it red, with the line that brings it down (half the particles, a lower
/// render scale); a faulted one is red with the line that puts its parameters back; one still settling is amber. With no renderer
/// the rows are grey; a show with nothing on the air answers one green row.</summary>
internal sealed class ContentPreflight : IPreflightCheck
{
    public string Section => "content";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(2);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = context.Engine;
        var node = engine.Services.Settings.NodeId;
        var budget = engine.Services.Settings.Content.BudgetMs;
        var rows = new List<PreflightRow>();
        foreach (var v in engine.Content.Views(engine.Store.Project, engine.LeaderNowNs))
        {
            if (v.State == "faulted")
            {
                rows.Add(new PreflightRow("content:" + v.Generator.Id, node, Section, v.Label, Light.Red, "faulted", v.Words,
                    $"GENERATOR RESET {v.Generator.Id}"));
                continue;
            }
            if (!v.OnAir)
                continue;
            var cost = string.Create(CultureInfo.InvariantCulture, $"{v.GpuMsP95:0.0} ms of {budget:0.#} ms (p95)");
            if (v.GpuMsP95 > budget)
            {
                rows.Add(new PreflightRow("content:" + v.Generator.Id, node, Section, v.Label, v.GpuMsP95 > 2 * budget ? Light.Red : Light.Amber,
                    "over budget", cost + " — over the content budget", Relief(v.Generator)));
                continue;
            }
            if (v.State == "warming")
            {
                rows.Add(new PreflightRow("content:" + v.Generator.Id, node, Section, v.Label, Light.Amber, "warming",
                    string.Create(CultureInfo.InvariantCulture, $"settling: {v.Progress * 100:0} %"), null));
                continue;
            }
            rows.Add(new PreflightRow("content:" + v.Generator.Id, node, Section, v.Label, Light.Green, "ok",
                v.GpuMsP95 > 0 ? cost : "drawing (no GPU timer on this context)", null));
        }
        if (rows.Count == 0)
            rows.Add(engine.Content.Frame is null
                ? new PreflightRow("content", node, Section, "Content", Light.Grey, "no renderer", "no renderer on this node draws generators", null)
                : new PreflightRow("content", node, Section, "Content", Light.Green, "none", "no generator is on the air", null));
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }

    /// <summary>The line that brings a generator under budget: half its particles, a lower render scale, else a calmer quality.</summary>
    public static string? Relief(Generator g)
    {
        ArgumentNullException.ThrowIfNull(g);
        var kind = GeneratorCatalog.Find(g.Kind);
        if (kind is null)
            return null;
        if (kind.Param("count") is { } count)
        {
            var now = ParamValues.Integer(g.Params, kind, "count");
            var half = Math.Max((int)count.Min, now / 2);
            return half < now ? string.Create(CultureInfo.InvariantCulture, $"GENERATOR SET {g.Id} count {half}") : null;
        }
        if (kind.Param("renderScale") is { } scale)
        {
            var now = ParamValues.Number(g.Params, kind, "renderScale");
            var lower = Math.Max(scale.Min, Math.Round(now * 0.66, 2));
            return lower < now ? string.Create(CultureInfo.InvariantCulture, $"GENERATOR SET {g.Id} renderScale {lower}") : null;
        }
        return null;
    }
}

/// <summary>INDEX section "content": one row per generator (its light and state from what the renderer observed), each with a key that
/// restarts its own time.</summary>
internal sealed class ContentIndex(EngineCore engine) : IIndexContributor
{
    public string Section => "content";

    public IndexPresetHint Hint { get; } = new("Content", "generator",
    [
        new IndexKey("Retime", "GENERATOR RETIME $(id)", "content.state", "instant"),
    ]);

    public void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(w);
        var budget = engine.Services.Settings.Content.BudgetMs;
        w.WriteStartArray();
        foreach (var v in engine.Content.Views(project, engine.LeaderNowNs))
        {
            w.WriteStartObject();
            w.WriteString("id", v.Generator.Id);
            w.WriteString("kind", "generator");
            w.WriteString("label", $"{v.Label} · {GeneratorCatalog.Find(v.Generator.Kind)?.Name ?? v.Generator.Kind}");
            w.WriteString("parent", "");
            w.WriteString("light", v.Light(budget).ToString().ToLowerInvariant());
            w.WriteString("state", v.State);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}

/// <summary>INDEX section "sting": one row per sting preset, each a key that fires it on the show clock.</summary>
internal sealed class StingIndex(EngineCore engine) : IIndexContributor
{
    public string Section => "sting";

    public IndexPresetHint Hint { get; } = new("Stings", "sting",
    [
        new IndexKey("Fire", "STING $(id)", "content.sting", "instant"),
    ]);

    public void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        var running = engine.Content.Sting is { } s && engine.LeaderNowNs < s.EndLeaderNs ? s.Preset.Name : null;
        w.WriteStartArray();
        foreach (var p in Stings.All)
        {
            w.WriteStartObject();
            w.WriteString("id", p.Name);
            w.WriteString("kind", "sting");
            w.WriteString("label", p.Name);
            w.WriteString("parent", "");
            w.WriteString("light", p.Name == running ? "green" : "grey");
            w.WriteString("state", p.Name == running ? "surging" : "ready");
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}

/// <summary>wp2-content's part of the node: its preflight check in the services; nothing to start.</summary>
internal sealed class ContentExtension : IEngineExtension
{
    public static readonly EngineExtensionFactory Factory = _ => new ContentExtension();

    public string Name => "content";

    public EngineServices Configure(EngineServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services with { PreflightChecks = [.. services.PreflightChecks, new ContentPreflight()] };
    }

    public Task StartAsync(EngineCore engine, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
