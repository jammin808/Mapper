using System.Collections.Immutable;
using PlayMap.Render.Engine;
using PlayMap.Render.Passes;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-videoio's hooks into the render host: the send stage in every frame while the hub's published plan has a send (or a
/// ring still has frames out), its pack targets and read-back rings made with the context and let go with it (render thread).</summary>
public sealed partial class RenderHost
{
#pragma warning disable CA2213 // Disposed by OnContextLostVideoIo on the render thread that made it, never from DisposeAsync's thread.
    private SendStage? _sendStage;
#pragma warning restore CA2213
    private ImmutableArray<IFramePass> _sendGiven;
    private ImmutableArray<IFramePass> _sendPasses;

    partial void ComposeVideoIo(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        if (_sendStage is not { } stage)
            return;
        var plan = snap.Project.Project.Sends.IsEmpty && !stage.Busy ? [] : engine.VideoIo.Plan;
        stage.Plan = plan;
        if (plan.IsEmpty && !stage.Busy)
            return;
        if (_sendPasses.IsDefault || inputs.Passes != _sendGiven)
        {
            _sendGiven = inputs.Passes;
            _sendPasses = inputs.Passes.IsDefaultOrEmpty ? [stage] : inputs.Passes.Add(stage);
        }
        inputs = inputs with { Passes = _sendPasses };
    }

    partial void OnContextVideoIo(RenderEngine renderer)
    {
        _sendStage = new SendStage(renderer);
    }

    partial void OnContextLostVideoIo()
    {
        _sendStage?.Dispose();
        _sendStage = null;
        _sendGiven = default;
        _sendPasses = default;
    }
}
