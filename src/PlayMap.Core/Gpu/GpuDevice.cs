using System.Collections.Frozen;
using System.Collections.Immutable;
using PlayMap.Core.Media;

namespace PlayMap.Core.Gpu;

/// <summary>The API a device draws through.</summary>
public enum GpuBackendKind
{
    Gl,
    D3D12,
}

/// <summary>The adapter a device draws on: its LUID (0 where the API names none), PCI vendor, name, driver version, the API's own
/// words for itself, and whether it is a software rasteriser (WARP, llvmpipe).</summary>
public sealed record GpuAdapterInfo(long Luid, int VendorId, string Name, string Driver, string Api, bool Software);

/// <summary>What the device can do, asked once at creation: per format, sampled, target, blendable; the block formats; timers;
/// shared handles; the shader model on Direct3D 12. A format the device lacks is refused with words, never silently replaced.</summary>
public sealed record GpuFeatures(FrozenDictionary<TextureFormat, FormatSupport> Formats, BlockFormats Blocks, bool Timers,
    bool SharedHandles, string ShaderModel, ImmutableArray<string> Words);

/// <summary>What a device does with one format.</summary>
public readonly record struct FormatSupport(bool Sampled, bool Target, bool Blend);

/// <summary>A GPU behind the render hardware interface: resources, the frame's command lists, fences, programs, rings, imports. A
/// backend implements it whole; every caller draws through it and names no API.
///
/// Threading: <see cref="BeginFrame"/>, the direct list, <see cref="Submit"/> and presents on the render thread; resources made on
/// the render thread on GL (a GL context is one thread's), on any thread on Direct3D 12; a resource disposed on any thread, its
/// release deferred to the last fence value that used it; <see cref="IsComplete"/> from any thread, lock-free;
/// <see cref="CreateProgramAsync"/> and the pipeline warm-up from any thread.</summary>
public interface IGpuDevice : IDisposable
{
    GpuBackendKind Kind { get; }

    GpuAdapterInfo Adapter { get; }

    GpuFeatures Features { get; }

    /// <summary>Bumped by every rebuild; every resource carries the epoch it was made in.</summary>
    long Epoch { get; }

    IProgramLibrary Programs { get; }

    IPipelineCache Pipelines { get; }

    GpuTexture CreateTexture(in TextureDesc desc);

    RenderTarget CreateTarget(in TargetDesc desc);

    GpuBuffer CreateBuffer(in BufferDesc desc);

    void SetBufferData<T>(GpuBuffer buffer, ReadOnlySpan<T> data) where T : unmanaged;

    GpuMesh CreateMesh(ReadOnlySpan<VertexAttribute> attributes, GpuBuffer? vertices, GpuBuffer? indices = null,
        GpuBuffer? instances = null);

    /// <summary>The frame's direct list (render thread).</summary>
    ICommandList BeginFrame(long frame);

    FenceValue Submit(ICommandList list);

    bool IsComplete(in FenceValue value);

    /// <summary>Blocks until the value completes: start-up, scans and tests only — the render loop never calls it mid-show.</summary>
    void WaitFor(in FenceValue value);

    /// <summary>A user program, made off the render thread: GLSL on the compile lane's shared context (GL); a pipeline state from
    /// DXIL and reflection (Direct3D 12). The id is valid on this device's epoch only.</summary>
    Task<ProgramMade> CreateProgramAsync(ProgramSource source, CancellationToken ct);

    /// <summary>Releases a user program after the last frame that drew with it completes.</summary>
    void ReleaseProgram(ProgramId id);

    /// <summary>A block a plan places transient textures in.</summary>
    ITransientMemory CreateTransientMemory(long bytes, string name);

    /// <summary>A resource a plan manages: the backend's automatic barriers stop for it and the plan's own take over (GL: no
    /// effect).</summary>
    void SetPlanManaged(GpuResource resource, bool planManaged);

    IUploadRing Uploads { get; }

    IReadbackRing Readbacks { get; }

    IGpuTimers Timers { get; }

    ITextureImporter Importer { get; }

    ITextureExporter? Exporter { get; }

    /// <summary>A target's pixels now, waiting for the GPU: tests and snapshots only.</summary>
    void ReadNow(RenderTarget target, Span<byte> bgra);

    void ReadNowFloat(RenderTarget target, int attachment, Span<float> rgba);
}
