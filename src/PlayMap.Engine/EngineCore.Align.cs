using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>wp2-align's hooks into the engine (EngineCore.Modules.cs): its verbs, lane work, STATE keys, context items,
/// checks and recovery facts. The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RouteAlign(VerbRoutes routes)
    {
        // wp2-align fills this.
    }

    partial void StateAlign(Utf8JsonWriter w)
    {
        // wp2-align fills this.
    }

    partial void OutputAlign(Utf8JsonWriter w, Output o)
    {
        // wp2-align fills this.
    }

    partial void ContextAlign(List<ContextItem> items, List<ContextLink> links)
    {
        // wp2-align fills this.
    }

    partial void ValidateAlign(Project p, object entity, ref string? why)
    {
        // wp2-align fills this.
    }
}
