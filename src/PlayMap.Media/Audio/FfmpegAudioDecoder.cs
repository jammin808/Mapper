using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using PlayMap.Core.Model;
using PlayMap.Media.Ffmpeg;

namespace PlayMap.Media.Audio;

/// <summary>A file's sound as interleaved float32 stereo at 48 kHz: demuxed, decoded and resampled
/// (swresample) into a native FIFO, read in blocks of any size. <see cref="PositionNs"/> is the media time of
/// the next sample <see cref="Read"/> returns, counted in output samples from the stream's first timestamp, so
/// it advances exactly with what is read. <see cref="Seek"/> is sample-exact: back to the packet before the
/// target, decode, and trim (or pad with silence) to the target sample. Owned by one thread at a time.</summary>
public sealed unsafe class FfmpegAudioDecoder : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private readonly MediaInput _input;
    private readonly StreamDecoder _decoder;
    private readonly int _stream;
    private readonly AVRational _tb;
    private readonly long _startTs;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private SwrContext* _swr;
    private AVChannelLayout* _swrInLayout;
    private int _swrInRate;
    private AVSampleFormat _swrInFormat = AVSampleFormat.AV_SAMPLE_FMT_NONE;

    private float* _fifo;
    private int _fifoCapacity;  // frames
    private int _fifoStart;     // frames
    private int _fifoCount;     // frames
    private long _headSample;   // output sample index of the FIFO's first frame
    private bool _timingKnown;
    private long _targetSample = -1;
    private bool _draining;
    private bool _flushed;
    private bool _disposed;

    /// <summary>Opens the file's best audio stream; throws <see cref="MediaOpenException"/> (no audio stream,
    /// decoder not available) or <see cref="FfmpegUnavailableException"/>.</summary>
    public FfmpegAudioDecoder(string path)
    {
        Path = path;
        _input = MediaInput.Open(path);
        try
        {
            _stream = _input.BestStream(AVMediaType.AVMEDIA_TYPE_AUDIO);
            Info = MediaInfoReader.Read(_input, _input.BestStream(AVMediaType.AVMEDIA_TYPE_VIDEO), _stream, path);
            if (_stream < 0)
                throw new MediaOpenException(MediaProblem.NoStream, "no audio stream in " + System.IO.Path.GetFileName(path));
            _input.KeepOnly(_stream);
            var s = _input.Stream(_stream);
            _tb = s->time_base;
            _startTs = _input.StartTs(_stream);
            DurationNs = _input.DurationNs(_stream);
            _decoder = StreamDecoder.Open(s, DecoderOptions.Default);
            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            _swrInLayout = (AVChannelLayout*)NativeMemory.AllocZeroed((nuint)sizeof(AVChannelLayout));
            EnsureCapacity(SampleRate / 4);
        }
        catch
        {
            _decoder?.Dispose();
            _input.Dispose();
            throw;
        }
    }

    public string Path { get; }

    public MediaInfo Info { get; }

    public long DurationNs { get; }

    public string Codec => _decoder.Name;

    /// <summary>The media time of the next sample <see cref="Read"/> returns.</summary>
    public long PositionNs => SampleToNs(_headSample);

    /// <summary>Output samples (stereo frames) read since open or the last seek, plus the seek target.</summary>
    public long PositionSamples => _headSample;

    /// <summary>True when every sample has been read.</summary>
    public bool EndOfStream => _flushed && _fifoCount == 0;

    /// <summary>The frames decoded since open (resampled into the read queue): the work the reads and skips did; a seek decodes
    /// nothing until the next read.</summary>
    internal long FramesDecoded { get; private set; }

    public static long SampleToNs(long sample) => (long)Math.Round(sample * 1e9 / SampleRate);

    public static long NsToSample(long ns) => (long)Math.Round(ns * (SampleRate / 1e9));

    /// <summary>Fills the span with interleaved stereo frames; returns the frames written (fewer only at the
    /// end).</summary>
    public int Read(Span<float> interleaved)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var want = interleaved.Length / Channels;
        Fill(want);
        var n = Math.Min(want, _fifoCount);
        new ReadOnlySpan<float>(_fifo + (nint)_fifoStart * Channels, n * Channels).CopyTo(interleaved);
        Consume(n);
        return n;
    }

    /// <summary>Discards frames (the null output's consumption, a resync's skip); returns the frames skipped.</summary>
    public long Skip(long frames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long done = 0;
        while (done < frames)
        {
            var chunk = (int)Math.Min(frames - done, SampleRate / 10);
            Fill(chunk);
            var n = Math.Min(chunk, _fifoCount);
            if (n == 0)
                break;
            Consume(n);
            done += n;
        }
        return done;
    }

    /// <summary>Moves to a media time, sample-exact.</summary>
    public void Seek(long mediaNs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var target = Math.Max(0, NsToSample(mediaNs));
        var ts = FfmpegUtil.FromNs(SampleToNs(target), _tb) + _startTs;
        var fmt = _input.Format;
        if (ffmpeg.avformat_seek_file(fmt, _stream, long.MinValue, ts, ts, 0) < 0)
            ffmpeg.av_seek_frame(fmt, _stream, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
        ffmpeg.avcodec_flush_buffers(_decoder.Context);
        FreeResampler();
        _fifoStart = 0;
        _fifoCount = 0;
        _headSample = target;
        _targetSample = target;
        _timingKnown = false;
        _draining = false;
        _flushed = false;
    }

    // Decodes until the FIFO holds the frames wanted or the stream is done.
    private void Fill(int want)
    {
        while (_fifoCount < want && !_flushed)
        {
            var r = ffmpeg.avcodec_receive_frame(_decoder.Context, _frame);
            if (r == 0)
            {
                Append(_frame);
                ffmpeg.av_frame_unref(_frame);
                continue;
            }
            if (r == ffmpeg.AVERROR_EOF || (r != FfmpegUtil.ErrorAgain && _draining))
            {
                FlushResampler();
                _flushed = true;
                break;
            }
            if (r != FfmpegUtil.ErrorAgain)
                continue; // A broken frame: the decoder recovers at the next packet.
            r = ffmpeg.av_read_frame(_input.Format, _packet);
            if (r < 0)
            {
                ffmpeg.avcodec_send_packet(_decoder.Context, null);
                _draining = true;
                continue;
            }
            if (_packet->stream_index == _stream)
                ffmpeg.avcodec_send_packet(_decoder.Context, _packet);
            ffmpeg.av_packet_unref(_packet);
        }
    }

    private void Append(AVFrame* frame)
    {
        EnsureResampler(frame);
        if (!_timingKnown)
        {
            // The first frame after open or a seek fixes where the output stands; a seek then trims to its target
            // (or pads with silence when the stream starts after it).
            var ts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
            var first = ts == ffmpeg.AV_NOPTS_VALUE ? _headSample : NsToSample(FfmpegUtil.ToNs(ts - _startTs, _tb));
            _timingKnown = true;
            if (_targetSample < 0)
            {
                _headSample = Math.Max(0, first);
            }
            else if (first > _targetSample)
            {
                var gap = (int)Math.Min(first - _targetSample, SampleRate * 10L);
                EnsureCapacity(_fifoCount + gap);
                new Span<float>(_fifo + (nint)(_fifoStart + _fifoCount) * Channels, gap * Channels).Clear();
                _fifoCount += gap;
                _headSample = _targetSample;
                _targetSample = -1;
            }
            else
            {
                _headSample = first;
            }
        }
        var outMax = ffmpeg.swr_get_out_samples(_swr, frame->nb_samples);
        if (outMax <= 0)
            return;
        EnsureCapacity(_fifoCount + outMax);
        var dst = (byte*)(_fifo + (nint)(_fifoStart + _fifoCount) * Channels);
        var got = ffmpeg.swr_convert(_swr, &dst, outMax, frame->extended_data, frame->nb_samples);
        if (got > 0)
        {
            _fifoCount += got;
            FramesDecoded += got;
        }
        TrimToTarget();
    }

    private void TrimToTarget()
    {
        if (_targetSample < 0 || !_timingKnown)
            return;
        var excess = _targetSample - _headSample;
        if (excess <= 0)
        {
            _targetSample = -1;
            return;
        }
        var n = (int)Math.Min(excess, _fifoCount);
        _fifoStart += n;
        _fifoCount -= n;
        _headSample += n;
        if (_headSample >= _targetSample)
            _targetSample = -1;
    }

    private void FlushResampler()
    {
        if (_swr is null)
            return;
        var outMax = ffmpeg.swr_get_out_samples(_swr, 0);
        if (outMax <= 0)
            return;
        EnsureCapacity(_fifoCount + outMax);
        var dst = (byte*)(_fifo + (nint)(_fifoStart + _fifoCount) * Channels);
        var got = ffmpeg.swr_convert(_swr, &dst, outMax, null, 0);
        if (got > 0)
        {
            _fifoCount += got;
            FramesDecoded += got;
        }
        TrimToTarget();
    }

    private void EnsureResampler(AVFrame* frame)
    {
        var format = (AVSampleFormat)frame->format;
        var layout = frame->ch_layout;
        if (layout.nb_channels == 0)
            layout = _decoder.Context->ch_layout;
        if (_swr is not null && format == _swrInFormat && frame->sample_rate == _swrInRate && SameLayout(layout))
            return;
        FreeResampler();
        AVChannelLayout stereo;
        ffmpeg.av_channel_layout_default(&stereo, Channels);
        SwrContext* swr = null;
        var r = ffmpeg.swr_alloc_set_opts2(&swr, &stereo, AVSampleFormat.AV_SAMPLE_FMT_FLT, SampleRate, &layout, format, frame->sample_rate, 0, null);
        if (r < 0 || ffmpeg.swr_init(swr) < 0)
        {
            ffmpeg.swr_free(&swr);
            throw new FfmpegException("audio resampler", r < 0 ? r : ffmpeg.AVERROR_EXTERNAL);
        }
        _swr = swr;
        _swrInFormat = format;
        _swrInRate = frame->sample_rate;
        ffmpeg.av_channel_layout_uninit(_swrInLayout);
        ffmpeg.av_channel_layout_copy(_swrInLayout, &layout);
    }

    private bool SameLayout(AVChannelLayout layout) => ffmpeg.av_channel_layout_compare(_swrInLayout, &layout) == 0;

    private void FreeResampler()
    {
        if (_swr is null)
            return;
        var swr = _swr;
        ffmpeg.swr_free(&swr);
        _swr = null;
    }

    private void Consume(int n)
    {
        _fifoStart += n;
        _fifoCount -= n;
        _headSample += n;
        if (_fifoCount == 0)
            _fifoStart = 0;
    }

    private void EnsureCapacity(int frames)
    {
        if (_fifoStart + frames <= _fifoCapacity)
            return;
        if (frames <= _fifoCapacity && _fifo is not null)
        {
            // Room exists behind the read point: move the unread frames to the front.
            Buffer.MemoryCopy(_fifo + (nint)_fifoStart * Channels, _fifo, (long)_fifoCapacity * Channels * sizeof(float), (long)_fifoCount * Channels * sizeof(float));
            _fifoStart = 0;
            return;
        }
        var capacity = Math.Max(frames, _fifoCapacity * 2);
        var grown = (float*)NativeMemory.Alloc((nuint)capacity * Channels * sizeof(float));
        if (_fifo is not null)
        {
            Buffer.MemoryCopy(_fifo + (nint)_fifoStart * Channels, grown, (long)capacity * Channels * sizeof(float), (long)_fifoCount * Channels * sizeof(float));
            NativeMemory.Free(_fifo);
        }
        _fifo = grown;
        _fifoCapacity = capacity;
        _fifoStart = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        FreeResampler();
        if (_swrInLayout is not null)
        {
            ffmpeg.av_channel_layout_uninit(_swrInLayout);
            NativeMemory.Free(_swrInLayout);
            _swrInLayout = null;
        }
        if (_frame is not null)
        {
            var f = _frame;
            ffmpeg.av_frame_free(&f);
            _frame = null;
        }
        if (_packet is not null)
        {
            var p = _packet;
            ffmpeg.av_packet_free(&p);
            _packet = null;
        }
        _decoder.Dispose();
        _input.Dispose();
        if (_fifo is not null)
        {
            NativeMemory.Free(_fifo);
            _fifo = null;
        }
    }
}
