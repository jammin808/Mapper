using FFmpeg.AutoGen;
using PlayMap.Core.Media;

namespace PlayMap.Media.Frames;

/// <summary>Writes a decoded (system-memory) AVFrame into a pooled slot by its plan: a plane copy for the
/// layouts the renderer reads, swscale straight into the slot's planes otherwise, half floats for deep stills.
/// One writer per producer thread; its scratch arrays and scaler are reused for every frame, so a frame
/// written allocates nothing once warm.</summary>
internal sealed unsafe class FrameWriter : IDisposable
{
    private readonly bool _still;
    private readonly byte*[] _srcData = new byte*[8];
    private readonly int[] _srcStride = new int[8];
    private readonly byte*[] _dstData = new byte*[4];
    private readonly int[] _dstStride = new int[4];
    private SwsContext* _sws;
    private int _swsColorKey = -1;
    private byte* _temp;
    private nuint _tempBytes;

    private AVPixelFormat _cachedFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private FramePlan _cachedPlan;
    private FrameLayout? _cachedLayout;

    public FrameWriter(bool still) => _still = still;

    /// <summary>The layout a frame of this format and size becomes (cached while the format holds).</summary>
    public FrameLayout LayoutFor(AVFrame* frame, out FramePlan plan)
    {
        var format = (AVPixelFormat)frame->format;
        if (format != _cachedFormat)
        {
            _cachedFormat = format;
            _cachedPlan = FrameFormats.Plan(format, _still);
            _cachedLayout = null;
        }
        plan = _cachedPlan;
        if (_cachedLayout is null || _cachedLayout.Width != frame->width || _cachedLayout.Height != frame->height)
            _cachedLayout = FrameLayout.For(plan.Layout, frame->width, frame->height);
        return _cachedLayout;
    }

    /// <summary>Writes the frame's picture and colour metadata into a slot prepared with
    /// <see cref="LayoutFor"/>'s layout.</summary>
    public void Write(AVFrame* frame, FrameSlot slot, in FramePlan plan)
    {
        var layout = slot.Layout!;
        var format = (AVPixelFormat)frame->format;
        if (plan.PassThrough)
        {
            for (var p = 0; p < layout.Planes; p++)
                ffmpeg.av_image_copy_plane(slot.PlaneData(p), layout.Stride(p), frame->data[(uint)p], frame->linesize[(uint)p], layout.RowBytes(p), layout.Rows(p));
        }
        else if (plan.ToHalf)
        {
            WriteHalf(frame, slot, plan);
        }
        else
        {
            for (var p = 0; p < layout.Planes; p++)
            {
                _dstData[p] = slot.PlaneData(p);
                _dstStride[p] = layout.Stride(p);
            }
            Scale(frame, plan.Convert);
        }

        var rgbOut = plan.Layout is PixelLayout.Rgba8 or PixelLayout.Bgra8 or PixelLayout.RgbaHalf;
        slot.Matrix = FrameFormats.Matrix(frame->colorspace, frame->height);
        slot.Range = rgbOut ? ColorRange.Full : FrameFormats.Range(frame->color_range, format);
        slot.Transfer = FrameFormats.Transfer(frame->color_trc);
        slot.HasAlpha = plan.HasAlpha;
    }

    private void Scale(AVFrame* frame, AVPixelFormat target)
    {
        var format = (AVPixelFormat)frame->format;
        _sws = ffmpeg.sws_getCachedContext(_sws, frame->width, frame->height, format, frame->width, frame->height, target,
            (int)SwsFlags.SWS_BICUBIC | (int)SwsFlags.SWS_ACCURATE_RND | (int)SwsFlags.SWS_FULL_CHR_H_INT, null, null, null);
        if (_sws is null)
            throw new Ffmpeg.FfmpegException($"no conversion from {FrameFormats.Name(format)} to {FrameFormats.Name(target)}");
        var desc = ffmpeg.av_pix_fmt_desc_get(format);
        var srcRgb = desc is not null && (desc->flags & (ulong)ffmpeg.AV_PIX_FMT_FLAG_RGB) != 0;
        if (!srcRgb)
        {
            // The scaler's YUV matrix and range follow the frame's tags (an alpha clip is BT.709 at HD).
            var cs = FrameFormats.SwsColorspace(frame->colorspace, frame->height);
            var full = FrameFormats.Range(frame->color_range, format) == ColorRange.Full ? 1 : 0;
            var key = cs * 2 + full;
            if (key != _swsColorKey)
            {
                var coefficients = ffmpeg.sws_getCoefficients(cs);
                var inv = default(int_array4);
                for (uint i = 0; i < 4; i++)
                    inv[i] = coefficients[i];
                var table = inv;
                var dstFull = target is AVPixelFormat.AV_PIX_FMT_RGBA or AVPixelFormat.AV_PIX_FMT_BGRA or AVPixelFormat.AV_PIX_FMT_RGBA64LE ? 1 : full;
                ffmpeg.sws_setColorspaceDetails(_sws, in inv, full, in table, dstFull, 0, 1 << 16, 1 << 16);
                _swsColorKey = key;
            }
        }
        for (uint i = 0; i < 8; i++)
        {
            _srcData[i] = frame->data[i];
            _srcStride[i] = frame->linesize[i];
        }
        var rows = ffmpeg.sws_scale(_sws, _srcData, _srcStride, 0, frame->height, _dstData, _dstStride);
        if (rows < 0)
            throw new Ffmpeg.FfmpegException("scale", rows);
    }

