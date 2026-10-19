using PlayMap.Core.Gpu;

namespace PlayMap.Render.Gl;

/// <summary>The kinds of GL buffer the device makes: vertices (and instances) or indices.</summary>
public enum BufferKind
{
    Vertex,
    Index,
}

/// <summary>What the GL device's draw calls draw (the first three of <see cref="Topology"/>).</summary>
public enum PrimitiveKind
{
    Triangles,
    TriangleStrip,
    Lines,
}

/// <summary>Something wrong in the GL: a failed compile, an incomplete framebuffer, an error flag.</summary>
public sealed class GlException : GpuException
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

/// <summary>A texture of the GL device. Owned by the <see cref="GlDevice"/> that made it and deleted on that device's thread with its
/// context current; disposing twice does nothing.</summary>
public sealed class GlTexture : GpuTexture
{
    /// <summary>The GL device's resources all belong to its one epoch.</summary>
    internal const long GlEpoch = 1;

    private readonly GlDevice _device;

    internal GlTexture(GlDevice device, uint id, int width, int height, TextureFormat format, TextureFilter filter, TextureWrap wrap,
        TextureUsage usage = TextureUsage.Sampled | TextureUsage.CopySource | TextureUsage.CopyDest, string name = "")
        : base(new TextureDesc(width, height, format, usage, filter, wrap, name), GlEpoch)
    {
        _device = device;
        Id = id;
    }

    internal uint Id { get; private set; }

    protected override void Dispose(bool disposing)
    {
        if (Id == 0)
            return;
        _device.DeleteTexture(Id);
        Id = 0;
        IsDisposed = true;
    }
}

/// <summary>An offscreen colour target of the GL device (a framebuffer over a texture) with an optional depth buffer.</summary>
public sealed class GlRenderTarget : RenderTarget
{
    private readonly GlDevice _device;

    internal GlRenderTarget(GlDevice device, uint framebuffer, GpuTexture color, uint depth)
        : this(device, framebuffer, color, null, depth, "")
    {
    }

    /// <summary>A target over one or two colour attachments (<paramref name="color1"/> the fragment shader's output 1).</summary>
    internal GlRenderTarget(GlDevice device, uint framebuffer, GpuTexture color, GpuTexture? color1, uint depth, string name)
        : base(color, color1, depth != 0, name, GlTexture.GlEpoch)
    {
        _device = device;
        Framebuffer = framebuffer;
        DepthBuffer = depth;
    }

    internal uint Framebuffer { get; private set; }
    internal uint DepthBuffer { get; private set; }

    protected override void Dispose(bool disposing)
    {
        if (Framebuffer == 0)
            return;
        _device.DeleteRenderTarget(Framebuffer, DepthBuffer);
        Framebuffer = 0;
        DepthBuffer = 0;
        Color.Dispose();
        Color1?.Dispose();
        IsDisposed = true;
    }
}

/// <summary>A GL buffer of vertices or indices; its store grows to fit what is set into it and never shrinks.</summary>
public sealed class GlBuffer : GpuBuffer
{
    private readonly GlDevice _device;

    internal GlBuffer(GlDevice device, uint id, BufferKind kind)
        : this(device, id, kind, kind == BufferKind.Index ? BufferUsage.Index : BufferUsage.Vertex, "")
    {
    }

    /// <summary>A buffer made for the given uses (an index buffer binds as the element array, every other as an array buffer).</summary>
    internal GlBuffer(GlDevice device, uint id, BufferKind kind, BufferUsage usage, string name)
        : base(new BufferDesc(0, usage, name), GlTexture.GlEpoch)
    {
        _device = device;
        Id = id;
        Kind = kind;
    }

    internal uint Id { get; private set; }

    public BufferKind Kind { get; }

    /// <summary>Bytes allocated (it grows; it never shrinks).</summary>
    public int Capacity
    {
        get => (int)Bytes;
        internal set => Bytes = value;
    }

    protected override void Dispose(bool disposing)
    {
        if (Id == 0)
            return;
        _device.DeleteBuffer(Id);
        Id = 0;
        IsDisposed = true;
    }
}

/// <summary>A vertex array: which buffers feed which shader inputs. A container object, so it belongs to the one context that made
/// it (each output window keeps its own for the present).</summary>
public sealed class VertexArray : GpuMesh
{
    private readonly GlDevice _device;

    internal VertexArray(GlDevice device, uint id, ReadOnlySpan<VertexAttribute> attributes, GpuBuffer? vertices, GpuBuffer? indices,
        GpuBuffer? instances)
        : base(attributes, vertices, indices, instances, "", GlTexture.GlEpoch)
    {
        _device = device;
        Id = id;
    }

    internal uint Id { get; private set; }

    protected override void Dispose(bool disposing)
    {
        if (Id == 0)
            return;
        _device.DeleteVertexArray(Id);
        Id = 0;
        IsDisposed = true;
    }
}

/// <summary>The GL names behind the contracts' resources, for this assembly's GL code: a resource another backend made is not a GL
/// one, and the cast refuses it.</summary>
internal static class GlNames
{
    extension(GpuTexture texture)
    {
        /// <summary>The texture's GL name (0 once deleted).</summary>
        internal uint Id => ((GlTexture)texture).Id;
    }

    extension(RenderTarget target)
    {
        /// <summary>The target's framebuffer (0 once deleted).</summary>
        internal uint Framebuffer => ((GlRenderTarget)target).Framebuffer;
    }

    extension(GpuBuffer buffer)
    {
        /// <summary>The buffer's GL name (0 once deleted).</summary>
        internal uint Id => ((GlBuffer)buffer).Id;
    }
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
