using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>wp2-facade's hooks into the engine (EngineCore.Modules.cs): its verbs, lane work, STATE keys, context items,
/// checks and recovery facts. The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RouteSite(VerbRoutes routes)
    {
        // wp2-facade fills this.
    }

    partial void StageSite(Utf8JsonWriter w)
    {
        // wp2-facade fills this.
    }

    partial void ProjectorSite(Utf8JsonWriter w, Projector pj)
    {
        // wp2-facade fills this.
    }

    partial void ContextSite(List<ContextItem> items, List<ContextLink> links)
    {
        // wp2-facade fills this.
    }

    partial void ValidateSite(Project p, object entity, ref string? why)
    {
        // wp2-facade fills this.
    }
}
