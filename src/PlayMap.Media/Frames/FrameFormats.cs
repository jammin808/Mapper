using FFmpeg.AutoGen;
using PlayMap.Core.Media;

namespace PlayMap.Media.Frames;

/// <summary>How a decoded picture becomes a frame: kept as it is (a layout the renderer reads directly), or
/// converted once on the CPU (swscale) to the nearest layout it reads, or widened to half floats.</summary>
internal readonly record struct FramePlan(PixelLayout Layout, AVPixelFormat Convert, bool ToHalf, bool HasAlpha)
{
    public bool PassThrough => Convert == AVPixelFormat.AV_PIX_FMT_NONE && !ToHalf;
}

/// <summary>What the renderer is handed for each FFmpeg pixel format, and the colour metadata words.</summary>
internal static unsafe class FrameFormats
{
    /// <summary>The plan for a decoded format. Video: YUV stays YUV (the GPU converts) — the planar layouts
    /// pass through, other 8-bit YUV is repacked to the planar layout that keeps its chroma, deeper YUV to
    /// 10-bit 4:2:0; RGB, palette and anything with alpha become RGBA. Stills: everything becomes RGBA, in half
    /// floats when the source is deeper than 8 bits or float (EXR, 16-bit PNG/TIFF/DPX).</summary>
    public static FramePlan Plan(AVPixelFormat format, bool still)
    {
        var desc = ffmpeg.av_pix_fmt_desc_get(format);
        if (desc is null)
            return new FramePlan(PixelLayout.Rgba8, AVPixelFormat.AV_PIX_FMT_RGBA, false, false);
        var flags = desc->flags;
        var alpha = (flags & (ulong)ffmpeg.AV_PIX_FMT_FLAG_ALPHA) != 0;
        var rgb = (flags & (ulong)ffmpeg.AV_PIX_FMT_FLAG_RGB) != 0;
        var isFloat = (flags & (ulong)ffmpeg.AV_PIX_FMT_FLAG_FLOAT) != 0;
        var pal = (flags & (ulong)ffmpeg.AV_PIX_FMT_FLAG_PAL) != 0;
        var depth = desc->comp[0].depth;
        var gray = desc->nb_components <= 2 && !rgb;

        if (still)
        {
            if (isFloat || depth > 8)
                return new FramePlan(PixelLayout.RgbaHalf, isFloat ? AVPixelFormat.AV_PIX_FMT_NONE : AVPixelFormat.AV_PIX_FMT_RGBA64LE, true, alpha);
            return format == AVPixelFormat.AV_PIX_FMT_RGBA
                ? new FramePlan(PixelLayout.Rgba8, AVPixelFormat.AV_PIX_FMT_NONE, false, true)
                : new FramePlan(PixelLayout.Rgba8, AVPixelFormat.AV_PIX_FMT_RGBA, false, alpha || pal);
        }

        switch (format)
        {
            case AVPixelFormat.AV_PIX_FMT_YUV420P:
            case AVPixelFormat.AV_PIX_FMT_YUVJ420P:
                return Keep(PixelLayout.Yuv420P);
            case AVPixelFormat.AV_PIX_FMT_YUV422P:
            case AVPixelFormat.AV_PIX_FMT_YUVJ422P:
                return Keep(PixelLayout.Yuv422P);
            case AVPixelFormat.AV_PIX_FMT_YUV444P:
            case AVPixelFormat.AV_PIX_FMT_YUVJ444P:
                return Keep(PixelLayout.Yuv444P);
            case AVPixelFormat.AV_PIX_FMT_YUV420P10LE:
                return Keep(PixelLayout.Yuv420P10);
            case AVPixelFormat.AV_PIX_FMT_NV12:
                return Keep(PixelLayout.Nv12);
            case AVPixelFormat.AV_PIX_FMT_P010LE:
                return Keep(PixelLayout.P010);
            case AVPixelFormat.AV_PIX_FMT_GRAY8:
                return Keep(PixelLayout.Gray8);
            case AVPixelFormat.AV_PIX_FMT_RGBA:
                return new FramePlan(PixelLayout.Rgba8, AVPixelFormat.AV_PIX_FMT_NONE, false, true);
            case AVPixelFormat.AV_PIX_FMT_BGRA:
                return new FramePlan(PixelLayout.Bgra8, AVPixelFormat.AV_PIX_FMT_NONE, false, true);
        }

        if (alpha || rgb || pal || isFloat)
            return new FramePlan(PixelLayout.Rgba8, AVPixelFormat.AV_PIX_FMT_RGBA, false, alpha || pal);
        if (gray)
            return depth > 8
                ? new FramePlan(PixelLayout.Yuv420P10, AVPixelFormat.AV_PIX_FMT_YUV420P10LE, false, false)
                : new FramePlan(PixelLayout.Gray8, AVPixelFormat.AV_PIX_FMT_GRAY8, false, false);
        if (depth > 8)
            return new FramePlan(PixelLayout.Yuv420P10, AVPixelFormat.AV_PIX_FMT_YUV420P10LE, false, false);
        if (desc->log2_chroma_w == 0 && desc->log2_chroma_h == 0)
            return new FramePlan(PixelLayout.Yuv444P, AVPixelFormat.AV_PIX_FMT_YUV444P, false, false);
        if (desc->log2_chroma_w == 1 && desc->log2_chroma_h == 0)
            return new FramePlan(PixelLayout.Yuv422P, AVPixelFormat.AV_PIX_FMT_YUV422P, false, false);
        return new FramePlan(PixelLayout.Yuv420P, AVPixelFormat.AV_PIX_FMT_YUV420P, false, false);

        static FramePlan Keep(PixelLayout layout) => new(layout, AVPixelFormat.AV_PIX_FMT_NONE, false, false);
    }

