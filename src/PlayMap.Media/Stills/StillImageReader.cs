using FFmpeg.AutoGen;
using PlayMap.Media.Ffmpeg;
using PlayMap.Media.Frames;

namespace PlayMap.Media.Stills;

/// <summary>A still picture as grey: <see cref="Gray8"/> always (the top 8 bits for a deep picture), <see cref="Gray16"/> when the file
/// holds more than 8 bits a sample (a 16-bit TIFF), and the file's pixel format by name.</summary>
public sealed record StillImage(int Width, int Height, byte[] Gray8, ushort[]? Gray16, string Format);

/// <summary>
/// Reads a still (JPEG, PNG, TIFF 8 or 16 bits) through the FFmpeg the user installed: the first picture of its first video stream,
/// as grey — a grey file's samples as they are, colour through the scaler's luma — at 16 bits when the file has more than 8 a sample.
/// A RAW file is refused with words (a tethering tool converts it). Throws <see cref="MediaOpenException"/> for a file that does not
/// decode, and <see cref="FfmpegUnavailableException"/> when there is no FFmpeg.
/// </summary>
public static unsafe class StillImageReader
{
    private static readonly string[] RawExtensions = [".cr2", ".cr3", ".nef", ".arw", ".raf", ".rw2", ".orf", ".dng", ".pef", ".srw"];

    public static StillImage Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (RawExtensions.Contains(Path.GetExtension(path).ToLowerInvariant(), StringComparer.Ordinal))
            throw new MediaOpenException(MediaProblem.DecoderNotAvailable, $"{Path.GetFileName(path)} is a camera RAW file: shoot JPEG or TIFF, or let the tethering tool convert");
        using var input = MediaInput.Open(path);
        var stream = input.BestStream(AVMediaType.AVMEDIA_TYPE_VIDEO);
        if (stream < 0)
            throw new MediaOpenException(MediaProblem.NoStream, "no picture in " + Path.GetFileName(path));
        input.KeepOnly(stream);
        using var decoder = StreamDecoder.Open(input.Stream(stream), new DecoderOptions { AllowHardware = false });
        var frame = ffmpeg.av_frame_alloc();
        SwsContext* sws = null;
        try
        {
            if (!FirstFrame.Decode(input, stream, decoder, frame))
                throw new MediaOpenException(MediaProblem.DecodeFailed, decoder.Name + " produced no picture from " + Path.GetFileName(path));
            var format = (AVPixelFormat)frame->format;
            var desc = ffmpeg.av_pix_fmt_desc_get(format);
            var deep = desc is not null && desc->comp[0].depth > 8;
            var w = frame->width;
            var h = frame->height;
            var target = deep ? AVPixelFormat.AV_PIX_FMT_GRAY16LE : AVPixelFormat.AV_PIX_FMT_GRAY8;
            var bytesPerSample = deep ? 2 : 1;
            var plane = new byte[w * h * bytesPerSample];
            fixed (byte* dst = plane)
            {
                if (format == target)
                {
                    for (var y = 0; y < h; y++)
                        Buffer.MemoryCopy(frame->data[0] + (long)y * frame->linesize[0], dst + (long)y * w * bytesPerSample, w * bytesPerSample, w * bytesPerSample);
                }
                else
                {
                    sws = ffmpeg.sws_getContext(w, h, format, w, h, target, (int)SwsFlags.SWS_BICUBIC | (int)SwsFlags.SWS_ACCURATE_RND, null, null, null);
                    if (sws is null)
                        throw new MediaOpenException(MediaProblem.DecodeFailed, $"no conversion from {FrameFormats.Name(format)} to grey");
                    var dstData = new byte_ptrArray4 { [0] = dst };
                    var dstStride = new int_array4 { [0] = w * bytesPerSample };
                    var srcData = new byte_ptrArray4();
                    var srcStride = new int_array4();
                    for (uint i = 0; i < 4; i++)
                    {
                        srcData[i] = frame->data[i];
                        srcStride[i] = frame->linesize[i];
                    }
                    if (ffmpeg.sws_scale(sws, srcData, srcStride, 0, h, dstData, dstStride) < 0)
                        throw new MediaOpenException(MediaProblem.DecodeFailed, $"the picture in {Path.GetFileName(path)} could not be made grey");
                }
            }
            if (!deep)
                return new StillImage(w, h, plane, null, FrameFormats.Name(format));
            var gray16 = new ushort[w * h];
            var gray8 = new byte[w * h];
            for (var k = 0; k < gray16.Length; k++)
            {
                gray16[k] = (ushort)(plane[2 * k] | plane[2 * k + 1] << 8);
                gray8[k] = (byte)(gray16[k] >> 8);
            }
            return new StillImage(w, h, gray8, gray16, FrameFormats.Name(format));
        }
        finally
        {
            ffmpeg.sws_freeContext(sws);
            ffmpeg.av_frame_free(&frame);
        }
    }
}
