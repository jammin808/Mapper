using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>wp2-led's hooks into the engine (EngineCore.Modules.cs): its verbs, lane work, STATE keys, context items,
/// checks and recovery facts. The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RouteLed(VerbRoutes routes)
    {
        // wp2-led fills this.
    }

    partial void TickLed(long leaderNs)
    {
        // wp2-led fills this.
    }

    partial void StateLed(Utf8JsonWriter w)
    {
        // wp2-led fills this.
    }

    partial void ContextLed(List<ContextItem> items, List<ContextLink> links)
    {
        // wp2-led fills this.
    }

    partial void ValidateLed(Project p, object entity, ref string? why)
    {
        // wp2-led fills this.
    }

    partial void RecoverySaveLed(IDictionary<string, string> into)
    {
        // wp2-led fills this.
    }

    partial void RecoveryLoadLed(IReadOnlyDictionary<string, string> from)
    {
        // wp2-led fills this.
    }
}
