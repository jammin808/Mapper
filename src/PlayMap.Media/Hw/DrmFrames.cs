using System.Globalization;
using System.Runtime.InteropServices;
using PlayMap.Core.Media;

namespace PlayMap.Media.Hw;

// FFmpeg's DRM PRIME frame descriptor (libavutil/hwcontext_drm.h, the public header), declared by hand: FFmpeg.AutoGen does not
// bind it. Layout pinned by DrmLayoutTests against the sizes and offsets of the FFmpeg 9 header: object 24 B, plane 24 B,
// layer 104 B, frame 528 B, with AV_DRM_MAX_PLANES = 4.

/// <summary>One DRM object (a dmabuf): its fd, its size and its format modifier.</summary>
[StructLayout(LayoutKind.Sequential)]
public record struct AVDRMObjectDescriptor
{
    public int Fd;
    public nuint Size;
    public ulong FormatModifier;
}

/// <summary>One plane of a layer: the object it lives in, its byte offset there and its row pitch.</summary>
[StructLayout(LayoutKind.Sequential)]
public record struct AVDRMPlaneDescriptor
{
    public int ObjectIndex;
    public nint Offset;
    public nint Pitch;
}

/// <summary>One layer: a DRM fourcc and up to four planes.</summary>
[StructLayout(LayoutKind.Sequential)]
public record struct AVDRMLayerDescriptor
{
    public uint Format;
    public int PlaneCount;
    public AVDRMPlaneDescriptor Plane0;
    public AVDRMPlaneDescriptor Plane1;
    public AVDRMPlaneDescriptor Plane2;
    public AVDRMPlaneDescriptor Plane3;
}

/// <summary>A whole DRM PRIME frame: its objects, then its layers.</summary>
[StructLayout(LayoutKind.Sequential)]
public record struct AVDRMFrameDescriptor
{
    public int ObjectCount;
    public AVDRMObjectDescriptor Object0;
    public AVDRMObjectDescriptor Object1;
    public AVDRMObjectDescriptor Object2;
    public AVDRMObjectDescriptor Object3;
    public int LayerCount;
    public AVDRMLayerDescriptor Layer0;
    public AVDRMLayerDescriptor Layer1;
    public AVDRMLayerDescriptor Layer2;
    public AVDRMLayerDescriptor Layer3;
}

/// <summary>DRM fourccs (drm_fourcc.h: four ASCII characters, the first in the low byte) of the layers VAAPI exports for 4:2:0
/// pictures, as separate layers (one plane each) or one composed layer.</summary>
public static class DrmFourcc
{
    public const uint R8 = 0x20203852;     // "R8  "
    public const uint Gr88 = 0x38385247;   // "GR88"
    public const uint R16 = 0x20363152;    // "R16 "
    public const uint Gr32 = 0x32335247;   // "GR32"
    public const uint Nv12 = 0x3231564E;   // "NV12"
    public const uint P010 = 0x30313050;   // "P010"

    /// <summary>The modifier that names no layout (the driver's implicit one).</summary>
    public const ulong ModInvalid = 0x00FFFFFFFFFFFFFF;

    public const ulong ModLinear = 0;

    /// <summary>The four characters, for words ("Y210").</summary>
    public static string Name(uint fourcc)
    {
        Span<char> c = stackalloc char[4];
        for (var i = 0; i < 4; i++)
        {
            var b = (char)((fourcc >> (8 * i)) & 0xFF);
            c[i] = b is >= ' ' and <= '~' ? b : '?';
        }
        return new string(c).TrimEnd();
    }
}

