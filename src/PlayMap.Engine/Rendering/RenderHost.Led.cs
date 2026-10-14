using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-led's hooks into the render host: what it adds to each frame's inputs, and its GPU objects made when the
/// context exists and let go when it goes (render thread).</summary>
public sealed partial class RenderHost
{
    partial void ComposeLed(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        // wp2-led fills this.
    }

    partial void OnContextLed(RenderEngine renderer)
    {
        // wp2-led fills this.
    }

    partial void OnContextLostLed()
    {
        // wp2-led fills this.
    }
}
