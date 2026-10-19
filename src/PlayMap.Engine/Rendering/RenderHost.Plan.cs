using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>wp3-d3d12-passes' hooks into the render host: what it adds to each frame's inputs, and its GPU objects made when the
/// context exists and let go when it goes (render thread).</summary>
public sealed partial class RenderHost
{
    partial void ComposePlan(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        // wp3-d3d12-passes fills this.
    }

    partial void OnContextPlan(RenderEngine renderer)
    {
        // wp3-d3d12-passes fills this.
    }

    partial void OnContextLostPlan()
    {
        // wp3-d3d12-passes fills this.
    }
}
