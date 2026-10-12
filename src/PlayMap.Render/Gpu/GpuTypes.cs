namespace PlayMap.Render.Gpu;

/// <summary>How a texture stores its texels on the GPU. The 16-bit float formats are the pipeline's
/// intermediates (canvases, the projector pass, converted video): 8-bit in, float through, 8-bit out.</summary>
public enum TextureFormat
{
    Rgba8,
    R8,
    Rg8,
    /// <summary>16-bit unsigned normalised (10-bit video in a 16-bit container).</summary>
    R16,
    Rg16,
    R16F,
    Rg16F,
    Rgba16F,
    R32F,
    Rg32F,
}

/// <summary>The layout of CPU pixels handed to an upload.</summary>
public enum UploadFormat
{
    Rgba8,
    Bgra8,
    R8,
    Rg8,
    /// <summary>16-bit unsigned, one channel.</summary>
    R16,
    /// <summary>16-bit unsigned, two channels.</summary>
    Rg16,
    /// <summary>Half floats, four channels.</summary>
    RgbaHalf,
    /// <summary>32-bit floats, one channel.</summary>
    R32F,
    /// <summary>32-bit floats, two channels.</summary>
    Rg32F,
    /// <summary>32-bit floats, four channels.</summary>
    Rgba32F,
}

public enum TextureFilter
{
    Linear,
    Nearest,
}

public enum TextureWrap
{
    Clamp,
    Repeat,
}

/// <summary>How a draw combines with what is already in the target. Premultiplied sources use
/// <see cref="Premultiplied"/>; the canvas compositor's modes are <see cref="Normal"/> (straight alpha),
/// <see cref="Add"/>, <see cref="Multiply"/> and <see cref="Screen"/> (the shader shapes its output for each).
/// Every mode leaves the target's alpha as it was.</summary>
public enum BlendMode
{
    Opaque,
    Normal,
    Premultiplied,
    Add,
    /// <summary>Premultiplied additive (glow).</summary>
    AddPremultiplied,
    Multiply,
    Screen,
}

public enum CullMode
{
    None,
    /// <summary>Cull triangles that are clockwise in the target's raster (front faces counter-clockwise in
    /// the world as seen from the camera, after the renderer's top-down target convention).</summary>
    Back,
}

public enum PrimitiveKind
{
    Triangles,
    TriangleStrip,
    Lines,
}

public enum BufferKind
{
    Vertex,
    Index,
}

/// <summary>One vertex attribute: the shader location, how many floats, where it sits in its buffer, which
/// buffer (0 = the vertex buffer, 1 = the instance buffer) and the instancing divisor.</summary>
public readonly record struct VertexAttribute(int Location, int Components, int StrideBytes, int OffsetBytes, int Buffer = 0, int Divisor = 0);

/// <summary>Something wrong in the GL: a failed compile, an incomplete framebuffer, an error flag.</summary>
public sealed class GlException : Exception
{
    public GlException()
    {
    }

    public GlException(string message)
        : base(message)
    {
    }

    public GlException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>A texture on the GPU. Owned by the <see cref="GlDevice"/> that made it and deleted on that
/// device's thread with its context current; <see cref="Dispose"/> is idempotent.</summary>
public sealed class GpuTexture : IDisposable
{
    private readonly GlDevice _device;

    internal GpuTexture(GlDevice device, uint id, int width, int height, TextureFormat format, TextureFilter filter, TextureWrap wrap)
    {
        _device = device;
        Id = id;
        Width = width;
        Height = height;
        Format = format;
        Filter = filter;
        Wrap = wrap;
    }

    internal uint Id { get; private set; }

    public int Width { get; }
    public int Height { get; }
    public TextureFormat Format { get; }
    public TextureFilter Filter { get; }
    public TextureWrap Wrap { get; }
    public bool IsDisposed => Id == 0;

    /// <summary>Bumped by every upload or render into the texture (a consumer's cheap "did it change").</summary>
    public long Version { get; internal set; }

    public void Dispose()
    {
        if (Id == 0)
            return;
        _device.DeleteTexture(Id);
        Id = 0;
    }
}

/// <summary>An offscreen colour target (a framebuffer over a texture) with an optional depth buffer. Its
/// image is stored top row first: drawing through the renderer's top-down convention puts image row 0 at
/// framebuffer row 0, so a read-back is in image order and sampling at v = 0 reads the top.</summary>
public sealed class RenderTarget : IDisposable
{
    private readonly GlDevice _device;

    internal RenderTarget(GlDevice device, uint framebuffer, GpuTexture color, uint depth)
    {
        _device = device;
        Framebuffer = framebuffer;
        Color = color;
        DepthBuffer = depth;
    }

    internal uint Framebuffer { get; private set; }
    internal uint DepthBuffer { get; private set; }

    public GpuTexture Color { get; }
    public int Width => Color.Width;
    public int Height => Color.Height;
    public TextureFormat Format => Color.Format;
    public bool HasDepth => DepthBuffer != 0;
    public bool IsDisposed => Framebuffer == 0;

    public void Dispose()
    {
        if (Framebuffer == 0)
            return;
        _device.DeleteRenderTarget(Framebuffer, DepthBuffer);
        Framebuffer = 0;
        DepthBuffer = 0;
        Color.Dispose();
    }
}

/// <summary>A GPU buffer of vertices or indices.</summary>
public sealed class GpuBuffer : IDisposable
{
    private readonly GlDevice _device;

    internal GpuBuffer(GlDevice device, uint id, BufferKind kind)
    {
        _device = device;
        Id = id;
        Kind = kind;
    }

    internal uint Id { get; private set; }

    public BufferKind Kind { get; }

    /// <summary>Bytes allocated (it grows; it never shrinks).</summary>
    public int Capacity { get; internal set; }

    public void Dispose()
    {
        if (Id == 0)
            return;
        _device.DeleteBuffer(Id);
        Id = 0;
    }
}

/// <summary>A vertex array: which buffers feed which shader inputs. A container object, so it belongs to the
/// one context that made it (each output window keeps its own for the present).</summary>
public sealed class VertexArray : IDisposable
{
    private readonly GlDevice _device;

    internal VertexArray(GlDevice device, uint id, GpuBuffer? vertices, GpuBuffer? indices, GpuBuffer? instances)
    {
        _device = device;
        Id = id;
        Vertices = vertices;
        Indices = indices;
        Instances = instances;
    }

    internal uint Id { get; private set; }

    public GpuBuffer? Vertices { get; }
    public GpuBuffer? Indices { get; }
    public GpuBuffer? Instances { get; }

    public void Dispose()
    {
        if (Id == 0)
            return;
        _device.DeleteVertexArray(Id);
        Id = 0;
    }
}
