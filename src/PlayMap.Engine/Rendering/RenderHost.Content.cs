using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-content's hooks into the render host: what it adds to each frame's inputs, and its GPU objects made when the
/// context exists and let go when it goes (render thread).</summary>
public sealed partial class RenderHost
{
    partial void ComposeContent(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        // wp2-content fills this.
    }

    partial void OnContextContent(RenderEngine renderer)
    {
        // wp2-content fills this.
    }

    partial void OnContextLostContent()
    {
        // wp2-content fills this.
    }
}
