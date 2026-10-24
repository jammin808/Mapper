using System.Text.Json;

namespace PlayMap.Engine;

/// <summary>wp3-d3d12-passes' hooks into the engine (EngineCore.Modules.cs): STATE's render.graph, written inside the render block
/// (wp3-d3d12-passes-2 continues it). The package fills each body.</summary>
public sealed partial class EngineCore
{
    partial void RenderPlan(Utf8JsonWriter w)
    {
        // wp3-d3d12-passes fills this.
    }
}
