using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>wp2-control's hooks into the engine (EngineCore.Modules.cs): its verbs, lane work, STATE keys, context items,
/// checks and recovery facts. The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RouteControl(VerbRoutes routes)
    {
        // wp2-control fills this.
    }

    partial void TickControl(long leaderNs)
    {
        // wp2-control fills this.
    }

    partial void StateControl(Utf8JsonWriter w)
    {
        // wp2-control fills this.
    }

    partial void PlaylistControl(Utf8JsonWriter w, Playlist list)
    {
        // wp2-control fills this.
    }

    partial void ContextControl(List<ContextItem> items, List<ContextLink> links)
    {
        // wp2-control fills this.
    }

    partial void ValidateControl(Project p, object entity, ref string? why)
    {
        // wp2-control fills this.
    }

    partial void RecoverySaveControl(IDictionary<string, string> into)
    {
        // wp2-control fills this.
    }

    partial void RecoveryLoadControl(IReadOnlyDictionary<string, string> from)
    {
        // wp2-control fills this.
    }
}
