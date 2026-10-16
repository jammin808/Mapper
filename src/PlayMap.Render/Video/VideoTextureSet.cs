using System.Diagnostics;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Hap;
using PlayMap.Render.Decode;
using PlayMap.Render.Gpu;
using PlayMap.Render.Hap;

namespace PlayMap.Render.Video;

/// <summary>One video source's pictures on the GPU: its planes as textures (NV12 and P010 as a luma and an
/// interleaved chroma texture, planar YUV as three, RGBA/BGRA/half as one) and, for YUV, the picture
/// converted to RGB in a 16-bit float target with the frame's own matrix, range and transfer. A lease whose
/// generation is already on the GPU is not uploaded again. Planes stream through pixel-unpack buffers (any
/// row stride; the decoder's buffer is free as soon as the copy returns).
///
/// HAP pictures arrive block-compressed: each plane fills a compressed texture at the coded size (tight rows of 4×4 blocks),
/// and the `hap` pass turns them into the RGBA16F picture at the visible size (YCoCg → RGB, the alpha plane, HAP HDR's reading).
/// A block format this context cannot sample is refused with words (<see cref="Refusal"/>, and a NotSupportedException), never
/// shown as a black frame.
///
/// A lease with a GPU surface (a hardware decoder's own picture) is not uploaded: its planes are imported (dmabuf EGLImages on
/// Linux, ANGLE plane images of the D3D11 texture on Windows) and converted by the same `yuv` pass, cropped to the visible size
/// when the planes carry the decoder's padding. The importer holds the surface past the lease until the GPU has read it. An import
/// that fails keeps the last picture on the GPU (never a black frame), tells the share the source's import failed (the source
/// then steps down the ladder and sends its next frame through memory) and says why (<see cref="Refusal"/>).
///
/// A HAP lease whose planes were written into the context's upload arena is uploaded from the arena's buffer at the block's
/// offset, with no copy of its own; a block of an arena that is gone is never read.
///
/// Owned by the render thread; the caller disposes the lease after <see cref="Update"/>.</summary>
public sealed class VideoTextureSet : IDisposable
{
    private readonly GlDevice _device;
    private readonly PixelUnpackRing? _ring;
    private CompressedUploadRing? _blocks;
    private PackedUnpack? _packed;
    private readonly GpuTexture?[] _planes = new GpuTexture?[3];
    private RenderTarget? _converted;
    private DmaBufImporter? _dmabuf;
    private object? _angle;
    private bool _imported;
    private PixelLayout _layout;
    private int _width;
    private int _height;
    private bool _disposed;

    public VideoTextureSet(GlDevice device, bool streaming = true)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        Caps = device.Caps;
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

    /// <summary>What the context samples (the device's; tests stand in a context that lacks a family).</summary>
    internal GlCaps Caps { get; set; }

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

    /// <summary>Surfaces the importer still holds (their reads not yet completed on the GPU).</summary>
    public int HeldSurfaces => _dmabuf?.Held ?? 0;

    /// <summary>Puts the lease's picture on the GPU unless its generation is already there. True when it uploaded.</summary>
    public bool Update(VideoFrameLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _dmabuf?.Collect();
        if (Picture is not null && lease.Generation == Generation)
            return false;
        if (lease.Surface is { } surface)
            return UpdateSurface(lease, surface);
        if (HapCodecs.IsBlockLayout(lease.Layout))
            return UpdateBlocks(lease);
        if (LivePlanes.IsPacked(lease.Layout))
            return UpdatePacked(lease);
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
        Refusal = null;
        Generation = lease.Generation;
        Uploads++;
        return true;
    }

