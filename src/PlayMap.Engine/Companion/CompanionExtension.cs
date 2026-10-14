using PlayMap.Core.Bus;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Host;

namespace PlayMap.Engine.Companion;

/// <summary>Preflight's controllers rows: each Companion connected now, green when it is paired and live, amber when it is connected
/// but not paired (its keys are refused until it proves the node's token — the fix is on Companion's side, so the note says where);
/// a node that announces itself on the network and could not is red with the words of the failure. A show with no Companion
/// connected answers one grey row.</summary>
internal sealed class CompanionPreflight(Func<IReadOnlyList<NodeFault>>? announcer = null) : IPreflightCheck
{
    public string Section => "controllers";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(1);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = context.Engine;
        var node = engine.Services.Settings.NodeId;
        var rows = new List<PreflightRow>();
        foreach (var c in engine.Companion.Clients.All.Where(c => c.Kind == "companion"))
        {
            rows.Add(c.Paired
                ? new PreflightRow("client:" + c.Session, node, Section, c.Name, Light.Green, "paired",
                    c.Module is null ? "paired and live" : $"paired and live (module {c.Module})", null)
                : new PreflightRow("client:" + c.Session, node, Section, c.Name, Light.Amber, "not paired",
                    $"connected from {c.Address} but not paired: its keys are refused — enter this node's pairing token (Network drawer) in Companion's PlayMap connection", null));
        }
        foreach (var f in announcer?.Invoke() ?? [])
            rows.Add(new PreflightRow("announce:" + f.Part, node, Section, "Network announcement", Light.Red, "not announced", f.Words, null));
        if (rows.Count == 0)
            rows.Add(new PreflightRow("controllers", node, Section, "Controllers", Light.Grey, "none", "no Companion is connected", null));
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }
}

/// <summary>wp2-companion's part of the node: the controllers preflight in the services, and the network announcement
/// (<c>_playmap._tcp</c>) for the node's life once the transports are open. A responder that cannot open is a fault in STATE, never a
/// stopped node.</summary>
internal sealed class CompanionExtension(ExtensionSetup setup) : IEngineExtension
{
    private Announcer? _announcer;

    public string Name => "companion";

    public EngineServices Configure(EngineServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services with { PreflightChecks = [.. services.PreflightChecks, new CompanionPreflight(() => _announcer?.Faults() ?? [])] };
    }

    public Task StartAsync(EngineCore engine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (!setup.Settings.Companion.Mdns)
            return Task.CompletedTask;
        _announcer = new Announcer(setup, engine);
        setup.AddFaults?.Invoke(_announcer);
        _announcer.Start();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_announcer is not null)
            await _announcer.DisposeAsync();
    }
}
