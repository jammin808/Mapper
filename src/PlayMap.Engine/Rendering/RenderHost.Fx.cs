using System.Collections.Immutable;
using PlayMap.Render.Effects;
using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-fx's hooks into the render host: the effects pass in every frame (item, layer and surface chains as their pictures
/// are made, output chains before the output stage), made with the device (its programs asked of the device's lane) and let go
/// with it, handed the node's quarantine (a quarantined kind's slots draw bypassed), and the engine told when a slot's observed
/// state moves (the numbers ride the next push; a state change makes one).</summary>
public sealed partial class RenderHost
{
#pragma warning disable CA2213 // Disposed by OnContextLostFx on the render thread that made it, never from DisposeAsync's thread.
    private FxPass? _fx;
#pragma warning restore CA2213
    private EngineCore? _fxEngine;
    private ImmutableArray<IFramePass> _fxGiven;
    private ImmutableArray<IFramePass> _fxPasses;
    private long _fxVersion;

    partial void ComposeFx(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        if (_fx is not { } fx)
            return;
        if (!ReferenceEquals(_fxEngine, engine))
        {
            _fxEngine = engine;
            // The node's quarantine (one for the node's life), handed to each pass the device makes before the engine reads it.
            fx.Quarantine = engine.Services.Quarantine;
            engine.Fx.Attach(fx);
        }
        if (_fxPasses.IsDefault || inputs.Passes != _fxGiven)
        {
            _fxGiven = inputs.Passes;
            _fxPasses = inputs.Passes.IsDefaultOrEmpty ? [fx] : inputs.Passes.Add(fx);
        }
        inputs = inputs with { Passes = _fxPasses };
        // DATE reads the frame's leader instant as wall time, not the moment this frame is composed.
        fx.UnixMs = engine.Services.Wall.UnixMs + (inputs.LeaderNs - engine.LeaderNowNs) / 1_000_000;
        var version = fx.Stats.Version;
        if (version != _fxVersion)
        {
            _fxVersion = version;
            Volatile.Read(ref _facts)?.Raise();
        }
    }

    partial void OnContextFx(RenderEngine renderer)
    {
        _fx = new FxPass(renderer.Device, gpuTiming: renderer.GpuTiming);
    }

    partial void OnContextLostFx()
    {
        _fxEngine?.Fx.Attach(null);
        _fx?.Dispose();
        _fx = null;
        // Nothing keeps the disposed pass: a context made again attaches its own pass and builds the frame's passes anew.
        _fxEngine = null;
        _fxGiven = default;
        _fxPasses = default;
    }
}
