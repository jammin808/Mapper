using System.Collections.Frozen;
using System.Collections.Immutable;
using PlayMap.Core.Media;

namespace PlayMap.Core.Gpu;

/// <summary>What a device refused or failed at, in its own words: a program that did not compile or link, a target it cannot make, an
/// error the API reported. Each backend's errors are of this kind, so frame code catches them without naming a backend.</summary>
public class GpuException : Exception
{
    public GpuException()
    {
    }

    public GpuException(string message)
        : base(message)
    {
    }

    public GpuException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

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
/// shared handles; the shader model on Direct3D 12; the largest texture side it makes. A format the device lacks is refused with
/// words, never silently replaced.</summary>
public sealed record GpuFeatures(FrozenDictionary<TextureFormat, FormatSupport> Formats, BlockFormats Blocks, bool Timers,
    bool SharedHandles, string ShaderModel, ImmutableArray<string> Words)
{
    /// <summary>The largest width or height of a texture or target, texels (Direct3D 12's floor where a device says nothing).</summary>
    public int MaxTextureSize { get; init; } = 16384;

    /// <summary>Whether the device reads targets back to the CPU (<see cref="IReadbackRing"/>, <see cref="IHeldReadbackRing"/>): an
    /// OpenGL ES 3.0 context that reads no BGRA reads none, and a reader of such a device says so rather than wait for reads.</summary>
    public bool Readback { get; init; } = true;

    /// <summary>Why the device lacks a format it lacks, in its own words, per format it can say it for (a block family the context
    /// does not offer); a format missing here and absent from <see cref="Formats"/>' sampled set has no words.</summary>
    public FrozenDictionary<TextureFormat, string> Refusals { get; init; } = FrozenDictionary<TextureFormat, string>.Empty;

    /// <summary>The words that refuse sampling a format here, or null when the device samples it.</summary>
    public string? RefusalOf(TextureFormat format) =>
        Formats.TryGetValue(format, out var support) && support.Sampled ? null
        : Refusals.TryGetValue(format, out var words) ? words
        : $"this GPU samples no {format} textures";
}

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

    /// <summary>A buffer of the uses asked; a desc of 0 bytes makes no store until the first set (Direct3D 12 makes no 0-byte
    /// resource).</summary>
    GpuBuffer CreateBuffer(in BufferDesc desc);

    /// <summary>Replaces the buffer's content with <paramref name="data"/> for the draws recorded after this call; the draws recorded
    /// or submitted before it keep the content they were recorded with (the backend renames the store and releases the old one at
    /// the last fence that read it). <see cref="GpuBuffer.Bytes"/> is then the data's size. Render thread.</summary>
    void SetBufferData<T>(GpuBuffer buffer, ReadOnlySpan<T> data) where T : unmanaged;

    GpuMesh CreateMesh(ReadOnlySpan<VertexAttribute> attributes, GpuBuffer? vertices, GpuBuffer? indices = null,
        GpuBuffer? instances = null);

    /// <summary>A direct list for frame <paramref name="frame"/> (render thread), submitted with <see cref="Submit"/> before the next
    /// list opens. One frame number may open several lists in turn — the frame's own, then a list for each preview drawn after it, a
    /// resolve or an upload asked between frames — and a list outside the renderer's frames is numbered 0 or below them: the number is a
    /// label (it names the frame's timestamps, <see cref="IGpuTimers"/>), not a list's identity. A backend fences what a list used
    /// (command memory, ring pages, constants, released resources) by the <see cref="FenceValue"/> its <see cref="Submit"/> returns,
    /// never by the frame number.</summary>
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

    /// <summary>A read-back ring of its own for one reader (a preview, a send, an LED map) with <paramref name="slots"/> reads in
    /// flight: its reads come back in its own order, never another reader's. The reader disposes it.</summary>
    IReadbackRing CreateReadbackRing(int slots = 3);

    /// <summary>A read-back ring whose reads the reader keeps in place until it lets go of them (a send's packed frames, read by the
    /// sender's own thread from the GPU's bytes with no copy), with <paramref name="slots"/> reads in flight or held. The reader
    /// disposes it once it holds no read.</summary>
    IHeldReadbackRing CreateHeldReadbackRing(int slots = 4);

    IGpuTimers Timers { get; }

    ITextureImporter Importer { get; }

    ITextureExporter? Exporter { get; }

    /// <summary>A target's pixels now, waiting for the GPU: tests and snapshots only.</summary>
    void ReadNow(RenderTarget target, Span<byte> bgra);

    void ReadNowFloat(RenderTarget target, int attachment, Span<float> rgba);
}