    /// <summary>True for the JPEG ("full range") YUV formats.</summary>
    public static bool IsFullRangeFormat(AVPixelFormat format) => format is AVPixelFormat.AV_PIX_FMT_YUVJ420P
        or AVPixelFormat.AV_PIX_FMT_YUVJ422P or AVPixelFormat.AV_PIX_FMT_YUVJ444P or AVPixelFormat.AV_PIX_FMT_YUVJ440P;

    public static bool IsHardware(AVPixelFormat format)
    {
        var desc = ffmpeg.av_pix_fmt_desc_get(format);
        return desc is not null && (desc->flags & (ulong)ffmpeg.AV_PIX_FMT_FLAG_HWACCEL) != 0;
    }

    public static bool HasAlpha(AVPixelFormat format)
    {
        var desc = ffmpeg.av_pix_fmt_desc_get(format);
        return desc is not null && (desc->flags & (ulong)ffmpeg.AV_PIX_FMT_FLAG_ALPHA) != 0;
    }

    public static string Name(AVPixelFormat format) => ffmpeg.av_get_pix_fmt_name(format) ?? "";

    /// <summary>The YUV matrix: the frame's own tag, else the height convention (HD and up 709, SD 601).</summary>
    public static ColorMatrix Matrix(AVColorSpace space, int height) => space switch
    {
        AVColorSpace.AVCOL_SPC_BT709 => ColorMatrix.Bt709,
        AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M or AVColorSpace.AVCOL_SPC_FCC => ColorMatrix.Bt601,
        AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL => ColorMatrix.Bt2020,
        _ => height > 576 ? ColorMatrix.Bt709 : ColorMatrix.Bt601,
    };

    public static ColorRange Range(AVColorRange range, AVPixelFormat format) =>
        range == AVColorRange.AVCOL_RANGE_JPEG || IsFullRangeFormat(format) ? ColorRange.Full : ColorRange.Limited;

    public static TransferFunction Transfer(AVColorTransferCharacteristic trc) => trc switch
    {
        AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 => TransferFunction.Pq,
        AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67 => TransferFunction.Hlg,
        AVColorTransferCharacteristic.AVCOL_TRC_LINEAR => TransferFunction.Linear,
        _ => TransferFunction.Sdr,
    };

    /// <summary>swscale's coefficient table id for a colour space.</summary>
    public static int SwsColorspace(AVColorSpace space, int height) => Matrix(space, height) switch
    {
        ColorMatrix.Bt709 => ffmpeg.SWS_CS_ITU709,
        ColorMatrix.Bt2020 => ffmpeg.SWS_CS_BT2020,
        _ => ffmpeg.SWS_CS_ITU601,
    };
}
