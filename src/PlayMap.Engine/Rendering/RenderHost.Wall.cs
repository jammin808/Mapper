using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-wall's hooks into the render host: what it adds to each frame's inputs, and its GPU objects made when the
/// context exists and let go when it goes (render thread).</summary>
public sealed partial class RenderHost
{
    partial void ComposeWall(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        // wp2-wall fills this.
    }

    partial void OnContextWall(RenderEngine renderer)
    {
        // wp2-wall fills this.
    }

    partial void OnContextLostWall()
    {
        // wp2-wall fills this.
    }
}
