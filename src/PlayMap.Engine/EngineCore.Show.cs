using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Engine;

/// <summary>wp2-show's hooks into the engine (EngineCore.Modules.cs): its verbs, lane work, STATE keys, context items,
/// checks and recovery facts. The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RouteShow(VerbRoutes routes)
    {
        // wp2-show fills this.
    }

    partial void TickShow(long leaderNs)
    {
        // wp2-show fills this.
    }

    partial void StateShow(Utf8JsonWriter w)
    {
        // wp2-show fills this.
    }

    partial void AirShow(Utf8JsonWriter w)
    {
        // wp2-show fills this.
    }

    partial void ContextShow(List<ContextItem> items, List<ContextLink> links)
    {
        // wp2-show fills this.
    }

    partial void HeadlineShow(ref string headline)
    {
        // wp2-show fills this.
    }

    partial void ValidateShow(Project p, object entity, ref string? why)
    {
        // wp2-show fills this.
    }

    partial void RecoverySaveShow(IDictionary<string, string> into)
    {
        // wp2-show fills this.
    }

    partial void RecoveryLoadShow(IReadOnlyDictionary<string, string> from)
    {
        // wp2-show fills this.
    }

    partial void GateLine(VerbSpec spec, WireLine line, SessionInfo session, ref CommandReply? refused, ref ProjectStore? lineStore)
    {
        // wp2-show fills this.
    }

    partial void SnapshotShow(ref EngineSnapshot snapshot)
    {
        // wp2-show fills this.
    }

    private partial ProjectStore? BlindStoreFor(SessionInfo session) =>
        null; // wp2-show fills this (wp2-blind: the session's draft).
}
