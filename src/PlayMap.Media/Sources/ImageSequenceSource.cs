using System.Diagnostics;
using System.Globalization;
using FFmpeg.AutoGen;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Media.Ffmpeg;
using PlayMap.Media.Frames;

namespace PlayMap.Media.Sources;

/// <summary>How an image sequence plays.</summary>
public sealed record ImageSequenceOptions
{
    public QueueSettings Queue { get; init; } = QueueSettings.Default;
    public bool Loop { get; init; }
    public long InNs { get; init; }

    /// <summary>0 = the last frame's end.</summary>
    public long OutNs { get; init; }

    public static readonly ImageSequenceOptions Default = new();
}

/// <summary>A folder or pattern of numbered images played at a frame rate: frame i covers
/// [TimeOf(i), TimeOf(i+1)). A thread of its own decodes the images ahead of the play head into pooled frames
/// (a JPEG run stays YUV; PNG, EXR, DPX become RGBA, half floats when deep); a seek is a jump to an index; a
/// loop continues the times by the loop length, as a clip's does, so the same queue serves both.</summary>
public sealed unsafe class ImageSequenceSource : IVideoSource
{
    private readonly string _location;
    private readonly FrameRate _rate;
    private readonly ImageSequenceOptions _options;
    private readonly SourceStats _stats = new();
    private readonly FrameQueue _queue;
    private readonly FramePool _pool;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly TaskCompletionSource<bool> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private volatile MediaInfo _info = MediaInfo.Unknown;
    private volatile string _status = "opening";
    private volatile string _decoderName = "";
    private volatile bool _isOpen;
    private volatile bool _stop;
    private int _disposed;

    // ---- decode-thread state --------------------------------------------------------------------------
    private IReadOnlyList<string> _files = [];
    private StreamDecoder? _decoder;
    private AVCodecID _decoderCodec = AVCodecID.AV_CODEC_ID_NONE;
    private FrameWriter? _writer;
    private AVFrame* _frame;
    private long _next;
    private long _iteration;
    private bool _atEnd;
    private long _generation;

