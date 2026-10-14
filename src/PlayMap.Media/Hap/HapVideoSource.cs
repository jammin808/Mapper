using System.Diagnostics;
using System.Globalization;
using FFmpeg.AutoGen;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Hap;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Media.Ffmpeg;
using PlayMap.Media.Frames;

namespace PlayMap.Media.Hap;

/// <summary>How a HAP clip is opened: the block families the renderer samples (a codec needing one it lacks does not open),
/// the decoded frames held, the read-ahead caps, the worker count, the loop, and the item's colour override (HAP HDR).</summary>
public sealed record HapSourceOptions
{
    /// <summary>What the renderer samples; until it says, the three families every desktop GL has.</summary>
    public BlockFormats Formats { get; init; } = BlockFormats.Bc1 | BlockFormats.Bc3 | BlockFormats.Bc4;

    public int QueueFrames { get; init; } = 4;
    public int ReadAheadMs { get; init; } = 250;
    public int ReadAheadMb { get; init; } = 192;

    /// <summary>Threads of the process's chunk pool when this source makes it (0 = cores − 1).</summary>
    public int Workers { get; init; }

    public bool Loop { get; init; }
    public long InNs { get; init; }
    public long OutNs { get; init; }

    public ColorOverride Color { get; init; } = ColorOverride.Auto;

    public static readonly HapSourceOptions Default = new();
}

/// <summary>A HAP clip decoded for the GPU: the frames stay block-compressed, and only the second stage (Snappy or a copy,
/// chunk by chunk) runs on the CPU.
/// <para>A read thread demuxes (libavformat) into a ring of packets it parses as they arrive (a refused packet carries its
/// words), reading ahead no more than <see cref="HapSourceOptions.ReadAheadMs"/> of frames and no more than
/// <see cref="HapSourceOptions.ReadAheadMb"/> of bytes; it loops and seeks the file. A decode thread takes packets in order,
/// drops one wholly before the play head without decompressing it, fans a frame's chunks out to the process's
/// <see cref="HapWorkers"/> (working chunks itself while it waits) and publishes the frame. A frame's time is its packet's, so
/// the frame shown for a media time is a pure function of that time. Every HAP frame is a keyframe: a seek reads from the frame
/// at or before the target.</para>
/// <para>Nothing allocates per frame once warm: packets, parse tables, slots, leases and chunk jobs are all reused. Disposal
/// stops both threads (a blocking read is interrupted) and frees the pooled buffers as their last leases end.</para></summary>
public sealed unsafe class HapVideoSource : IVideoSource
{
    private const long FallbackFrameNs = 40_000_000;
    private const int MaxReadErrors = 20;
    private const int CorruptRun = 10;
    private const int MaxRing = 64;

    private readonly string _path;
    private readonly HapSourceOptions _options;
    private readonly SourceStats _stats = new();
    private readonly FrameQueue _queue;
    private readonly FramePool _pool;
    private readonly FrameCountdown _countdown = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly ManualResetEventSlim _readWake = new(false);
    private readonly TaskCompletionSource<bool> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _readThread;
    private volatile Thread? _decodeThread;
    private readonly Lock _inputGate = new();
    private readonly Lock _ringGate = new();
    private MediaInput? _input;
    private volatile MediaInfo _info;
    private volatile string _status = "opening";
    private volatile bool _failed;
    private volatile bool _isOpen;
    private volatile bool _stop;
    private int _disposed;

    // ---- the ring (entries [head, head+count) are the decode thread's to read; the tail is the read thread's) -------------
    private Entry[] _ring = [];
    private int _head;
    private int _count;
    private long _ringBytes;
    private long _epoch;
    private bool _seekRequested;
    private long _seekTarget;
    private int _maxRingFrames;
    private long _maxRingBytes;

    // ---- facts the open learned (written before either loop starts) -----------------------------------------------------
    private HapCodec _codec;
    private int _width;
    private int _height;
    private int _stream = -1;
    private AVRational _tb;
    private long _startTs;
    private long _frameNs = FallbackFrameNs;
    private FrameRate _rate;
    private double _fps;
    private int _capFrames = 1;
    private long _capBytes;
    private HapReadAhead _plan;
    private HapHdrReading _hdr;
    private bool _alpha;
    private long _textureBytes;

    // ---- read-thread state ------------------------------------------------------------------------------------------------
    private long _iteration;
    private long _readEpoch;
    private bool _readAtEnd;
    private int _readErrors;
    private long _lastFileNs = -1;
    private long _lastDurNs;
    private long _readAllocated;
    private Entry? _held;