    private void WriteHalf(AVFrame* frame, FrameSlot slot, in FramePlan plan)
    {
        var layout = slot.Layout!;
        var w = frame->width;
        var h = frame->height;
        var dst = slot.PlaneData(0);
        var dstStride = layout.Stride(0);
        var desc = ffmpeg.av_pix_fmt_desc_get((AVPixelFormat)frame->format);
        var bigEndian = desc is not null && (desc->flags & (ulong)ffmpeg.AV_PIX_FMT_FLAG_BE) != 0;
        if (plan.Convert == AVPixelFormat.AV_PIX_FMT_NONE && !bigEndian && desc is not null)
        {
            WriteHalfFromFloat(frame, desc, dst, dstStride);
            return;
        }

        // Through 16-bit RGBA: the scaler widens any deep format, then each sample becomes a half in 0..1.
        var tempStride = w * 8;
        var need = (nuint)tempStride * (nuint)h;
        if (_tempBytes < need)
        {
            if (_temp is not null)
                System.Runtime.InteropServices.NativeMemory.AlignedFree(_temp);
            _temp = (byte*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc(need, FrameLayout.Alignment);
            _tempBytes = need;
        }
        _dstData[0] = _temp;
        _dstStride[0] = tempStride;
        _dstData[1] = _dstData[2] = _dstData[3] = null;
        _dstStride[1] = _dstStride[2] = _dstStride[3] = 0;
        Scale(frame, AVPixelFormat.AV_PIX_FMT_RGBA64LE);
        const float scale = 1f / 65535f;
        for (var y = 0; y < h; y++)
        {
            var s = (ushort*)(_temp + (nint)y * tempStride);
            var d = (Half*)(dst + (nint)y * dstStride);
            for (var i = 0; i < w * 4; i++)
                d[i] = (Half)(s[i] * scale);
        }
    }

    // Float stills (EXR, float TIFF) keep their range, above 1.0 included; components are read through the
    // format's descriptor (R, G, B, A in component order, whatever the planes).
    private static void WriteHalfFromFloat(AVFrame* frame, AVPixFmtDescriptor* desc, byte* dst, int dstStride)
    {
        var w = frame->width;
        var h = frame->height;
        var n = desc->nb_components;
        var gray = (desc->flags & (ulong)ffmpeg.AV_PIX_FMT_FLAG_RGB) == 0;
        for (var y = 0; y < h; y++)
        {
            var d = (Half*)(dst + (nint)y * dstStride);
            for (var x = 0; x < w; x++)
            {
                float r, g, b, a = 1f;
                if (gray)
                {
                    r = g = b = Read(frame, desc->comp[0], x, y);
                    if (n > 1)
                        a = Read(frame, desc->comp[1], x, y);
                }
                else
                {
                    r = Read(frame, desc->comp[0], x, y);
                    g = Read(frame, desc->comp[1], x, y);
                    b = Read(frame, desc->comp[2], x, y);
                    if (n > 3)
                        a = Read(frame, desc->comp[3], x, y);
                }
                d[x * 4] = (Half)r;
                d[x * 4 + 1] = (Half)g;
                d[x * 4 + 2] = (Half)b;
                d[x * 4 + 3] = (Half)a;
            }
        }
    }

    private static float Read(AVFrame* frame, AVComponentDescriptor c, int x, int y)
    {
        var p = frame->data[(uint)c.plane] + (nint)y * frame->linesize[(uint)c.plane] + (nint)x * c.step + c.offset;
        return c.depth == 32 ? *(float*)p : (float)*(Half*)p;
    }

    public void Dispose()
    {
        if (_sws is not null)
        {
            ffmpeg.sws_freeContext(_sws);
            _sws = null;
        }
        if (_temp is not null)
        {
            System.Runtime.InteropServices.NativeMemory.AlignedFree(_temp);
            _temp = null;
            _tempBytes = 0;
        }
    }
}
