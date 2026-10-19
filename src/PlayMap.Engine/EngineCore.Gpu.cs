using System.Text.Json;
using PlayMap.Core.Bus;

namespace PlayMap.Engine;

/// <summary>wp3-d3d12-core's hooks into the engine (EngineCore.Modules.cs): its verbs, STATE's render block and the render device's
/// context items. The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RouteGpu(VerbRoutes routes)
    {
        // wp3-d3d12-core fills this.
    }

    partial void StateGpu(Utf8JsonWriter w)
    {
        // wp3-d3d12-core fills this.
    }

    partial void ContextGpu(List<ContextItem> items, List<ContextLink> links)
    {
        // wp3-d3d12-core fills this.
    }
}
