using System.Text.Json;
using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>wp3-canvas' hooks into the engine (EngineCore.Modules.cs): its verbs, the outputs' hand key and its checks. The package
/// fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RouteCanvas(VerbRoutes routes)
    {
        // wp3-canvas fills this.
    }

    partial void OutputCanvas(Utf8JsonWriter w, Output o)
    {
        // wp3-canvas fills this.
    }

    partial void ValidateCanvas(Project p, object entity, ref string? why)
    {
        // wp3-canvas fills this. Until then a canvas's span is refused: CANVAS SPAN writes it, and nothing reads it yet.
        if (why is null && entity is Canvas c)
            why = NotDrawnYet("span", c.Span, p.FindCanvas(c.Id)?.Span, null, p.FindCanvas(c.Id) is null);
    }
}
