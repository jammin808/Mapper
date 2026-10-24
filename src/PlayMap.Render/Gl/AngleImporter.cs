using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PlayMap.Core.Gpu;
using PlayMap.Core.Media;
using PlayMap.Render.Gl.Windows;

namespace PlayMap.Render.Gl;

/// <summary>D3D11 structures and vtable slots the ANGLE importer uses, as d3d11.h declares them (x64). Plain data, so their layout
/// is pinned on every platform (AngleLayoutTests); the calls are Windows-only.</summary>
public static class D3D11Layout
{
    /// <summary>ID3D11Device::CreateTexture2D, ::GetImmediateContext (IUnknown's three, then the device's methods in header order).</summary>
    public const int DeviceCreateTexture2D = 5;
    public const int DeviceGetImmediateContext = 40;

    /// <summary>ID3D11DeviceContext::CopySubresourceRegion (IUnknown's three, ID3D11DeviceChild's four, then the context's methods).</summary>
    public const int ContextCopySubresourceRegion = 46;

    public const int IUnknownRelease = 2;

    // DXGI_FORMAT (dxgiformat.h).
    public const uint FormatNv12 = 103;
    public const uint FormatP010 = 104;

    // D3D11_USAGE_DEFAULT, D3D11_BIND_SHADER_RESOURCE.
    public const uint UsageDefault = 0;
    public const uint BindShaderResource = 0x8;
}

/// <summary>D3D11_TEXTURE2D_DESC: 44 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
public record struct D3D11Texture2DDesc(uint Width, uint Height, uint MipLevels, uint ArraySize, uint Format, uint SampleCount,
    uint SampleQuality, uint Usage, uint BindFlags, uint CpuAccessFlags, uint MiscFlags);

/// <summary>D3D11_BOX: 24 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
public record struct D3D11Box(uint Left, uint Top, uint Front, uint Right, uint Bottom, uint Back);

