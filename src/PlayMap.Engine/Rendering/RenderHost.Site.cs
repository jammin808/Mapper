using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-facade's hooks into the render host: what it adds to each frame's inputs, and its GPU objects made when the
/// context exists and let go when it goes (render thread).</summary>
public sealed partial class RenderHost
{
    partial void ComposeSite(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        // wp2-facade fills this.
    }

    partial void OnContextSite(RenderEngine renderer)
    {
        // wp2-facade fills this.
    }

    partial void OnContextLostSite()
    {
        // wp2-facade fills this.
    }
}
