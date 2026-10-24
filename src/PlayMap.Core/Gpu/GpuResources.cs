using System.Collections.Immutable;

namespace PlayMap.Core.Gpu;

/// <summary>How a texture is used, which a backend makes it for.</summary>
[Flags]
public enum TextureUsage
{
    None = 0,
    Sampled = 1,
    RenderTarget = 2,
    DepthStencil = 4,
    Storage = 8,
    CopySource = 16,
    CopyDest = 32,

    /// <summary>Opened by another device or process through an NT handle (previews, Spout's bridge).</summary>
    Shared = 64,
}

/// <summary>How a texture's channels are read when it is sampled.</summary>
public enum TextureSwizzle
{
    /// <summary>As stored.</summary>
    None,

    /// <summary>A one-channel texture read as grey: its red in red, green and blue, alpha one (a Gray8 picture).</summary>
    Gray,
}

/// <summary>A texture to make: its size, format, uses, the filter and wrap it is sampled with, a name for the debuggers, and how its
/// channels are read.</summary>
public readonly record struct TextureDesc(int Width, int Height, TextureFormat Format, TextureUsage Usage,
    TextureFilter Filter = TextureFilter.Linear, TextureWrap Wrap = TextureWrap.Clamp, string Name = "",
    TextureSwizzle Swizzle = TextureSwizzle.None);

/// <summary>A target's attachments: one or two colour textures (the particle step writes two) and an optional depth.</summary>
public readonly record struct TargetDesc(int Width, int Height, TextureFormat Color0, TextureFormat? Color1 = null,
    bool Depth = false, TextureFilter Filter = TextureFilter.Linear, string Name = "");

/// <summary>A buffer to make: its size in bytes (0: no store until the first set), its uses and a name for the debuggers.</summary>
public readonly record struct BufferDesc(long Bytes, BufferUsage Usage, string Name = "");

/// <summary>What a buffer holds.</summary>
[Flags]
public enum BufferUsage
{
    None = 0,
    Vertex = 1,
    Index = 2,
    Instance = 4,
    Constant = 8,
    Structured = 16,
    Indirect = 32,

    /// <summary>Set again every frame or so (particles, the editor's lines, a cursor): a backend keeps it where the CPU writes cheaply
    /// (GL: a streaming store; Direct3D 12: upload memory). Without it a buffer is static, set once or seldom (a mesh: GL's static store,
    /// Direct3D 12's device memory).</summary>
    Dynamic = 64,
}

/// <summary>Anything a device made. Disposal on any thread: the backend releases it only once the GPU has finished with it
/// (GL at once, the driver keeping it alive; Direct3D 12 when the fence value of its last use completes). Disposing twice does
/// nothing.</summary>
public abstract class GpuResource : IDisposable
{
    protected GpuResource(string name, long epoch)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        Epoch = epoch;
    }

    public string Name { get; }

    /// <summary>The device epoch it belongs to: a resource of an older epoch is gone (the device was rebuilt).</summary>
    public long Epoch { get; }

    /// <summary>Set once the backend has let go of it.</summary>
    public bool IsDisposed { get; protected set; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Lets go of the resource once; a second call does nothing. Sets <see cref="IsDisposed"/>.</summary>
    protected abstract void Dispose(bool disposing);
}

/// <summary>A texture on the GPU, sampled with its own filter and wrap. Its image is stored top row first: a texture's v = 0 is its
/// top row, as a picture uploaded from the CPU is.</summary>
public abstract class GpuTexture : GpuResource
{
    protected GpuTexture(in TextureDesc desc, long epoch)
        : base(desc.Name ?? "", epoch)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(desc.Width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(desc.Height, 1);
        Width = desc.Width;
        Height = desc.Height;
        Format = desc.Format;
        Filter = desc.Filter;
        Wrap = desc.Wrap;
        Usage = desc.Usage;
    }

    public int Width { get; }
    public int Height { get; }
    public TextureFormat Format { get; }
    public TextureFilter Filter { get; }
    public TextureWrap Wrap { get; }
    public TextureUsage Usage { get; }

    /// <summary>Bumped by whoever draws or uploads into it: caches compare it to know a picture changed.</summary>
    public long Version { get; set; }
}

/// <summary>Where a pass draws: one or two colour textures and an optional depth buffer, the image top row first (clip y = −w is the
/// target's row 0: <see cref="ClipSpace"/>), so a read-back is in image order.</summary>
public abstract class RenderTarget : GpuResource
{
    protected RenderTarget(GpuTexture color, GpuTexture? color1, bool hasDepth, string name, long epoch)
        : base(name, epoch)
    {
        ArgumentNullException.ThrowIfNull(color);
        Color = color;
        Color1 = color1;
        HasDepth = hasDepth;
    }

    public GpuTexture Color { get; }

    /// <summary>The second colour attachment (a particle step's velocities), null for a one-colour target.</summary>
    public GpuTexture? Color1 { get; }

    public bool HasDepth { get; }
    public int Width => Color.Width;
    public int Height => Color.Height;

    /// <summary>The format of the first colour attachment.</summary>
    public TextureFormat Format => Color.Format;
}

/// <summary>A buffer on the GPU. Setting its data (<see cref="IGpuDevice.SetBufferData{T}"/>) replaces its content for the draws recorded
/// after the set; draws recorded or submitted before it keep reading the content they were recorded with (a backend renames the store
/// and lets the old one go at the last fence that read it), so a buffer is set again freely between frames and between a frame's
/// lists.</summary>
public abstract class GpuBuffer : GpuResource
{
    protected GpuBuffer(in BufferDesc desc, long epoch)
        : base(desc.Name ?? "", epoch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(desc.Bytes);
        Bytes = desc.Bytes;
        Usage = desc.Usage;
    }

    /// <summary>The size of its content: the bytes last set, or before any set the size it was made with (0: no store yet).</summary>
    public long Bytes { get; protected set; }

    public BufferUsage Usage { get; }
}

/// <summary>Vertex, index and instance buffers with their attribute layout (attribute buffer 0 reads the vertices, 1 the instances).
/// The mesh reads the buffers; it does not own them.</summary>
public abstract class GpuMesh : GpuResource
{
    protected GpuMesh(ReadOnlySpan<VertexAttribute> attributes, GpuBuffer? vertices, GpuBuffer? indices, GpuBuffer? instances,
        string name, long epoch)
        : base(name, epoch)
    {
        Attributes = [.. attributes];
        Vertices = vertices;
        Indices = indices;
        Instances = instances;
    }

    public GpuBuffer? Vertices { get; }
    public GpuBuffer? Indices { get; }
    public GpuBuffer? Instances { get; }
    public ImmutableArray<VertexAttribute> Attributes { get; }
}
