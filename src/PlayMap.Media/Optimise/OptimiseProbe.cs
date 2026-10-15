using FFmpeg.AutoGen;
using PlayMap.Core.Media.Optimise;
using PlayMap.Core.Model;
using PlayMap.Media.Ffmpeg;

namespace PlayMap.Media.Optimise;

/// <summary>The planner's facts about a file beyond the library's: the bit depth of its pixel format, its transfer (the stream's
/// colour tags: PQ, HLG, linear, else SDR), whether its primaries are BT.2020, and whether its codec is intra-only (its
/// descriptor's own property).</summary>
public static unsafe class OptimiseProbe
{
    public static ProbeFacts Read(string path, MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var facts = ProbeFacts.From(item);
        FfmpegLoader.EnsureLoaded();
        using var input = MediaInput.Open(path);
        var video = input.BestStream(AVMediaType.AVMEDIA_TYPE_VIDEO);
        if (video < 0)
            return facts;
        var p = input.Stream(video)->codecpar;
        var depth = facts.BitDepth;
        var desc = ffmpeg.av_pix_fmt_desc_get((AVPixelFormat)p->format);
        if (desc is not null)
            depth = desc->comp[0].depth;
        var transfer = p->color_trc switch
        {
            AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 => "pq",
            AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67 => "hlg",
            AVColorTransferCharacteristic.AVCOL_TRC_LINEAR => "linear",
            _ => "sdr",
        };
        var wide = p->color_primaries == AVColorPrimaries.AVCOL_PRI_BT2020;
        var codec = ffmpeg.avcodec_descriptor_get(p->codec_id);
        var intra = codec is not null && (codec->props & ffmpeg.AV_CODEC_PROP_INTRA_ONLY) != 0;
        return facts with { BitDepth = depth, Transfer = transfer, WideGamut = wide, LongGop = !intra && facts.Kind == MediaKind.Video };
    }
}
