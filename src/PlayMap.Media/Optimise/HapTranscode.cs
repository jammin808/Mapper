using System.Diagnostics;
using FFmpeg.AutoGen;
using PlayMap.Core.Media.Hap;
using PlayMap.Core.Media.Optimise;
using PlayMap.Core.Model;
using PlayMap.Media.Ffmpeg;
using PlayMap.Media.Frames;

namespace PlayMap.Media.Optimise;

/// <summary>Turns one decoded picture (straight RGBA, rows tight) into one HAP frame in <paramref name="packet"/> (grown as
/// needed); answers the frame's length.</summary>
public delegate int HapFrameEncoder(ReadOnlySpan<byte> rgba, int width, int height, ref byte[] packet);

/// <summary>What a transcode is asked: the source file, the .mov it writes, the codec, whether the source's audio is copied,
/// whether hardware decode is allowed, the decoder's threads (0 = FFmpeg's choice) and how many frames are compared with the
/// source afterwards (first, middle and last at three).</summary>
public sealed record HapTranscodeRequest(string Source, string Output, HapCodec Codec)
{
    public bool CopyAudio { get; init; } = true;
    public bool AllowHardware { get; init; }
    public int Threads { get; init; }
}

/// <summary>What a transcode made: frames written, the picture's size, the audio's fate in words ("aac copied", "no audio",
/// or why it was left out), the quality measured on the sampled frames through FFmpeg's own HAP decoder, and every packet of the
/// file parsed as a HAP frame of its codec.</summary>
public sealed record HapTranscodeResult(long Frames, int Width, int Height, string Audio, VersionQuality Quality, long Parsed);

/// <summary>Progress of a transcode: frames done, the frames expected (from the stream's duration and rate; 0 = unknown) and the
/// pace of the last second.</summary>
public readonly record struct HapTranscodeProgress(long Frames, long Expected, double Fps);