/// <summary>Imports D3D11VA frames into an ANGLE context made on the decoder's own device (wp2-d3d11's share), through
/// EGL_ANGLE_image_d3d11_texture with the plane and array-slice attributes of EGL_ANGLE_d3d_texture_client_buffer (NV12 and P010 need
/// the plane: 0 luma, 1 chroma). By default one CopySubresourceRegion a frame copies the decoder's array slice into one of three
/// pooled NV12/P010 textures whose plane images are made once; on the direct rung (proven, or forced) the slice itself is
/// imported, its images cached per (array, slice, plane) for the decoder pool's life. Plane textures cover the coded size, so the
/// `yuv` pass crops (uCrop, uMaxUv).
/// <para>One device, one immediate context: the decoder's writes, the copy and the draw are recorded on one context, which runs
/// them in order, so a frame is released as soon as its read is recorded — no fence and no extra held surface. The images are made
/// through the platform's own ANGLE loader on the display made on the share's device (<see cref="IAngleDisplay"/>), never a libEGL
/// found by name. Render thread only, inside the render loop's hold of the device's lock (the decoders take it around their
/// brackets), so it takes no lock of its own. Compile-only here; written to the D3D11 and ANGLE extension documentation.</para></summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class AngleImporter : IDisposable
{
    private const int EglNone = 0x3038;
    // EGL_ANGLE_image_d3d11_texture, EGL_ANGLE_d3d_texture_client_buffer.
    private const int EglD3D11TextureAngle = 0x3484;
    private const int EglD3D11TexturePlaneAngle = 0x3492;
    private const int EglD3D11TextureArraySliceAngle = 0x3493;
    private const int PoolSize = 3;

    private readonly GlDevice _device;
    private readonly IGpuShare _share;
    private readonly nint _display;
    private readonly nint _create;
    private readonly nint _destroy;
    private readonly AngleEgl _egl;
    private readonly Pooled[] _pool = new Pooled[PoolSize];
    private readonly Dictionary<(nint, int, int), (nint Image, GpuTexture Texture)> _direct = [];
    private long _directEpoch = -1;
    private int _next;
    private bool _disposed;

    private record struct Pooled
    {
        public nint Texture;
        public int Width;
        public int Height;
        public GpuSurfaceFormat Format;
        public nint LumaImage;
        public nint ChromaImage;
        public GpuTexture? Luma;
        public GpuTexture? Chroma;
    }

    private AngleImporter(GlDevice device, IGpuShare share, nint display, nint create, nint destroy, AngleEgl egl)
    {
        _device = device;
        _share = share;
        _display = display;
        _create = create;
        _destroy = destroy;
        _egl = egl;
    }

    /// <summary>An importer for the share's ANGLE display, current on this thread, or null with words.</summary>
    public static AngleImporter? ForCurrentContext(GlDevice device, IGpuShare share, out string words)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(share);
        words = "";
        if (share.Api != GpuApi.D3D11 || share.D3D11Device == 0)
        {
            words = "the renderer shares no D3D11 device";
            return null;
        }
        if (share is not IAngleDisplay angle || angle.Display == 0)
        {
            words = "the D3D11 device is not ANGLE's: its frames cannot be imported";
            return null;
        }
        var egl = angle.Egl;
        if (egl.CreateImageKhr is null || egl.DestroyImageKhr is null || egl.GetCurrentDisplay is null)
        {
            words = "ANGLE has no image entry points (EGL_KHR_image_base)";
            return null;
        }
        if (egl.GetCurrentDisplay() != angle.Display)
        {
            words = "the current context is not on the display made on the share's device";
            return null;
        }
        return new AngleImporter(device, share, angle.Display, (nint)egl.CreateImageKhr, (nint)egl.DestroyImageKhr, egl);
    }

    private nint GlProc(string name) => _egl.Proc(name);

    /// <summary>The plane textures of the frame (luma, chroma) at the coded size the `yuv` pass crops from; false with words when
    /// ANGLE refused (the source steps down).</summary>
    public bool TryImport(GpuSurface surface, out GpuTexture luma, out GpuTexture chroma, out int codedWidth, out int codedHeight, out string words)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ObjectDisposedException.ThrowIf(_disposed, this);
        luma = null!;
        chroma = null!;
        codedWidth = surface.CodedWidth;
        codedHeight = surface.CodedHeight;
        words = "";
        if (surface.Api != GpuApi.D3D11 || surface.Handle == 0)
        {
            words = "not a D3D11 frame";
            return false;
        }
        // The rung is the source's (GpuSurface.Direct): what it reports is what is done here.
        return surface.Direct ? Direct(surface, out luma, out chroma, out words)
            : Copy(surface, out luma, out chroma, out codedWidth, out codedHeight, out words);
    }

    private bool Copy(GpuSurface surface, out GpuTexture luma, out GpuTexture chroma, out int codedWidth, out int codedHeight, out string words)
    {
        luma = null!;
        chroma = null!;
        words = "";
        // The copy covers the visible size rounded up to even (4:2:0 chroma needs whole pairs).
        var w = (surface.Width + 1) & ~1;
        var h = (surface.Height + 1) & ~1;
        codedWidth = w;
        codedHeight = h;
        ref var p = ref _pool[_next];
        _next = (_next + 1) % PoolSize;
        if (p.Texture == 0 || p.Width != w || p.Height != h || p.Format != surface.Format)
        {
            FreePooled(ref p);
            var desc = new D3D11Texture2DDesc((uint)w, (uint)h, 1, 1, surface.Format == GpuSurfaceFormat.P010 ? D3D11Layout.FormatP010 : D3D11Layout.FormatNv12,
                1, 0, D3D11Layout.UsageDefault, D3D11Layout.BindShaderResource, 0, 0);
            nint texture = 0;
            var device = _share.D3D11Device;
            var hr = ((delegate* unmanaged[Stdcall]<nint, D3D11Texture2DDesc*, nint, nint*, int>)Slot(device, D3D11Layout.DeviceCreateTexture2D))(device, &desc, 0, &texture);
            if (hr < 0 || texture == 0)
            {
                words = $"CreateTexture2D refused the copy texture (0x{hr:X8})";
                return false;
            }
            p.Texture = texture;
            p.Width = w;
            p.Height = h;
            p.Format = surface.Format;
            if (!Images(texture, 0, surface.Format, w, h, out p.LumaImage, out p.ChromaImage, out p.Luma, out p.Chroma, out words))
            {
                FreePooled(ref p);
                return false;
            }
        }
        var context = ImmediateContext();
        var box = new D3D11Box(0, 0, 0, (uint)w, (uint)h, 1);
        // Subresource of slice s, mip 0, in a one-mip array: s (D3D11CalcSubresource).
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, D3D11Box*, void>)Slot(context, D3D11Layout.ContextCopySubresourceRegion))(
            context, p.Texture, 0, 0, 0, 0, surface.Handle, (uint)surface.Slice, &box);
        Release(context);
        luma = p.Luma!;
        chroma = p.Chroma!;
        return true;
    }

    private bool Direct(GpuSurface surface, out GpuTexture luma, out GpuTexture chroma, out string words)
    {
        luma = null!;
        chroma = null!;
        words = "";
        if (surface.PoolEpoch != _directEpoch)
        {
            // A new decoder pool: the cached images name arrays that are gone.
            ClearDirect();
            _directEpoch = surface.PoolEpoch;
        }
        var cached = _direct.TryGetValue((surface.Handle, surface.Slice, 0), out var y);
        cached &= _direct.TryGetValue((surface.Handle, surface.Slice, 1), out var c);
        if (!cached)
        {
            if (!Images(surface.Handle, surface.Slice, surface.Format, surface.CodedWidth, surface.CodedHeight, out var yi, out var ci, out var yt, out var ct, out words))
                return false;
            y = (yi, yt!);
            c = (ci, ct!);
            _direct[(surface.Handle, surface.Slice, 0)] = y;
            _direct[(surface.Handle, surface.Slice, 1)] = c;
        }
        luma = y.Texture;
        chroma = c.Texture;
        return true;
    }

    private bool Images(nint texture, int slice, GpuSurfaceFormat format, int w, int h, out nint lumaImage, out nint chromaImage,
        out GpuTexture? luma, out GpuTexture? chroma, out string words)
    {
        words = "";
        luma = null;
        chroma = null;
        lumaImage = Create(texture, 0, slice);
        chromaImage = lumaImage == 0 ? 0 : Create(texture, 1, slice);
        if (lumaImage == 0 || chromaImage == 0)
        {
            Destroy(lumaImage);
            lumaImage = 0;
            words = "ANGLE refused a plane image of the D3D11 texture";
            return false;
        }
        var ten = format == GpuSurfaceFormat.P010;
        luma = _device.AdoptExternal(w, h, ten ? TextureFormat.R16 : TextureFormat.R8);
        chroma = _device.AdoptExternal((w + 1) / 2, (h + 1) / 2, ten ? TextureFormat.Rg16 : TextureFormat.Rg8);
        if (!_device.BindImage(luma, lumaImage, GlProc) || !_device.BindImage(chroma, chromaImage, GlProc))
        {
            // Nothing half-made is kept: the images and textures go with the refusal.
            words = "the GL refused to sample a D3D11 plane image";
            Destroy(lumaImage);
            Destroy(chromaImage);
            lumaImage = 0;
            chromaImage = 0;
            luma.Dispose();
            chroma.Dispose();
            luma = null;
            chroma = null;
            return false;
        }
        return true;
    }

    private nint Create(nint texture, int plane, int slice)
    {
        var attribs = stackalloc int[] { EglD3D11TexturePlaneAngle, plane, EglD3D11TextureArraySliceAngle, slice, EglNone };
        return ((delegate* unmanaged<nint, nint, int, nint, int*, nint>)_create)(_display, 0, EglD3D11TextureAngle, texture, attribs);
    }

    private void Destroy(nint image)
    {
        if (image != 0)
            _ = ((delegate* unmanaged<nint, nint, uint>)_destroy)(_display, image);
    }

    private nint ImmediateContext()
    {
        nint context = 0;
        var device = _share.D3D11Device;
        ((delegate* unmanaged[Stdcall]<nint, nint*, void>)Slot(device, D3D11Layout.DeviceGetImmediateContext))(device, &context);
        return context;
    }

    private static nint Slot(nint unknown, int index) => (*(nint**)unknown)[index];

    private static void Release(nint unknown)
    {
        if (unknown != 0)
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(unknown, D3D11Layout.IUnknownRelease))(unknown);
    }

    private void FreePooled(ref Pooled p)
    {
        Destroy(p.LumaImage);
        Destroy(p.ChromaImage);
        p.Luma?.Dispose();
        p.Chroma?.Dispose();
        Release(p.Texture);
        p = default;
    }

    private void ClearDirect()
    {
        foreach (var (image, texture) in _direct.Values)
        {
            Destroy(image);
            texture.Dispose();
        }
        _direct.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        for (var i = 0; i < PoolSize; i++)
            FreePooled(ref _pool[i]);
        ClearDirect();
    }
}
