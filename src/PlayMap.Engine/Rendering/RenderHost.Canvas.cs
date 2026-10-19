using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>wp3-canvas' hooks into the render host: what it adds to each frame's inputs, and its GPU objects made when the
/// context exists and let go when it goes (render thread).</summary>
public sealed partial class RenderHost
{
    partial void ComposeCanvas(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        // wp3-canvas fills this.
    }

    partial void OnContextCanvas(RenderEngine renderer)
    {
        // wp3-canvas fills this.
    }

    partial void OnContextLostCanvas()
    {
        // wp3-canvas fills this.
    }
}
