using FFmpeg.AutoGen;
using PlayMap.Core.Model;
using PlayMap.Media.Frames;

namespace PlayMap.Media.Ffmpeg;

/// <summary>Reads a <see cref="MediaInfo"/> from an opened input's streams.</summary>
internal static unsafe class MediaInfoReader
{
    public static MediaInfo Read(MediaInput input, int video, int audio, string? path)
    {
        long duration = 0;
        int width = 0, height = 0, channels = 0, sampleRate = 0;
        string rate = "", videoCodec = "", pixelFormat = "", audioCodec = "";
        var alpha = false;
        if (video >= 0)
        {
            var s = input.Stream(video);
            var p = s->codecpar;
            width = p->width;
            height = p->height;
            rate = FfmpegUtil.RationalText(input.FrameRate(video));
            videoCodec = ffmpeg.avcodec_get_name(p->codec_id) ?? "";
            var format = (AVPixelFormat)p->format;
            pixelFormat = format == AVPixelFormat.AV_PIX_FMT_NONE ? "" : FrameFormats.Name(format);
            alpha = (format != AVPixelFormat.AV_PIX_FMT_NONE && FrameFormats.HasAlpha(format)) || AlphaTagged(s);
            duration = input.DurationNs(video);
        }
        if (audio >= 0)
        {
            var p = input.Stream(audio)->codecpar;
            audioCodec = ffmpeg.avcodec_get_name(p->codec_id) ?? "";
            channels = p->ch_layout.nb_channels;
            sampleRate = p->sample_rate;
            if (duration == 0)
                duration = input.DurationNs(audio);
        }
        return new MediaInfo(duration, width, height, rate, videoCodec, pixelFormat, audioCodec, channels, sampleRate, alpha,
            ContainerName(input.ContainerName, path));
    }

    // WebM carries a VP8/VP9 alpha plane as side data, tagged on the stream.
    private static bool AlphaTagged(AVStream* s)
    {
        var e = ffmpeg.av_dict_get(s->metadata, "alpha_mode", null, 0);
        return e is not null && FfmpegUtil.Text(e->value) == "1";
    }

    /// <summary>The demuxer's name, narrowed to the file's extension when the demuxer lists it ("mov,mp4,…" for
    /// a .mp4 says "mp4").</summary>
    public static string ContainerName(string demuxer, string? path)
    {
        if (string.IsNullOrEmpty(demuxer))
            return "";
        var names = demuxer.Split(',');
        var ext = string.IsNullOrEmpty(path) ? "" : Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        foreach (var n in names)
        {
            if (n == ext)
                return n;
        }
        return names[0];
    }
}
