using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-show's hooks into the render host: each frame takes the air's levels the lane built last (the master, a panic,
/// a return from one); null leaves every output at full. The show makes no GPU objects of its own.</summary>
public sealed partial class RenderHost
{
    partial void ComposeShow(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        if (engine.AirLevelsNow is { } levels)
            inputs = inputs with { Levels = levels };
    }

    partial void OnContextShow(RenderEngine renderer)
    {
        // The show makes no GPU objects: its levels ride on uAir and the pattern-override path.
    }

    partial void OnContextLostShow()
    {
        // Nothing to let go: the show holds no GPU objects.
    }
}
