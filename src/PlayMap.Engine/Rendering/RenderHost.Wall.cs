using System.Collections.Immutable;
using PlayMap.Render.Engine;
using PlayMap.Render.Passes;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-wall's hooks into the render host: each frame takes the air's edit overlay and whether the show is armed (the
/// overlay pass then draws nothing), and adds the overlay pass, made with the context and let go with it (render thread).</summary>
public sealed partial class RenderHost
{
#pragma warning disable CA2213 // Disposed by OnContextLostWall on the render thread that made it, never from DisposeAsync's thread.
    private OverlayPass? _overlay;
#pragma warning restore CA2213
    private ImmutableArray<IFramePass> _overlayGiven;
    private ImmutableArray<IFramePass> _overlayPasses;

    partial void ComposeWall(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        inputs = inputs with { Overlay = snap.Air.Overlay, Armed = inputs.Armed || engine.Services.ShowGate.Armed };
        if (_overlay is not { } pass)
            return;
        if (_overlayPasses.IsDefault || inputs.Passes != _overlayGiven)
        {
            _overlayGiven = inputs.Passes;
            _overlayPasses = inputs.Passes.IsDefaultOrEmpty ? [pass] : inputs.Passes.Add(pass);
        }
        inputs = inputs with { Passes = _overlayPasses };
        pass.Prepare(in inputs);
    }

    partial void OnContextWall(RenderEngine renderer)
    {
        _overlay = new OverlayPass(renderer.Device);
    }

    partial void OnContextLostWall()
    {
        _overlay?.Dispose();
        _overlay = null;
        _overlayGiven = default;
        _overlayPasses = default;
    }
}
