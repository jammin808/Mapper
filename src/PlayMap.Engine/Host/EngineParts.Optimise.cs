using Microsoft.Extensions.Logging;
using PlayMap.Engine.Optimise;

namespace PlayMap.Engine.Host;

/// <summary>wp2-optimise's parts in the exe's composition (EngineParts.Standard), added in the spine's order: the optimise
/// service (its queue in the node's folder, its worker this same exe in its "optimise-worker" role) and its preflight check.</summary>
public sealed partial record EngineParts
{
    static partial void AddOptimiseParts(List<EngineExtensionFactory> extensions)
    {
        extensions.Add(setup => new OptimiseExtension(setup, new OptimiseOptions()));
    }
}

/// <summary>The optimise part of a node: made with the node's folder and settings, attached once the engine runs, stopped (its
/// worker asked to end) with the node.</summary>
public sealed class OptimiseExtension(ExtensionSetup setup, OptimiseOptions options) : IEngineExtension
{
    private OptimiseService? _service;

    public string Name => "optimise";

    public EngineServices Configure(EngineServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _service = new OptimiseService(setup.Home, setup.Settings.Optimise, setup.Logs.CreateLogger("optimise"), options);
        return services with { Optimise = _service, PreflightChecks = [.. services.PreflightChecks, new OptimisePreflight()] };
    }

    public Task StartAsync(EngineCore engine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (_service is { } s)
        {
            s.Said += (kind, words, ok) => engine.Journal.Add(kind, words, ok: ok);
            s.Attach(engine);
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => _service?.DisposeAsync() ?? ValueTask.CompletedTask;
}