    private bool UpdateBlocks(VideoFrameLease lease)
    {
        var (first, second) = HapPass.Formats(lease.Layout);
        var why = GlDevice.RefuseBlockFormat(Caps, first) ?? (second is { } s ? GlDevice.RefuseBlockFormat(Caps, s) : null);
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
        var arena = staged.IsEmpty ? null : _device.DecodeContext?.Arena;
        if (!staged.IsEmpty && (arena is null || !arena.Owns(staged)))
        {
            // A block of an arena this context no longer has: its memory may be gone, so it is never read. The last picture holds.
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
            if (arena is not null)
            {
                // Straight from the persistently mapped buffer: the driver reads the producer's bytes where they were written.
                var t0 = Stopwatch.GetTimestamp();
                var offset = staged.Offset + (plane.Data - staged.Pointer);
                _device.UploadCompressedFromUnpackBuffer(tex, arena.Buffer, (nint)offset, (int)bytes);
                HapUploadMeter.Add(bytes, Stopwatch.GetTimestamp() - t0);
                StagedUploads++;
            }
            else if (_blocks is not null)
            {
                _blocks.Upload(tex, plane.Data, (int)bytes);
            }
            else
            {
                var t0 = Stopwatch.GetTimestamp();
                _device.UploadCompressed(tex, plane.Data, (int)bytes);
                HapUploadMeter.Add(bytes, Stopwatch.GetTimestamp() - t0);
            }
        }
        var scale = lease is IHapPicture hap ? hap.HdrScale : 1;
        HapPass.Draw(_device, _converted!, _planes[0]!, _planes[1], HapPass.Mode(lease.Layout), scale, HapPass.Transfer(lease.Transfer));
        HasAlpha = lease.HasAlpha;
        Refusal = null;
        Generation = lease.Generation;
        Uploads++;
        return true;
    }

