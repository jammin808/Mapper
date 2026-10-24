using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>wp3-workflows' hooks into the engine (EngineCore.Modules.cs): its verbs, STATE's guides block, the context's guide
/// items and its checks. The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RouteGuide(VerbRoutes routes)
    {
        // wp3-workflows fills this.
    }

    partial void StateGuide(Utf8JsonWriter w)
    {
        // wp3-workflows fills this.
    }

    partial void ContextGuide(List<ContextItem> items, List<ContextLink> links)
    {
        // wp3-workflows fills this.
    }

    partial void ValidateGuide(Project p, object entity, ref string? why)
    {
        // wp3-workflows fills this. Until then a viewpoint's fit is refused: VIEWPOINT FIT writes it, and nothing reads it yet.
        if (why is null && entity is Viewpoint v)
        {
            var stored = p.Stage.Viewpoints.FirstOrDefault(x => x.Id == v.Id);
            why = NotDrawnYet("fit", v.Fit, stored?.Fit, null, stored is null);
        }
    }
}
