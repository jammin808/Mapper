using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace PlayMap.Render.Gpu;

public sealed unsafe partial class GlDevice
{
    // ---- textures ----

    public GpuTexture CreateTexture(int width, int height, TextureFormat format, TextureFilter filter = TextureFilter.Linear, TextureWrap wrap = TextureWrap.Clamp)
    {
        AssertOwner();
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (TextureFormats.IsBlockCompressed(format))
            throw new ArgumentException($"{format} is block-compressed: it is made and filled only through the compressed upload path, never as a plain texture.", nameof(format));
        if (width > MaxTextureSize || height > MaxTextureSize)
            throw new GlException($"A {width}×{height} texture is larger than this GPU's {MaxTextureSize}.");
        var id = _gl.GenTexture();
        _liveTextures.Add(id);
        BindTextureId(0, id);
        var (internalFormat, pixelFormat, type) = Describe(format);
        _gl.TexImage2D(GLEnum.Texture2D, 0, (int)internalFormat, (uint)width, (uint)height, 0, pixelFormat, type, null);
        var f = filter == TextureFilter.Nearest ? (int)GLEnum.Nearest : (int)GLEnum.Linear;
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, f);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, f);
        var w = wrap == TextureWrap.Repeat ? (int)GLEnum.Repeat : (int)GLEnum.ClampToEdge;
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapS, w);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapT, w);
        Check("texture");
        return new GpuTexture(this, id, width, height, format, filter, wrap);
    }

    /// <summary>Shows a one-channel texture as grey (R in R, G and B, alpha one): a Gray8 picture samples as
    /// a picture.</summary>
    public void SetGraySwizzle(GpuTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        BindTextureId(0, texture.Id);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureSwizzleR, (int)GLEnum.Red);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureSwizzleG, (int)GLEnum.Red);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureSwizzleB, (int)GLEnum.Red);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureSwizzleA, (int)GLEnum.One);
    }

    /// <summary>Uploads a whole picture from CPU memory. <paramref name="rowPixels"/> is the source row length
    /// in pixels when rows are padded (0 = tightly packed).</summary>
    public void Upload(GpuTexture texture, ReadOnlySpan<byte> pixels, UploadFormat source, int rowPixels = 0)
    {
        ArgumentNullException.ThrowIfNull(texture);
        var need = (long)(rowPixels > 0 ? rowPixels : texture.Width) * (texture.Height - 1) * BytesPerPixel(source) + (long)texture.Width * BytesPerPixel(source);
        if (pixels.Length < need)
            throw new ArgumentException($"An upload of {texture.Width}×{texture.Height} needs {need} bytes; {pixels.Length} given.", nameof(pixels));
        fixed (byte* p = pixels)
            UploadPointer(texture, (nint)p, source, rowPixels);
    }

    /// <summary>Uploads from a pointer the caller keeps valid for the call.</summary>
    public void UploadPointer(GpuTexture texture, nint pixels, UploadFormat source, int rowPixels = 0)
    {
        ArgumentNullException.ThrowIfNull(texture);
        AssertOwner();
        var (format, type) = Describe(source);
        BindTextureId(0, texture.Id);
        _gl.PixelStore(GLEnum.UnpackRowLength, rowPixels);
        _gl.TexSubImage2D(GLEnum.Texture2D, 0, 0, 0, (uint)texture.Width, (uint)texture.Height, format, type, (void*)pixels);
        _gl.PixelStore(GLEnum.UnpackRowLength, 0);
        texture.Version++;
        Check("upload");
    }

    /// <summary>Uploads from the bound pixel-unpack buffer at a byte offset (the streaming path).</summary>
    internal void UploadFromUnpackBuffer(GpuTexture texture, uint buffer, nint offset, UploadFormat source, int rowPixels)
    {
        AssertOwner();
        var (format, type) = Describe(source);
        BindTextureId(0, texture.Id);
        _gl.BindBuffer(GLEnum.PixelUnpackBuffer, buffer);
        _gl.PixelStore(GLEnum.UnpackRowLength, rowPixels);
        _gl.TexSubImage2D(GLEnum.Texture2D, 0, 0, 0, (uint)texture.Width, (uint)texture.Height, format, type, (void*)offset);
        _gl.PixelStore(GLEnum.UnpackRowLength, 0);
        _gl.BindBuffer(GLEnum.PixelUnpackBuffer, 0);
        texture.Version++;
        Check("upload pbo");
    }

    internal void DeleteTexture(uint id)
    {
        if (_disposed || !_liveTextures.Remove(id))
            return;
        AssertOwner();
        for (var i = 0; i < _boundTextures.Length; i++)
            if (_boundTextures[i] == id)
                _boundTextures[i] = uint.MaxValue;
        _gl.DeleteTexture(id);
    }

    // ---- render targets ----

    public RenderTarget CreateRenderTarget(int width, int height, TextureFormat format, bool depth = false, TextureFilter filter = TextureFilter.Linear)
    {
        if (!Caps.SupportsRenderTarget(format))
            throw new ArgumentException($"This context cannot render to {format} ({Caps.Dialect}); the caller falls back to a format it can (RGBA16F for 16-bit unsigned).", nameof(format));
        var color = CreateTexture(width, height, format, filter);
        var fbo = _gl.GenFramebuffer();
        _liveFramebuffers.Add(fbo);
        _gl.BindFramebuffer(GLEnum.Framebuffer, fbo);
        _framebuffer = fbo;
        _gl.FramebufferTexture2D(GLEnum.Framebuffer, GLEnum.ColorAttachment0, GLEnum.Texture2D, color.Id, 0);
        uint rb = 0;
        if (depth)
        {
            rb = _gl.GenRenderbuffer();
            _liveRenderbuffers.Add(rb);
            _gl.BindRenderbuffer(GLEnum.Renderbuffer, rb);
            _gl.RenderbufferStorage(GLEnum.Renderbuffer, GLEnum.DepthComponent24, (uint)width, (uint)height);
            _gl.FramebufferRenderbuffer(GLEnum.Framebuffer, GLEnum.DepthAttachment, GLEnum.Renderbuffer, rb);
            _gl.BindRenderbuffer(GLEnum.Renderbuffer, 0);
        }
        var status = _gl.CheckFramebufferStatus(GLEnum.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            var target = new RenderTarget(this, fbo, color, rb);
            target.Dispose();
            throw new GlException($"A {width}×{height} {format} target is incomplete: {status}.");
        }
        Check("target");
        return new RenderTarget(this, fbo, color, rb);
    }

    internal void DeleteRenderTarget(uint framebuffer, uint depth)
    {
        if (_disposed)
            return;
        AssertOwner();
        if (_liveFramebuffers.Remove(framebuffer))
        {
            if (_framebuffer == framebuffer)
                _framebuffer = uint.MaxValue;
            _gl.DeleteFramebuffer(framebuffer);
        }
        if (depth != 0 && _liveRenderbuffers.Remove(depth))
            _gl.DeleteRenderbuffer(depth);
    }

    /// <summary>Reads a target back synchronously as BGRA bytes, top row first (tests and one-off
    /// snapshots; the preview path uses <see cref="PixelPackRing"/>, which never stalls).</summary>
    public void ReadPixels(RenderTarget target, Span<byte> bgra)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (bgra.Length < target.Width * target.Height * 4)
            throw new ArgumentException("The buffer is smaller than the target.", nameof(bgra));
        BindTarget(target);
        fixed (byte* p = bgra)
            _gl.ReadPixels(0, 0, (uint)target.Width, (uint)target.Height, GLEnum.Bgra, GLEnum.UnsignedByte, p);
        Check("read");
    }

    /// <summary>Reads a target back synchronously as floats (RGBA, top row first) — a float pipeline's own
    /// values, for tests that measure below one 8-bit code.</summary>
    public void ReadPixelsFloat(RenderTarget target, Span<float> rgba)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (rgba.Length < target.Width * target.Height * 4)
            throw new ArgumentException("The buffer is smaller than the target.", nameof(rgba));
        BindTarget(target);
        fixed (float* p = rgba)
            _gl.ReadPixels(0, 0, (uint)target.Width, (uint)target.Height, GLEnum.Rgba, GLEnum.Float, p);
        Check("read float");
    }

    // ---- buffers ----

    public GpuBuffer CreateBuffer(BufferKind kind)
    {
        AssertOwner();
        var id = _gl.GenBuffer();
        _liveBuffers.Add(id);
        return new GpuBuffer(this, id, kind);
    }

    /// <summary>Replaces a buffer's contents. The store is orphaned (re-specified) so the GPU may keep reading
    /// the old contents while the new ones stream in; it grows to fit and never shrinks.</summary>
    public void SetBufferData<T>(GpuBuffer buffer, ReadOnlySpan<T> data, bool dynamic = true) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(buffer);
        AssertOwner();
        var target = Target(buffer.Kind);
        if (buffer.Kind == BufferKind.Index)
            BindVertexArrayId(0); // an element buffer binds into the bound vertex array; keep it out of a live one
        _gl.BindBuffer(target, buffer.Id);
        var bytes = data.Length * sizeof(T);
        if (bytes > buffer.Capacity)
            buffer.Capacity = Math.Max(16, Math.Max(bytes, buffer.Capacity + buffer.Capacity / 2));
        _gl.BufferData(target, (nuint)buffer.Capacity, null, dynamic ? GLEnum.StreamDraw : GLEnum.StaticDraw);
        if (bytes > 0)
            fixed (T* p = data)
                _gl.BufferSubData(target, 0, (nuint)bytes, p);
        if (buffer.Kind == BufferKind.Vertex)
            _gl.BindBuffer(target, 0);
        Check("buffer");
    }

    internal void DeleteBuffer(uint id)
    {
        if (_disposed || !_liveBuffers.Remove(id))
            return;
        AssertOwner();
        _gl.DeleteBuffer(id);
    }

    private static GLEnum Target(BufferKind kind) => kind == BufferKind.Index ? GLEnum.ElementArrayBuffer : GLEnum.ArrayBuffer;

    // ---- vertex arrays ----

    /// <summary>A vertex array over a vertex buffer (attribute buffer 0), an optional index buffer and an
    /// optional instance buffer (attribute buffer 1). It belongs to the current context.</summary>
    public VertexArray CreateVertexArray(ReadOnlySpan<VertexAttribute> attributes, GpuBuffer? vertices, GpuBuffer? indices = null, GpuBuffer? instances = null)
    {
        AssertOwner();
        var id = _gl.GenVertexArray();
        _liveVertexArrays.Add(id);
        _gl.BindVertexArray(id);
        _vao = id;
        foreach (var a in attributes)
        {
            var buffer = a.Buffer == 1 ? instances : vertices;
            if (buffer is null)
                throw new ArgumentException($"Attribute {a.Location} reads a buffer that was not given.", nameof(attributes));
            _gl.BindBuffer(GLEnum.ArrayBuffer, buffer.Id);
            _gl.EnableVertexAttribArray((uint)a.Location);
            _gl.VertexAttribPointer((uint)a.Location, a.Components, GLEnum.Float, false, (uint)a.StrideBytes, (void*)a.OffsetBytes);
            if (a.Divisor != 0)
                _gl.VertexAttribDivisor((uint)a.Location, (uint)a.Divisor);
        }
        if (indices is not null)
            _gl.BindBuffer(GLEnum.ElementArrayBuffer, indices.Id);
        _gl.BindVertexArray(0);
        _vao = 0;
        _gl.BindBuffer(GLEnum.ArrayBuffer, 0);
        Check("vertex array");
        return new VertexArray(this, id, vertices, indices, instances);
    }

    internal void DeleteVertexArray(uint id)
    {
        if (_disposed || !_liveVertexArrays.Remove(id))
            return;
        AssertOwner();
        if (_vao == id)
            _vao = uint.MaxValue;
        _gl.DeleteVertexArray(id);
    }

    // ---- fences and queries ----

    /// <summary>A fence after the commands queued so far.</summary>
    public nint InsertFence() => _gl.FenceSync(GLEnum.SyncGpuCommandsComplete, (SyncBehaviorFlags)0);

    /// <summary>True once the GPU passed the fence; never waits.</summary>
    public bool IsSignaled(nint fence)
    {
        if (fence == 0)
            return true;
        var r = _gl.ClientWaitSync(fence, 0u, 0);
        return r == GLEnum.AlreadySignaled || r == GLEnum.ConditionSatisfied;
    }

    public void DeleteFence(nint fence)
    {
        if (fence != 0 && !_disposed)
            _gl.DeleteSync(fence);
    }

    internal uint CreateQuery() => _gl.GenQuery();

    internal void DeleteQuery(uint id)
    {
        if (id != 0 && !_disposed)
            _gl.DeleteQuery(id);
    }

    internal void BeginTimeQuery(uint id) => _gl.BeginQuery(GLEnum.TimeElapsed, id);

    internal void EndTimeQuery() => _gl.EndQuery(GLEnum.TimeElapsed);

    /// <summary>The query's nanoseconds when the GPU has it; false when not yet (never waits).</summary>
    internal bool TryQueryResult(uint id, out long nanoseconds)
    {
        nanoseconds = 0;
        long available = 0;
        _gl.GetQueryObject(id, GLEnum.QueryResultAvailable, &available);
        if (available == 0)
            return false;
        long ns = 0;
        _gl.GetQueryObject(id, GLEnum.QueryResult, &ns);
        nanoseconds = ns;
        return true;
    }

    // ---- pixel buffers ----

    internal uint CreatePixelBuffer(bool pack, int bytes)
    {
        var id = _gl.GenBuffer();
        _liveBuffers.Add(id);
        var target = pack ? GLEnum.PixelPackBuffer : GLEnum.PixelUnpackBuffer;
        _gl.BindBuffer(target, id);
        _gl.BufferData(target, (nuint)bytes, null, pack ? GLEnum.StreamRead : GLEnum.StreamDraw);
        _gl.BindBuffer(target, 0);
        return id;
    }

    /// <summary>Maps an unpack buffer for writing, orphaning its old store; null when the driver refuses.</summary>
    internal byte* MapUnpack(uint buffer, int bytes)
    {
        _gl.BindBuffer(GLEnum.PixelUnpackBuffer, buffer);
        _gl.BufferData(GLEnum.PixelUnpackBuffer, (nuint)bytes, null, GLEnum.StreamDraw);
        var p = (byte*)_gl.MapBufferRange(GLEnum.PixelUnpackBuffer, 0, (nuint)bytes, (uint)GLEnum.MapWriteBit | (uint)GLEnum.MapInvalidateBufferBit);
        _gl.BindBuffer(GLEnum.PixelUnpackBuffer, 0);
        return p;
    }

    internal void UnmapUnpack(uint buffer)
    {
        _gl.BindBuffer(GLEnum.PixelUnpackBuffer, buffer);
        _gl.UnmapBuffer(GLEnum.PixelUnpackBuffer);
        _gl.BindBuffer(GLEnum.PixelUnpackBuffer, 0);
    }

    /// <summary>Queues a read of a target into a pack buffer (BGRA, top row first); returns at once.</summary>
    internal void ReadIntoPackBuffer(RenderTarget target, uint buffer)
    {
        BindTarget(target);
        _gl.BindBuffer(GLEnum.PixelPackBuffer, buffer);
        _gl.ReadPixels(0, 0, (uint)target.Width, (uint)target.Height, GLEnum.Bgra, GLEnum.UnsignedByte, null);
        _gl.BindBuffer(GLEnum.PixelPackBuffer, 0);
        Check("read pbo");
    }

    internal byte* MapPack(uint buffer, int bytes)
    {
        _gl.BindBuffer(GLEnum.PixelPackBuffer, buffer);
        var p = (byte*)_gl.MapBufferRange(GLEnum.PixelPackBuffer, 0, (nuint)bytes, (uint)GLEnum.MapReadBit);
        _gl.BindBuffer(GLEnum.PixelPackBuffer, 0);
        return p;
    }

    internal void UnmapPack(uint buffer)
    {
        _gl.BindBuffer(GLEnum.PixelPackBuffer, buffer);
        _gl.UnmapBuffer(GLEnum.PixelPackBuffer);
        _gl.BindBuffer(GLEnum.PixelPackBuffer, 0);
    }

    // ---- the present path (runs in an output window's context) ----

    /// <summary>A vertex array for the context current now (an output window's): container objects are not
    /// shared, so each window keeps its own for the present draw.</summary>
    internal uint CreateContextVertexArray() => _gl.GenVertexArray();

    internal void DeleteContextVertexArray(uint id)
    {
        if (id != 0 && !_disposed)
            _gl.DeleteVertexArray(id);
    }

    /// <summary>Draws a texture over the default framebuffer of the context current now (an output window's),
    /// flipping it so its top row is the screen's top. Raw calls: the window's context has its own state,
    /// which this sets in full; the device's cached state (its own context's) is untouched.</summary>
    internal void DrawPresent(uint windowVao, GpuTexture texture, int width, int height)
    {
        var program = Program("present");
        _gl.BindFramebuffer(GLEnum.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        _gl.Disable(GLEnum.Blend);
        _gl.Disable(GLEnum.DepthTest);
        _gl.Disable(GLEnum.CullFace);
        _gl.UseProgram(program.Id);
        _gl.Uniform1(_gl.GetUniformLocation(program.Id, "uTex"), 0);
        _gl.ActiveTexture(GLEnum.Texture0);
        _gl.BindTexture(GLEnum.Texture2D, texture.Id);
        _gl.BindVertexArray(windowVao);
        _gl.DrawArrays(GLEnum.Triangles, 0, 3);
        _gl.BindVertexArray(0);
    }

    /// <summary>Clears the default framebuffer of the context current now to black (a blackout with no picture yet).</summary>
    internal void ClearDefault(int width, int height)
    {
        _gl.BindFramebuffer(GLEnum.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        _gl.ClearColor(0, 0, 0, 1);
        _gl.Clear((uint)GLEnum.ColorBufferBit);
    }

    // ---- format tables ----

    /// <summary>Each format's internal format and its upload layout; a block format carries its compressed internal format and is
    /// uploaded only through the compressed path (its layout is unused). Every value has its own arm.</summary>
    internal static (GLEnum Internal, GLEnum Format, GLEnum Type) Describe(TextureFormat format) => format switch
    {
        TextureFormat.Rgba8 => (GLEnum.Rgba8, GLEnum.Rgba, GLEnum.UnsignedByte),
        TextureFormat.R8 => (GLEnum.R8, GLEnum.Red, GLEnum.UnsignedByte),
        TextureFormat.Rg8 => (GLEnum.RG8, GLEnum.RG, GLEnum.UnsignedByte),
        TextureFormat.R16 => (GLEnum.R16, GLEnum.Red, GLEnum.UnsignedShort),
        TextureFormat.Rg16 => (GLEnum.RG16, GLEnum.RG, GLEnum.UnsignedShort),
        TextureFormat.R16F => (GLEnum.R16f, GLEnum.Red, GLEnum.HalfFloat),
        TextureFormat.Rg16F => (GLEnum.RG16f, GLEnum.RG, GLEnum.HalfFloat),
        TextureFormat.Rgba16F => (GLEnum.Rgba16f, GLEnum.Rgba, GLEnum.HalfFloat),
        TextureFormat.R32F => (GLEnum.R32f, GLEnum.Red, GLEnum.Float),
        TextureFormat.Rg32F => (GLEnum.RG32f, GLEnum.RG, GLEnum.Float),
        TextureFormat.Rgba32F => (GLEnum.Rgba32f, GLEnum.Rgba, GLEnum.Float),
        TextureFormat.Rgba16 => (GLEnum.Rgba16, GLEnum.Rgba, GLEnum.UnsignedShort),
        TextureFormat.Rgb10A2 => (GLEnum.Rgb10A2, GLEnum.Rgba, GLEnum.UnsignedInt2101010Rev),
        TextureFormat.Bc1 => ((GLEnum)0x83F0, GLEnum.Rgba, GLEnum.UnsignedByte),        // COMPRESSED_RGB_S3TC_DXT1_EXT
        TextureFormat.Bc1Alpha => ((GLEnum)0x83F1, GLEnum.Rgba, GLEnum.UnsignedByte),   // COMPRESSED_RGBA_S3TC_DXT1_EXT
        TextureFormat.Bc3 => ((GLEnum)0x83F3, GLEnum.Rgba, GLEnum.UnsignedByte),        // COMPRESSED_RGBA_S3TC_DXT5_EXT
        TextureFormat.Bc4 => ((GLEnum)0x8DBB, GLEnum.Red, GLEnum.UnsignedByte),         // COMPRESSED_RED_RGTC1
        TextureFormat.Bc5 => ((GLEnum)0x8DBD, GLEnum.RG, GLEnum.UnsignedByte),          // COMPRESSED_RG_RGTC2
        TextureFormat.Bc7 => ((GLEnum)0x8E8C, GLEnum.Rgba, GLEnum.UnsignedByte),        // COMPRESSED_RGBA_BPTC_UNORM
        TextureFormat.Bc6hSfloat => ((GLEnum)0x8E8E, GLEnum.Rgb, GLEnum.Float),         // COMPRESSED_RGB_BPTC_SIGNED_FLOAT
        TextureFormat.Bc6hUfloat => ((GLEnum)0x8E8F, GLEnum.Rgb, GLEnum.Float),         // COMPRESSED_RGB_BPTC_UNSIGNED_FLOAT
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "a texture format with no description"),
    };

    private static (GLEnum Format, GLEnum Type) Describe(UploadFormat format) => format switch
    {
        UploadFormat.Bgra8 => (GLEnum.Bgra, GLEnum.UnsignedByte),
        UploadFormat.R8 => (GLEnum.Red, GLEnum.UnsignedByte),
        UploadFormat.Rg8 => (GLEnum.RG, GLEnum.UnsignedByte),
        UploadFormat.R16 => (GLEnum.Red, GLEnum.UnsignedShort),
        UploadFormat.Rg16 => (GLEnum.RG, GLEnum.UnsignedShort),
        UploadFormat.RgbaHalf => (GLEnum.Rgba, GLEnum.HalfFloat),
        UploadFormat.R32F => (GLEnum.Red, GLEnum.Float),
        UploadFormat.Rg32F => (GLEnum.RG, GLEnum.Float),
        UploadFormat.Rgba32F => (GLEnum.Rgba, GLEnum.Float),
        UploadFormat.Rgba16 => (GLEnum.Rgba, GLEnum.UnsignedShort),
        _ => (GLEnum.Rgba, GLEnum.UnsignedByte),
    };

    /// <summary>Bytes per pixel of a CPU layout.</summary>
    public static int BytesPerPixel(UploadFormat format) => format switch
    {
        UploadFormat.R8 => 1,
        UploadFormat.Rg8 => 2,
        UploadFormat.R16 => 2,
        UploadFormat.Rg16 => 4,
        UploadFormat.RgbaHalf => 8,
        UploadFormat.R32F => 4,
        UploadFormat.Rg32F => 8,
        UploadFormat.Rgba32F => 16,
        UploadFormat.Rgba16 => 8,
        _ => 4,
    };

    /// <summary>A span over unmanaged memory the caller owns for the duration of a use.</summary>
    internal static Span<byte> Span(byte* p, int bytes) => new(p, bytes);

    internal static void Copy(nint source, byte* destination, long bytes) =>
        System.Buffer.MemoryCopy((void*)source, destination, bytes, bytes);

    internal static ReadOnlySpan<byte> Bytes<T>(ReadOnlySpan<T> data) where T : unmanaged => MemoryMarshal.AsBytes(data);
}
