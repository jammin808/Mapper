using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>wp2-align's hooks into the engine (EngineCore.Modules.cs): the ALIGN verbs and INDEX section, STATE.align,
/// outputs[].measured, the align context items and the groups' validation (Align/EngineCore.AlignVerbs.cs); the double-correction
/// guard (Align/EngineCore.NativeGuard.cs).</summary>
public sealed partial class EngineCore
{
    partial void RouteAlign(VerbRoutes routes) => RouteAlignVerbs(routes);

    partial void StateAlign(Utf8JsonWriter w) => WriteAlign(w);

    partial void OutputAlign(Utf8JsonWriter w, Output o) => WriteOutputMeasured(w, o);

    partial void ContextAlign(List<ContextItem> items, List<ContextLink> links)
    {
        AlignContext(items);
        NativeContext(items);
    }

    partial void ValidateAlign(Project p, object entity, ref string? why)
    {
        if (why is null && entity is AlignGroup g)
            why = ValidateAlignGroup(p, g);
    }
}
