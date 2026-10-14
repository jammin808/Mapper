using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>wp2-fleet's hooks into the engine (EngineCore.Modules.cs): its verbs, lane work, STATE keys, context items,
/// checks and recovery facts. The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RouteFleet(VerbRoutes routes)
    {
        // wp2-fleet fills this.
    }

    partial void TickFleet(long leaderNs)
    {
        // wp2-fleet fills this.
    }

    partial void StateFleet(Utf8JsonWriter w)
    {
        // wp2-fleet fills this.
    }

    partial void SyncFleet(Utf8JsonWriter w)
    {
        // wp2-fleet fills this.
    }

    partial void ContextFleet(List<ContextItem> items, List<ContextLink> links)
    {
        // wp2-fleet fills this.
    }

    partial void ShapeContext(List<ContextItem> items, List<ContextLink> links)
    {
        // wp2-fleet fills this.
    }

    partial void HeadlineFleet(ref string headline)
    {
        // wp2-fleet fills this.
    }

    partial void ValidateFleet(Project p, object entity, ref string? why)
    {
        // wp2-fleet fills this.
    }

    partial void RecoverySaveFleet(IDictionary<string, string> into)
    {
        // wp2-fleet fills this.
    }

    partial void RecoveryLoadFleet(IReadOnlyDictionary<string, string> from)
    {
        // wp2-fleet fills this.
    }
}