    public ImageSequenceSource(string location, FrameRate rate, ImageSequenceOptions? options = null, string? key = null)
    {
        if (!rate.IsValid)
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "a sequence needs a frame rate");
        _location = location;
        _rate = rate;
        _options = options ?? ImageSequenceOptions.Default;
        Key = key ?? location;
        var wake = new Action(() => _wake.Set());
        _queue = new FrameQueue(_options.Queue, _stats, wake);
        _pool = new FramePool(_queue.Capacity + 4, wake);
        _queue.SetLoopInitial(_options.Loop, _options.InNs, _options.OutNs);
        _thread = new Thread(Run) { IsBackground = true, Name = "sequence " + Path.GetFileName(location) };
        _thread.Start();
    }

    public string Key { get; }

    public MediaInfo Info => _info;

    public FrameRate Rate => _rate;

    /// <summary>The number of images (known once open).</summary>
    public int FrameCount => _files.Count;

    public Task<bool> Opened => _opened.Task;

    public SourceHealth Health => _stats.Health(_isOpen, _status, _decoderName, false);

    public VideoFrameLease? Acquire(long mediaNs) => _queue.Acquire(mediaNs);

    public void Prepare(long mediaNs, bool playing, double rate) => _queue.Prepare(mediaNs, playing, rate);

    public bool IsReadyAt(long mediaNs) => _queue.IsReadyAt(mediaNs);

    public bool WaitUntilReady(long mediaNs, TimeSpan timeout) => _queue.WaitUntilReady(mediaNs, timeout);

    public void SetLoop(bool loop, long inNs, long outNs) => _queue.SetLoop(loop, inNs, outNs);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop = true;
        _wake.Set();
        _queue.Close();
        if (!_thread.Join(TimeSpan.FromSeconds(10)))
        {
            // Stuck inside a decode: the thread frees its own objects when it returns; nothing is freed under it.
            _status = "closing (decoder busy)";
            return;
        }
        _pool.Dispose();
        _queue.Dispose();
        _wake.Dispose();
        _opened.TrySetResult(false);
    }

    private void Run()
    {
        try
        {
            _files = ImageSequence.Resolve(_location);
            if (_files.Count == 0)
            {
                _status = "no images match " + _location;
                _opened.TrySetResult(false);
                return;
            }
            _writer = new FrameWriter(PlanMode.Sequence);
            _frame = ffmpeg.av_frame_alloc();
            var first = MediaProbe.Probe(_files[0]);
            if (!first.Decodable)
            {
                _status = first.Message;
                _opened.TrySetResult(false);
                return;
            }
            _info = first.Info with
            {
                DurationNs = _rate.TimeOf(_files.Count),
                FrameRate = string.Create(CultureInfo.InvariantCulture, $"{_rate.Numerator}/{_rate.Denominator}"),
                Container = "sequence",
            };
            _decoderName = first.Decoder;
            _queue.SetDuration(_info.DurationNs);
            var start = Math.Max(0, _options.InNs);
            _queue.SetStart(start);
            _next = _rate.FrameAt(start);
            _status = "ready";
            _isOpen = true;
            _opened.TrySetResult(true);
            Loop();
        }
        catch (Exception e)
        {
            // The last guard of a thread: an exception here would end the process.
            _status = "decode failed: " + e.Message;
            _isOpen = false;
            _opened.TrySetResult(false);
        }
        finally
        {
            Close();
        }
    }

    // Frees the decode thread's FFmpeg objects, on the decode thread.
    private void Close()
    {
        _decoder?.Dispose();
        _decoder = null;
        _writer?.Dispose();
        _writer = null;
        if (_frame is not null)
        {
            var f = _frame;
            ffmpeg.av_frame_free(&f);
            _frame = null;
        }
    }

    private void Loop()
    {
        while (!_stop)
        {
            _wake.Reset();
            var control = _queue.Control();
            if (_queue.TakeSeek(out var target))
            {
                long file = target;
                _iteration = 0;
                if (control.Loop && target >= control.LoopInNs)
                {
                    _iteration = (target - control.LoopInNs) / control.LoopLengthNs;
                    file = target - _iteration * control.LoopLengthNs;
                }
                _next = Math.Clamp(_rate.FrameAt(file), 0, _files.Count - 1);
                _atEnd = false;
                continue;
            }
            if (_atEnd || !_queue.WantMore())
            {
                _wake.Wait(100);
                continue;
            }
            long inIndex = 0, outIndex = _files.Count;
            if (control.Loop)
            {
                inIndex = _rate.FrameAt(control.LoopInNs);
                outIndex = Math.Min(_files.Count, _rate.FrameAt(control.LoopOutNs - 1) + 1);
            }
            if (_next >= outIndex)
            {
                if (!control.Loop)
                {
                    _atEnd = true;
                    _status = "end";
                    _queue.MarkEnded(true);
                    continue;
                }
                _iteration++;
                _next = inIndex;
            }
            var t = _rate.TimeOf(_next) + (control.Loop ? _iteration * control.LoopLengthNs : 0);
            var dur = _rate.TimeOf(_next + 1) - _rate.TimeOf(_next);
            // A frame wholly behind the play head is not decoded at all (the decoder is catching up).
            if (control.HasConsumer && t + dur <= control.ConsumerNs && _next + 1 < outIndex)
            {
                if (control.Playing)
                    _stats.FrameDropped();
                _next++;
                continue;
            }
            if (!_queue.HasRoom())
            {
                _wake.Wait(100);
                continue;
            }
            DecodeAndPublish((int)_next, t, dur);
            _next++;
        }
    }

    private void DecodeAndPublish(int index, long t, long dur)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var input = MediaInput.Open(_files[index], findStreamInfo: false);
            var stream = input.BestStream(AVMediaType.AVMEDIA_TYPE_VIDEO);
            if (stream < 0)
                throw new MediaOpenException(MediaProblem.NoStream, "no picture in " + Path.GetFileName(_files[index]));
            var codec = input.Stream(stream)->codecpar->codec_id;
            if (_decoder is null || codec != _decoderCodec)
            {
                _decoder?.Dispose();
                _decoder = StreamDecoder.Open(input.Stream(stream), new DecoderOptions { AllowHardware = false });
                _decoderCodec = codec;
                _decoderName = _decoder.Name;
            }
            ffmpeg.avcodec_flush_buffers(_decoder.Context);
            ffmpeg.av_frame_unref(_frame);
            if (!FirstFrame.Decode(input, stream, _decoder, _frame))
                throw new MediaOpenException(MediaProblem.DecodeFailed, "no picture in " + Path.GetFileName(_files[index]));
            _stats.FrameDecoded();
            var layout = _writer!.LayoutFor(_frame, out var plan);
            FrameSlot? slot;
            while ((slot = _pool.TryRent(layout)) is null)
            {
                if (_stop)
                    return;
                _wake.Wait(50);
                _wake.Reset();
            }
            _writer.Write(_frame, slot, plan);
            slot.PtsNs = t;
            slot.DurationNs = dur;
            slot.Generation = ++_generation;
            slot.ArrivalNs = MonoClock.Instance.NowNs;
            _stats.DecodeTime(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            _queue.Publish(slot);
        }
        catch (MediaOpenException e)
        {
            // One unreadable image is a dropped frame (the one before it holds), not a stopped sequence.
            _stats.FrameDropped();
            _status = e.Message;
        }
        finally
        {
            ffmpeg.av_frame_unref(_frame);
        }
    }
}
