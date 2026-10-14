using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using PlayMap.Core.Media.Hap;
using PlayMap.Media.Ffmpeg;

namespace PlayMap.Media.Hap;

/// <summary>Proves a HAP stream decodable the way it will play: its first frame read, parsed, and every chunk decoded to its
/// texture's bytes on the CPU — for all seven codecs, including the two FFmpeg has no decoder for. Whether this node's GPU
/// samples the textures is the renderer's to say (<see cref="HapCodecs.Missing"/>).</summary>
internal static unsafe class HapProbe
{
    /// <summary>The codec a stream's fourcc names, or false for a stream that is not HAP.</summary>
    public static bool TryCodec(AVStream* s, out HapCodec codec) => HapCodecs.TryFromTag(s->codecpar->codec_tag, out codec);

    /// <summary>Decodes the stream's first frame. Answers (problem, words); MediaProblem.None when every chunk came out whole.</summary>
    public static (MediaProblem Problem, string Message) DecodeFirst(MediaInput input, int stream, HapCodec codec)
    {
        var p = input.Stream(stream)->codecpar;
        var packet = ffmpeg.av_packet_alloc();
        try
        {
            while (true)
            {
                var r = ffmpeg.av_read_frame(input.Format, packet);
                if (r < 0)
                    return (MediaProblem.DecodeFailed, $"{HapCodecs.Words(codec)} stream has no frame");
                if (packet->stream_index == stream)
                    break;
                ffmpeg.av_packet_unref(packet);
            }
            var frame = new HapFrame();
            if (!frame.TryParse(new ReadOnlySpan<byte>(packet->data, packet->size), p->width, p->height, codec, out _))
                return (MediaProblem.DecodeFailed, frame.Words(0));
            for (var t = 0; t < frame.TextureCount; t++)
            {
                var bytes = frame.Texture(t).Bytes;
                var plane = (byte*)NativeMemory.Alloc((nuint)bytes);
                try
                {
                    foreach (var c in frame.Chunks(t))
                    {
                        if (!HapWorkers.Decode(packet->data + c.SourceOffset, c.SourceLength, plane + c.TargetOffset, c.TargetLength, c.Compressor))
                            return (MediaProblem.DecodeFailed, "frame 0 has a chunk whose Snappy data is corrupt");
                    }
                }
                finally
                {
                    NativeMemory.Free(plane);
                }
            }
            return (MediaProblem.None, "");
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
        }
    }
}
