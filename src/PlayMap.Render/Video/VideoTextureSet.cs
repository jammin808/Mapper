using System.Diagnostics;
using PlayMap.Core.Gpu;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Hap;
using PlayMap.Render.Engine;
using PlayMap.Render.Hap;

namespace PlayMap.Render.Video;

/// <summary>One video source's pictures on the GPU: its planes as textures (NV12 and P010 as a luma and an
/// interleaved chroma texture, planar YUV as three, RGBA/BGRA/half as one) and, for YUV, the picture
/// converted to RGB in a 16-bit float target with the frame's own matrix, range and transfer. A lease whose
/// generation is already on the GPU is not uploaded again. Planes stream through the device's upload ring (any
/// row stride; the decoder's buffer is free as soon as the copy returns), or go through the frame's list. Every texture and target is
/// the device's, the planes' uploads and the conversions are the frame's list's, block planes stream through the upload ring, and
/// surfaces are imported by the device's importer.
///
/// HAP pictures arrive block-compressed: each plane fills a compressed texture at the coded size (tight rows of 4×4 blocks),
/// and the `hap` pass turns them into the RGBA16F picture at the visible size (YCoCg → RGB, the alpha plane, HAP HDR's reading).
/// A block format this device cannot sample is refused with the device's words (<see cref="Refusal"/>, and a
/// NotSupportedException), never shown as a black frame.
///
/// A lease with a GPU surface (a hardware decoder's own picture) is not uploaded: its planes are imported by the device's importer
/// (<see cref="IGpuDevice.Importer"/>: dmabuf EGLImages on Linux, ANGLE plane images of the D3D11 texture on Windows) and converted
/// by the same `yuv` pass, cropped to the visible size when the planes carry the decoder's padding. The importer holds the surface
/// past the lease until the GPU has read it. An import that fails keeps the last picture on the GPU (never a black frame), the
/// importer tells the share the source's import failed (the source then steps down the ladder and sends its next frame through
/// memory), and the set says why (<see cref="Refusal"/>).
///
/// A HAP lease whose planes were written into the device's upload arena is uploaded from the arena at the block's offset, with no
/// copy of its own; a block of an arena that is gone is never read.
///
/// Owned by the render thread; the caller disposes the lease after <see cref="Update(VideoFrameLease, ICommandList)"/>.</summary>
public sealed class VideoTextureSet : IDisposable
{
    private readonly IGpuDevice _device;
    private readonly bool _streaming;
    private readonly ProgramId _yuv;
    private readonly ProgramId _hap;
    private PackedUnpack? _packed;
    private readonly GpuTexture?[] _planes = new GpuTexture?[3];
    private RenderTarget? _converted;
    private bool _imported;
    private PixelLayout _layout;
    private int _width;
    private int _height;
    private bool _disposed;

