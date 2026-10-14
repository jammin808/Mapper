using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>wp2-phonescan's hooks into the engine (EngineCore.Modules.cs): its verbs, lane work, STATE keys, context items,
/// checks and recovery facts. The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RoutePhone(VerbRoutes routes)
    {
        // wp2-phonescan fills this.
    }

    partial void StatePhone(Utf8JsonWriter w)
    {
        // wp2-phonescan fills this.
    }

    partial void ContextPhone(List<ContextItem> items, List<ContextLink> links)
    {
        // wp2-phonescan fills this.
    }

    partial void ValidatePhone(Project p, object entity, ref string? why)
    {
        // wp2-phonescan fills this.
    }
}
