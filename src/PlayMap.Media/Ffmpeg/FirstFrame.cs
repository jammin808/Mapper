using FFmpeg.AutoGen;

namespace PlayMap.Media.Ffmpeg;

/// <summary>Decodes the first frame of one stream: the probe's decodability check, a still's one decode, an
/// image sequence's frame.</summary>
internal static unsafe class FirstFrame
{
    /// <summary>Reads packets of the stream until the decoder gives a frame (into <paramref name="frame"/>).
    /// False when the stream ends without one; throws <see cref="MediaOpenException"/> (DecodeFailed) when the
    /// decoder rejects everything it is given.</summary>
    public static bool Decode(MediaInput input, int stream, StreamDecoder decoder, AVFrame* frame, int maxPackets = 2000)
    {
        var ctx = decoder.Context;
        var packet = ffmpeg.av_packet_alloc();
        try
        {
            var packets = 0;
            var sentEnd = false;
            var lastError = 0;
            var drainTries = 0;
            while (true)
            {
                var r = ffmpeg.avcodec_receive_frame(ctx, frame);
                if (r == 0)
                    return true;
                if (r == ffmpeg.AVERROR_EOF)
                    return lastError == 0 ? false : throw new MediaOpenException(MediaProblem.DecodeFailed, $"{decoder.Name} could not decode the stream: {FfmpegUtil.ErrorText(lastError)}");
                if (r != FfmpegUtil.ErrorAgain)
                    lastError = r;
                if (sentEnd)
                {
                    // Draining: frames, then the end. A decoder that neither gives one nor ends is given up on.
                    if (r == FfmpegUtil.ErrorAgain || ++drainTries > 64)
                        return false;
                    continue;
                }
                r = ffmpeg.av_read_frame(input.Format, packet);
                if (r < 0)
                {
                    ffmpeg.avcodec_send_packet(ctx, null);
                    sentEnd = true;
                    continue;
                }
                if (packet->stream_index != stream)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }
                if (++packets > maxPackets)
                    throw new MediaOpenException(MediaProblem.DecodeFailed, $"{decoder.Name} gave no picture in {maxPackets} packets");
                r = ffmpeg.avcodec_send_packet(ctx, packet);
                ffmpeg.av_packet_unref(packet);
                if (r < 0 && r != FfmpegUtil.ErrorAgain)
                    lastError = r;
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
        }
    }
}
