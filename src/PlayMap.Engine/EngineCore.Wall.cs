using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>wp2-wall's hooks into the engine (EngineCore.Modules.cs): its verbs, lane work, STATE keys, context items,
/// checks and recovery facts. The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RouteWall(VerbRoutes routes)
    {
        // wp2-wall fills this.
    }

    partial void TickWall(long leaderNs)
    {
        // wp2-wall fills this.
    }

    partial void StateWall(Utf8JsonWriter w)
    {
        // wp2-wall fills this.
    }

    partial void AirWall(Utf8JsonWriter w)
    {
        // wp2-wall fills this.
    }

    partial void ProjectorWall(Utf8JsonWriter w, Projector pj)
    {
        // wp2-wall fills this.
    }

    partial void ContextWall(List<ContextItem> items, List<ContextLink> links)
    {
        // wp2-wall fills this.
    }

    partial void ValidateWall(Project p, object entity, ref string? why)
    {
        // wp2-wall fills this.
    }
}
