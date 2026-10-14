using System.Collections.Immutable;
using PlayMap.Render.Effects;
using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-fx's hooks into the render host: the effects pass in every frame (output chains before the output stage), its
/// compile lane made with the context and let go with it, and the engine told when a slot's observed state moves (the numbers
/// ride the next push; a state change makes one).</summary>
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
            engine.Fx.Attach(fx);
        }
        if (_fxPasses.IsDefault || inputs.Passes != _fxGiven)
        {
            _fxGiven = inputs.Passes;
            _fxPasses = inputs.Passes.IsDefaultOrEmpty ? [fx] : inputs.Passes.Add(fx);
        }
        inputs = inputs with { Passes = _fxPasses };
        fx.UnixMs = engine.Services.Wall.UnixMs;
        fx.Prepare(in inputs);
        var version = fx.Stats.Version;
        if (version != _fxVersion)
        {
            _fxVersion = version;
            ThreadPool.UnsafeQueueUserWorkItem(static e => e.FactsChanged(), engine, preferLocal: false);
        }
    }

    partial void OnContextFx(RenderEngine renderer)
    {
        _fx = new FxPass(_context!);
    }

    partial void OnContextLostFx()
    {
        _fxEngine?.Fx.Attach(null);
        _fx?.Dispose();
        _fx = null;
    }
}
