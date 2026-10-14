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

    /// <summary>Four 32-bit float channels (particle state, accumulation).</summary>
    Rgba32F,

    /// <summary>Four 16-bit unsigned normalised channels (P216/PA16 unpack, deep sends).</summary>
    Rgba16,

    /// <summary>10-bit colour, 2-bit alpha (the 10-bit output picture).</summary>
    Rgb10A2,

    // Block-compressed (4×4 blocks; sampled only, never a render target). Present only when the context's extensions say so
    // (GlCaps); an absent format refuses with words, it never falls back silently.
    Bc1,
    Bc1Alpha,
    Bc3,
    Bc4,
    Bc5,
    Bc7,
    Bc6hUfloat,
    Bc6hSfloat,
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

    /// <summary>16-bit unsigned, four channels.</summary>
    Rgba16,
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

/// <summary>What a texture format is, named per format (never by enum order: formats are appended, and a later uncompressed one must
/// not read as a block format).</summary>
public static class TextureFormats
{
    public static bool IsBlockCompressed(TextureFormat f) => f is TextureFormat.Bc1 or TextureFormat.Bc1Alpha or TextureFormat.Bc3
        or TextureFormat.Bc4 or TextureFormat.Bc5 or TextureFormat.Bc7 or TextureFormat.Bc6hUfloat or TextureFormat.Bc6hSfloat;

    /// <summary>Bytes per 4×4 block (8 for BC1 and BC4, 16 for BC3, BC5, BC7 and BC6H); 0 for an uncompressed format.</summary>
    public static int BlockBytes(TextureFormat f) => f switch
    {
        TextureFormat.Bc1 or TextureFormat.Bc1Alpha or TextureFormat.Bc4 => 8,
        TextureFormat.Bc3 or TextureFormat.Bc5 or TextureFormat.Bc7 or TextureFormat.Bc6hUfloat or TextureFormat.Bc6hSfloat => 16,
        _ => 0,
    };

    /// <summary>Bytes of one level of a block-compressed texture (sizes round up to whole blocks).</summary>
    public static long CompressedSize(TextureFormat f, int width, int height) =>
        (long)((width + 3) / 4) * ((height + 3) / 4) * BlockBytes(f);
}

/// <summary>What the context in hand can sample and render to, from its version and extensions (never from
/// GL_COMPRESSED_TEXTURE_FORMATS, which drivers under-report); filled once per context by the device.</summary>
public sealed record GlCaps(bool Dxt1, bool Dxt5, bool Rgtc, bool Bptc, bool Norm16, bool FloatBlend, bool ColorBufferFloat,
    string Dialect)
{
    public static readonly GlCaps None = new(false, false, false, false, false, false, false, "core330");

    public bool IsCore => Dialect == "core330";

    /// <summary>Pure, so tests feed fake extension lists. Desktop: RGTC is core from 3.0, BPTC from 4.2; ES needs the EXT names.
    /// S3TC gives DXT1 and DXT5; EXT_texture_compression_dxt1 gives DXT1 only; ANGLE_texture_compression_dxt5 gives DXT5.</summary>
    public static GlCaps From(string dialect, int major, int minor, IReadOnlySet<string> ext)
    {
        ArgumentNullException.ThrowIfNull(ext);
        var core = dialect == "core330";
        var s3tc = ext.Contains("GL_EXT_texture_compression_s3tc");
        return new GlCaps(
            Dxt1: s3tc || ext.Contains("GL_EXT_texture_compression_dxt1"),
            Dxt5: s3tc || ext.Contains("GL_ANGLE_texture_compression_dxt5"),
            Rgtc: (core && major >= 3) || ext.Contains("GL_ARB_texture_compression_rgtc") || ext.Contains("GL_EXT_texture_compression_rgtc"),
            Bptc: (core && (major > 4 || (major == 4 && minor >= 2))) || ext.Contains("GL_ARB_texture_compression_bptc")
                  || ext.Contains("GL_EXT_texture_compression_bptc"),
            Norm16: core || ext.Contains("GL_EXT_texture_norm16"),
            FloatBlend: core || ext.Contains("GL_EXT_float_blend"),
            ColorBufferFloat: core || ext.Contains("GL_EXT_color_buffer_float"),
            Dialect: dialect);
    }

    /// <summary>Can it be sampled? (Block formats only through the compressed upload path.)</summary>
    public bool Supports(TextureFormat f) => f switch
    {
        TextureFormat.Bc1 or TextureFormat.Bc1Alpha => Dxt1,
        TextureFormat.Bc3 => Dxt5,
        TextureFormat.Bc4 or TextureFormat.Bc5 => Rgtc,
        TextureFormat.Bc7 or TextureFormat.Bc6hUfloat or TextureFormat.Bc6hSfloat => Bptc,
        TextureFormat.R16 or TextureFormat.Rg16 or TextureFormat.Rgba16 => Norm16,
        _ => true,
    };

    /// <summary>Can it be a render target (and, for 32-bit float, be blended)? Block formats never are.</summary>
    public bool SupportsRenderTarget(TextureFormat f, bool blended = false) => f switch
    {
        _ when TextureFormats.IsBlockCompressed(f) => false,
        TextureFormat.R16 or TextureFormat.Rg16 or TextureFormat.Rgba16 => Norm16,
        TextureFormat.R16F or TextureFormat.Rg16F or TextureFormat.Rgba16F => ColorBufferFloat,
        TextureFormat.R32F or TextureFormat.Rg32F or TextureFormat.Rgba32F => ColorBufferFloat && (!blended || FloatBlend),
        _ => true,
    };
}
