using PlayMap.Core.Bus;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Host;

namespace PlayMap.Engine.Fx;

/// <summary>Preflight's effects rows: every slot of a chain on an enabled output, compiled and drawing, none faulted or missing.
/// A faulted or missing slot is red with the line that takes it off the air (bypass); one still compiling is amber; with no
/// renderer the rows are grey. A show with no output effects answers one green row.</summary>
internal sealed class FxPreflight : IPreflightCheck
{
    public string Section => "effects";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(2);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = context.Engine;
        var node = engine.Services.Settings.NodeId;
        var rows = new List<PreflightRow>();
        foreach (var v in engine.Fx.Views(engine.Store.Project).Where(v => v.Owner.Kind == "output" && v.Owner.Drawn && v.State != "bypassed"))
        {
            var (light, fix) = v.State switch
            {
                "ok" => (Light.Green, (string?)null),
                "faulted" or "missing" => (Light.Red, $"FX BYPASS {v.Slot.Id} ON"),
                "compiling" => (Light.Amber, (string?)null),
                _ => (Light.Grey, (string?)null),
            };
            var note = v.State switch
            {
                "ok" => v.Deterministic ? "compiled and drawing" : "compiled and drawing; " + FxBoard.HistoryWords,
                "compiling" => "still compiling: it shows its input until it is in",
                _ => v.Words,
            };
            rows.Add(new PreflightRow("fx:" + v.Slot.Id, node, Section, $"{v.Label} on {v.Owner.Name}", light, v.State, note, fix));
        }
        if (rows.Count == 0)
            rows.Add(new PreflightRow("fx", node, Section, "Effects", Light.Green, "none", "no effect runs on an output", null));
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }
}

/// <summary>wp2-fx's part of the node: its preflight check in the services; nothing to start.</summary>
internal sealed class FxExtension : IEngineExtension
{
    public string Name => "fx";

    public EngineServices Configure(EngineServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services with { PreflightChecks = [.. services.PreflightChecks, new FxPreflight()] };
    }

    public Task StartAsync(EngineCore engine, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
