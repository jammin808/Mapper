using System.Runtime.InteropServices;
using PlayMap.Core.Gpu;
using Silk.NET.OpenGL;

namespace PlayMap.Render.Gl;

public sealed unsafe partial class GlDevice : IFenceSignals
{
    // ---- textures ----

    public GpuTexture CreateTexture(int width, int height, TextureFormat format, TextureFilter filter = TextureFilter.Linear, TextureWrap wrap = TextureWrap.Clamp) =>
        CreateTexture(width, height, format, filter, wrap, TextureUsage.Sampled | TextureUsage.CopySource | TextureUsage.CopyDest);

    /// <summary>A texture made for the given uses (a target's attachment is also drawn into).</summary>
    internal GpuTexture CreateTexture(int width, int height, TextureFormat format, TextureFilter filter, TextureWrap wrap, TextureUsage usage)
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
        var (internalFormat, pixelFormat, type) = GlFormatTable.Describe(Dialect, format);
        _gl.TexImage2D(GLEnum.Texture2D, 0, (int)internalFormat, (uint)width, (uint)height, 0, pixelFormat, type, null);
        var f = filter == TextureFilter.Nearest ? (int)GLEnum.Nearest : (int)GLEnum.Linear;
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, f);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, f);
        var w = wrap == TextureWrap.Repeat ? (int)GLEnum.Repeat : (int)GLEnum.ClampToEdge;
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapS, w);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapT, w);
        Check("texture");
        return new GlTexture(this, id, width, height, format, filter, wrap, usage);
    }

    /// <summary>Shows a one-channel texture as grey (R in R, G and B, alpha one): a Gray8 picture samples as
    /// a picture.</summary>
    public void SetGraySwizzle(GpuTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        BindTextureId(0, texture.Id);
        _swappedRedBlue.Remove(texture.Id);
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
        var (format, type) = UploadLayout(texture, source);
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
        var (format, type) = UploadLayout(texture, source);
        _gl.BindBuffer(GLEnum.PixelUnpackBuffer, buffer);
        _gl.PixelStore(GLEnum.UnpackRowLength, rowPixels);
        _gl.TexSubImage2D(GLEnum.Texture2D, 0, 0, 0, (uint)texture.Width, (uint)texture.Height, format, type, (void*)offset);
        _gl.PixelStore(GLEnum.UnpackRowLength, 0);
        _gl.BindBuffer(GLEnum.PixelUnpackBuffer, 0);
        texture.Version++;
        Check("upload pbo");
    }

    /// <summary>Binds the texture on unit 0 and answers the upload layout of a CPU format on this dialect; a BGRA upload on ES
    /// swaps red and blue through the texture's swizzle (set once, and put back when an RGBA upload follows).</summary>
    private (GLEnum Format, GLEnum Type) UploadLayout(GpuTexture texture, UploadFormat source)
    {
        var (format, type, swap) = GlFormatTable.Upload(Dialect, source);
        BindTextureId(0, texture.Id);
        if (swap && _swappedRedBlue.Add(texture.Id))
        {
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureSwizzleR, (int)GLEnum.Blue);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureSwizzleB, (int)GLEnum.Red);
        }
        else if (!swap && _swappedRedBlue.Remove(texture.Id))
        {
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureSwizzleR, (int)GLEnum.Red);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureSwizzleB, (int)GLEnum.Blue);
        }
        return (format, type);
    }

    internal void DeleteTexture(uint id)
    {
        if (_disposed || !_liveTextures.Remove(id))
            return;
        _swappedRedBlue.Remove(id);
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
        var color = CreateTexture(width, height, format, filter, TextureWrap.Clamp, AttachmentUsage);
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
            var target = new GlRenderTarget(this, fbo, color, rb);
            target.Dispose();
            throw new GlException($"A {width}×{height} {format} target is incomplete: {status}.");
        }
        Check("target");
        return new GlRenderTarget(this, fbo, color, rb);
    }

    /// <summary>What a target's colour attachment is made for: drawn into, sampled, copied.</summary>
    internal const TextureUsage AttachmentUsage = TextureUsage.Sampled | TextureUsage.RenderTarget | TextureUsage.CopySource | TextureUsage.CopyDest;

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
            _gl.ReadPixels(0, 0, (uint)target.Width, (uint)target.Height, HasReadBgra ? GLEnum.Bgra : GLEnum.Rgba, GLEnum.UnsignedByte, p);
        if (!HasReadBgra)
        {
            // ES without EXT_read_format_bgra reads RGBA: red and blue change places here.
            var n = target.Width * target.Height * 4;
            for (var i = 0; i < n; i += 4)
                (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        }
        Check("read");
    }

    /// <summary>Reads a target back synchronously as floats (RGBA, top row first) — a float pipeline's own
    /// values, for tests that measure below one 8-bit code. Either dialect gives the same values: a fixed-point target reads as
    /// its codes over 2^bits − 1, a float target as its floats.</summary>
    public void ReadPixelsFloat(RenderTarget target, Span<float> rgba)
    {
        ArgumentNullException.ThrowIfNull(target);
        var count = target.Width * target.Height * 4;
        if (rgba.Length < count)
            throw new ArgumentException("The buffer is smaller than the target.", nameof(rgba));
        BindTarget(target);
        if (Dialect == GlDialect.Es300 && !IsFloatTarget(target.Format))
            ReadFixedPointAsFloat(target, rgba[..count]);
        else
            fixed (float* p = rgba)
                _gl.ReadPixels(0, 0, (uint)target.Width, (uint)target.Height, GLEnum.Rgba, GLEnum.Float, p);
        Check("read float");
    }

    private static bool IsFloatTarget(TextureFormat format) => format is TextureFormat.R16F or TextureFormat.Rg16F
        or TextureFormat.Rgba16F or TextureFormat.R32F or TextureFormat.Rg32F or TextureFormat.Rgba32F;

    /// <summary>ES reads a fixed-point buffer only as its own codes (ES 3.0 §4.3.2: RGBA with UNSIGNED_BYTE for 8-bit, with
    /// UNSIGNED_INT_2_10_10_10_REV for 10-bit, and with UNSIGNED_SHORT for 16-bit through EXT_texture_norm16), so they are read
    /// that way and scaled here to code / (2^bits − 1), the values desktop GL's float read of the same buffer converts to.</summary>
    private void ReadFixedPointAsFloat(RenderTarget target, Span<float> rgba)
    {
        var (w, h) = ((uint)target.Width, (uint)target.Height);
        switch (target.Format)
        {
            case TextureFormat.R16 or TextureFormat.Rg16 or TextureFormat.Rgba16:
            {
                var codes = new ushort[rgba.Length];
                fixed (ushort* p = codes)
                    _gl.ReadPixels(0, 0, w, h, GLEnum.Rgba, GLEnum.UnsignedShort, p);
                for (var i = 0; i < rgba.Length; i++)
                    rgba[i] = codes[i] / 65535f;
                break;
            }
            case TextureFormat.Rgb10A2:
            {
                var words = new uint[rgba.Length / 4];
                fixed (uint* p = words)
                    _gl.ReadPixels(0, 0, w, h, GLEnum.Rgba, GLEnum.UnsignedInt2101010Rev, p);
                for (var i = 0; i < words.Length; i++)
                {
                    var word = words[i];
                    rgba[i * 4] = (word & 0x3FF) / 1023f;
                    rgba[i * 4 + 1] = ((word >> 10) & 0x3FF) / 1023f;
                    rgba[i * 4 + 2] = ((word >> 20) & 0x3FF) / 1023f;
                    rgba[i * 4 + 3] = (word >> 30) / 3f;
                }
                break;
            }
            default:
            {
                var codes = new byte[rgba.Length];
                fixed (byte* p = codes)
                    _gl.ReadPixels(0, 0, w, h, GLEnum.Rgba, GLEnum.UnsignedByte, p);
                for (var i = 0; i < rgba.Length; i++)
                    rgba[i] = codes[i] / 255f;
                break;
            }
        }
    }

    // ---- buffers ----

    public GpuBuffer CreateBuffer(BufferKind kind)
    {
        AssertOwner();
        var id = _gl.GenBuffer();
        _liveBuffers.Add(id);
        return new GlBuffer(this, id, kind);
    }

    /// <summary>Replaces a buffer's contents. The store is orphaned (re-specified) so the GPU may keep reading
    /// the old contents while the new ones stream in; it grows to fit and never shrinks.</summary>
    public void SetBufferData<T>(GpuBuffer buffer, ReadOnlySpan<T> data, bool dynamic = true) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(buffer);
        AssertOwner();
        var gl = (GlBuffer)buffer;
        var target = Target(gl.Kind);
        if (gl.Kind == BufferKind.Index)
            BindVertexArrayId(0); // an element buffer binds into the bound vertex array; keep it out of a live one
        _gl.BindBuffer(target, gl.Id);
        var bytes = data.Length * sizeof(T);
        if (bytes > gl.Capacity)
            gl.Capacity = Math.Max(16, Math.Max(bytes, gl.Capacity + gl.Capacity / 2));
        _gl.BufferData(target, (nuint)gl.Capacity, null, dynamic ? GLEnum.StreamDraw : GLEnum.StaticDraw);
        if (bytes > 0)
            fixed (T* p = data)
                _gl.BufferSubData(target, 0, (nuint)bytes, p);
        if (gl.Kind == BufferKind.Vertex)
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
        return new VertexArray(this, id, attributes, vertices, indices, instances);
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

    nint IFenceSignals.Insert() => InsertFence();

    bool IFenceSignals.IsSignaled(nint signal) => IsSignaled(signal);

    void IFenceSignals.Delete(nint signal) => DeleteFence(signal);

    internal uint CreateQuery() => _gl.GenQuery();

    internal void DeleteQuery(uint id)
    {
        if (id != 0 && !_disposed)
            _gl.DeleteQuery(id);
    }

    internal void BeginTimeQuery(uint id) => _gl.BeginQuery(GLEnum.TimeElapsed, id);

    internal void EndTimeQuery() => _gl.EndQuery(GLEnum.TimeElapsed);

    /// <summary>The query's nanoseconds when the GPU has it; false when not yet (never waits). On ES a result taken across a
    /// disjoint event (GL_GPU_DISJOINT_EXT: the GPU's clock jumped) is no measurement: it answers true with −1.</summary>
    internal bool TryQueryResult(uint id, out long nanoseconds)
    {
        nanoseconds = 0;
        long available = 0;
        _gl.GetQueryObject(id, GLEnum.QueryResultAvailable, &available);
        if (available == 0)
            return false;
        long ns = 0;
        _gl.GetQueryObject(id, GLEnum.QueryResult, &ns);
        nanoseconds = Dialect == GlDialect.Es300 && _gl.GetInteger((GLEnum)0x8FBB) != 0 ? -1 : ns;
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
        if (!HasReadBgra)
            throw new NotSupportedException("this ES context reads no BGRA (EXT_read_format_bgra is absent): streamed read-backs are off here");
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
    internal void DrawPresent(uint windowVao, GpuTexture texture, int width, int height) => DrawPresent(windowVao, texture, width, height, flipY: true);

    /// <summary>As <see cref="DrawPresent(uint, GpuTexture, int, int)"/>; <paramref name="flipY"/> false copies rows as they are, for a
    /// default framebuffer whose rows already run top first (ANGLE's surface on a Direct3D 11 back buffer).</summary>
    internal void DrawPresent(uint windowVao, GpuTexture texture, int width, int height, bool flipY)
    {
        var program = Program(flipY ? "present" : "blit");
        _gl.BindFramebuffer(GLEnum.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        _gl.Disable(GLEnum.Blend);
        _gl.Disable(GLEnum.DepthTest);
        _gl.Disable(GLEnum.CullFace);
        _gl.UseProgram(program.Id);
        _gl.Uniform1(_gl.GetUniformLocation(program.Id, "uTex"), 0);
        if (!flipY)
            _gl.Uniform4(_gl.GetUniformLocation(program.Id, "uSrc"), 0f, 0f, 1f, 1f);
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

    /// <summary>Each format's internal format and its upload layout on desktop GL (<see cref="GlFormatTable"/> holds the table
    /// per dialect); a block format carries its compressed internal format and is uploaded only through the compressed path.</summary>
    internal static (GLEnum Internal, GLEnum Format, GLEnum Type) Describe(TextureFormat format) => GlFormatTable.Describe(GlDialect.Core330, format);

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
