using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-videoio's hooks into the render host: what it adds to each frame's inputs, and its GPU objects made when the
/// context exists and let go when it goes (render thread).</summary>
public sealed partial class RenderHost
{
    partial void ComposeVideoIo(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        // wp2-videoio fills this.
    }

    partial void OnContextVideoIo(RenderEngine renderer)
    {
        // wp2-videoio fills this.
    }

    partial void OnContextLostVideoIo()
    {
        // wp2-videoio fills this.
    }
}
