using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-output's hooks into the render host: what it adds to each frame's inputs, and its GPU objects made when the
/// context exists and let go when it goes (render thread).</summary>
public sealed partial class RenderHost
{
    partial void ComposeOutput(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        // wp2-output fills this.
    }

    partial void OnContextOutput(RenderEngine renderer)
    {
        // wp2-output fills this.
    }

    partial void OnContextLostOutput()
    {
        // wp2-output fills this.
    }
}
