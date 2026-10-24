using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>wp3-d3d12-core's hooks into the render host: what it adds to each frame's inputs, and its GPU objects made when the
/// context exists and let go when it goes (render thread).</summary>
public sealed partial class RenderHost
{
    partial void ComposeGpu(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        // wp3-d3d12-core fills this.
    }

    partial void OnContextGpu(RenderEngine renderer)
    {
        // wp3-d3d12-core fills this.
    }

    partial void OnContextLostGpu()
    {
        // wp3-d3d12-core fills this.
    }
}
