using PlayMap.Core.Gpu;
using PlayMap.Core.Media;
using PlayMap.Render.Engine;

namespace PlayMap.Render.Video;

/// <summary>A packed network frame (UYVY, UYVA, P216, PA16; <see cref="LivePlanes"/>) put on the GPU and turned into RGB by the
/// <c>video.unpack</c> pass: UYVY as one RGBA8 texture of w/2 × h, P216 as an R16 luma and an RG16 chroma texture, the alpha plane as
/// R8 (UYVA) or R16 (PA16). The picture is RGBA16F for the 8-bit layouts and RGBA16 (unsigned normalised, every 16-bit code kept)
/// for P216 and PA16 where the context renders to it, RGBA16F otherwise. A size the layout cannot have (an odd width) or a 16-bit
/// layout on a device without normalised 16-bit textures is refused with words. The planes stream through the device's upload ring
/// (or go through the frame's list) and the conversion is a pass of the frame's list. Render thread; owned by its
/// <see cref="VideoTextureSet"/>.</summary>
internal sealed class PackedUnpack : IDisposable
{
    /// <summary>The conversion's state: the picture written whole, opaque.</summary>
    private static readonly DrawState Unpacking = DrawState.Opaque;

    /// <summary>The unpack into half float, or into 16 bits for a deep layout where the device renders them.</summary>
    internal static void Pipelines(PipelineList keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        keys.Add("video.unpack", Unpacking, TargetShape.Rgba16F);
        if (keys.Device.Features.Formats.TryGetValue(TextureFormat.Rgba16, out var deep) && deep.Target)
            keys.Add("video.unpack", Unpacking, new TargetShape(TextureFormat.Rgba16));
    }

    private readonly IGpuDevice _device;
    private readonly bool _streaming;
    private readonly ProgramId _unpack;
    private readonly GpuTexture?[] _planes = new GpuTexture?[3];
    private RenderTarget? _target;
    private PixelLayout _layout;
    private int _width;
    private int _height;

    public PackedUnpack(IGpuDevice device, bool streaming)
    {
        _device = device;
        _streaming = streaming;
        _unpack = device.Programs.Id("video.unpack");
    }

    /// <summary>The picture (RGB with alpha, top row first), or null before the first frame.</summary>
    public GpuTexture? Picture => _target?.Color;

    /// <summary>Uploads the lease's planes and converts them through the frame's list; throws with words when the frame cannot be
    /// shown here.</summary>
    public void Update(VideoFrameLease lease, ICommandList list)
    {
        var layout = lease.Layout;
        if (LivePlanes.Refuse(layout, lease.Width, lease.Height) is { } why)
            throw new ArgumentException(why, nameof(lease));
        if (LivePlanes.IsDeep(layout) && !_device.Features.Formats[TextureFormat.R16].Sampled)
            throw new NotSupportedException($"this device samples no 16-bit normalised textures ({_device.Adapter.Api}): a {LivePlanes.Word(layout)} input needs them — ask the sender for 8-bit (UYVY)");
        var planes = lease.Planes;
        var count = LivePlanes.PlaneCount(layout);
        if (planes.Count < count)
            throw new ArgumentException($"a {LivePlanes.Word(layout)} frame has {count} planes, not {planes.Count}", nameof(lease));
        Ensure(layout, lease.Width, lease.Height);
        var w = lease.Width;
        var h = lease.Height;
        if (LivePlanes.IsDeep(layout))
        {
            Upload(list, _planes[0]!, planes[0], UploadFormat.R16, 2 * w, h);
            Upload(list, _planes[1]!, planes[1], UploadFormat.Rg16, 2 * w, h);
            if (count == 3)
                Upload(list, _planes[2]!, planes[2], UploadFormat.R16, 2 * w, h);
        }
        else
        {
            Upload(list, _planes[0]!, planes[0], UploadFormat.Rgba8, 2 * w, h);
            if (count == 2)
                Upload(list, _planes[1]!, planes[1], UploadFormat.R8, w, h);
        }
        Convert(list, layout, w, lease.Matrix);
    }

    private unsafe void Upload(ICommandList list, GpuTexture texture, FramePlane plane, UploadFormat format, int rowBytes, int rows)
    {
        if (plane.Data == 0 || plane.Stride < rowBytes)
            throw new ArgumentException("a plane is missing or narrower than its picture", nameof(plane));
        if (_streaming && _device.Uploads.TryUpload(texture, plane.Data, plane.Stride, rowBytes, rows, format, out _))
            return;
        var bpp = UploadFormats.BytesPerPixel(format);
        if (plane.Stride % bpp != 0)
            throw new ArgumentException("a plane's row is not whole samples and there is no streaming buffer to repack it", nameof(plane));
        // The rows as they lie, padded: the last one ends at its picture's width.
        var bytes = (long)plane.Stride * (texture.Height - 1) + (long)texture.Width * bpp;
        list.Upload(texture, new ReadOnlySpan<byte>((void*)plane.Data, checked((int)bytes)), format, plane.Stride / bpp);
    }

    private void Convert(ICommandList list, PixelLayout layout, int width, ColorMatrix matrix)
    {
        var deep = LivePlanes.IsDeep(layout);
        var (kr, kb) = matrix switch
        {
            ColorMatrix.Bt601 => (0.299f, 0.114f),
            ColorMatrix.Bt2020 => (0.2627f, 0.0593f),
            _ => (0.2126f, 0.0722f),
        };
        var alpha = LivePlanes.HasAlpha(layout) ? _planes[deep ? 2 : 1]! : _planes[0]!;
        using (list.Pass(_target!, PassDesc.Keep))
            list.Draw(_unpack, Unpacking)
                .SetInt("uMode", deep ? 1 : 0)
                .SetInt("uHasAlpha", LivePlanes.HasAlpha(layout) ? 1 : 0)
                .SetInt("uPairs", width / 2)
                .Set("uCode", deep ? 65535f / 256f : 255f)
                .Set("uMatrix", kr, kb)
                .Texture("uLuma", _planes[0]!)
                .Texture("uChroma", deep ? _planes[1]! : _planes[0]!)
                .Texture("uAlpha", alpha)
                .Fullscreen();
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
            _planes[0] = Plane(width, height, TextureFormat.R16);
            _planes[1] = Plane(width / 2, height, TextureFormat.Rg16);
            if (layout == PixelLayout.Pa16)
                _planes[2] = Plane(width, height, TextureFormat.R16);
        }
        else
        {
            _planes[0] = Plane(width / 2, height, TextureFormat.Rgba8);
            if (layout == PixelLayout.Uyva)
                _planes[1] = Plane(width, height, TextureFormat.R8);
        }
        var format = LivePlanes.IsDeep(layout) && _device.Features.Formats[TextureFormat.Rgba16].Target ? TextureFormat.Rgba16 : TextureFormat.Rgba16F;
        _target = _device.CreateTarget(new TargetDesc(width, height, format));
    }

    /// <summary>A plane's texture, filled from the CPU and read texel for texel.</summary>
    private GpuTexture Plane(int width, int height, TextureFormat format) =>
        _device.CreateTexture(new TextureDesc(width, height, format, FrameLists.Uploaded, TextureFilter.Nearest));

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
