using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>wp2-output's hooks into the engine (EngineCore.Modules.cs): its verbs, lane work, STATE keys, context items,
/// checks and recovery facts. The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RouteOutput(VerbRoutes routes)
    {
        // wp2-output fills this.
    }

    partial void TickOutput(long leaderNs)
    {
        // wp2-output fills this.
    }

    partial void OutputOutput(Utf8JsonWriter w, Output o)
    {
        // wp2-output fills this.
    }

    partial void SyncOutput(Utf8JsonWriter w)
    {
        // wp2-output fills this.
    }

    partial void MachineOutput(Utf8JsonWriter w)
    {
        // wp2-output fills this.
    }

    partial void ContextOutput(List<ContextItem> items, List<ContextLink> links)
    {
        // wp2-output fills this.
    }

    partial void ValidateOutput(Project p, object entity, ref string? why)
    {
        // wp2-output fills this.
    }
}
