using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>wp3-stability's hooks into the engine (EngineCore.Modules.cs): its verbs, STATE's observe block, each output's observed
/// key, the observed outputs' context items and its checks. The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RouteObserve(VerbRoutes routes)
    {
        // wp3-stability fills this.
    }

    partial void StateObserve(Utf8JsonWriter w)
    {
        // wp3-stability fills this.
    }

    partial void OutputObserve(Utf8JsonWriter w, Output o)
    {
        // wp3-stability fills this.
    }

    partial void ContextObserve(List<ContextItem> items, List<ContextLink> links)
    {
        // wp3-stability fills this.
    }

    partial void ValidateObserve(Project p, object entity, ref string? why)
    {
        // wp3-stability fills this. Until then an output's watermark is refused: nothing draws it yet.
        if (why is null && entity is Output o)
            why = NotDrawnYet("watermark", o.Watermark, p.FindOutput(o.Id)?.Watermark, null, p.FindOutput(o.Id) is null);
    }
}