    /// <summary>A packed network frame (UYVY, UYVA, P216, PA16): its own unpacker's picture (wp2-videoio).</summary>
    private bool UpdatePacked(VideoFrameLease lease)
    {
        if (Picture is not null && !LivePlanes.IsPacked(_layout))
            DisposeTextures();
        _packed ??= new PackedUnpack(_device, _ring);
        _packed.Update(lease);
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
        _planes[0] = _device.CreateCompressedTexture(width, height, first);
        if (second is { } s)
            _planes[1] = _device.CreateCompressedTexture(width, height, s);
        _converted = _device.CreateRenderTarget(width, height, TextureFormat.Rgba16F, filter: TextureFilter.Linear);
        Picture = _converted.Color;
        if (_ring is not null)
            _blocks ??= new CompressedUploadRing(_device);
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

    /// <summary>A hardware decoder's surface: its planes imported and converted, held until the GPU read them. False (the last
    /// picture holds) when the import failed or every import slot is still in flight.</summary>
    private bool UpdateSurface(VideoFrameLease lease, GpuSurface surface)
    {
        GpuTexture luma, chroma;
        bool ok;
        var busy = false;
        string words;
        int codedWidth = surface.Width, codedHeight = surface.Height;
        var share = _device.DecodeContext?.Share;
        if (surface.Api == GpuApi.DmaBuf)
        {
            words = "";
            if (_dmabuf is null)
                _dmabuf = DmaBufImporter.ForCurrentContext(_device, out words);
            ok = _dmabuf is not null ? _dmabuf.TryImport(surface, out luma, out chroma, out busy, out words) : FailImport(out luma, out chroma);
        }
        else if (surface.Api == GpuApi.D3D11 && OperatingSystem.IsWindows() && share is not null)
        {
            words = "";
            if (_angle is null)
                _angle = AngleImporter.ForCurrentContext(_device, share, out words);
            ok = _angle is AngleImporter angle ? angle.TryImport(surface, out luma, out chroma, out codedWidth, out codedHeight, out words)
                : FailImport(out luma, out chroma);
        }
        else
        {
            ok = FailImport(out luma, out chroma);
            words = $"this renderer imports no {surface.Api} frames";
        }
        if (!ok)
        {
            if (!busy)
            {
                Refusal = words;
                ImportFailures++;
                share?.ReportImportFailure(surface.SourceKey, words);
            }
            return false;
        }
        var layout = surface.Format == GpuSurfaceFormat.P010 ? PixelLayout.P010 : PixelLayout.Nv12;
        ConvertCoded(layout, luma, chroma, surface.Width, surface.Height, codedWidth, codedHeight, lease.Matrix, lease.Range, lease.Transfer);
        // One device and one immediate context on Windows: released after record. A dmabuf is held until its fence passes.
        if (surface.Api == GpuApi.DmaBuf)
            _dmabuf!.Retain(surface);
        HasAlpha = false;
        Refusal = null;
        Generation = lease.Generation;
        Uploads++;
        Imports++;
        return true;
    }

    private static bool FailImport(out GpuTexture luma, out GpuTexture chroma)
    {
        luma = null!;
        chroma = null!;
        return false;
    }

    /// <summary>Converts planes whose textures cover a coded size larger than the visible picture (a decoder surface's padding rows
    /// and columns) into the RGB16F picture at the visible size: the pass samples only the visible part and chroma never filters
    /// into the padding. Plane textures at the visible size crop nothing.</summary>
    internal void ConvertCoded(PixelLayout layout, GpuTexture luma, GpuTexture chroma, int width, int height, int codedWidth, int codedHeight,
        ColorMatrix matrix, ColorRange range, TransferFunction transfer)
    {
        if (Picture is null || !_imported || _width != width || _height != height || _layout != layout)
        {
            DisposeTextures();
            _layout = layout;
            _width = width;
            _height = height;
            _imported = true;
            _converted = _device.CreateRenderTarget(width, height, TextureFormat.Rgba16F);
            Picture = _converted.Color;
        }
        var k = YuvConstants.For(layout, matrix, range, transfer);
        Draw(k, luma, chroma, chroma, Crop(width, height, codedWidth, codedHeight, chroma));
    }

    /// <summary>The crop of the visible size out of the coded one, and the chroma clamp: the last visible chroma texel's centre over
    /// the chroma texture's size.</summary>
    internal static (float X, float Y, float MaxU, float MaxV) Crop(int width, int height, int codedWidth, int codedHeight, GpuTexture chroma)
    {
        var visibleCw = (width + 1) / 2;
        var visibleCh = (height + 1) / 2;
        if (chroma.Width >= width)
        {
            // 4:4:4 (chroma at full size): the clamp is in luma texels.
            visibleCw = width;
            visibleCh = chroma.Height >= height ? height : visibleCh;
        }
        else if (chroma.Height >= height)
        {
            visibleCh = height;   // 4:2:2: full height
        }
        return (width / (float)codedWidth, height / (float)codedHeight,
            (visibleCw - 0.5f) / chroma.Width, (visibleCh - 0.5f) / chroma.Height);
    }

    private void Convert(VideoFrameLease lease)
    {
        var k = YuvConstants.For(lease.Layout, lease.Matrix, lease.Range, lease.Transfer);
        var chroma = _planes[1]!;
        Draw(k, _planes[0]!, chroma, _planes[k.Planes == 3 ? 2 : 1]!, Crop(lease.Width, lease.Height, lease.Width, lease.Height, chroma));
    }

    private void Draw(in YuvConstants k, GpuTexture y, GpuTexture u, GpuTexture v, (float X, float Y, float MaxU, float MaxV) crop)
    {
        var p = _device.Program("yuv");
        _device.BindTarget(_converted!);
        _device.SetBlend(BlendMode.Opaque);
        p.SetInt("uPlanes", k.Planes);
        p.Set("uCodeScale", k.CodeScale);
        p.Set("uRange", k.YOffset, k.YScale, k.COffset, k.CScale);
        p.Set("uMatrix", k.Kr, k.Kb);
        p.SetInt("uTransfer", k.Transfer);
        p.SetInt("uWide", k.Wide ? 1 : 0);
        p.Set("uCrop", crop.X, crop.Y);
        p.Set("uMaxUv", crop.MaxU, crop.MaxV);
        _device.BindSampler(p, "uY", 0, y);
        _device.BindSampler(p, "uU", 1, u);
        _device.BindSampler(p, "uV", 2, v);
        _device.DrawFullscreen(p);
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
        _ring?.Dispose();
        _blocks?.Dispose();
        _dmabuf?.Dispose();
        if (OperatingSystem.IsWindows() && _angle is AngleImporter angle)
            angle.Dispose();
        _disposed = true;
    }
}