    public VideoTextureSet(IGpuDevice device, bool streaming = true)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        Features = device.Features;
        _streaming = streaming;
        _yuv = device.Programs.Id("yuv");
        _hap = device.Programs.Id("hap");
    }

    /// <summary>The generation now on the GPU (−1 before the first upload).</summary>
    public long Generation { get; private set; } = -1;

    /// <summary>Uploads done (a skipped lease does not count).</summary>
    public int Uploads { get; private set; }

    /// <summary>The picture to sample (RGB, top row first), or null before the first upload.</summary>
    public GpuTexture? Picture { get; private set; }

    /// <summary>Whether the picture carries alpha that means something.</summary>
    public bool HasAlpha { get; private set; }

    /// <summary>What the device samples (its own; tests stand in a device that lacks a family).</summary>
    internal GpuFeatures Features { get; set; }

    /// <summary>The picture after its conversion pass (YUV and HAP layouts), or null.</summary>
    internal RenderTarget? Converted => _converted;

    /// <summary>Why the last picture could not be put on the GPU (a block format this context lacks, an import that failed), or null.</summary>
    public string? Refusal { get; private set; }

    /// <summary>Frames that came as GPU surfaces and were imported (no upload).</summary>
    public int Imports { get; private set; }

    /// <summary>Imports that failed (each reported to the share against the frame's source).</summary>
    public int ImportFailures { get; private set; }

    /// <summary>Block planes uploaded straight from the upload arena's buffer.</summary>
    public int StagedUploads { get; private set; }

    /// <summary>Puts the lease's picture on the GPU in a frame of its own (outside the renderer's frame), unless its generation is
    /// already there. True when it uploaded.</summary>
    public bool Update(VideoFrameLease lease)
    {
        var list = _device.BeginFrame(0);
        try
        {
            return Update(lease, list);
        }
        finally
        {
            _device.Submit(list);
        }
    }

    /// <summary>Puts the lease's picture on the GPU through the frame's list unless its generation is already there. True when it
    /// uploaded.</summary>
    public bool Update(VideoFrameLease lease, ICommandList list)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(list);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Picture is not null && lease.Generation == Generation)
            return false;
        if (lease.Surface is { } surface)
            return UpdateSurface(list, lease, surface);
        if (HapCodecs.IsBlockLayout(lease.Layout))
            return UpdateBlocks(list, lease);
        if (LivePlanes.IsPacked(lease.Layout))
            return UpdatePacked(list, lease);
        var planes = lease.Planes;
        if (lease.Width < 1 || lease.Height < 1 || planes.Count < PlaneCount(lease.Layout))
            return false;
        EnsureTextures(lease.Layout, lease.Width, lease.Height);
        for (var i = 0; i < PlaneCount(lease.Layout); i++)
        {
            var tex = _planes[i]!;
            var (format, bpp) = UploadOf(lease.Layout, i);
            UploadPlane(list, tex, planes[i], format, bpp);
        }
        if (IsYuv(lease.Layout))
            Convert(list, lease);
        HasAlpha = lease.HasAlpha;
        Refusal = null;
        Generation = lease.Generation;
        Uploads++;
        return true;
    }

    private bool UpdateBlocks(ICommandList list, VideoFrameLease lease)
    {
        var (first, second) = HapPass.Formats(lease.Layout);
        var why = Features.RefusalOf(first) ?? (second is { } s ? Features.RefusalOf(s) : null);
        if (why is not null)
        {
            Refusal = why;
            throw new NotSupportedException(why);
        }
        var planes = lease.Planes;
        var count = second is null ? 1 : 2;
        if (lease.Width < 1 || lease.Height < 1 || planes.Count < count)
            return false;
        var staged = lease.Staged;
        var uploads = _device.Uploads;
        if (!staged.IsEmpty && !uploads.InArena(staged))
        {
            // A block of an arena this device no longer has: its memory may be gone, so it is never read. The last picture holds.
            Refusal = "the frame was written into an upload arena this renderer no longer has";
            return false;
        }
        EnsureBlocks(lease.Layout, lease.Width, lease.Height, first, second);
        for (var i = 0; i < count; i++)
        {
            var plane = planes[i];
            var tex = _planes[i]!;
            var bytes = (long)plane.Stride * plane.Height;
            if (plane.Data == 0 || bytes != TextureFormats.CompressedSize(tex.Format, tex.Width, tex.Height))
                throw new ArgumentException("A block plane is missing or not the size of its picture.", nameof(lease));
            if (!staged.IsEmpty)
            {
                // Straight from the arena: the GPU reads the producer's bytes where they were written.
                if (!uploads.TryUploadStaged(tex, staged, plane.Data, (int)bytes, out _))
                    throw new ArgumentException("A staged block plane lies outside its arena block.", nameof(lease));
                StagedUploads++;
            }
            else if (!_streaming || !uploads.TryUploadCompressed(tex, plane.Data, (int)bytes, out _))
            {
                var t0 = Stopwatch.GetTimestamp();
                list.UploadCompressed(tex, plane.Data, (int)bytes);
                HapUploadMeter.Add(bytes, Stopwatch.GetTimestamp() - t0);
            }
        }
        var scale = lease is IHapPicture hap ? hap.HdrScale : 1;
        HapPass.Draw(list, _hap, _converted!, _planes[0]!, _planes[1], HapPass.Mode(lease.Layout), scale, HapPass.Transfer(lease.Transfer));
        HasAlpha = lease.HasAlpha;
        Refusal = null;
        Generation = lease.Generation;
        Uploads++;
        return true;
    }

    /// <summary>A packed network frame (UYVY, UYVA, P216, PA16): its own unpacker's picture (wp2-videoio).</summary>
    private bool UpdatePacked(ICommandList list, VideoFrameLease lease)
    {
        if (Picture is not null && !LivePlanes.IsPacked(_layout))
            DisposeTextures();
        _packed ??= new PackedUnpack(_device, _streaming);
        _packed.Update(lease, list);
        _layout = lease.Layout;
        _width = lease.Width;
        _height = lease.Height;
        Picture = _packed.Picture;
        // UYVA and PA16 carry an alpha plane whatever the lease says.
        HasAlpha = lease.HasAlpha || LivePlanes.HasAlpha(lease.Layout);
        Refusal = null;
        Generation = lease.Generation;
        Uploads++;
        return true;
    }

    private void EnsureBlocks(PixelLayout layout, int width, int height, TextureFormat first, TextureFormat? second)
    {
        if (Picture is not null && !_imported && layout == _layout && width == _width && height == _height)
            return;
        DisposeTextures();
        _layout = layout;
        _width = width;
        _height = height;
        _planes[0] = _device.CreateTexture(new TextureDesc(width, height, first, TextureUsage.Sampled | TextureUsage.CopyDest));
        if (second is { } s)
            _planes[1] = _device.CreateTexture(new TextureDesc(width, height, s, TextureUsage.Sampled | TextureUsage.CopyDest));
        _converted = _device.CreateTarget(new TargetDesc(width, height, TextureFormat.Rgba16F, Filter: TextureFilter.Linear));
        Picture = _converted.Color;
    }

    private unsafe void UploadPlane(ICommandList list, GpuTexture texture, FramePlane plane, UploadFormat format, int bytesPerPixel)
    {
        var rowBytes = texture.Width * bytesPerPixel;
        var rows = Math.Min(texture.Height, plane.Height > 0 ? plane.Height : texture.Height);
        if (plane.Data == 0 || plane.Stride < rowBytes)
            throw new ArgumentException("A plane is missing or narrower than its picture.", nameof(plane));
        if (rows < texture.Height)
            throw new ArgumentException("A plane is shorter than its picture.", nameof(plane));
        if (_streaming && _device.Uploads.TryUpload(texture, plane.Data, plane.Stride, rowBytes, rows, format, out _))
            return;
        if (plane.Stride % bytesPerPixel == 0)
        {
            // The rows as they lie, padded: the last one ends at its picture's width.
            var bytes = (long)plane.Stride * (rows - 1) + rowBytes;
            list.Upload(texture, new ReadOnlySpan<byte>((void*)plane.Data, checked((int)bytes)), format, plane.Stride / bytesPerPixel);
            return;
        }
        // A stride that is not whole pixels and no streaming buffer: repack the rows.
        var tight = new byte[rowBytes * rows];
        fixed (byte* dst = tight)
            for (var y = 0; y < rows; y++)
                Buffer.MemoryCopy((void*)(plane.Data + (nint)y * plane.Stride), dst + (long)y * rowBytes, rowBytes, rowBytes);
        list.Upload(texture, tight, format);
    }

    /// <summary>A hardware decoder's surface: its planes imported by the device and converted, held until the GPU read them. False
    /// (the last picture holds) when the import failed or every import slot is still in flight.</summary>
    private bool UpdateSurface(ICommandList list, VideoFrameLease lease, GpuSurface surface)
    {
        if (!_device.Importer.TryImport(surface, out var planes, out var words))
        {
            if (words is not null)
            {
                Refusal = words;
                ImportFailures++;
            }
            return false;
        }
        var layout = surface.Format == GpuSurfaceFormat.P010 ? PixelLayout.P010 : PixelLayout.Nv12;
        var chroma = planes.Chroma ?? planes.Luma;
        var codedWidth = planes.CodedWidth > 0 ? planes.CodedWidth : surface.Width;
        var codedHeight = planes.CodedHeight > 0 ? planes.CodedHeight : surface.Height;
        ConvertCoded(list, layout, planes.Luma, chroma, surface.Width, surface.Height, codedWidth, codedHeight, lease.Matrix, lease.Range,
            lease.Transfer);
        HasAlpha = false;
        Refusal = null;
        Generation = lease.Generation;
        Uploads++;
        Imports++;
        return true;
    }

    /// <summary>Converts planes whose textures cover a coded size larger than the visible picture (a decoder surface's padding rows
    /// and columns) into the RGB16F picture at the visible size: the pass samples only the visible part and chroma never filters
    /// into the padding. Plane textures at the visible size crop nothing.</summary>
    internal void ConvertCoded(ICommandList list, PixelLayout layout, GpuTexture luma, GpuTexture chroma, int width, int height, int codedWidth,
        int codedHeight, ColorMatrix matrix, ColorRange range, TransferFunction transfer)
    {
        if (Picture is null || !_imported || _width != width || _height != height || _layout != layout)
        {
            DisposeTextures();
            _layout = layout;
            _width = width;
            _height = height;
            _imported = true;
            _converted = _device.CreateTarget(new TargetDesc(width, height, TextureFormat.Rgba16F));
            Picture = _converted.Color;
        }
        var k = YuvConstants.For(layout, matrix, range, transfer);
        Draw(list, k, luma, chroma, chroma, Crop(layout, width, height, codedWidth, codedHeight, chroma));
    }

    /// <summary>The crop of the visible size out of the coded one, and the chroma clamp: the last visible chroma texel's centre over
    /// the chroma texture's size. The subsampling is the layout's (NV12, P010 and the 4:2:0 planar layouts halve both ways; 4:2:2
    /// halves the width; 4:4:4 neither), never guessed from the textures' sizes.</summary>
    internal static (float X, float Y, float MaxU, float MaxV) Crop(PixelLayout layout, int width, int height, int codedWidth, int codedHeight,
        GpuTexture chroma)
    {
        var (halfWidth, halfHeight) = layout switch
        {
            PixelLayout.Yuv444P => (false, false),
            PixelLayout.Yuv422P => (true, false),
            _ => (true, true),
        };
        var visibleCw = halfWidth ? (width + 1) / 2 : width;
        var visibleCh = halfHeight ? (height + 1) / 2 : height;
        return (width / (float)codedWidth, height / (float)codedHeight,
            (visibleCw - 0.5f) / chroma.Width, (visibleCh - 0.5f) / chroma.Height);
    }

    private void Convert(ICommandList list, VideoFrameLease lease)
    {
        var k = YuvConstants.For(lease.Layout, lease.Matrix, lease.Range, lease.Transfer);
        var chroma = _planes[1]!;
        Draw(list, k, _planes[0]!, chroma, _planes[k.Planes == 3 ? 2 : 1]!,
            Crop(lease.Layout, lease.Width, lease.Height, lease.Width, lease.Height, chroma));
    }

    /// <summary>The conversion's state: the picture written whole, opaque.</summary>
    private static readonly DrawState Converting = DrawState.Opaque;

    /// <summary>A frame's conversion to RGB and a HAP frame's decode, both into the set's half-float picture.</summary>
    internal static void Pipelines(PipelineList keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        keys.Add("yuv", Converting, TargetShape.Rgba16F);
        HapPass.Pipelines(keys, TargetShape.Rgba16F);
    }

    private void Draw(ICommandList list, in YuvConstants k, GpuTexture y, GpuTexture u, GpuTexture v, (float X, float Y, float MaxU, float MaxV) crop)
    {
        using (list.Pass(_converted!, PassDesc.Keep))
            list.Draw(_yuv, Converting)
                .SetInt("uPlanes", k.Planes)
                .Set("uCodeScale", k.CodeScale)
                .Set("uRange", k.YOffset, k.YScale, k.COffset, k.CScale)
                .Set("uMatrix", k.Kr, k.Kb)
                .SetInt("uTransfer", k.Transfer)
                .SetInt("uWide", k.Wide ? 1 : 0)
                .Set("uCrop", crop.X, crop.Y)
                .Set("uMaxUv", crop.MaxU, crop.MaxV)
                .Texture("uY", y)
                .Texture("uU", u)
                .Texture("uV", v)
                .Fullscreen();
        _converted!.Color.Version++;
    }

    private void EnsureTextures(PixelLayout layout, int width, int height)
    {
        if (Picture is not null && !_imported && layout == _layout && width == _width && height == _height)
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
                _planes[0] = Plane(width, height, TextureFormat.Rgba8);
                Picture = _planes[0];
                return;
            case PixelLayout.RgbaHalf:
                _planes[0] = Plane(width, height, TextureFormat.Rgba16F);
                Picture = _planes[0];
                return;
            case PixelLayout.Gray8:
                _planes[0] = _device.CreateTexture(new TextureDesc(width, height, TextureFormat.R8, FrameLists.Uploaded, Swizzle: TextureSwizzle.Gray));
                Picture = _planes[0];
                return;
            case PixelLayout.Nv12:
                _planes[0] = Plane(width, height, TextureFormat.R8);
                _planes[1] = Plane(cw, ch, TextureFormat.Rg8);
                break;
            case PixelLayout.P010:
                _planes[0] = Plane(width, height, TextureFormat.R16);
                _planes[1] = Plane(cw, ch, TextureFormat.Rg16);
                break;
            case PixelLayout.Yuv420P:
                _planes[0] = Plane(width, height, TextureFormat.R8);
                _planes[1] = Plane(cw, ch, TextureFormat.R8);
                _planes[2] = Plane(cw, ch, TextureFormat.R8);
                break;
            case PixelLayout.Yuv422P:
                _planes[0] = Plane(width, height, TextureFormat.R8);
                _planes[1] = Plane(cw, height, TextureFormat.R8);
                _planes[2] = Plane(cw, height, TextureFormat.R8);
                break;
            case PixelLayout.Yuv444P:
                _planes[0] = Plane(width, height, TextureFormat.R8);
                _planes[1] = Plane(width, height, TextureFormat.R8);
                _planes[2] = Plane(width, height, TextureFormat.R8);
                break;
            case PixelLayout.Yuv420P10:
                _planes[0] = Plane(width, height, TextureFormat.R16);
                _planes[1] = Plane(cw, ch, TextureFormat.R16);
                _planes[2] = Plane(cw, ch, TextureFormat.R16);
                break;
            default:
                throw new NotSupportedException($"No upload for {layout}.");
        }
        _converted = _device.CreateTarget(new TargetDesc(width, height, TextureFormat.Rgba16F));
        Picture = _converted.Color;
    }

    /// <summary>A plane's texture, filled from the CPU and sampled linearly.</summary>
    private GpuTexture Plane(int width, int height, TextureFormat format) =>
        _device.CreateTexture(new TextureDesc(width, height, format, FrameLists.Uploaded));

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
        _packed?.Dispose();
        _packed = null;
        _converted?.Dispose();
        _converted = null;
        for (var i = 0; i < _planes.Length; i++)
        {
            _planes[i]?.Dispose();
            _planes[i] = null;
        }
        Picture = null;
        Generation = -1;
        _imported = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        DisposeTextures();
        _disposed = true;
    }
}
