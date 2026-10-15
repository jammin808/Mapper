using System.Collections.Immutable;
using PlayMap.Render.Engine;
using PlayMap.Render.Passes;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-videoio's hooks into the render host: the send stage in every frame while the hub's published plan has a send (or a
/// ring still has frames out), its pack targets and read-back rings made with the context and let go with it (render thread); and,
/// last in every frame while a live input is kept, the mark that ends the frame's air, so a live input drawn after it (a preview) is
/// tallied preview.</summary>
public sealed partial class RenderHost
{
#pragma warning disable CA2213 // Disposed by OnContextLostVideoIo on the render thread that made it, never from DisposeAsync's thread.
    private SendStage? _sendStage;
    private AirEnd? _airEnd;
#pragma warning restore CA2213
    private ImmutableArray<IFramePass> _sendGiven;
    private ImmutableArray<IFramePass> _sendPasses;
    private int _sendShape;

    partial void ComposeVideoIo(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        if (_sendStage is not { } stage || _airEnd is not { } end)
            return;
        var plan = snap.Project.Project.Sends.IsEmpty && !stage.Busy ? [] : engine.VideoIo.Plan;
        stage.Plan = plan;
        var with = !plan.IsEmpty || stage.Busy;
        // The air's end is marked only while a live input is kept: a frame with none has nothing to tally.
        var air = engine.VideoIoMade?.HasInputs ?? false;
        if (!with && !air)
            return;
        var shape = (with ? 1 : 0) | (air ? 2 : 0);
        if (_sendPasses.IsDefault || inputs.Passes != _sendGiven || shape != _sendShape)
        {
            _sendGiven = inputs.Passes;
            _sendShape = shape;
            var given = inputs.Passes.IsDefault ? [] : inputs.Passes;
            if (with)
                given = given.Add(stage);
            _sendPasses = air ? given.Add(end) : given;
        }
        inputs = inputs with { Passes = _sendPasses };
    }

    partial void OnContextVideoIo(RenderEngine renderer)
    {
        _sendStage = new SendStage(renderer);
        _airEnd = new AirEnd(_sources);
    }

    partial void OnContextLostVideoIo()
    {
        _sendStage?.Dispose();
        _sendStage = null;
        _airEnd = null;
        _sendGiven = default;
        _sendPasses = default;
    }

    /// <summary>The last pass of every frame: its outputs and sends are drawn, so what the renderer's sources lend from here on is
    /// drawn for preview.</summary>
    private sealed class AirEnd(EngineSources? sources) : IFramePass
    {
        public string Name => "videoio.air";

        public int Order => int.MaxValue;

        public void FrameEnd(in PassFrame frame) => sources?.EndAir();

        public void Dispose()
        {
            // It owns nothing.
        }
    }
}
