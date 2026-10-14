using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-show's hooks into the render host: what it adds to each frame's inputs, and its GPU objects made when the
/// context exists and let go when it goes (render thread).</summary>
public sealed partial class RenderHost
{
    partial void ComposeShow(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        // wp2-show fills this.
    }

    partial void OnContextShow(RenderEngine renderer)
    {
        // wp2-show fills this.
    }

    partial void OnContextLostShow()
    {
        // wp2-show fills this.
    }
}
