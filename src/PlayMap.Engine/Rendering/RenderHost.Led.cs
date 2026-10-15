using System.Collections.Immutable;
using PlayMap.Render.Engine;
using PlayMap.Render.Led;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-led's hooks into the render host: the LED pass in every frame (sampling after every output is drawn, before the
/// presents), handed the plan of the published rig, the rig's live air and the monotonic instant of the frame's light; its GPU
/// objects made with the context and let go with it.</summary>
public sealed partial class RenderHost
{
#pragma warning disable CA2213 // Disposed by OnContextLostLed on the render thread that made it, never from DisposeAsync's thread.
    private LedPass? _led;
#pragma warning restore CA2213
    private EngineCore? _ledEngine;
    private ImmutableArray<IFramePass> _ledGiven;
    private ImmutableArray<IFramePass> _ledPasses;

    partial void ComposeLed(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        if (_led is not { } led)
            return;
        var board = engine.Led;
        if (!ReferenceEquals(_ledEngine, engine))
        {
            _ledEngine = engine;
            board.Attach(led);
        }
        var plan = board.Current;
        var sink = board.Output?.Sink;
        // Nothing to sample or nowhere to send: the pass stays out of the frame.
        if (sink is null || (plan.Pixels == 0 && plan.Rows.IsEmpty))
            return;
        led.Sink = sink;
        led.Offer(plan);
        led.Live = board.Live;
        // The light of this frame comes (leader instant − leader now) after now, on the clock the sender schedules with.
        led.LightMonoNs = engine.Services.Mono.NowNs + (inputs.LeaderNs - engine.LeaderNowNs);
        if (_ledPasses.IsDefault || inputs.Passes != _ledGiven)
        {
            _ledGiven = inputs.Passes;
            _ledPasses = inputs.Passes.IsDefaultOrEmpty ? [led] : inputs.Passes.Add(led);
        }
        inputs = inputs with { Passes = _ledPasses };
    }

    partial void OnContextLed(RenderEngine renderer)
    {
        _led = new LedPass(_context!.Device, new RendererPictures(renderer));
    }

    partial void OnContextLostLed()
    {
        _ledEngine?.Led.Attach(null);
        _led?.Dispose();
        _led = null;
        _ledEngine = null;
        _ledGiven = default;
        _ledPasses = default;
    }
}
