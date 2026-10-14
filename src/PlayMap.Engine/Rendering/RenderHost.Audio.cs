using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-audio's hooks into the render host: what it adds to each frame's inputs, and its GPU objects made when the
/// context exists and let go when it goes (render thread).</summary>
public sealed partial class RenderHost
{
    partial void ComposeAudio(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        // wp2-audio fills this.
    }

    partial void OnContextAudio(RenderEngine renderer)
    {
        // wp2-audio fills this.
    }

    partial void OnContextLostAudio()
    {
        // wp2-audio fills this.
    }
}
