using System.Collections.Immutable;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Machine;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Machine;

/// <summary>Preflight's machine section: the platform's own rows (the OS and its edition, Smart App Control, servicing, power, the screen
/// saver, a pending reboot, the networks' firewall profiles, each claim Windows refused, the timer's lateness), then what only the
/// engine knows — the drift from MACHINE LOCK's set and each enabled display output on the primary display. On a node whose platform
/// claims nothing, its one row says why. Off the lane, on the thread the preflight runner gives each check: the platform reads the
/// registry and the network list.</summary>
internal sealed class MachinePreflight : IPreflightCheck
{
    public string Section => "machine";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(5);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Rows(context.Engine));
    }

    /// <summary>The section's rows now, from published state only.</summary>
    public static IReadOnlyList<PreflightRow> Rows(EngineCore engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var node = engine.Services.Settings.NodeId;
        return [.. Facts(engine).Select(f => new PreflightRow("machine." + f.Item, node, "machine", MachineRows.Label(f.Item), f.Light, f.Value, f.Words, f.Fix))];
    }

    /// <summary>The platform's facts and the engine's, in that order.</summary>
    public static ImmutableArray<MachineFact> Facts(EngineCore engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var platform = engine.Services.MachinePlatform;
        var identity = platform.Identity();
        var primary = engine.Services.Outputs.Displays().Where(d => d.Primary).Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        var onPrimary = engine.Store.Project.Outputs
            .Where(o => o.Enabled && o.Kind == OutputKind.Display && o.DisplayKey.Length > 0 && primary.Contains(o.DisplayKey))
            .Select(o => (o.Name, o.DisplayKey)).ToList();
        var locked = engine.Services.MachineNode?.Saved.Locked;
        return [.. platform.Facts(), .. MachineRows.Engine(identity.Os, locked, LockedDriverSet.Of(identity, engine.Services.Wall.UnixMs), onPrimary)];
    }
}

/// <summary>INDEX's machine section: one row per claim the node made of its operating system, lit green where it applied and amber
/// where it was refused, as feedback (its key asks MACHINE STATUS, a query).</summary>
internal sealed class MachineIndex(EngineCore engine) : IIndexContributor
{
    private static readonly IndexPresetHint PresetHint = new("Machine: claims", "claim",
    [
        new IndexKey("$(label)", "MACHINE STATUS", "machine.platform.light", PressShapes.Instant),
    ]);

    public string Section => "machine";

    public IndexPresetHint Hint => PresetHint;

    public void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartArray();
        foreach (var c in (engine.Services.MachinePlatform as RecordedPlatform)?.Claims ?? [])
        {
            w.WriteStartObject();
            w.WriteString("id", "claim:" + c.What);
            w.WriteString("kind", "claim");
            w.WriteString("label", c.What);
            w.WriteNull("parent");
            w.WriteString("light", ShowContext.LightWord(EngineCore.ClaimLight(c)));
            w.WriteString("state", c.Applied ? c.ReadBack : c.Words);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
