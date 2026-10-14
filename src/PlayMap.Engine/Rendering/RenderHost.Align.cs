using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-align's hooks into the render host: what it adds to each frame's inputs, and its GPU objects made when the
/// context exists and let go when it goes (render thread).</summary>
public sealed partial class RenderHost
{
    partial void ComposeAlign(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        // wp2-align fills this.
    }

    partial void OnContextAlign(RenderEngine renderer)
    {
        // wp2-align fills this.
    }

    partial void OnContextLostAlign()
    {
        // wp2-align fills this.
    }
}