/// <summary>Reads a DRM PRIME descriptor into a <see cref="GpuSurface"/>'s planes: NV12 as an R8 luma plane and a GR88 chroma plane,
/// P010 as R16 and GR32, each plane imported at its own visible size (so the renderer samples it with no crop). The fds stay owned
/// by the mapped frame and close when it is freed.</summary>
public static unsafe class DrmFrames
{
    /// <summary>Fills the surface from the descriptor of a <paramref name="width"/>×<paramref name="height"/> picture; false with the
    /// words when its layers are not a 4:2:0 layout the renderer imports (4:2:2 and 4:4:4 hardware formats are not this round's).</summary>
    public static bool ToSurface(AVDRMFrameDescriptor* d, int width, int height, GpuSurface surface, out string words)
    {
        ArgumentNullException.ThrowIfNull(surface);
        words = "";
        if (d is null || d->LayerCount < 1 || d->ObjectCount < 1 || d->ObjectCount > 4 || d->LayerCount > 4)
        {
            words = "the mapped frame has no DRM layers";
            return false;
        }
        var cw = (width + 1) / 2;
        var ch = (height + 1) / 2;
        surface.Api = GpuApi.DmaBuf;
        surface.Width = width;
        surface.Height = height;
        surface.CodedWidth = width;
        surface.CodedHeight = height;
        surface.Handle = 0;
        surface.Slice = 0;
        surface.Persistent = false;
        var first = Layer(d, 0);
        if (d->LayerCount == 1 && first->Format is DrmFourcc.Nv12 or DrmFourcc.P010 && first->PlaneCount == 2)
        {
            // One composed layer of two planes: the planes are imported as the separate-layer fourccs would name them.
            var ten = first->Format == DrmFourcc.P010;
            surface.Format = ten ? GpuSurfaceFormat.P010 : GpuSurfaceFormat.Nv12;
            if (!Plane(d, first, 0, ten ? DrmFourcc.R16 : DrmFourcc.R8, width, height, out surface.Planes[0], out words)
                || !Plane(d, first, 1, ten ? DrmFourcc.Gr32 : DrmFourcc.Gr88, cw, ch, out surface.Planes[1], out words))
                return false;
            surface.PlaneCount = 2;
            return true;
        }
        if (d->LayerCount == 2)
        {
            var second = Layer(d, 1);
            var nv12 = first->Format == DrmFourcc.R8 && second->Format == DrmFourcc.Gr88;
            var p010 = first->Format == DrmFourcc.R16 && second->Format == DrmFourcc.Gr32;
            if ((nv12 || p010) && first->PlaneCount >= 1 && second->PlaneCount >= 1)
            {
                surface.Format = p010 ? GpuSurfaceFormat.P010 : GpuSurfaceFormat.Nv12;
                if (!Plane(d, first, 0, first->Format, width, height, out surface.Planes[0], out words)
                    || !Plane(d, second, 0, second->Format, cw, ch, out surface.Planes[1], out words))
                    return false;
                surface.PlaneCount = 2;
                return true;
            }
        }
        var names = DrmFourcc.Name(first->Format);
        for (var i = 1; i < d->LayerCount; i++)
            names += "+" + DrmFourcc.Name(Layer(d, i)->Format);
        words = string.Create(CultureInfo.InvariantCulture,
            $"layers {names} are not a 4:2:0 layout this renderer imports (NV12 as R8+GR88, P010 as R16+GR32)");
        return false;
    }

    private static AVDRMLayerDescriptor* Layer(AVDRMFrameDescriptor* d, int i) => &(&d->Layer0)[i];

    private static bool Plane(AVDRMFrameDescriptor* d, AVDRMLayerDescriptor* layer, int index, uint fourcc, int width, int height,
        out DmaBufPlane plane, out string words)
    {
        plane = default;
        words = "";
        if (index >= layer->PlaneCount)
        {
            words = "a DRM layer has fewer planes than its format needs";
            return false;
        }
        var p = &(&layer->Plane0)[index];
        if (p->ObjectIndex < 0 || p->ObjectIndex >= d->ObjectCount)
        {
            words = "a DRM plane names an object the frame does not have";
            return false;
        }
        var o = &(&d->Object0)[p->ObjectIndex];
        if (o->Fd < 0 || p->Pitch <= 0 || p->Offset < 0)
        {
            words = "a DRM plane has no fd, pitch or offset";
            return false;
        }
        plane = new DmaBufPlane(o->Fd, fourcc, width, height, p->Offset, (int)p->Pitch, o->FormatModifier);
        return true;
    }
}
