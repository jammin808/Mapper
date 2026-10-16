using System.Runtime.InteropServices;
using PlayMap.Core.Media;
using PlayMap.Render.Decode;
using Silk.NET.OpenGL;

namespace PlayMap.Render.Gpu;

/// <summary>The compressed upload path (HAP): block-compressed textures are made at a whole number of 4×4 blocks and filled
/// with tight rows of blocks, from client memory or from a pixel-unpack buffer. What can be sampled comes from <see cref="Caps"/>
/// (the context's version and extensions), never from a driver's compressed-format list; a format the context lacks is
/// refused with words, never uploaded into a texture that would sample black.</summary>
public sealed unsafe partial class GlDevice
{
    /// <summary>The block families this context samples (from <see cref="Caps"/>): BC1 with DXT1, BC3 with DXT5, BC4 with RGTC,
    /// BC7 and BC6H with BPTC. No persistent staging: frames are copied into the driver as they are uploaded.</summary>
    public BlockFormats BlockFormats =>
        (Caps.Dxt1 ? BlockFormats.Bc1 : 0) | (Caps.Dxt5 ? BlockFormats.Bc3 : 0) | (Caps.Rgtc ? BlockFormats.Bc4 : 0)
        | (Caps.Bptc ? BlockFormats.Bc7 | BlockFormats.Bc6h : 0);

    /// <summary>The window-system binding of the context current on the calling thread — "egl", "glx", "wgl" or "cgl" — asked of
    /// the binding's own library (the one that names a current context), never guessed from the environment: GLFW on Linux
    /// takes EGL under Wayland and GLX under X11, and an XWayland session sets both displays. "" when no binding answers.
    /// Render thread, with the context current.</summary>
    public static string CurrentBinding()
    {
        var egl = OperatingSystem.IsWindows() ? "libEGL.dll" : OperatingSystem.IsMacOS() ? "libEGL.dylib" : "libEGL.so.1";
        if (HasCurrent(egl, "eglGetCurrentContext"))
            return "egl";
        if (OperatingSystem.IsWindows())
            return HasCurrent("opengl32.dll", "wglGetCurrentContext") ? "wgl" : "";
        if (OperatingSystem.IsMacOS())
            return HasCurrent("/System/Library/Frameworks/OpenGL.framework/OpenGL", "CGLGetCurrentContext") ? "cgl" : "";
        return HasCurrent("libGL.so.1", "glXGetCurrentContext") || HasCurrent("libGLX.so.0", "glXGetCurrentContext") ? "glx" : "";
    }

    // A library loaded to ask stays loaded: a binding library is never unloaded under a live context.
    private static bool HasCurrent(string library, string function) =>
        NativeLibrary.TryLoad(library, out var handle) && NativeLibrary.TryGetExport(handle, function, out var get)
        && ((delegate* unmanaged<nint>)get)() != 0;

    /// <summary>The words that refuse a block format this context cannot sample (null when it can).</summary>
    public string? RefuseBlockFormat(TextureFormat format) => RefuseBlockFormat(Caps, format);

    /// <summary>The words that refuse a block format a context with <paramref name="caps"/> cannot sample (null when it can).</summary>
    public static string? RefuseBlockFormat(GlCaps caps, TextureFormat format)
    {
        ArgumentNullException.ThrowIfNull(caps);
        if (!TextureFormats.IsBlockCompressed(format))
            return $"{format} is not a block-compressed format";
        if (caps.Supports(format))
            return null;
        var family = format switch
        {
            TextureFormat.Bc1 or TextureFormat.Bc1Alpha => "S3TC (DXT1)",
            TextureFormat.Bc3 => "S3TC (DXT5)",
            TextureFormat.Bc4 or TextureFormat.Bc5 => "RGTC",
            _ => "BPTC",
        };
        return $"this GPU's GL ({caps.Dialect}) does not offer {family} textures, which {format} needs";
    }