    // ---- decode-thread state ------------------------------------------------------------------------------------------------
    private long _generation;
    private bool _ended;
    private bool _gap;
    private FrameLayout? _layout;
    private FrameLayout? _reserved;
    private long _decodeAllocated;
    private long _corrupt;
    private int _corruptRun;
    private long _corruptFirst;
    private volatile string _lastRefusal = "";
    private volatile int _lastChunks;
    private long _chunksSeen;
    private long _rawSeen;
    private volatile PixelLayout _lastLayout;

    private sealed class Entry
    {
        public AVPacket* Packet;
        public readonly HapFrame Frame = new();
        public long Time;
        public long Duration;
        public long Index;
        public bool Eof;
        public bool Refused;
        public string Words = "";
        public int Bytes;
    }

    public HapVideoSource(string path, HapSourceOptions? options = null, string? key = null)
    {
        _path = path;
        _options = options ?? HapSourceOptions.Default;
        Key = key ?? path;
        _info = MediaInfo.Unknown;
        var queue = new QueueSettings
        {
            QueueFrames = Math.Max(2, _options.QueueFrames) + 2,
            KeepBehindNs = 34_000_000,
        };
        var wake = new Action(() => _wake.Set());
        _queue = new FrameQueue(queue, _stats, wake);
        _pool = new FramePool(_queue.Capacity + 4, wake);
        _queue.SetLoopInitial(_options.Loop, _options.InNs, _options.OutNs);
        _readThread = new Thread(ReadRun) { IsBackground = true, Name = "hap read " + System.IO.Path.GetFileName(path) };
        _readThread.Start();
    }

    public string Key { get; }

    public string Path => _path;

    public MediaInfo Info => _info;

    /// <summary>The codec the file's fourcc names (valid once <see cref="Opened"/> completes true).</summary>
    public HapCodec Codec => _codec;

    /// <summary>Completes true when the clip is open and decoding, false when it could not be opened (the words are in
    /// <see cref="Health"/>.Status).</summary>
    public Task<bool> Opened => _opened.Task;

    /// <summary>The read-ahead caps this clip got: frames by time, bytes by the byte cap.</summary>
    public HapReadAhead ReadAhead => _plan;

    /// <summary>The most packets and bytes the read-ahead ring has held.</summary>
    public (int Frames, long Bytes) ReadAheadPeak
    {
        get
        {
            lock (_ringGate)
                return (_maxRingFrames, _maxRingBytes);
        }
    }

    /// <summary>The byte cap of the read-ahead ring.</summary>
    public long ReadAheadCapBytes => _capBytes;

    /// <summary>The managed bytes the decode thread had allocated at its latest frame (flat once warm).</summary>
    internal long DecodeThreadAllocatedBytes => Interlocked.Read(ref _decodeAllocated);

    /// <summary>The managed bytes the read thread had allocated at its latest packet (flat once warm).</summary>
    internal long ReadThreadAllocatedBytes => Interlocked.Read(ref _readAllocated);

    public SourceHealth Health
    {
        get
        {
            var h = _stats.Health(_isOpen && !_failed, _status, "hap", false);
            if (!_isOpen)
                return h with { Path = DecodePath.HapGpu };
            var chunks = Interlocked.Read(ref _chunksSeen);
            var raw = Interlocked.Read(ref _rawSeen);
            var rawShare = chunks > 0 ? Math.Round(raw / (double)chunks, 3) : 0;
            var stage = chunks == 0 ? "" : raw == 0 ? "snappy" : raw == chunks ? "none" : "mixed";
            var texture = HapCodecs.TextureWord(_lastLayout);
            var facts = new HapFacts(HapCodecs.Name(_codec), texture, _lastChunks, rawShare, stage, h.DecodeMsP95, _textureBytes, "copy",
                Interlocked.Read(ref _corrupt), _lastRefusal, _codec == HapCodec.HapHdr ? _hdr.Words : "");
            return h with { Path = DecodePath.HapGpu, Hap = facts, BusBytesPerFrame = _textureBytes };
        }
    }

    public VideoFrameLease? Acquire(long mediaNs) => _queue.Acquire(mediaNs);

    public void Prepare(long mediaNs, bool playing, double rate) => _queue.Prepare(mediaNs, playing, rate);

    public bool IsReadyAt(long mediaNs) => _queue.IsReadyAt(mediaNs);

