using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>wp2-videoio's hooks into the engine (EngineCore.Modules.cs): its verbs, lane work, STATE keys, context items,
/// checks and recovery facts. The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RouteVideoIo(VerbRoutes routes)
    {
        // wp2-videoio fills this.
    }

    partial void StateVideoIo(Utf8JsonWriter w)
    {
        // wp2-videoio fills this.
    }

    partial void ContextVideoIo(List<ContextItem> items, List<ContextLink> links)
    {
        // wp2-videoio fills this.
    }

    partial void ValidateVideoIo(Project p, object entity, ref string? why)
    {
        // wp2-videoio fills this.
    }
}