/// <summary>Source → HAP .mov in one pass: the video decoded in order (hardware only when asked), each picture converted to
/// RGBA (the frame's own matrix and range) and handed to the encoder; the HAP frames written with the source's timestamps, and
/// the source's audio stream-copied with its own (when the mov muxer takes the codec — AAC, PCM, ALAC among them). After the
/// trailer the file is read back: every packet must parse as a frame of the codec, and the sampled frames, decoded by FFmpeg's
/// HAP decoder, are compared with the source's in PSNR over RGB. The caller paces it between frames (<c>afterFrame</c>) and
/// stops it with the token; a stopped transcode leaves a partial file the caller deletes.</summary>
public static unsafe class HapTranscode
{
    public static HapTranscodeResult Run(HapTranscodeRequest request, HapFrameEncoder encode, Action<HapTranscodeProgress>? progress,
        Action<double>? afterFrame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(encode);
        FfmpegLoader.EnsureLoaded();
        using var input = MediaInput.Open(request.Source);
        var video = input.BestStream(AVMediaType.AVMEDIA_TYPE_VIDEO);
        if (video < 0)
            throw new MediaOpenException(MediaProblem.NoStream, $"{Path.GetFileName(request.Source)} has no video stream");
        var audio = request.CopyAudio ? input.BestStream(AVMediaType.AVMEDIA_TYPE_AUDIO) : -1;
        input.KeepOnly(video, audio);
        var vin = input.Stream(video);
        var expected = ExpectedFrames(input, video);
        using var decoder = StreamDecoder.Open(vin, new DecoderOptions { AllowHardware = request.AllowHardware, Threads = request.Threads });
        using var mux = new Muxer(request.Output, request.Codec, vin, input.FrameRate(video), audio >= 0 ? input.Stream(audio) : null);
        var samples = new Samples(expected);
        using var scaler = new RgbaScaler();
        var packet = ffmpeg.av_packet_alloc();
        var frame = ffmpeg.av_frame_alloc();
        byte[] hap = [];
        long frames = 0;
        var width = vin->codecpar->width;
        var height = vin->codecpar->height;
        var clock = Stopwatch.StartNew();
        var second = 0L;
        var secondFrames = 0L;
        var fps = 0.0;
        try
        {
            var ended = false;
            while (!ended)
            {
                ct.ThrowIfCancellationRequested();
                var r = ffmpeg.av_read_frame(input.Format, packet);
                if (r < 0)
                {
                    ended = true;
                    FfmpegUtil.Check(ffmpeg.avcodec_send_packet(decoder.Context, null), "decoder drain");
                }
                else if (packet->stream_index == audio)
                {
                    mux.WriteAudio(packet);
                    continue;
                }
                else if (packet->stream_index != video)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }
                else
                {
                    var sent = ffmpeg.avcodec_send_packet(decoder.Context, packet);
                    ffmpeg.av_packet_unref(packet);
                    if (sent < 0 && sent != FfmpegUtil.ErrorAgain)
                        throw new FfmpegException($"{decoder.Name} refused frame {frames + 1}", sent);
                }
                while (true)
                {
                    var got = ffmpeg.avcodec_receive_frame(decoder.Context, frame);
                    if (got == FfmpegUtil.ErrorAgain || got == ffmpeg.AVERROR_EOF)
                        break;
                    FfmpegUtil.Check(got, $"{decoder.Name} frame {frames + 1}");
                    ct.ThrowIfCancellationRequested();
                    var started = clock.Elapsed.TotalMilliseconds;
                    var picture = decoder.TransferIfHardware(frame);
                    var rgba = scaler.Convert(picture);
                    width = picture->width;
                    height = picture->height;
                    var len = encode(rgba, width, height, ref hap);
                    var pts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
                    mux.WriteVideo(hap.AsSpan(0, len), pts, frame->duration);
                    samples.Offer(frames, FfmpegUtil.ToNs(pts == ffmpeg.AV_NOPTS_VALUE ? 0 : pts - input.StartTs(video), vin->time_base) / 1_000_000, rgba);
                    ffmpeg.av_frame_unref(frame);
                    frames++;
                    secondFrames++;
                    var now = clock.ElapsedMilliseconds;
                    if (now - second >= 1000)
                    {
                        fps = secondFrames * 1000.0 / Math.Max(1, now - second);
                        second = now;
                        secondFrames = 0;
                    }
                    progress?.Invoke(new HapTranscodeProgress(frames, Math.Max(expected, frames), fps));
                    afterFrame?.Invoke(clock.Elapsed.TotalMilliseconds - started);
                }
            }
            mux.Finish();
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
        }
        if (frames == 0)
            throw new MediaOpenException(MediaProblem.DecodeFailed, $"{decoder.Name} gave no picture");
        var (quality, parsed) = Verify(request, frames, width, height, samples, ct);
        return new HapTranscodeResult(frames, width, height, mux.AudioWords, quality, parsed);
    }

    private static long ExpectedFrames(MediaInput input, int video)
    {
        var s = input.Stream(video);
        if (s->nb_frames > 0)
            return s->nb_frames;
        var rate = input.FrameRate(video);
        var ns = input.DurationNs(video);
        return rate.num > 0 && rate.den > 0 && ns > 0 ? (long)Math.Round(ns / 1e9 * rate.num / rate.den) : 0;
    }

    /// <summary>Reads the written file back: every packet parses as a HAP frame of the codec, and the sampled frames decoded by
    /// FFmpeg's HAP decoder are compared with the source's.</summary>
    private static (VersionQuality Quality, long Parsed) Verify(HapTranscodeRequest request, long frames, int width, int height, Samples samples, CancellationToken ct)
    {
        using var input = MediaInput.Open(request.Output);
        var video = input.BestStream(AVMediaType.AVMEDIA_TYPE_VIDEO);
        if (video < 0)
            throw new InvalidDataException("the written file has no video stream");
        input.KeepOnly(video);
        using var decoder = StreamDecoder.Open(input.Stream(video), new DecoderOptions { AllowHardware = false });
        using var scaler = new RgbaScaler();
        var parser = new HapFrame();
        var packet = ffmpeg.av_packet_alloc();
        var frame = ffmpeg.av_frame_alloc();
        long parsed = 0;
        long decoded = 0;
        var results = new List<(double Psnr, long AtMs)>();
        try
        {
            var ended = false;
            while (!ended)
            {
                ct.ThrowIfCancellationRequested();
                if (ffmpeg.av_read_frame(input.Format, packet) < 0)
                {
                    ended = true;
                    ffmpeg.avcodec_send_packet(decoder.Context, null);
                }
                else
                {
                    if (!parser.TryParse(new ReadOnlySpan<byte>(packet->data, packet->size), width, height, request.Codec, out _))
                        throw new InvalidDataException("the written file does not read back: " + parser.Words(parsed));
                    parsed++;
                    ffmpeg.avcodec_send_packet(decoder.Context, packet);
                    ffmpeg.av_packet_unref(packet);
                }
                while (ffmpeg.avcodec_receive_frame(decoder.Context, frame) == 0)
                {
                    if (samples.TryGet(decoded, out var source, out var atMs))
                        results.Add((Psnr.Rgb(scaler.Convert(frame), source), atMs));
                    decoded++;
                    ffmpeg.av_frame_unref(frame);
                }
            }
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
        }
        if (parsed != frames)
            throw new InvalidDataException($"the written file holds {parsed} frames; {frames} were encoded");
        if (results.Count == 0)
            throw new InvalidDataException("no sampled frame decoded back from the written file");
        var worst = results.MinBy(r => r.Psnr);
        return (new VersionQuality(Math.Round(results.Average(r => r.Psnr), 2), Math.Round(worst.Psnr, 2), results.Count, worst.AtMs), parsed);
    }

    /// <summary>The source frames kept for the comparison: the first, the one nearest the middle of what was expected, and the last
    /// (each picture is copied as it passes; the last is the latest one copied).</summary>
    private sealed class Samples(long expected)
    {
        private readonly Dictionary<long, (byte[] Rgba, long AtMs)> _kept = [];
        private readonly long _middle = expected > 2 ? expected / 2 : -1;
        private long _last = -1;
        private byte[] _lastRgba = [];
        private long _lastAt;

        public void Offer(long index, long atMs, ReadOnlySpan<byte> rgba)
        {
            if (index == 0 || index == _middle)
                _kept[index] = (rgba.ToArray(), atMs);
            if (_lastRgba.Length != rgba.Length)
                _lastRgba = new byte[rgba.Length];
            rgba.CopyTo(_lastRgba);
            _last = index;
            _lastAt = atMs;
        }

        public bool TryGet(long index, out byte[] rgba, out long atMs)
        {
            if (index == _last && !_kept.ContainsKey(index))
            {
                (rgba, atMs) = (_lastRgba, _lastAt);
                return true;
            }
            if (_kept.TryGetValue(index, out var k))
            {
                (rgba, atMs) = k;
                return true;
            }
            (rgba, atMs) = ([], 0);
            return false;
        }
    }

    /// <summary>Any decoded picture as straight RGBA, 8 bits a channel, rows tight; YUV through the frame's own matrix and range.</summary>
    private sealed class RgbaScaler : IDisposable
    {
        private SwsContext* _sws;
        private byte[] _rgba = [];
        private int _key = -1;

        public ReadOnlySpan<byte> Convert(AVFrame* frame)
        {
            var w = frame->width;
            var h = frame->height;
            var format = (AVPixelFormat)frame->format;
            if (_rgba.Length != w * h * 4)
                _rgba = new byte[w * h * 4];
            _sws = ffmpeg.sws_getCachedContext(_sws, w, h, format, w, h, AVPixelFormat.AV_PIX_FMT_RGBA,
                (int)SwsFlags.SWS_BICUBIC | (int)SwsFlags.SWS_ACCURATE_RND | (int)SwsFlags.SWS_FULL_CHR_H_INT, null, null, null);
            if (_sws is null)
                throw new FfmpegException($"no conversion from {FrameFormats.Name(format)} to rgba");
            var desc = ffmpeg.av_pix_fmt_desc_get(format);
            if (desc is not null && (desc->flags & (ulong)ffmpeg.AV_PIX_FMT_FLAG_RGB) == 0)
            {
                var cs = FrameFormats.SwsColorspace(frame->colorspace, h);
                var full = FrameFormats.Range(frame->color_range, format) == PlayMap.Core.Media.ColorRange.Full ? 1 : 0;
                if (cs * 2 + full != _key)
                {
                    var coefficients = ffmpeg.sws_getCoefficients(cs);
                    var inv = default(int_array4);
                    for (uint i = 0; i < 4; i++)
                        inv[i] = coefficients[i];
                    var table = inv;
                    ffmpeg.sws_setColorspaceDetails(_sws, in inv, full, in table, 1, 0, 1 << 16, 1 << 16);
                    _key = cs * 2 + full;
                }
            }
            fixed (byte* dst = _rgba)
            {
                var srcData = new byte*[8];
                var srcStride = new int[8];
                for (uint i = 0; i < 8; i++)
                {
                    srcData[i] = frame->data[i];
                    srcStride[i] = frame->linesize[i];
                }
                var rows = ffmpeg.sws_scale(_sws, srcData, srcStride, 0, h, [dst], [w * 4]);
                if (rows < 0)
                    throw new FfmpegException("scale", rows);
            }
            return _rgba;
        }

        public void Dispose()
        {
            if (_sws is not null)
                ffmpeg.sws_freeContext(_sws);
            _sws = null;
        }
    }

    /// <summary>The .mov: one HAP video stream tagged with the codec's fourcc at the source's time base and rate, and the source's
    /// audio stream copied when the muxer takes its codec.</summary>
    private sealed class Muxer : IDisposable
    {
        private AVFormatContext* _oc;
        private readonly AVRational _videoIn;
        private readonly AVRational _audioIn;
        private readonly int _audioOut = -1;
        private bool _open;
        private bool _finished;
        private AVPacket* _pkt;

        public Muxer(string path, HapCodec codec, AVStream* video, AVRational rate, AVStream* audio)
        {
            AVFormatContext* oc = null;
            FfmpegUtil.Check(ffmpeg.avformat_alloc_output_context2(&oc, null, "mov", path), "the mov muxer");
            _oc = oc;
            _pkt = ffmpeg.av_packet_alloc();
            try
            {
                var vs = ffmpeg.avformat_new_stream(oc, null);
                var p = vs->codecpar;
                p->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
                p->codec_id = AVCodecID.AV_CODEC_ID_HAP;
                p->codec_tag = HapCodecs.Tag(codec);
                p->width = video->codecpar->width;
                p->height = video->codecpar->height;
                p->sample_aspect_ratio = video->codecpar->sample_aspect_ratio;
                p->color_primaries = video->codecpar->color_primaries;
                p->color_trc = video->codecpar->color_trc;
                p->color_space = video->codecpar->color_space;
                vs->time_base = video->time_base;
                vs->avg_frame_rate = rate;
                vs->r_frame_rate = rate;
                _videoIn = video->time_base;
                AudioWords = "no audio";
                if (audio is not null)
                {
                    var id = audio->codecpar->codec_id;
                    var name = ffmpeg.avcodec_get_name(id) ?? "audio";
                    if (ffmpeg.avformat_query_codec(oc->oformat, id, ffmpeg.FF_COMPLIANCE_NORMAL) == 1)
                    {
                        var a = ffmpeg.avformat_new_stream(oc, null);
                        FfmpegUtil.Check(ffmpeg.avcodec_parameters_copy(a->codecpar, audio->codecpar), "audio parameters");
                        a->codecpar->codec_tag = 0;
                        a->time_base = audio->time_base;
                        _audioIn = audio->time_base;
                        _audioOut = a->index;
                        AudioWords = name + " copied";
                    }
                    else
                    {
                        AudioWords = $"the source's {name} audio does not go in a .mov without re-encoding: the version is silent";
                    }
                }
                FfmpegUtil.Check(ffmpeg.avio_open(&oc->pb, path, ffmpeg.AVIO_FLAG_WRITE), "the output file");
                _open = true;
                FfmpegUtil.Check(ffmpeg.avformat_write_header(oc, null), "the mov header");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public string AudioWords { get; } = "";

        public void WriteAudio(AVPacket* packet)
        {
            if (_audioOut < 0)
            {
                ffmpeg.av_packet_unref(packet);
                return;
            }
            var to = _oc->streams[_audioOut]->time_base;
            ffmpeg.av_packet_rescale_ts(packet, _audioIn, to);
            packet->stream_index = _audioOut;
            packet->pos = -1;
            FfmpegUtil.Check(ffmpeg.av_interleaved_write_frame(_oc, packet), "an audio packet");
        }

        public void WriteVideo(ReadOnlySpan<byte> frame, long pts, long duration)
        {
            FfmpegUtil.Check(ffmpeg.av_new_packet(_pkt, frame.Length), "a video packet");
            frame.CopyTo(new Span<byte>(_pkt->data, frame.Length));
            var to = _oc->streams[0]->time_base;
            _pkt->pts = pts == ffmpeg.AV_NOPTS_VALUE ? ffmpeg.AV_NOPTS_VALUE : ffmpeg.av_rescale_q(pts, _videoIn, to);
            _pkt->dts = _pkt->pts;
            _pkt->duration = duration > 0 ? ffmpeg.av_rescale_q(duration, _videoIn, to) : 0;
            _pkt->flags |= ffmpeg.AV_PKT_FLAG_KEY;
            _pkt->stream_index = 0;
            FfmpegUtil.Check(ffmpeg.av_interleaved_write_frame(_oc, _pkt), "a video packet");
        }

        public void Finish()
        {
            FfmpegUtil.Check(ffmpeg.av_write_trailer(_oc), "the mov trailer");
            _finished = true;
        }

        public void Dispose()
        {
            if (_pkt is not null)
            {
                var p = _pkt;
                ffmpeg.av_packet_free(&p);
                _pkt = null;
            }
            if (_oc is null)
                return;
            if (_open && !_finished)
                ffmpeg.av_write_trailer(_oc);
            if (_open)
                ffmpeg.avio_closep(&_oc->pb);
            ffmpeg.avformat_free_context(_oc);
            _oc = null;
        }
    }
}
