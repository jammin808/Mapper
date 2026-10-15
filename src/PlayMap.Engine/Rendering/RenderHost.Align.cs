using PlayMap.Engine.Align;
using PlayMap.Render.Engine;
using PlayMap.Render.Gpu;
using PlayMap.Render.Passes;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-align's hooks into the render host: each frame hands the output stage every output's measured correction. The files
/// were read off this thread (<see cref="MeasuredLibrary"/>); when the set they make changes, this thread uploads what changed into
/// its own textures and lets go of what went — nothing is read, allocated or uploaded on a frame where the set is the same.</summary>
public sealed partial class RenderHost
{
#pragma warning disable CA2213 // The device is the renderer's; the stages' textures are disposed by OnContextLostAlign on this thread.
    private GlDevice? _alignDevice;
#pragma warning restore CA2213
    private MeasuredSet? _alignSet;
    private readonly Dictionary<string, (string Key, MeasuredStage Stage)> _alignStages = new(StringComparer.Ordinal);
    private MeasuredStages? _alignInputs;

    /// <summary>The stages the output stage reads this frame, by output.</summary>
    private sealed class MeasuredStages(Dictionary<string, MeasuredStage> stages) : IMeasuredStages
    {
        public MeasuredStage? For(string outputId) => stages.GetValueOrDefault(outputId);
    }

    partial void ComposeAlign(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        if (_alignDevice is not { } device)
            return;
        var set = engine.AlignMeasured;
        if (!ReferenceEquals(set, _alignSet))
            UploadAlign(device, set);
        if (_alignInputs is { } stages)
            inputs = inputs with { Measured = stages };
    }

    /// <summary>The stages made to match a new set: what changed uploaded, what went let go. Its own method, so the closures it makes
    /// are made only when the set changes and never on a frame where it is the same.</summary>
    private void UploadAlign(GlDevice device, MeasuredSet set)
    {
        _alignSet = set;
        foreach (var id in _alignStages.Keys.Where(id => !set.Loads.TryGetValue(id, out var l) || l.Key != _alignStages[id].Key).ToList())
        {
            _alignStages[id].Stage.Dispose();
            _alignStages.Remove(id);
        }
        foreach (var (id, load) in set.Loads)
        {
            if (_alignStages.ContainsKey(id))
                continue;
            var stage = MeasuredStage.Upload(device,
                load.Grid is { } g ? (g.Width, g.Height, g.Values) : null,
                load.Blend is { } b ? (b.Width, b.Height, b.Values) : null,
                load.Gains, load.Gamma, 1f);
            _alignStages[id] = (load.Key, stage);
        }
        _alignInputs = _alignStages.Count == 0 ? null : new MeasuredStages(_alignStages.ToDictionary(kv => kv.Key, kv => kv.Value.Stage, StringComparer.Ordinal));
    }

    partial void OnContextAlign(RenderEngine renderer)
    {
        _alignDevice = renderer.Device;
        _alignSet = null;
    }

    partial void OnContextLostAlign()
    {
        foreach (var (_, stage) in _alignStages.Values)
            stage.Dispose();
        _alignStages.Clear();
        _alignInputs = null;
        _alignSet = null;
        _alignDevice = null;
    }
}
