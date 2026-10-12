using PlayMap.Core.Media;
using PlayMap.Render.Gpu;

namespace PlayMap.Render.Video;

/// <summary>One video source's pictures on the GPU: its planes as textures (NV12 and P010 as a luma and an
/// interleaved chroma texture, planar YUV as three, RGBA/BGRA/half as one) and, for YUV, the picture
/// converted to RGB in a 16-bit float target with the frame's own matrix, range and transfer. A lease whose
/// generation is already on the GPU is not uploaded again. Planes stream through pixel-unpack buffers (any
/// row stride; the decoder's buffer is free as soon as the copy returns).
///
/// Owned by the render thread; the caller disposes the lease after <see cref="Update"/>.</summary>
public sealed class VideoTextureSet : IDisposable
{
    private readonly GlDevice _device;
    private readonly PixelUnpackRing? _ring;
    private readonly GpuTexture?[] _planes = new GpuTexture?[3];
    private RenderTarget? _converted;
    private PixelLayout _layout;
    private int _width;
    private int _height;
    private bool _disposed;

    public VideoTextureSet(GlDevice device, bool streaming = true)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        if (streaming)
            _ring = new PixelUnpackRing(device);
    }

    /// <summary>The generation now on the GPU (−1 before the first upload).</summary>
    public long Generation { get; private set; } = -1;

    /// <summary>Uploads done (a skipped lease does not count).</summary>
    public int Uploads { get; private set; }

    /// <summary>The picture to sample (RGB, top row first), or null before the first upload.</summary>
    public GpuTexture? Picture { get; private set; }

    /// <summary>Whether the picture carries alpha that means something.</summary>
    public bool HasAlpha { get; private set; }

    /// <summary>Puts the lease's picture on the GPU unless its generation is already there. True when it uploaded.</summary>
    public bool Update(VideoFrameLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Picture is not null && lease.Generation == Generation)
            return false;
        var planes = lease.Planes;
        if (lease.Width < 1 || lease.Height < 1 || planes.Count < PlaneCount(lease.Layout))
            return false;
        EnsureTextures(lease.Layout, lease.Width, lease.Height);
        for (var i = 0; i < PlaneCount(lease.Layout); i++)
        {
            var tex = _planes[i]!;
            var (format, bpp) = UploadOf(lease.Layout, i);
            UploadPlane(tex, planes[i], format, bpp);
        }
        if (IsYuv(lease.Layout))
            Convert(lease);
        HasAlpha = lease.HasAlpha;
        Generation = lease.Generation;
        Uploads++;
        return true;
    }

    private void UploadPlane(GpuTexture texture, FramePlane plane, UploadFormat format, int bytesPerPixel)
    {
        var rowBytes = texture.Width * bytesPerPixel;
        var rows = Math.Min(texture.Height, plane.Height > 0 ? plane.Height : texture.Height);
        if (plane.Data == 0 || plane.Stride < rowBytes)
            throw new ArgumentException("A plane is missing or narrower than its picture.", nameof(plane));
        if (rows < texture.Height)
            throw new ArgumentException("A plane is shorter than its picture.", nameof(plane));
        if (_ring is not null && _ring.Upload(texture, plane.Data, plane.Stride, rowBytes, rows, format))
            return;
        if (plane.Stride % bytesPerPixel == 0)
        {
            _device.UploadPointer(texture, plane.Data, format, plane.Stride / bytesPerPixel);
            return;
        }
        // A stride that is not whole pixels and no streaming buffer: repack the rows.
        var tight = new byte[rowBytes * rows];
        unsafe
        {
            fixed (byte* dst = tight)
                for (var y = 0; y < rows; y++)
                    GlDevice.Copy(plane.Data + (nint)y * plane.Stride, dst + (long)y * rowBytes, rowBytes);
        }
        _device.Upload(texture, tight, format);
    }

    private void Convert(VideoFrameLease lease)
    {
        var k = YuvConstants.For(lease.Layout, lease.Matrix, lease.Range, lease.Transfer);
        var p = _device.Program("yuv");
        _device.BindTarget(_converted!);
        _device.SetBlend(BlendMode.Opaque);
        p.SetInt("uPlanes", k.Planes);
        p.Set("uCodeScale", k.CodeScale);
        p.Set("uRange", k.YOffset, k.YScale, k.COffset, k.CScale);
        p.Set("uMatrix", k.Kr, k.Kb);
        p.SetInt("uTransfer", k.Transfer);
        p.SetInt("uWide", k.Wide ? 1 : 0);
        _device.BindSampler(p, "uY", 0, _planes[0]!);
        _device.BindSampler(p, "uU", 1, _planes[1]!);
        _device.BindSampler(p, "uV", 2, _planes[k.Planes == 3 ? 2 : 1]!);
        _device.DrawFullscreen(p);
        _converted!.Color.Version++;
    }

    private void EnsureTextures(PixelLayout layout, int width, int height)
    {
        if (Picture is not null && layout == _layout && width == _width && height == _height)
            return;
        DisposeTextures();
        _layout = layout;
        _width = width;
        _height = height;
        var cw = (width + 1) / 2;
        var ch = (height + 1) / 2;
        switch (layout)
        {
            case PixelLayout.Rgba8:
            case PixelLayout.Bgra8:
                _planes[0] = _device.CreateTexture(width, height, TextureFormat.Rgba8);
                Picture = _planes[0];
                return;
            case PixelLayout.RgbaHalf:
                _planes[0] = _device.CreateTexture(width, height, TextureFormat.Rgba16F);
                Picture = _planes[0];
                return;
            case PixelLayout.Gray8:
                _planes[0] = _device.CreateTexture(width, height, TextureFormat.R8);
                _device.SetGraySwizzle(_planes[0]!);
                Picture = _planes[0];
                return;
            case PixelLayout.Nv12:
                _planes[0] = _device.CreateTexture(width, height, TextureFormat.R8);
                _planes[1] = _device.CreateTexture(cw, ch, TextureFormat.Rg8);
                break;
            case PixelLayout.P010:
                _planes[0] = _device.CreateTexture(width, height, TextureFormat.R16);
                _planes[1] = _device.CreateTexture(cw, ch, TextureFormat.Rg16);
                break;
            case PixelLayout.Yuv420P:
                _planes[0] = _device.CreateTexture(width, height, TextureFormat.R8);
                _planes[1] = _device.CreateTexture(cw, ch, TextureFormat.R8);
                _planes[2] = _device.CreateTexture(cw, ch, TextureFormat.R8);
                break;
            case PixelLayout.Yuv422P:
                _planes[0] = _device.CreateTexture(width, height, TextureFormat.R8);
                _planes[1] = _device.CreateTexture(cw, height, TextureFormat.R8);
                _planes[2] = _device.CreateTexture(cw, height, TextureFormat.R8);
                break;
            case PixelLayout.Yuv444P:
                _planes[0] = _device.CreateTexture(width, height, TextureFormat.R8);
                _planes[1] = _device.CreateTexture(width, height, TextureFormat.R8);
                _planes[2] = _device.CreateTexture(width, height, TextureFormat.R8);
                break;
            case PixelLayout.Yuv420P10:
                _planes[0] = _device.CreateTexture(width, height, TextureFormat.R16);
                _planes[1] = _device.CreateTexture(cw, ch, TextureFormat.R16);
                _planes[2] = _device.CreateTexture(cw, ch, TextureFormat.R16);
                break;
            default:
                throw new NotSupportedException($"No upload for {layout}.");
        }
        _converted = _device.CreateRenderTarget(width, height, TextureFormat.Rgba16F);
        Picture = _converted.Color;
    }

    /// <summary>How many planes a layout has.</summary>
    public static int PlaneCount(PixelLayout layout) => layout switch
    {
        PixelLayout.Nv12 or PixelLayout.P010 => 2,
        PixelLayout.Yuv420P or PixelLayout.Yuv422P or PixelLayout.Yuv444P or PixelLayout.Yuv420P10 => 3,
        _ => 1,
    };

    public static bool IsYuv(PixelLayout layout) => PlaneCount(layout) > 1;

    private static (UploadFormat Format, int BytesPerPixel) UploadOf(PixelLayout layout, int plane) => layout switch
    {
        PixelLayout.Rgba8 => (UploadFormat.Rgba8, 4),
        PixelLayout.Bgra8 => (UploadFormat.Bgra8, 4),
        PixelLayout.RgbaHalf => (UploadFormat.RgbaHalf, 8),
        PixelLayout.Gray8 => (UploadFormat.R8, 1),
        PixelLayout.Nv12 => plane == 0 ? (UploadFormat.R8, 1) : (UploadFormat.Rg8, 2),
        PixelLayout.P010 => plane == 0 ? (UploadFormat.R16, 2) : (UploadFormat.Rg16, 4),
        PixelLayout.Yuv420P10 => (UploadFormat.R16, 2),
        _ => (UploadFormat.R8, 1),
    };

    private void DisposeTextures()
    {
        _converted?.Dispose();
        _converted = null;
        for (var i = 0; i < _planes.Length; i++)
        {
            _planes[i]?.Dispose();
            _planes[i] = null;
        }
        Picture = null;
        Generation = -1;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        DisposeTextures();
        _ring?.Dispose();
        _disposed = true;
    }
}
