using PlayMap.Core.Gpu;
using PlayMap.Render.Effects;
using PlayMap.Render.Generators;
using PlayMap.Render.Led;
using PlayMap.Render.Passes;
using PlayMap.Render.Projection;
using PlayMap.Render.Video;

namespace PlayMap.Render.Engine;

/// <summary>What a pipeline knows of the target a draw goes into: its colour formats and whether it has depth.</summary>
internal readonly record struct TargetShape(TextureFormat Color0, TextureFormat? Color1 = null, bool Depth = false)
{
    /// <summary>8-bit: an output's final picture and its delay ring, a send's packed frame, an LED rig's slots, a pattern generator.</summary>
    public static readonly TargetShape Rgba8 = new(TextureFormat.Rgba8);

    /// <summary>8-bit with depth: a preview's picture, the editor's view drawing the stage into it.</summary>
    public static readonly TargetShape Rgba8Depth = new(TextureFormat.Rgba8, Depth: true);

    /// <summary>Half float: canvases, mixes, the float generators, converted video.</summary>
    public static readonly TargetShape Rgba16F = new(TextureFormat.Rgba16F);

    /// <summary>Float: an LED rig's sampled colours.</summary>
    public static readonly TargetShape Rgba32F = new(TextureFormat.Rgba32F);

    /// <summary>A projector's picture with its depth (and a projector preview's), in half float where the device renders it.</summary>
    public static TargetShape Projector(IGpuDevice device) => new(device.TargetFormat(TextureFormat.Rgba16F), Depth: true);
}

/// <summary>The pipelines passes can draw on one device, each pass adding its own: every product program it draws, in every state it
/// draws it in, into every target shape it draws into there (formats as the frame code makes its targets on that device). A key
/// listed twice is held once. A user's effect programs are made at run time and listed by no pass.</summary>
internal sealed class PipelineList(IGpuDevice device)
{
    private readonly HashSet<PipelineKey> _keys = [];

    public IGpuDevice Device { get; } = device ?? throw new ArgumentNullException(nameof(device));

    public IReadOnlyCollection<PipelineKey> Keys => _keys;

    /// <summary>A program's draws in <paramref name="state"/> into targets of each of <paramref name="targets"/>.</summary>
    public void Add(string program, in DrawState state, params ReadOnlySpan<TargetShape> targets)
    {
        var id = Device.Programs.Id(program);
        foreach (var t in targets)
            _keys.Add(new PipelineKey(id, state, t.Color0, t.Color1, t.Depth));
    }
}

/// <summary>Every pipeline the frame's passes can draw on a device, so the device makes them before the show (the render engine's
/// warm-up; Direct3D 12 compiles them at arm): a draw whose key no pass lists is made at its first draw, on the render thread, and
/// counted (<see cref="IPipelineCache.RenderThreadMakes"/>).</summary>
internal static class FramePipelines
{
    public static PipelineList Of(IGpuDevice device)
    {
        var keys = new PipelineList(device);
        RenderEngine.Pipelines(keys);
        OutputStage.Pipelines(keys);
        OverlayPass.Pipelines(keys);
        SourceResolver.Pipelines(keys);
        GeneratorRenderer.OverlayPipelines(keys);
        ShaderGenerator.Pipelines(keys);
        ParticleGenerator.Pipelines(keys);
        FlowGenerator.Pipelines(keys);
        RaymarchGenerator.Pipelines(keys);
        EffectGenerator.Pipelines(keys);
        VideoTextureSet.Pipelines(keys);
        PackedUnpack.Pipelines(keys);
        PackPass.Pipelines(keys);
        StageRenderer.Pipelines(keys);
        LedPass.Pipelines(keys);
        FxChainRunner.Pipelines(keys);
        return keys;
    }
}
