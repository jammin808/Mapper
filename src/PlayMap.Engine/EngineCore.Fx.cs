using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>wp2-fx's hooks into the engine (EngineCore.Modules.cs): its verbs, lane work, STATE keys, context items,
/// checks and recovery facts. The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RouteFx(VerbRoutes routes)
    {
        // wp2-fx fills this.
    }

    partial void StateFx(Utf8JsonWriter w)
    {
        // wp2-fx fills this.
    }

    partial void OutputFx(Utf8JsonWriter w, Output o)
    {
        // wp2-fx fills this.
    }

    partial void ContextFx(List<ContextItem> items, List<ContextLink> links)
    {
        // wp2-fx fills this.
    }

    partial void ValidateFx(Project p, object entity, ref string? why)
    {
        // wp2-fx fills this.
    }
}
