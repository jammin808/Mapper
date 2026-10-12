using System.Diagnostics;
using FFmpeg.AutoGen;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Media.Ffmpeg;
using PlayMap.Media.Frames;

namespace PlayMap.Media.Sources;

/// <summary>How a clip is opened and played.</summary>
public sealed record VideoSourceOptions
{
    public DecoderOptions Decoder { get; init; } = DecoderOptions.Default;
    public QueueSettings Queue { get; init; } = QueueSettings.Default;

    /// <summary>Loop from <see cref="InNs"/> to <see cref="OutNs"/> (0 = the end), gaplessly.</summary>
    public bool Loop { get; init; }

    public long InNs { get; init; }
    public long OutNs { get; init; }

    /// <summary>At or above this rate the decoder skips frames nothing references (B-frames).</summary>
    public double SkipNonReferenceAtRate { get; init; } = 2.0;

    public static readonly VideoSourceOptions Default = new();
}

/// <summary>A clip decoded by FFmpeg on a thread of its own, frames queued ahead of the play head.
/// <para>The decode thread demuxes and decodes (hardware first where the platform has it, frames moved to
/// system memory as NV12/P010; software otherwise, planar YUV passed through), writes each frame into a pooled
/// native buffer and queues it. <see cref="Acquire"/> answers the newest frame at or before the media time
/// without waiting; <see cref="Prepare"/> moves the play head and, when the time leaves the decoded window,
/// seeks: back to the keyframe at or before the target, flush, decode and discard to the frame covering the
/// target. A loop restarts inside the decode thread (seek to the in point, times continued by the loop length),
/// so the queue never empties across the loop point.</para>
/// <para>Disposal stops the thread (a blocking read is interrupted), frees every FFmpeg object on that thread,
/// and frees the pooled buffers as their last leases end.</para></summary>
public sealed unsafe class FfmpegVideoSource : IVideoSource
{
    private const long FallbackFrameNs = 40_000_000;
    private const int MaxReadErrors = 20;

    private readonly string _path;
    private readonly VideoSourceOptions _options;
    private readonly SourceStats _stats = new();
    private readonly FrameQueue _queue;
    private readonly FramePool _pool;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly TaskCompletionSource<bool> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly Lock _inputGate = new();
    private MediaInput? _input;
    private volatile MediaInfo _info;
    private volatile string _status = "opening";
    private volatile string _decoderName = "";
    private volatile bool _hardware;
    private volatile bool _failed;
    private volatile bool _isOpen;
    private volatile bool _stop;
    private int _disposed;

    // ---- decode-thread state (touched only by the decode thread) --------------------------------------
    private StreamDecoder? _decoder;
    private FrameWriter? _writer;
    private int _stream = -1;
    private AVRational _tb;
    private long _startTs;
    private FrameRate _rate;
    private long _frameNs = FallbackFrameNs;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private AVFrame* _candidate;
    private long _candidateNs;
    private long _candidateDur;
    private bool _hasCandidate;
    private readonly nint[] _pendingFrames = new nint[2];
    private readonly long[] _pendingNs = new long[2];
    private readonly long[] _pendingDur = new long[2];
    private int _pendingCount;
    private bool _holdPacket;
    private bool _draining;
    private bool _atEnd;
    private int _readErrors;
    private long _iteration;
    private bool _discarding;
    private long _discardTarget;
    private long _discardFileTarget;
    private int _seekRetries;
    private long _lastFileNs = -1;
    private long _lastDurNs;
    private long _generation;
    private long _busyTicks;
    private AVDiscard _skip = AVDiscard.AVDISCARD_DEFAULT;
    private long _threadAllocated;

    public FfmpegVideoSource(string path, VideoSourceOptions? options = null, string? key = null)
    {
        _path = path;
        _options = options ?? VideoSourceOptions.Default;
        Key = key ?? path;
        _info = MediaInfo.Unknown;
        var wake = new Action(() => _wake.Set());
        _queue = new FrameQueue(_options.Queue, _stats, wake);
        _pool = new FramePool(_queue.Capacity + 4, wake);
        _queue.SetLoopInitial(_options.Loop, _options.InNs, _options.OutNs);
        _thread = new Thread(Run) { IsBackground = true, Name = "decode " + System.IO.Path.GetFileName(path) };
        _thread.Start();
    }

