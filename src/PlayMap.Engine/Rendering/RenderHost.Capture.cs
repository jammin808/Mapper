using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>wp3-capture's hooks into the render host: what it adds to each frame's inputs, and its GPU objects made when the
/// context exists and let go when it goes (render thread).</summary>
public sealed partial class RenderHost
{
    partial void ComposeCapture(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        // wp3-capture fills this.
    }

    partial void OnContextCapture(RenderEngine renderer)
    {
        // wp3-capture fills this.
    }

    partial void OnContextLostCapture()
    {
        // wp3-capture fills this.
    }
}
