using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace PlayMap.Core.Gpu;

/// <summary>Every piece of fixed-function state a draw sets, as one value (GL's loose state, Direct3D 12's pipeline state).
/// <see cref="Wireframe"/> draws triangles as their edges.</summary>
public readonly record struct DrawState(BlendMode Blend, DepthMode Depth, CullMode Cull, Topology Topology, bool Wireframe = false)
{
    public static readonly DrawState Opaque = new(BlendMode.Opaque, DepthMode.Off, CullMode.None, Topology.Triangles);
}

/// <summary>A pipeline's identity: the program, the state and the formats of the target it draws into.</summary>
public readonly record struct PipelineKey(ProgramId Program, DrawState State, TextureFormat Color0, TextureFormat? Color1,
    bool Depth)
{
    /// <summary>The one key a draw of <paramref name="program"/> with <paramref name="state"/> into <paramref name="target"/>
    /// takes.</summary>
    public static PipelineKey For(ProgramId program, in DrawState state, RenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new PipelineKey(program, state, target.Color.Format, target.Color1?.Format, target.HasDepth);
    }
}

/// <summary>A made pipeline (GL: a linked program and a state set; Direct3D 12: a pipeline state object).</summary>
public abstract class GpuPipeline : GpuResource
{
    protected GpuPipeline(in PipelineKey key, string name, long epoch)
        : base(name, epoch)
    {
        Key = key;
    }

    public PipelineKey Key { get; }
}

/// <summary>The device's pipelines: looked up on the render thread, made off it.</summary>
public interface IPipelineCache
{
    /// <summary>"adapter LUID · driver version · build" — a different key means every pipeline is made again.</summary>
    string Key { get; }

    int Count { get; }

    /// <summary>Pipelines made on the render thread because no warm-up had made them (Direct3D 12 counts; GL's are cheap and counted
    /// too).</summary>
    long RenderThreadMakes { get; }

    /// <summary>The pipeline, if made. Never makes one: the render thread calls only this.</summary>
    bool TryGet(in PipelineKey key, [NotNullWhen(true)] out GpuPipeline? pipeline);

    /// <summary>Makes the pipelines off the render thread (GL: on the compile lane's shared context where there is one, else one a
    /// frame on the render thread, counted).</summary>
    Task<PipelineWarmResult> WarmAsync(IReadOnlyCollection<PipelineKey> keys, CancellationToken ct);
}

/// <summary>What a warm-up did: the keys asked, made, found already made, and the words of each that failed.</summary>
public sealed record PipelineWarmResult(int Asked, int Made, int FromCache, ImmutableArray<string> Failed);