    /// <summary>Waits until the frame for the media time is decoded (preroll), or the timeout. Not for the render thread.</summary>
    public bool WaitUntilReady(long mediaNs, TimeSpan timeout) => _queue.WaitUntilReady(mediaNs, timeout);

    public void SetLoop(bool loop, long inNs, long outNs) => _queue.SetLoop(loop, inNs, outNs);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop = true;
        lock (_inputGate)
            _input?.Interrupt();
        _wake.Set();
        _readWake.Set();
        _queue.Close();
        // The decode thread is started by the read thread: read the field after the read thread has ended.
        var readDone = _readThread.Join(TimeSpan.FromSeconds(10));
        var decode = _decodeThread;
        var decodeDone = decode is null || decode.Join(TimeSpan.FromSeconds(10));
        if (!readDone || !decodeDone)
        {
            // A thread is stuck inside a native call; it frees its own objects when it returns. Nothing is freed under it.
            _status = "closing (reader busy)";
            return;
        }
        FreeRing();
        _pool.Dispose();
        _queue.Dispose();
        _wake.Dispose();
        _readWake.Dispose();
        _countdown.Dispose();
        _opened.TrySetResult(false);
    }

    // ---- the read thread ------------------------------------------------------------------------------------------------

    private void ReadRun()
    {
        try
        {
            if (!Open())
                return;
            var decode = new Thread(DecodeRun) { IsBackground = true, Name = "hap decode " + System.IO.Path.GetFileName(_path) };
            _decodeThread = decode;
            decode.Start();
            ReadLoop();
        }
        catch (Exception e)
        {
            // The last guard of a thread: an exception here would end the process. The source reports it and stops.
            Fail("read failed: " + e.Message);
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
            var s = input.Stream(_stream);
            var p = s->codecpar;
            if (!HapCodecs.TryFromTag(p->codec_tag, out _codec))
                throw new MediaOpenException(MediaProblem.DecoderNotAvailable, System.IO.Path.GetFileName(_path) + " is not a HAP stream");
            var missing = HapCodecs.Missing(_codec, _options.Formats);
            if (missing.Length > 0)
                throw new MediaOpenException(MediaProblem.DecoderNotAvailable, NotOnThisGpu(_codec, missing));
            _width = p->width;
            _height = p->height;
            if (_width < 1 || _height < 1)
                throw new MediaOpenException(MediaProblem.CannotOpen, "the HAP stream does not say its picture size");
            input.KeepOnly(_stream);
            _tb = s->time_base;
            _startTs = input.StartTs(_stream);
            var fr = input.FrameRate(_stream);
            _rate = fr.num > 0 && fr.den > 0 ? new FrameRate(fr.num, fr.den) : default;
            _frameNs = _rate.IsValid ? _rate.FrameDurationNs : FallbackFrameNs;
            _fps = _rate.IsValid ? fr.num / (double)fr.den : 1e9 / FallbackFrameNs;
            _textureBytes = HapCodecs.FrameBytes(_codec, _width, _height);
            _plan = HapBudget.ReadAhead(_options.ReadAheadMs, _options.ReadAheadMb, _fps, _textureBytes);
            _capFrames = Math.Clamp(HapBudget.ReadAhead(_options.ReadAheadMs, _options.ReadAheadMb, _fps, 0).Frames, 1, MaxRing);
            _capBytes = (long)(Math.Max(1, _options.ReadAheadMb) * HapBudget.Mb);
            _alpha = _codec is HapCodec.HapAlpha or HapCodec.HapQAlpha or HapCodec.HapR;
            if (_codec == HapCodec.HapHdr)
                _hdr = HapHdr.Read(_options.Color, (int)p->color_trc, WhiteTag(s));
            _lastLayout = HapCodecs.Layout(_codec, HapCodecs.FirstTexture(_codec));
            // One more entry than the cap: the tail is where the next packet is read while the ring is full.
            _ring = new Entry[_capFrames + 1];
            for (var i = 0; i < _ring.Length; i++)
                _ring[i] = new Entry { Packet = ffmpeg.av_packet_alloc() };
            _queue.SetDuration(_info.DurationNs);
            var start = Math.Max(0, _options.InNs);
            _queue.SetStart(start);
            if (start > 0)
                SeekFile(start);
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

    /// <summary>Why a HAP item does not play on a renderer that lacks a block family, and the line that fixes it.</summary>
    public static string NotOnThisGpu(HapCodec codec, string missing) =>
        codec is HapCodec.HapR or HapCodec.HapHdr
            ? $"{HapCodecs.Words(codec)} needs BPTC textures, which this GPU's GL does not offer — optimise to HAP Q for this node"
            : $"this GPU's GL lacks {missing}; FFmpeg decodes {HapCodecs.Words(codec)} on the CPU";

    private static double WhiteTag(AVStream* s)
    {
        var e = ffmpeg.av_dict_get(s->metadata, HapHdr.WhiteTag, null, 0);
        return e is not null && double.TryParse(FfmpegUtil.Text(e->value), NumberStyles.Float, CultureInfo.InvariantCulture, out var nits) ? nits : 0;
    }

    private void Fail(string why)
    {
        _status = why;
        _failed = true;
        _opened.TrySetResult(false);
    }

    private void ReadLoop()
    {
        _readWake.Reset();
        _readWake.Wait(2);
        while (!_stop)
        {
            _readWake.Reset();
            if (TakeSeekRequest(out var target, out var epoch))
            {
                var control = _queue.Control();
                long iteration = 0, file = target;
                if (control.Loop && control.LoopLengthNs > 0 && target >= control.LoopInNs)
                {
                    iteration = (target - control.LoopInNs) / control.LoopLengthNs;
                    file = target - iteration * control.LoopLengthNs;
                }
                if (_held is { } stale)
                {
                    ffmpeg.av_packet_unref(stale.Packet);
                    _held = null;
                }
                _iteration = iteration;
                _readEpoch = epoch;
                SeekFile(file);
                continue;
            }
            if (_held is { } held)
            {
                if (!Commit(held))
                    _readWake.Wait(100);
                continue;
            }
            if (_readAtEnd || !TailFree(out var tail))
            {
                _readWake.Wait(100);
                continue;
            }
            ReadOne(tail);
        }
    }

    private bool TakeSeekRequest(out long target, out long epoch)
    {
        lock (_ringGate)
        {
            target = _seekTarget;
            epoch = _epoch;
            if (!_seekRequested)
                return false;
            _seekRequested = false;
            return true;
        }
    }

    // The tail entry is free while the ring holds fewer packets than the time cap allows. A packet read into it is held there
    // until it fits under the byte cap (or the ring is empty), so the ring never holds more bytes than the cap allows.
    private bool TailFree(out Entry tail)
    {
        lock (_ringGate)
        {
            tail = _ring[(_head + _count) % _ring.Length];
            return _count < _capFrames;
        }
    }

    private void ReadOne(Entry e)
    {
        var fmt = _input!.Format;
        var r = ffmpeg.av_read_frame(fmt, e.Packet);
        if (r < 0)
        {
            if (_input.Interrupted)
                return;
            if (r != ffmpeg.AVERROR_EOF && ++_readErrors <= MaxReadErrors)
                return;
            OnEnd(e);
            return;
        }
        _readErrors = 0;
        if (e.Packet->stream_index != _stream)
        {
            ffmpeg.av_packet_unref(e.Packet);
            return;
        }
        var control = _queue.Control();
        var ts = e.Packet->pts != ffmpeg.AV_NOPTS_VALUE ? e.Packet->pts : e.Packet->dts;
        var fileNs = ts == ffmpeg.AV_NOPTS_VALUE
            ? (_lastFileNs < 0 ? 0 : _lastFileNs + Math.Max(1, _lastDurNs))
            : Snap(FfmpegUtil.ToNs(ts - _startTs, _tb));
        // The end is rounded the way the start is, so a frame ends on the nanosecond its successor starts.
        var dur = ts != ffmpeg.AV_NOPTS_VALUE && e.Packet->duration > 0
            ? Math.Max(1, Snap(FfmpegUtil.ToNs(ts - _startTs + e.Packet->duration, _tb)) - fileNs)
            : _frameNs;
        _lastFileNs = fileNs;
        _lastDurNs = dur;
        if (control.Loop && fileNs >= control.LoopOutNs)
        {
            ffmpeg.av_packet_unref(e.Packet);
            Restart(control);
            return;
        }
        e.Time = control.Loop ? fileNs + _iteration * control.LoopLengthNs : fileNs;
        e.Duration = dur;
        e.Index = (fileNs + _frameNs / 2) / Math.Max(1, _frameNs);
        e.Eof = false;
        e.Bytes = e.Packet->size;
        e.Refused = !e.Frame.TryParse(new ReadOnlySpan<byte>(e.Packet->data, e.Packet->size), _width, _height, _codec, out _);
        e.Words = e.Refused ? e.Frame.Words(e.Index) : "";
        _ = Commit(e);
        Interlocked.Exchange(ref _readAllocated, GC.GetAllocatedBytesForCurrentThread());
    }

    private void OnEnd(Entry e)
    {
        if (_info.DurationNs <= 0 && _lastFileNs >= 0)
        {
            // The container did not say how long the clip is; now it is known.
            _info = _info with { DurationNs = _lastFileNs + _lastDurNs };
            _queue.SetDuration(_info.DurationNs);
        }
        var control = _queue.Control();
        if (control.Loop)
        {
            Restart(control);
            return;
        }
        e.Eof = true;
        e.Bytes = 0;
        e.Refused = false;
        _ = Commit(e);
        _readAtEnd = true;
    }

    private void Restart(in QueueControl control)
    {
        _iteration++;
        SeekFile(control.LoopInNs);
    }

    // Appends the tail entry when it fits under the byte cap (false: it stays held), unless a seek was asked for since this
    // packet's read began (then it belongs to the old timeline and is let go).
    private bool Commit(Entry e)
    {
        lock (_ringGate)
        {
            if (_seekRequested || _readEpoch != _epoch)
            {
                ffmpeg.av_packet_unref(e.Packet);
                _held = null;
                return true;
            }
            if (_count > 0 && _ringBytes + e.Bytes > _capBytes)
            {
                _held = e;
                return false;
            }
            _held = null;
            _count++;
            _ringBytes += e.Bytes;
            _maxRingFrames = Math.Max(_maxRingFrames, _count);
            _maxRingBytes = Math.Max(_maxRingBytes, _ringBytes);
        }
        _wake.Set();
        return true;
    }

    // Snaps a time within a millisecond of the clip's frame grid onto it: container time bases round frame times, and a frame
    // must start on the nanosecond the frame maths gives.
    private long Snap(long ns)
    {
        if (!_rate.IsValid)
            return ns;
        var grid = _rate.TimeOf(_rate.FrameAt(ns + _frameNs / 2));
        return Math.Abs(ns - grid) <= Durations.NsPerMs ? grid : ns;
    }

    private void SeekFile(long fileNs)
    {
        var fmt = _input!.Format;
        var ts = FfmpegUtil.FromNs(Math.Max(0, fileNs), _tb) + _startTs;
        // Every HAP frame is a keyframe: the frame at or before the target is the one that covers it.
        var r = ffmpeg.avformat_seek_file(fmt, _stream, long.MinValue, ts, ts, 0);
        if (r < 0)
            r = ffmpeg.av_seek_frame(fmt, _stream, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (r < 0)
            ffmpeg.av_seek_frame(fmt, _stream, _startTs, ffmpeg.AVSEEK_FLAG_BACKWARD);
        _readAtEnd = false;
        _readErrors = 0;
        _lastFileNs = -1;
    }

    private void Close()
    {
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

    // ---- the decode thread ------------------------------------------------------------------------------------------------

    private void DecodeRun()
    {
        try
        {
            DecodeLoop();
        }
        catch (Exception e)
        {
            // The last guard of a thread: the source reports it and stops.
            Fail("decode failed: " + e.Message);
        }
    }

    private void DecodeLoop()
    {
        // One real block before the first frame (the event makes its lock object on the first wait that blocks).
        _wake.Reset();
        _wake.Wait(2);
        while (!_stop)
        {
            _wake.Reset();
            var control = _queue.Control();
            if (_queue.TakeSeek(out var target))
            {
                RequestReadSeek(target);
                continue;
            }
            if (_ended || !_queue.WantMore())
            {
                _wake.Wait(100);
                continue;
            }
            if (!Peek(out var e))
            {
                _wake.Wait(100);
                continue;
            }
            if (!Work(e, control))
            {
                _wake.Wait(20);
                continue;
            }
            Release();
        }
    }

    private void RequestReadSeek(long target)
    {
        lock (_ringGate)
        {
            _epoch++;
            _seekRequested = true;
            _seekTarget = target;
            while (_count > 0)
            {
                var e = _ring[_head];
                ffmpeg.av_packet_unref(e.Packet);
                _head = (_head + 1) % _ring.Length;
                _count--;
            }
            _ringBytes = 0;
        }
        _ended = false;
        _status = "ready";
        _readWake.Set();
    }

    private bool Peek(out Entry e)
    {
        lock (_ringGate)
        {
            e = _ring[_head];
            return _count > 0;
        }
    }

    private void Release()
    {
        lock (_ringGate)
        {
            if (_count == 0)
                return;
            var e = _ring[_head];
            ffmpeg.av_packet_unref(e.Packet);
            _ringBytes -= e.Bytes;
            _head = (_head + 1) % _ring.Length;
            _count--;
        }
        _readWake.Set();
    }

    // Works one packet; false when no slot is free (the packet stays at the head for the next try).
    private bool Work(Entry e, in QueueControl control)
    {
        if (e.Eof)
        {
            _ended = true;
            _status = "end";
            _queue.MarkEnded(true);
            return true;
        }
        if (e.Refused)
        {
            Corrupt(e.Index, e.Words);
            return true;
        }
        // A frame wholly before the play head is the cheapest catch-up there is: dropped without decompressing.
        if (control.HasConsumer && e.Time + e.Duration <= control.ConsumerNs)
        {
            if (control.Playing)
                _stats.FrameDropped();
            _gap = true;
            return true;
        }
        var t0 = Stopwatch.GetTimestamp();
        var f = e.Frame;
        var layout = HapCodecs.Layout(_codec, f.Texture(0).Kind);
        if (_layout is null || _layout.Layout != layout)
            _layout = FrameLayout.For(layout, _width, _height);
        if (!_layout.Equals(_reserved))
        {
            _pool.Reserve(_layout);
            _reserved = _layout;
        }
        var slot = _pool.TryRent(_layout);
        if (slot is null)
            return false;
        _stats.FrameDecoded();
        var chunks = f.AllChunks.Length;
        _countdown.Arm(chunks);
        var data = e.Packet->data;
        var workers = HapWorkers.Shared(_options.Workers);
        ChunkJob first = default;
        var k = 0;
        for (var t = 0; t < f.TextureCount; t++)
        {
            var plane = slot.PlaneData(t);
            foreach (var c in f.Chunks(t))
            {
                var job = new ChunkJob((nint)(data + c.SourceOffset), c.SourceLength, (nint)(plane + c.TargetOffset), c.TargetLength, c.Compressor, _countdown);
                if (k++ == 0)
                    first = job;
                else if (!workers.Post(job))
                    HapWorkers.Run(job);
            }
        }
        // The decode thread works its first chunk, then helps the pool until its own frame is done.
        HapWorkers.Run(first);
        while (_countdown.Left > 0)
        {
            if (!workers.TryHelp())
                _countdown.Wait();
        }
        Interlocked.Add(ref _chunksSeen, chunks);
        Interlocked.Add(ref _rawSeen, f.RawChunks);
        _lastChunks = chunks;
        _lastLayout = layout;
        if (_countdown.Failed)
        {
            slot.Release();
            Corrupt(e.Index, "frame " + e.Index.ToString(CultureInfo.InvariantCulture) + " has a chunk whose Snappy data is corrupt");
            return true;
        }
        slot.PtsNs = e.Time;
        slot.DurationNs = e.Duration;
        slot.Generation = ++_generation;
        slot.ArrivalNs = MonoClock.Instance.NowNs;
        slot.HasAlpha = _alpha;
        slot.Range = ColorRange.Full;
        if (_codec == HapCodec.HapHdr)
        {
            slot.Transfer = _hdr.Transfer;
            slot.HdrScale = _hdr.Scale;
        }
        _corruptRun = 0;
        _stats.DecodeTime(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        if (_status != "ready")
            _status = "ready";
        _queue.Publish(slot, _gap);
        _gap = false;
        Interlocked.Exchange(ref _decodeAllocated, GC.GetAllocatedBytesForCurrentThread());
        return true;
    }

    private void Corrupt(long index, string words)
    {
        _stats.FrameDropped();
        Interlocked.Increment(ref _corrupt);
        _lastRefusal = words;
        if (_corruptRun++ == 0)
            _corruptFirst = index;
        if (_corruptRun >= CorruptRun)
            _status = string.Create(CultureInfo.InvariantCulture, $"corrupt: {words} (frames {_corruptFirst}–{index})");
    }

    private void FreeRing()
    {
        foreach (var e in _ring)
        {
            var p = e.Packet;
            if (p is not null)
                ffmpeg.av_packet_free(&p);
            e.Packet = null;
        }
    }
}
