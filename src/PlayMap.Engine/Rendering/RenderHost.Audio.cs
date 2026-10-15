using PlayMap.Core.Modulation;
using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-audio's hooks into the render host: each frame the render thread's bus starts at the frame's leader instant, the
/// project handed to the renderer carries every bound number at its live value (the bindings resolved once per project), and the
/// bus is the frame's modulation and audio for the packages that read them after this one. Nothing is written to the project.</summary>
public sealed partial class RenderHost
{
    private ModulationPlan _modPlan = ModulationPlan.Empty;

    partial void ComposeAudio(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        var rt = engine.AudioRt;
        var bus = rt.RenderBus;
        bus.BeginFrame(inputs.LeaderNs);
        if (!ReferenceEquals(_modPlan.Project, inputs.Project))
            _modPlan = ModulationPlan.For(inputs.Project, rt.FxRanges);
        var project = _modPlan.Apply(bus, inputs.LeaderNs, engine.ModBypassAll, engine.ModBypassed);
        inputs = inputs with { Project = project, Modulation = bus, Audio = bus };
    }

    partial void OnContextAudio(RenderEngine renderer)
    {
        // Nothing on the GPU: the bus and the plan are plain memory, kept across a lost context.
    }

    partial void OnContextLostAudio()
    {
        // Nothing on the GPU to let go.
    }
}
