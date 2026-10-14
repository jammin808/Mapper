using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>wp2-optimise's hooks into the engine (EngineCore.Modules.cs): its verbs, lane work, STATE keys, context items,
/// checks and recovery facts. The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RouteOptimise(VerbRoutes routes)
    {
        // wp2-optimise fills this.
    }

    partial void MediaOptimise(Utf8JsonWriter w)
    {
        // wp2-optimise fills this.
    }

    partial void ContextOptimise(List<ContextItem> items, List<ContextLink> links)
    {
        // wp2-optimise fills this.
    }

    partial void ValidateOptimise(Project p, object entity, ref string? why)
    {
        // wp2-optimise fills this.
    }
}
