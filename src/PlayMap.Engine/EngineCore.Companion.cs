using System.Text.Json;
using PlayMap.Core.Bus;

namespace PlayMap.Engine;

/// <summary>wp2-companion's hooks into the engine (EngineCore.Modules.cs): its verbs, lane work, STATE keys, context items,
/// checks and recovery facts. The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RouteCompanion(VerbRoutes routes)
    {
        // wp2-companion fills this.
    }

    partial void StateCompanion(Utf8JsonWriter w)
    {
        // wp2-companion fills this.
    }

    partial void ProjectCompanion(Utf8JsonWriter w)
    {
        // wp2-companion fills this.
    }

    partial void ContextCompanion(List<ContextItem> items, List<ContextLink> links)
    {
        // wp2-companion fills this.
    }
}
