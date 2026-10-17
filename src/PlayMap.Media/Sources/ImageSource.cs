using FFmpeg.AutoGen;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Media.Ffmpeg;
using PlayMap.Media.Frames;

namespace PlayMap.Media.Sources;

/// <summary>A still: any image FFmpeg reads (PNG, JPEG, TIFF, EXR, WebP, DPX…) decoded once, off the caller's
/// thread, into one RGBA frame — 8-bit, or half floats when the image is deeper than 8 bits or float (EXR keeps
/// its values above 1). Every acquire answers that frame whatever the time; it is ready once decoded.</summary>
public sealed unsafe class ImageSource : IVideoSource
{
    private readonly string _path;
    private readonly FramePool _pool = new(1);
    private readonly LeasePool _leases = new(8);
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource<bool> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FrameSlot? _slot;
    private volatile MediaInfo _info = MediaInfo.Unknown;
    private volatile string _status = "decoding";
    private volatile string _decoder = "";
    private double _decodeMs;
    private bool _disposed;

    public ImageSource(string path, string? key = null)
    {
        _path = path;
        Key = key ?? path;
        // On the pool, not the compute threads: the air waits for this picture, and a job queued on the compute threads waits behind
        // a solve that holds them all for its whole length (StillBesideComputeTests measures it).
        _ = Task.Run(Decode);
    }

    public string Key { get; }

    public MediaInfo Info => _info;

    /// <summary>Completes true once the picture is decoded, false when it cannot be (the reason in
    /// <see cref="Health"/>.Status).</summary>
    public Task<bool> Opened => _opened.Task;

    public SourceHealth Health
    {
        get
        {
            lock (_gate)
                return new SourceHealth(_slot is not null, _status, _slot is null ? 0 : 1, 0, 0, Math.Round(_decodeMs, 3), _decoder, false);
        }
    }

    public VideoFrameLease? Acquire(long mediaNs)
    {
        lock (_gate)
        {
            if (_slot is null || _disposed)
                return null;
            _slot.AddRef();
            _slot.Shown = true;
            return _leases.Rent(_slot);
        }
    }

    public void Prepare(long mediaNs, bool playing, double rate)
    {
        // A still has one picture for every time: nothing to move.
    }

    public bool IsReadyAt(long mediaNs)
    {
        lock (_gate)
            return _slot is not null && !_disposed;
    }

    public void Dispose()
    {
        FrameSlot? slot;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            slot = _slot;
            _slot = null;
        }
        slot?.Release();
        _pool.Dispose();
        _opened.TrySetResult(false);
    }

    private void Decode()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var slot = DecodeStill(_path, _pool, out var info, out var decoder);
            _decodeMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _info = info;
            _decoder = decoder;
            if (slot is null)
            {
                _status = "closed";
                _opened.TrySetResult(false);
                return;
            }
            slot.Generation = 1;
            slot.ArrivalNs = MonoClock.Instance.NowNs;
            lock (_gate)
            {
                if (!_disposed)
                {
                    _slot = slot;
                    slot = null;
                    _status = "ready";
                }
            }
            slot?.Release();
            _opened.TrySetResult(_status == "ready");
        }
        catch (Exception e) when (e is MediaOpenException or FfmpegUnavailableException or FfmpegException)
        {
            _status = e.Message;
            _opened.TrySetResult(false);
        }
        catch (Exception e)
        {
            // The last guard of a pool task: report, never throw into the pool.
            _status = "decode failed: " + e.Message;
            _opened.TrySetResult(false);
        }
    }

    /// <summary>Decodes an image file's first picture into a slot of the pool (RGBA or half-float RGBA). Null
    /// when the pool is closed.</summary>
    internal static FrameSlot? DecodeStill(string path, FramePool pool, out MediaInfo info, out string decoderName)
    {
        using var input = MediaInput.Open(path);
        var stream = input.BestStream(AVMediaType.AVMEDIA_TYPE_VIDEO);
        info = MediaInfoReader.Read(input, stream, -1, path) with { DurationNs = 0, FrameRate = "" };
        if (stream < 0)
            throw new MediaOpenException(MediaProblem.NoStream, "no picture in " + Path.GetFileName(path));
        input.KeepOnly(stream);
        using var decoder = StreamDecoder.Open(input.Stream(stream), new DecoderOptions { AllowHardware = false });
        decoderName = decoder.Name;
        using var writer = new FrameWriter(PlanMode.Still);
        var frame = ffmpeg.av_frame_alloc();
        try
        {
            if (!FirstFrame.Decode(input, stream, decoder, frame))
                throw new MediaOpenException(MediaProblem.DecodeFailed, decoder.Name + " produced no picture");
            var layout = writer.LayoutFor(frame, out var plan);
            var slot = pool.TryRent(layout);
            if (slot is null)
                return null;
            writer.Write(frame, slot, plan);
            info = info with { PixelFormat = FrameFormats.Name((AVPixelFormat)frame->format), HasAlpha = plan.HasAlpha };
            return slot;
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
        }
    }
}
