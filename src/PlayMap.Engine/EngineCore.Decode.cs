using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>wp2-decode's hooks into the engine (EngineCore.Modules.cs): its verbs, lane work, STATE keys, context items,
/// checks and recovery facts. The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RouteDecode(VerbRoutes routes)
    {
        // wp2-decode fills this.
    }

    partial void StateDecode(Utf8JsonWriter w)
    {
        // wp2-decode fills this.
    }

    partial void MachineDecode(Utf8JsonWriter w)
    {
        // wp2-decode fills this.
    }

    partial void ContextDecode(List<ContextItem> items, List<ContextLink> links)
    {
        // wp2-decode fills this.
    }

    partial void ValidateDecode(Project p, object entity, ref string? why)
    {
        // wp2-decode fills this.
    }
}
