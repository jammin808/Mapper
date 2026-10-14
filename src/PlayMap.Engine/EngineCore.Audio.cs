using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>wp2-audio's hooks into the engine (EngineCore.Modules.cs): its verbs, lane work, STATE keys, context items,
/// checks and recovery facts. The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RouteAudio(VerbRoutes routes)
    {
        // wp2-audio fills this.
    }

    partial void TickAudio(long leaderNs)
    {
        // wp2-audio fills this.
    }

    partial void StateAudio(Utf8JsonWriter w)
    {
        // wp2-audio fills this.
    }

    partial void ContextAudio(List<ContextItem> items, List<ContextLink> links)
    {
        // wp2-audio fills this.
    }

    partial void ValidateAudio(Project p, object entity, ref string? why)
    {
        // wp2-audio fills this.
    }
}