    public string Key { get; }

    public string Path => _path;

    public MediaInfo Info => _info;

    /// <summary>Completes true when the clip is open and decoding, false when it could not be opened (the
    /// reason is in <see cref="Health"/>.Status).</summary>
    public Task<bool> Opened => _opened.Task;

    public SourceHealth Health => _stats.Health(_isOpen && !_failed, _status, _decoderName, _hardware);

    public VideoFrameLease? Acquire(long mediaNs) => _queue.Acquire(mediaNs);

    public void Prepare(long mediaNs, bool playing, double rate) => _queue.Prepare(mediaNs, playing, rate);

    public bool IsReadyAt(long mediaNs) => _queue.IsReadyAt(mediaNs);

    /// <summary>The managed bytes the decode thread had allocated when it published its latest frame (the
    /// allocation-free frame path is measured by its staying flat).</summary>
    internal long DecodeThreadAllocatedBytes => Interlocked.Read(ref _threadAllocated);

    /// <summary>Waits until the frame for the media time is decoded (preroll), or the timeout. Not for the
    /// render thread.</summary>
    public bool WaitUntilReady(long mediaNs, TimeSpan timeout) => _queue.WaitUntilReady(mediaNs, timeout);

    /// <summary>Sets the loop (on/off, in and out points; out 0 = the end). Refills from the play head.</summary>
    public void SetLoop(bool loop, long inNs, long outNs) => _queue.SetLoop(loop, inNs, outNs);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop = true;
        lock (_inputGate)
            _input?.Interrupt();
        _wake.Set();
        _queue.Close();
        if (!_thread.Join(TimeSpan.FromSeconds(10)))
        {
            // The thread is stuck inside a native call; it frees its own FFmpeg objects when it returns, and the
            // pool and signals stay alive for it. Nothing is freed under it.
            _status = "closing (decoder busy)";
            return;
        }
        _pool.Dispose();
        _queue.Dispose();
        _wake.Dispose();
        _opened.TrySetResult(false);
    }

    // ---- the decode thread ---------------------------------------------------------------------------

    private void Run()
    {
        try
        {
            if (!Open())
                return;
            Loop();
        }
        catch (Exception e)
        {
            // The last guard of a thread: an exception here would end the process. The source reports it and stops.
            Fail("decode failed: " + e.Message);
        }
        finally
        {
            Close();
        }
    }

    private bool Open()
    {
        try
        {
            var input = MediaInput.Open(_path);
            lock (_inputGate)
            {
                if (_stop)
                {
                    input.Dispose();
                    return false;
                }
                _input = input;
            }
            _stream = input.BestStream(AVMediaType.AVMEDIA_TYPE_VIDEO);
            var audio = input.BestStream(AVMediaType.AVMEDIA_TYPE_AUDIO);
            _info = MediaInfoReader.Read(input, _stream, audio, _path);
            if (_stream < 0)
                throw new MediaOpenException(MediaProblem.NoStream, "no video stream in " + System.IO.Path.GetFileName(_path));
            input.KeepOnly(_stream);
            var s = input.Stream(_stream);
            _tb = s->time_base;
            _startTs = input.StartTs(_stream);
            var fr = input.FrameRate(_stream);
            _rate = fr.num > 0 && fr.den > 0 ? new FrameRate(fr.num, fr.den) : default;
            _frameNs = _rate.IsValid ? _rate.FrameDurationNs : FallbackFrameNs;
            _decoder = StreamDecoder.Open(s, _options.Decoder);
            _decoderName = _decoder.Name;
            _hardware = _decoder.Hardware;
            _writer = new FrameWriter(PlanMode.Video);
            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            _candidate = ffmpeg.av_frame_alloc();
            _pendingFrames[0] = (nint)ffmpeg.av_frame_alloc();
            _pendingFrames[1] = (nint)ffmpeg.av_frame_alloc();
            _queue.SetDuration(_info.DurationNs);
            var start = Math.Max(0, _options.InNs);
            _queue.SetStart(start);
            if (start > 0)
                BeginSeek(start, _queue.Control());
            _status = "ready";
            _isOpen = true;
            _opened.TrySetResult(true);
            return true;
        }
        catch (MediaOpenException e)
        {
            Fail(e.Message);
            return false;
        }
        catch (FfmpegUnavailableException e)
        {
            Fail(e.Message);
            return false;
        }
    }

    private void Fail(string why)
    {
        _status = why;
        _failed = true;
        _opened.TrySetResult(false);
    }

    private void Loop()
    {
        // One real block before the first frame: the event makes its lock object lazily on the first wait that
        // blocks, and that allocation belongs to the start, not to some frame later in the show. A signal lost
        // to this reset is harmless — the loop reads the queue's state afresh.
        _wake.Reset();
        _wake.Wait(2);
        while (!_stop)
        {
            _wake.Reset();
            var control = _queue.Control();
            ApplyRate(control.Rate);
            if (_queue.TakeSeek(out var target))
            {
                ClearHeld();
                BeginSeek(target, control);
                continue;
            }
            if (_pendingCount > 0)
            {
                if (!PublishPending())
                    _wake.Wait(100);
                continue;
            }
            if (_atEnd || !_queue.WantMore())
            {
                _wake.Wait(100);
                continue;
            }
            Step(control);
        }
    }

    private void ApplyRate(double rate)
    {
        var skip = rate >= _options.SkipNonReferenceAtRate ? AVDiscard.AVDISCARD_NONREF : AVDiscard.AVDISCARD_DEFAULT;
        if (skip == _skip)
            return;
        _skip = skip;
        _decoder!.Context->skip_frame = skip;
    }

    private void Step(in QueueControl control)
    {
        var ctx = _decoder!.Context;
        var t0 = Stopwatch.GetTimestamp();
        var r = ffmpeg.avcodec_receive_frame(ctx, _frame);
        if (r == 0)
        {
            _busyTicks += Stopwatch.GetTimestamp() - t0;
            OnFrame(control);
            return;
        }
        if (r == ffmpeg.AVERROR_EOF)
        {
            OnDrained();
            return;
        }
        if (r != FfmpegUtil.ErrorAgain)
        {
            // A broken frame: the decoder recovers at the next packet; nothing is shown for it.
            _stats.FrameDropped();
            return;
        }
        if (_draining)
        {
            OnDrained();
            return;
        }
        if (!_holdPacket)
        {
            r = ffmpeg.av_read_frame(_input!.Format, _packet);
            if (r < 0)
            {
                if (_input.Interrupted)
                    return;
                if (r == ffmpeg.AVERROR_EOF || ++_readErrors > MaxReadErrors)
                {
                    ffmpeg.avcodec_send_packet(ctx, null);
                    _draining = true;
                }
                return;
            }
            _readErrors = 0;
            if (_packet->stream_index != _stream)
            {
                ffmpeg.av_packet_unref(_packet);
                return;
            }
        }
        r = ffmpeg.avcodec_send_packet(ctx, _packet);
        if (r == FfmpegUtil.ErrorAgain)
        {
            // The decoder is full: receive first, then send this packet again.
            _holdPacket = true;
            return;
        }
        _holdPacket = false;
        ffmpeg.av_packet_unref(_packet);
        _busyTicks += Stopwatch.GetTimestamp() - t0;
    }

    private void OnFrame(in QueueControl control)
    {
        _stats.FrameDecoded();
        var ts = _frame->best_effort_timestamp;
        if (ts == ffmpeg.AV_NOPTS_VALUE)
            ts = _frame->pts;
        long fileNs, endNs;
        if (ts == ffmpeg.AV_NOPTS_VALUE)
        {
            fileNs = _lastFileNs < 0 ? 0 : _lastFileNs + Math.Max(_lastDurNs, 1);
            endNs = fileNs + _frameNs;
        }
        else
        {
            fileNs = Snap(FfmpegUtil.ToNs(ts - _startTs, _tb));
            // The end is rounded the way the start is, so a frame ends on the nanosecond its successor starts.
            endNs = _frame->duration > 0
                ? Snap(FfmpegUtil.ToNs(ts - _startTs + _frame->duration, _tb))
                : _rate.IsValid ? _rate.TimeOf(_rate.FrameAt(fileNs) + 1) : fileNs + _frameNs;
        }
        var dur = Math.Max(1, endNs - fileNs);
        _lastFileNs = fileNs;
        _lastDurNs = dur;

        if (control.Loop && fileNs >= control.LoopOutNs)
        {
            // Past the out point: this iteration is over (frames still in the decoder go with the flush).
            ffmpeg.av_frame_unref(_frame);
            ReleaseCandidateInto();
            RestartLoop(control);
            return;
        }
        var t = control.Loop ? fileNs + _iteration * control.LoopLengthNs : fileNs;

        // A frame wholly before the play head is only worth keeping as the newest such frame (the one covering
        // the head when the next frame starts after it): the decoder is catching up after a jump or a stall.
        var stale = _discarding
            ? t <= _discardTarget
            : control.HasConsumer && t + dur <= control.ConsumerNs;
        if (stale)
        {
            if (_hasCandidate && !_discarding && control.Playing)
                _stats.FrameDropped();
            ffmpeg.av_frame_unref(_candidate);
            ffmpeg.av_frame_move_ref(_candidate, _frame);
            _candidateNs = t;
            _candidateDur = dur;
            _hasCandidate = true;
            return;
        }
        if (_discarding && !_hasCandidate && t > _discardTarget && _seekRetries < 3 && _discardFileTarget > 0)
        {
            // The seek landed after the target (an index that rounded forward): go further back and decode again.
            ffmpeg.av_frame_unref(_frame);
            _seekRetries++;
            SeekFile(_discardFileTarget - _seekRetries * 2 * FfmpegUtil.NsPerSecond);
            return;
        }
        ReleaseCandidateInto();
        EnqueuePending(_frame, t, dur);
        _discarding = false;
    }

    private void OnDrained()
    {
        ReleaseCandidateInto();
        _discarding = false;
        if (_info.DurationNs <= 0 && _lastFileNs >= 0)
        {
            // The container did not say how long the clip is; now it is known.
            _info = _info with { DurationNs = _lastFileNs + _lastDurNs };
            _queue.SetDuration(_info.DurationNs);
        }
        var now = _queue.Control();
        if (now.Loop)
        {
            RestartLoop(now);
            return;
        }
        _atEnd = true;
        _status = "end";
        _queue.MarkEnded(true);
    }

    private void RestartLoop(in QueueControl control)
    {
        _iteration++;
        SeekFile(control.LoopInNs);
        _discarding = true;
        _discardFileTarget = control.LoopInNs;
        _discardTarget = control.LoopInNs + _iteration * control.LoopLengthNs;
        _seekRetries = 0;
    }

    private void BeginSeek(long target, in QueueControl control)
    {
        _status = "ready";
        long iteration = 0, file = target;
        if (control.Loop && control.LoopLengthNs > 0 && target >= control.LoopInNs)
        {
            iteration = (target - control.LoopInNs) / control.LoopLengthNs;
            file = target - iteration * control.LoopLengthNs;
        }
        _iteration = iteration;
        _discarding = true;
        _discardTarget = target;
        _discardFileTarget = file;
        _seekRetries = 0;
        SeekFile(file);
    }

    private void SeekFile(long fileNs)
    {
        var fmt = _input!.Format;
        var ts = FfmpegUtil.FromNs(Math.Max(0, fileNs), _tb) + _startTs;
        var r = ffmpeg.avformat_seek_file(fmt, _stream, long.MinValue, ts, ts, 0);
        if (r < 0)
            r = ffmpeg.av_seek_frame(fmt, _stream, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (r < 0)
            ffmpeg.av_seek_frame(fmt, _stream, _startTs, ffmpeg.AVSEEK_FLAG_BACKWARD);
        ffmpeg.avcodec_flush_buffers(_decoder!.Context);
        if (_holdPacket)
            ffmpeg.av_packet_unref(_packet);
        _holdPacket = false;
        _draining = false;
        _atEnd = false;
        _readErrors = 0;
        _lastFileNs = -1;
    }

    // Snaps a time within a millisecond of the clip's frame grid onto it: containers that store milliseconds
    // (Matroska) round 29.97 fps times, and a frame must start on the same nanosecond the frame maths gives.
    private long Snap(long ns)
    {
        if (!_rate.IsValid)
            return ns;
        var n = _rate.FrameAt(ns + _frameNs / 2);
        var grid = _rate.TimeOf(n);
        return Math.Abs(ns - grid) <= Durations.NsPerMs ? grid : ns;
    }

    private void ReleaseCandidateInto()
    {
        if (!_hasCandidate)
            return;
        _hasCandidate = false;
        EnqueuePending(_candidate, _candidateNs, _candidateDur);
    }

    private void EnqueuePending(AVFrame* frame, long t, long dur)
    {
        var dst = (AVFrame*)_pendingFrames[_pendingCount];
        ffmpeg.av_frame_unref(dst);
        ffmpeg.av_frame_move_ref(dst, frame);
        _pendingNs[_pendingCount] = t;
        _pendingDur[_pendingCount] = dur;
        _pendingCount++;
    }

    private void ClearHeld()
    {
        for (var i = 0; i < _pendingFrames.Length; i++)
            ffmpeg.av_frame_unref((AVFrame*)_pendingFrames[i]);
        _pendingCount = 0;
        ffmpeg.av_frame_unref(_candidate);
        _hasCandidate = false;
    }

    // Publishes held frames in order; false when the pool has no free buffer (wait for a release).
    private bool PublishPending()
    {
        while (_pendingCount > 0)
        {
            if (_queue.SeekPending)
            {
                ClearHeld();
                return true;
            }
            if (!_queue.HasRoom())
                return false;
            var frame = (AVFrame*)_pendingFrames[0];
            var t0 = Stopwatch.GetTimestamp();
            AVFrame* src;
            try
            {
                src = _decoder!.TransferIfHardware(frame);
            }
            catch (FfmpegException)
            {
                // A surface that cannot be read back is a frame lost, not a stopped clip.
                _stats.FrameDropped();
                ShiftPending();
                continue;
            }
            var layout = _writer!.LayoutFor(src, out var plan);
            var slot = _pool.TryRent(layout);
            if (slot is null)
                return false;
            _writer.Write(src, slot, plan);
            slot.PtsNs = _pendingNs[0];
            slot.DurationNs = _pendingDur[0];
            slot.Generation = ++_generation;
            slot.ArrivalNs = MonoClock.Instance.NowNs;
            _busyTicks += Stopwatch.GetTimestamp() - t0;
            _stats.DecodeTime(Stopwatch.GetElapsedTime(0, _busyTicks).TotalMilliseconds);
            _busyTicks = 0;
            _queue.Publish(slot);
            ShiftPending();
            Interlocked.Exchange(ref _threadAllocated, GC.GetAllocatedBytesForCurrentThread());
        }
        return true;
    }

    private void ShiftPending()
    {
        var first = _pendingFrames[0];
        ffmpeg.av_frame_unref((AVFrame*)first);
        _pendingFrames[0] = _pendingFrames[1];
        _pendingNs[0] = _pendingNs[1];
        _pendingDur[0] = _pendingDur[1];
        _pendingFrames[1] = first;
        _pendingCount--;
    }

    private void Close()
    {
        if (_pendingFrames[0] != 0)
            ClearHeld();
        for (var i = 0; i < _pendingFrames.Length; i++)
        {
            var f = (AVFrame*)_pendingFrames[i];
            if (f is not null)
                ffmpeg.av_frame_free(&f);
            _pendingFrames[i] = 0;
        }
        if (_candidate is not null)
        {
            var c = _candidate;
            ffmpeg.av_frame_free(&c);
            _candidate = null;
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
        _writer?.Dispose();
        _writer = null;
        _decoder?.Dispose();
        _decoder = null;
        MediaInput? input;
        lock (_inputGate)
        {
            input = _input;
            _input = null;
        }
        input?.Dispose();
        if (!_failed && _status != "end")
            _status = "closed";
    }
}
