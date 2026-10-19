using Microsoft.Extensions.Logging;
using PlayMap.Core.Bus;
using PlayMap.Core.Effects;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Host;

namespace PlayMap.Engine.ShaderWorker;

/// <summary>Preflight's quarantine rows (section fx): each quarantined kind red with the line that lifts it (FX RELEASE by the kind's
/// hash, so each row lifts its own entry whatever its name), and each slot of the show that uses one red with the line that takes it
/// out (FX REMOVE). A record that could not be read whole at start is red too: the node does not know every kind it held. Only a node
/// that read its whole record and quarantines nothing answers the one green row.</summary>
internal sealed class QuarantinePreflight : IPreflightCheck
{
    public string Section => "fx";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(2);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = context.Engine;
        var node = engine.Services.Settings.NodeId;
        var quarantine = engine.Services.Quarantine;
        var rows = new List<PreflightRow>();
        if (quarantine.LoadWords.Length > 0)
            rows.Add(new PreflightRow("fx.quarantine.record", node, Section, "The quarantine record could not be read", Light.Red, "unreadable",
                quarantine.LoadWords, null));
        var slots = engine.QuarantinedSlots(engine.Store.Project);
        foreach (var e in quarantine.All)
        {
            var users = slots[e.KindHash].ToList();
            var drawn = users.Count == 0 ? "no slot of this show uses it" : $"{users.Count} slot{(users.Count == 1 ? " uses" : "s use")} it";
            rows.Add(new PreflightRow("fx.quarantine:" + e.KindHash, node, Section, $"{e.Kind} is quarantined", Light.Red,
                e.ReasonWord, $"{e.Words}; {drawn}", $"FX RELEASE {e.KindHash}"));
            foreach (var s in users)
                rows.Add(new PreflightRow("fx.quarantine:" + s.Id, node, Section, $"{(s.Name.Length > 0 ? s.Name : e.Kind)} uses a quarantined kind",
                    Light.Red, "quarantined", $"its kind {e.Kind} is quarantined on this node (its slot's state says what the renderer draws)", $"FX REMOVE {s.Id}"));
        }
        if (rows.Count == 0)
            rows.Add(new PreflightRow("fx.quarantine", node, Section, "Quarantine", Light.Green, "none", "no effect kind is quarantined on this node", null));
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }
}

/// <summary>wp3-shaders' services on a node: the quarantine and the shader worker as FX CHECK's stage check, with the quarantine's
/// preflight rows.</summary>
public static class ShaderParts
{
    /// <summary>The services with the quarantine, the worker (null: none) and the preflight check set.</summary>
    public static EngineServices Compose(EngineServices services, ShaderQuarantine quarantine, ShaderWorkerHost? worker)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(quarantine);
        return services with
        {
            Quarantine = quarantine,
            ShaderWorker = worker,
            FxStages = worker ?? services.FxStages,
            PreflightChecks = [.. services.PreflightChecks, new QuarantinePreflight()],
        };
    }

    /// <summary>The quarantine record's place in the node's home.</summary>
    public static string QuarantinePath(NodeHome home)
    {
        ArgumentNullException.ThrowIfNull(home);
        return Path.Combine(home.Folder, "playmap.quarantine.json");
    }

    /// <summary>The engine told of every change STATE reads from the quarantine and the worker.</summary>
    public static void Attach(EngineCore engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.Services.Quarantine.Changed += engine.FactsChanged;
        if (engine.Services.ShaderWorker is { } worker)
            worker.Changed += engine.FactsChanged;
        if (engine.Services.Quarantine.LoadWords is { Length: > 0 } words)
            engine.Journal.Add("fx.quarantine", words, ok: false);
    }
}

/// <summary>wp3-shaders' part of a node: the quarantine kept in its home and the shader worker (this exe in its "shader-worker" role),
/// stopped with the node.</summary>
public sealed class ShadersExtension(ExtensionSetup setup, ShaderWorkerOptions options) : IEngineExtension
{
    private ShaderWorkerHost? _worker;

    public string Name => "shaders";

    public EngineServices Configure(EngineServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var quarantine = new ShaderQuarantine(ShaderParts.QuarantinePath(setup.Home));
        _worker = new ShaderWorkerHost(options, quarantine, setup.Logs.CreateLogger("shaders"), () => setup.Wall.UnixMs, services.Build.Informational);
        return ShaderParts.Compose(services, quarantine, _worker);
    }

    public Task StartAsync(EngineCore engine, CancellationToken ct)
    {
        ShaderParts.Attach(engine);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => _worker?.DisposeAsync() ?? ValueTask.CompletedTask;
}
