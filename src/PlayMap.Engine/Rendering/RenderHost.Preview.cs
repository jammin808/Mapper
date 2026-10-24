using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>wp3-desk-gpu's hooks into the render host: what it adds to each frame's inputs, and its GPU objects made when the
/// context exists and let go when it goes (render thread).</summary>
public sealed partial class RenderHost
{
    partial void ComposePreview(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        // wp3-desk-gpu fills this.
    }

    partial void OnContextPreview(RenderEngine renderer)
    {
        // wp3-desk-gpu fills this.
    }

    partial void OnContextLostPreview()
    {
        // wp3-desk-gpu fills this.
    }
}
