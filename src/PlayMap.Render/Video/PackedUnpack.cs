using PlayMap.Core.Gpu;
using PlayMap.Core.Media;
using PlayMap.Render.Gl;

namespace PlayMap.Render.Video;

/// <summary>A packed network frame (UYVY, UYVA, P216, PA16; <see cref="LivePlanes"/>) put on the GPU and turned into RGB by the
/// <c>video.unpack</c> pass: UYVY as one RGBA8 texture of w/2 × h, P216 as an R16 luma and an RG16 chroma texture, the alpha plane as
/// R8 (UYVA) or R16 (PA16). The picture is RGBA16F for the 8-bit layouts and RGBA16 (unsigned normalised, every 16-bit code kept)
/// for P216 and PA16 where the context renders to it, RGBA16F otherwise. A size the layout cannot have (an odd width) or a 16-bit
/// layout on a context without normalised 16-bit textures is refused with words. Render thread; owned by its
/// <see cref="VideoTextureSet"/>.</summary>
internal sealed class PackedUnpack : IDisposable
{
    private readonly GlDevice _device;
    private readonly PixelUnpackRing? _ring;
    private readonly GpuTexture?[] _planes = new GpuTexture?[3];
    private RenderTarget? _target;
    private PixelLayout _layout;
    private int _width;
    private int _height;

    public PackedUnpack(GlDevice device, PixelUnpackRing? ring)
    {
        _device = device;
        _ring = ring;
    }

    /// <summary>The picture (RGB with alpha, top row first), or null before the first frame.</summary>
    public GpuTexture? Picture => _target?.Color;

    /// <summary>Uploads the lease's planes and converts them; throws with words when the frame cannot be shown here.</summary>
    public void Update(VideoFrameLease lease)
    {
        var layout = lease.Layout;
        if (LivePlanes.Refuse(layout, lease.Width, lease.Height) is { } why)
            throw new ArgumentException(why, nameof(lease));
        if (LivePlanes.IsDeep(layout) && !_device.Caps.Supports(TextureFormat.R16))
            throw new NotSupportedException($"this context samples no 16-bit normalised textures ({_device.Caps.Dialect}): a {LivePlanes.Word(layout)} input needs them — ask the sender for 8-bit (UYVY)");
        var planes = lease.Planes;
        var count = LivePlanes.PlaneCount(layout);
        if (planes.Count < count)
            throw new ArgumentException($"a {LivePlanes.Word(layout)} frame has {count} planes, not {planes.Count}", nameof(lease));
        Ensure(layout, lease.Width, lease.Height);
        var w = lease.Width;
        var h = lease.Height;
        if (LivePlanes.IsDeep(layout))
        {
            Upload(_planes[0]!, planes[0], UploadFormat.R16, 2 * w, h);
            Upload(_planes[1]!, planes[1], UploadFormat.Rg16, 2 * w, h);
            if (count == 3)
                Upload(_planes[2]!, planes[2], UploadFormat.R16, 2 * w, h);
        }
        else
        {
            Upload(_planes[0]!, planes[0], UploadFormat.Rgba8, 2 * w, h);
            if (count == 2)
                Upload(_planes[1]!, planes[1], UploadFormat.R8, w, h);
        }
        Convert(layout, w, lease.Matrix);
    }

    private void Upload(GpuTexture texture, FramePlane plane, UploadFormat format, int rowBytes, int rows)
    {
        if (plane.Data == 0 || plane.Stride < rowBytes)
            throw new ArgumentException("a plane is missing or narrower than its picture", nameof(plane));
        if (_ring is not null && _ring.Upload(texture, plane.Data, plane.Stride, rowBytes, rows, format))
            return;
        var bpp = GlDevice.BytesPerPixel(format);
        if (plane.Stride % bpp != 0)
            throw new ArgumentException("a plane's row is not whole samples and there is no streaming buffer to repack it", nameof(plane));
        _device.UploadPointer(texture, plane.Data, format, plane.Stride / bpp);
    }

    private void Convert(PixelLayout layout, int width, ColorMatrix matrix)
    {
        var p = _device.Program("video.unpack");
        _device.BindTarget(_target!);
        _device.SetBlend(BlendMode.Opaque);
        var deep = LivePlanes.IsDeep(layout);
        var (kr, kb) = matrix switch
        {
            ColorMatrix.Bt601 => (0.299f, 0.114f),
            ColorMatrix.Bt2020 => (0.2627f, 0.0593f),
            _ => (0.2126f, 0.0722f),
        };
        p.SetInt("uMode", deep ? 1 : 0);
        p.SetInt("uHasAlpha", LivePlanes.HasAlpha(layout) ? 1 : 0);
        p.SetInt("uPairs", width / 2);
        p.Set("uCode", deep ? 65535f / 256f : 255f);
        p.Set("uMatrix", kr, kb);
        _device.BindSampler(p, "uLuma", 0, _planes[0]!);
        _device.BindSampler(p, "uChroma", 1, deep ? _planes[1]! : _planes[0]!);
        var alpha = LivePlanes.HasAlpha(layout) ? _planes[deep ? 2 : 1]! : _planes[0]!;
        _device.BindSampler(p, "uAlpha", 2, alpha);
        _device.DrawFullscreen(p);
        _target!.Color.Version++;
    }

    private void Ensure(PixelLayout layout, int width, int height)
    {
        if (_target is not null && layout == _layout && width == _width && height == _height)
            return;
        DisposeTextures();
        _layout = layout;
        _width = width;
        _height = height;
        if (LivePlanes.IsDeep(layout))
        {
            _planes[0] = _device.CreateTexture(width, height, TextureFormat.R16, TextureFilter.Nearest);
            _planes[1] = _device.CreateTexture(width / 2, height, TextureFormat.Rg16, TextureFilter.Nearest);
            if (layout == PixelLayout.Pa16)
                _planes[2] = _device.CreateTexture(width, height, TextureFormat.R16, TextureFilter.Nearest);
        }
        else
        {
            _planes[0] = _device.CreateTexture(width / 2, height, TextureFormat.Rgba8, TextureFilter.Nearest);
            if (layout == PixelLayout.Uyva)
                _planes[1] = _device.CreateTexture(width, height, TextureFormat.R8, TextureFilter.Nearest);
        }
        var format = LivePlanes.IsDeep(layout) && _device.Caps.SupportsRenderTarget(TextureFormat.Rgba16) ? TextureFormat.Rgba16 : TextureFormat.Rgba16F;
        _target = _device.CreateRenderTarget(width, height, format);
    }

    private void DisposeTextures()
    {
        _target?.Dispose();
        _target = null;
        for (var i = 0; i < _planes.Length; i++)
        {
            _planes[i]?.Dispose();
            _planes[i] = null;
        }
    }

    public void Dispose() => DisposeTextures();
}
