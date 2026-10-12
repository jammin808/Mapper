using System.Globalization;
using FFmpeg.AutoGen;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Media.Ffmpeg;
using PlayMap.Media.Frames;

namespace PlayMap.Media;

/// <summary>What a probe found: the kind, the facts, whether a decoder turned the first frame into a picture,
/// and — when it did not — the problem in words an operator reads before the show.</summary>
public sealed record ProbeResult(
    MediaKind Kind,
    MediaInfo Info,
    bool Decodable,
    MediaProblem Problem,
    string Message,
    string Decoder,
    long FrameCount)
{
    public bool Ok => Problem == MediaProblem.None;
}

/// <summary>Reads a file (or a folder or pattern of images) with FFmpeg: its kind, duration, size, frame rate as
/// a rational, codecs, pixel format, audio, alpha and container; then opens the decoder and decodes the first
/// frame, so "decodable" is a fact a picture proved, not a codec name looked up.</summary>
public static unsafe class MediaProbe
{
    /// <summary>The frame rate an image sequence plays at when nothing says otherwise.</summary>
    public static readonly FrameRate DefaultSequenceRate = FrameRate.Fps25;

    /// <summary>Probes on the thread pool (the FFmpeg calls block on the file).</summary>
    public static Task<ProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Probe(path, null, cancellationToken), cancellationToken);

    /// <summary>Probes an image sequence at a given rate on the thread pool.</summary>
    public static Task<ProbeResult> ProbeSequenceAsync(string location, FrameRate rate, CancellationToken cancellationToken = default) =>
        Task.Run(() => Probe(location, rate, cancellationToken), cancellationToken);

    /// <summary>Probes synchronously. <paramref name="sequenceRate"/> is the rate an image sequence plays at
    /// (default 25).</summary>
    public static ProbeResult Probe(string path, FrameRate? sequenceRate = null, CancellationToken cancellationToken = default)
    {
        if (!FfmpegLoader.TryLoad(out var why))
            return Failed(GuessKind(path), MediaProblem.LibrariesMissing, why);
        cancellationToken.ThrowIfCancellationRequested();
        if (ImageSequence.IsSequenceLocation(path))
            return ProbeSequence(path, sequenceRate ?? DefaultSequenceRate);
        if (!File.Exists(path))
            return Failed(GuessKind(path), MediaProblem.CannotOpen, "no such file: " + path);

        MediaInput input;
        try
        {
            input = MediaInput.Open(path);
        }
        catch (MediaOpenException e)
        {
            return Failed(GuessKind(path), e.Problem, e.Message);
        }
        using (input)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var video = input.BestStream(AVMediaType.AVMEDIA_TYPE_VIDEO);
            var audio = input.BestStream(AVMediaType.AVMEDIA_TYPE_AUDIO);
            var info = MediaInfoReader.Read(input, video, audio, path);
            if (video < 0 && audio < 0)
                return new ProbeResult(GuessKind(path), info, false, MediaProblem.NoStream, "no video or audio stream", "", 0);
            var kind = video < 0 ? MediaKind.Audio : IsStill(input, video) ? MediaKind.Image : MediaKind.Video;
            if (kind == MediaKind.Image)
                info = info with { DurationNs = 0, FrameRate = "" };
            var frames = kind == MediaKind.Video ? FrameCount(input, video, info) : kind == MediaKind.Image ? 1 : 0;
            var stream = video >= 0 ? video : audio;
            input.KeepOnly(stream);
            var (problem, message, decoder, format) = DecodeFirst(input, stream);
            if (kind != MediaKind.Audio && format != AVPixelFormat.AV_PIX_FMT_NONE)
            {
                info = info with
                {
                    PixelFormat = FrameFormats.Name(format),
                    HasAlpha = info.HasAlpha || FrameFormats.HasAlpha(format),
                };
            }
            return new ProbeResult(kind, info, problem == MediaProblem.None, problem, message, decoder, frames);
        }
    }

    private static ProbeResult ProbeSequence(string location, FrameRate rate)
    {
        var files = ImageSequence.Resolve(location);
        if (files.Count == 0)
            return Failed(MediaKind.ImageSequence, MediaProblem.CannotOpen, "no images match " + location);
        var first = Probe(files[0]);
        var info = first.Info with
        {
            DurationNs = rate.TimeOf(files.Count),
            FrameRate = string.Create(CultureInfo.InvariantCulture, $"{rate.Numerator}/{rate.Denominator}"),
            Container = "sequence",
        };
        return first with { Kind = MediaKind.ImageSequence, Info = info, FrameCount = files.Count };
    }

    private static (MediaProblem Problem, string Message, string Decoder, AVPixelFormat Format) DecodeFirst(MediaInput input, int stream)
    {
        StreamDecoder decoder;
        try
        {
            decoder = StreamDecoder.Open(input.Stream(stream), new DecoderOptions { AllowHardware = false });
        }
        catch (MediaOpenException e)
        {
            return (e.Problem, e.Message, "", AVPixelFormat.AV_PIX_FMT_NONE);
        }
        using (decoder)
        {
            var frame = ffmpeg.av_frame_alloc();
            try
            {
                if (!FirstFrame.Decode(input, stream, decoder, frame))
                    return (MediaProblem.DecodeFailed, $"{decoder.Name} produced no frame", decoder.Name, AVPixelFormat.AV_PIX_FMT_NONE);
                var format = input.Stream(stream)->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO
                    ? (AVPixelFormat)frame->format
                    : AVPixelFormat.AV_PIX_FMT_NONE;
                return (MediaProblem.None, "", decoder.Name, format);
            }
            catch (MediaOpenException e)
            {
                return (e.Problem, e.Message, decoder.Name, AVPixelFormat.AV_PIX_FMT_NONE);
            }
            finally
            {
                ffmpeg.av_frame_free(&frame);
            }
        }
    }

    // A single picture: the image demuxers (image2 and the *_pipe family) and any one-frame stream of an
    // intra-only image codec.
    private static bool IsStill(MediaInput input, int video)
    {
        var name = input.ContainerName;
        if (name == "image2" || name.EndsWith("_pipe", StringComparison.Ordinal))
            return true;
        var s = input.Stream(video);
        return s->nb_frames == 1 && (s->codecpar->codec_id is AVCodecID.AV_CODEC_ID_PNG or AVCodecID.AV_CODEC_ID_MJPEG
            or AVCodecID.AV_CODEC_ID_TIFF or AVCodecID.AV_CODEC_ID_WEBP or AVCodecID.AV_CODEC_ID_GIF or AVCodecID.AV_CODEC_ID_BMP);
    }

    private static long FrameCount(MediaInput input, int video, MediaInfo info)
    {
        var s = input.Stream(video);
        if (s->nb_frames > 0)
            return s->nb_frames;
        return FrameRate.TryParse(info.FrameRate, out var rate) && rate.IsValid && info.DurationNs > 0
            ? rate.FrameAt(info.DurationNs - 1) + 1
            : 0;
    }

    /// <summary>The kind a path's extension suggests (for a file that cannot be opened).</summary>
    public static MediaKind GuessKind(string path)
    {
        if (ImageSequence.IsSequenceLocation(path))
            return MediaKind.ImageSequence;
        if (ImageSequence.IsImageFile(path))
            return MediaKind.Image;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".wav" or ".mp3" or ".aac" or ".m4a" or ".flac" or ".ogg" or ".opus" or ".aif" or ".aiff" => MediaKind.Audio,
            _ => MediaKind.Video,
        };
    }

    private static ProbeResult Failed(MediaKind kind, MediaProblem problem, string message) =>
        new(kind, MediaInfo.Unknown, false, problem, message, "", 0);
}
