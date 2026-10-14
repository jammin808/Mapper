using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-fx's hooks into the render host: what it adds to each frame's inputs, and its GPU objects made when the
/// context exists and let go when it goes (render thread).</summary>
public sealed partial class RenderHost
{
    partial void ComposeFx(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        // wp2-fx fills this.
    }

    partial void OnContextFx(RenderEngine renderer)
    {
        // wp2-fx fills this.
    }

    partial void OnContextLostFx()
    {
        // wp2-fx fills this.
    }
}