    /// <summary>A block-compressed texture for a picture of <paramref name="width"/>×<paramref name="height"/> pixels; its size
    /// rounds up to whole blocks (the coded size). Sampled with nearest filtering (the `hap` pass reads exact texels).</summary>
    public GpuTexture CreateCompressedTexture(int width, int height, TextureFormat format)
    {
        AssertOwner();
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (RefuseBlockFormat(format) is { } why)
            throw new NotSupportedException(why);
        var w = (width + 3) / 4 * 4;
        var h = (height + 3) / 4 * 4;
        if (w > MaxTextureSize || h > MaxTextureSize)
            throw new GlException($"A {w}×{h} texture is larger than this GPU's {MaxTextureSize}.");
        var id = _gl.GenTexture();
        _liveTextures.Add(id);
        BindTextureId(0, id);
        var (internalFormat, _, _) = Describe(format);
        var bytes = (uint)TextureFormats.CompressedSize(format, w, h);
        _gl.CompressedTexImage2D(GLEnum.Texture2D, 0, internalFormat, (uint)w, (uint)h, 0, bytes, null);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMaxLevel, 0);
        Check("compressed texture");
        return new GpuTexture(this, id, w, h, format, TextureFilter.Nearest, TextureWrap.Clamp);
    }

    /// <summary>Fills a whole compressed texture from client memory: <paramref name="bytes"/> of tight rows of blocks, top row
    /// first.</summary>
    public void UploadCompressed(GpuTexture texture, nint blocks, int bytes)
    {
        ArgumentNullException.ThrowIfNull(texture);
        AssertOwner();
        CheckCompressedSize(texture, bytes);
        BindTextureId(0, texture.Id);
        var (internalFormat, _, _) = Describe(texture.Format);
        _gl.CompressedTexSubImage2D(GLEnum.Texture2D, 0, 0, 0, (uint)texture.Width, (uint)texture.Height, internalFormat, (uint)bytes, (void*)blocks);
        texture.Version++;
        Check("compressed upload");
    }

    /// <summary>Fills a whole compressed texture from a pixel-unpack buffer at a byte offset (the streaming path).</summary>
    internal void UploadCompressedFromUnpackBuffer(GpuTexture texture, uint buffer, nint offset, int bytes)
    {
        AssertOwner();
        CheckCompressedSize(texture, bytes);
        BindTextureId(0, texture.Id);
        var (internalFormat, _, _) = Describe(texture.Format);
        _gl.BindBuffer(GLEnum.PixelUnpackBuffer, buffer);
        _gl.CompressedTexSubImage2D(GLEnum.Texture2D, 0, 0, 0, (uint)texture.Width, (uint)texture.Height, internalFormat, (uint)bytes, (void*)offset);
        _gl.BindBuffer(GLEnum.PixelUnpackBuffer, 0);
        texture.Version++;
        Check("compressed upload pbo");
    }

    private static void CheckCompressedSize(GpuTexture texture, int bytes)
    {
        var need = TextureFormats.CompressedSize(texture.Format, texture.Width, texture.Height);
        if (need == 0)
            throw new ArgumentException($"{texture.Format} is not block-compressed: it uploads through the plain path.", nameof(texture));
        if (bytes != need)
            throw new ArgumentException($"A {texture.Width}×{texture.Height} {texture.Format} texture takes {need} bytes; {bytes} given.", nameof(bytes));
    }
    // ---- zero-copy imports and the upload arena (wp2-zerocopy) ----

    // glEGLImageTargetTexture2DOES, resolved once per device (OES_EGL_image; Mesa offers it in desktop core contexts too).
    private nint _imageTarget;

    /// <summary>What this context's decode imports use: the share decoders see, the upload arena, the zero-copy facts. Set by the
    /// render host when the context comes (tests set their own); null when nothing imports into this context.</summary>
    public DecodeContext? DecodeContext { get; set; }

    /// <summary>A texture name whose storage an EGLImage provides (<see cref="BindImage"/>); filtered linearly and clamped, as plane
    /// textures are sampled by the `yuv` pass. Dispose deletes the name only (the image is its importer's).</summary>
    public GpuTexture AdoptExternal(int width, int height, TextureFormat format)
    {
        AssertOwner();
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        var id = _gl.GenTexture();
        _liveTextures.Add(id);
        BindTextureId(0, id);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapT, (int)GLEnum.ClampToEdge);
        Check("external texture");
        return new GpuTexture(this, id, width, height, format, TextureFilter.Linear, TextureWrap.Clamp);
    }

    /// <summary>Gives an adopted texture the EGLImage's storage. False when this context has no glEGLImageTargetTexture2DOES or the
    /// GL refused the image (its error is cleared, never left queued for a later check).</summary>
    public bool BindImage(GpuTexture texture, nint image, Func<string, nint> procAddress)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentNullException.ThrowIfNull(procAddress);
        AssertOwner();
        if (_imageTarget == 0)
            _imageTarget = procAddress("glEGLImageTargetTexture2DOES");
        if (_imageTarget == 0 || image == 0)
            return false;
        BindTextureId(0, texture.Id);
        while (_gl.GetError() != GLEnum.NoError)
        {
            // Errors queued before this call are not this import's.
        }
        ((delegate* unmanaged<GLEnum, nint, void>)_imageTarget)(GLEnum.Texture2D, image);
        var ok = _gl.GetError() == GLEnum.NoError;
        texture.Version++;
        return ok;
    }

    /// <summary>Whether this context can make a persistently mapped buffer (desktop 4.4 or ARB_buffer_storage; ES EXT_buffer_storage).</summary>
    public bool HasBufferStorage =>
        (Dialect == GlDialect.Es300 ? Extensions.Contains("GL_EXT_buffer_storage")
            : GlVersion >= 44 || Extensions.Contains("GL_ARB_buffer_storage"));

    /// <summary>A pixel-unpack buffer of <paramref name="bytes"/> mapped for writing for its whole life, coherent (a write from any
    /// thread is seen by every later GL command); 0 with a null pointer when this context cannot make one.</summary>
    internal uint CreatePersistentUnpackBuffer(long bytes, out nint pointer)
    {
        AssertOwner();
        pointer = 0;
        if (!HasBufferStorage || bytes <= 0)
            return 0;
        var id = _gl.GenBuffer();
        _liveBuffers.Add(id);
        _gl.BindBuffer(GLEnum.PixelUnpackBuffer, id);
        // GL_MAP_WRITE_BIT | GL_MAP_PERSISTENT_BIT | GL_MAP_COHERENT_BIT, for the storage and for the mapping alike.
        const uint flags = 0x0002 | 0x0040 | 0x0080;
        _gl.BufferStorage(BufferStorageTarget.PixelUnpackBuffer, (nuint)bytes, null, (BufferStorageMask)flags);
        var p = _gl.MapBufferRange(GLEnum.PixelUnpackBuffer, 0, (nuint)bytes, flags);
        _gl.BindBuffer(GLEnum.PixelUnpackBuffer, 0);
        if (p is null || _gl.GetError() != GLEnum.NoError)
        {
            DeleteBuffer(id);
            return 0;
        }
        pointer = (nint)p;
        return id;
    }

    /// <summary>Unmaps and deletes a persistent buffer (render thread, its context current).</summary>
    internal void DeletePersistentBuffer(uint id)
    {
        if (id == 0)
            return;
        _gl.BindBuffer(GLEnum.PixelUnpackBuffer, id);
        _gl.UnmapBuffer(GLEnum.PixelUnpackBuffer);
        _gl.BindBuffer(GLEnum.PixelUnpackBuffer, 0);
        DeleteBuffer(id);
    }
}
