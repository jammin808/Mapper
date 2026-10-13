using System.Diagnostics;
using FFmpeg.AutoGen;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Media.Ffmpeg;
using PlayMap.Media.Frames;

namespace PlayMap.Media.Sources;

/// <summary>A live input to open: an FFmpeg device (or a network stream).</summary>
public sealed record CaptureOptions
{
    /// <summary>FFmpeg's input format: "v4l2", "dshow", "avfoundation", "lavfi"…; null = the platform's capture
    /// format (<see cref="CaptureDevices.PlatformFormat"/>); empty = let FFmpeg probe (a network url).</summary>
    public string? Format { get; init; }

    /// <summary>The device: "/dev/video0", "video=Cam Link 4K", "0:none", a filter graph for lavfi, or a url.</summary>
    public string Device { get; init; } = "";

    /// <summary>Device options: video_size "1920x1080", framerate "60", input_format "mjpeg", pixel_format…</summary>
    public IReadOnlyDictionary<string, string>? DeviceOptions { get; init; }

    /// <summary>Compressed cameras (MJPEG, H.264) may use the hardware decoder; raw ones never need it.</summary>
    public bool AllowHardware { get; init; }
}

/// <summary>A live picture: a capture device read through libavdevice with every buffer the path allows turned
/// off (no demuxer buffering, packets flushed, no stream analysis, slice threads and low delay in the decoder),
/// the newest frame kept and the older one let go — the latest frame wins. <see cref="Acquire"/> answers the
/// newest frame whatever the media time; each frame is stamped on the monotonic clock when its packet arrived
/// (<see cref="MediaFrameLease.ArrivalNs"/>), so the renderer measures the app's share of the glass-to-glass
/// latency from there.</summary>
public sealed unsafe class CaptureSource : IVideoSource
{
    private const int PoolFrames = 6;

    private readonly CaptureOptions _options;
    private readonly SourceStats _stats = new();
    private readonly FramePool _pool;
    private FrameLayout? _reserved;
    private readonly LeasePool _leases = new(8);
    private readonly Lock _gate = new();
    private readonly Lock _inputGate = new();
    private readonly ManualResetEventSlim _arrived = new(false);
    private readonly TaskCompletionSource<bool> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private FrameSlot? _newest;
    private MediaInput? _input;
    private volatile MediaInfo _info = MediaInfo.Unknown;
    private volatile string _status = "opening";
    private volatile string _decoderName = "";
    private volatile bool _hardware;
    private volatile bool _live;
    private volatile bool _stop;
    private long _generation;
    private int _disposed;

    public CaptureSource(CaptureOptions options, string? key = null)
    {
        _options = options;
        Key = key ?? options.Device;
        _pool = new FramePool(PoolFrames);
        _thread = new Thread(Run) { IsBackground = true, Name = "capture " + options.Device };
        _thread.Start();
    }

    public string Key { get; }

    public MediaInfo Info => _info;

    /// <summary>Completes true when the device opened and its decoder started, false when it could not (the
    /// reason in <see cref="Health"/>.Status).</summary>
    public Task<bool> Opened => _opened.Task;

    public SourceHealth Health => _stats.Health(_live, _status, _decoderName, _hardware);

    /// <summary>How old the newest frame is now (monotonic ns since its arrival), or −1 before the first.</summary>
    public long NewestAgeNs
    {
        get
        {
            lock (_gate)
                return _newest is null ? -1 : MonoClock.Instance.NowNs - _newest.ArrivalNs;
        }
    }

    /// <summary>The newest frame, whatever the media time (a live source has only now).</summary>
    public VideoFrameLease? Acquire(long mediaNs)
    {
        lock (_gate)
        {
            if (_newest is null)
                return null;
            _newest.AddRef();
            _newest.Shown = true;
            return _leases.Rent(_newest);
        }
    }

    public void Prepare(long mediaNs, bool playing, double rate)
    {
        // A live source cannot be cued or sped up; there is nothing to prepare.
    }

    public bool IsReadyAt(long mediaNs)
    {
        lock (_gate)
            return _newest is not null;
    }

    /// <summary>Waits until a frame newer than the given generation arrives, or the timeout (not for the render
    /// thread).</summary>
    public bool WaitForFrame(long afterGeneration, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            _arrived.Reset();
            lock (_gate)
            {
                if (_newest is not null && _newest.Generation > afterGeneration)
                    return true;
            }
            var left = deadline - Environment.TickCount64;
            if (left <= 0 || _stop)
                return false;
            _arrived.Wait((int)Math.Min(left, 50));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop = true;
        lock (_inputGate)
            _input?.Interrupt();
        if (!_thread.Join(TimeSpan.FromSeconds(10)))
        {
            // A device read that ignores the interrupt (a stalled driver): the thread frees its own objects when it
            // returns; nothing is freed under it.
            _status = "closing (device busy)";
            return;
        }
        FrameSlot? newest;
        lock (_gate)
        {
            newest = _newest;
            _newest = null;
        }
        newest?.Release();
        _pool.Dispose();
        _arrived.Dispose();
        _opened.TrySetResult(false);
    }

    private void Run()
    {
        StreamDecoder? decoder = null;
        FrameWriter? writer = null;
        var packet = ffmpeg.av_packet_alloc();
        var frame = ffmpeg.av_frame_alloc();
        try
        {
            FfmpegLoader.EnsureLoaded();
            var format = _options.Format ?? CaptureDevices.PlatformFormat;
            var input = MediaInput.Open(_options.Device, string.IsNullOrEmpty(format) ? null : format, _options.DeviceOptions, lowLatency: true);
            lock (_inputGate)
            {
                if (_stop)
                {
                    input.Dispose();
                    return;
                }
                _input = input;
            }
            var stream = input.BestStream(AVMediaType.AVMEDIA_TYPE_VIDEO);
            _info = MediaInfoReader.Read(input, stream, -1, null);
            if (stream < 0)
                throw new MediaOpenException(MediaProblem.NoStream, "no video from " + _options.Device);
            input.KeepOnly(stream);
            decoder = StreamDecoder.Open(input.Stream(stream), new DecoderOptions { AllowHardware = _options.AllowHardware, LowDelay = true });
            _decoderName = decoder.Name;
            _hardware = decoder.Hardware;
            writer = new FrameWriter(PlanMode.Video);
            _status = "live";
            _live = true;
            _opened.TrySetResult(true);
            Read(input, stream, decoder, writer, packet, frame);
        }
        catch (Exception e) when (e is MediaOpenException or FfmpegUnavailableException)
        {
            _status = e.Message;
            _opened.TrySetResult(false);
        }
        catch (Exception e)
        {
            // The last guard of a thread: an exception here would end the process.
            _status = "capture failed: " + e.Message;
            _opened.TrySetResult(false);
        }
        finally
        {
            _live = false;
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
            writer?.Dispose();
            decoder?.Dispose();
            Close();
        }
    }

    // Frees the device input, on the capture thread (the owner); Dispose only interrupts it.
    private void Close()
    {
        MediaInput? input;
        lock (_inputGate)
        {
            input = _input;
            _input = null;
        }
        input?.Dispose();
    }

    private void Read(MediaInput input, int stream, StreamDecoder decoder, FrameWriter writer, AVPacket* packet, AVFrame* frame)
    {
        var ctx = decoder.Context;
        var errors = 0;
        while (!_stop)
        {
            var r = ffmpeg.av_read_frame(input.Format, packet);
            if (r < 0)
            {
                if (_stop || input.Interrupted)
                    return;
                if (r == FfmpegUtil.ErrorAgain)
                    continue;
                if (r == ffmpeg.AVERROR_EOF || ++errors > 50)
                {
                    _status = r == ffmpeg.AVERROR_EOF ? "ended" : "device lost: " + FfmpegUtil.ErrorText(r);
                    return;
                }
                continue;
            }
            errors = 0;
            var arrival = MonoClock.Instance.NowNs;
            var started = Stopwatch.GetTimestamp();
            if (packet->stream_index == stream)
                ffmpeg.avcodec_send_packet(ctx, packet);
            ffmpeg.av_packet_unref(packet);
            while (ffmpeg.avcodec_receive_frame(ctx, frame) == 0)
            {
                _stats.FrameDecoded();
                Publish(decoder, writer, frame, arrival, started);
                ffmpeg.av_frame_unref(frame);
            }
        }
    }

    private void Publish(StreamDecoder decoder, FrameWriter writer, AVFrame* frame, long arrival, long started)
    {
        AVFrame* src;
        try
        {
            src = decoder.TransferIfHardware(frame);
        }
        catch (FfmpegException)
        {
            // A surface that cannot be read back is one frame lost; the next one replaces it.
            _stats.FrameDropped();
            return;
        }
        var layout = writer.LayoutFor(src, out var plan);
        if (!layout.Equals(_reserved))
        {
            _pool.Reserve(layout);
            _reserved = layout;
        }
        var slot = _pool.TryRent(layout);
        if (slot is null)
        {
            // Every buffer is held by the renderer: this frame is dropped, the newest still stands.
            _stats.FrameDropped();
            return;
        }
        writer.Write(src, slot, plan);
        slot.PtsNs = arrival;
        slot.ArrivalNs = arrival;
        slot.DurationNs = 0;
        slot.Generation = ++_generation;
        _stats.DecodeTime(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        FrameSlot? previous;
        lock (_gate)
        {
            previous = _newest;
            _newest = slot;
        }
        if (previous is not null)
        {
            if (!previous.Shown)
                _stats.FrameDropped();
            previous.Release();
        }
        _arrived.Set();
    }
}

/// <summary>A capture device FFmpeg can list: its input format, name (what <see cref="CaptureOptions.Device"/>
/// takes), description, media types, and the formats it offers where FFmpeg can enumerate them.</summary>
public sealed record CaptureDevice(string Format, string Name, string Description, IReadOnlyList<string> MediaTypes, IReadOnlyList<string> Formats);

/// <summary>Lists capture devices through libavdevice (v4l2 on Linux, dshow on Windows; avfoundation lists only
/// through its log, so it is not enumerated here).</summary>
public static unsafe class CaptureDevices
{
    /// <summary>The platform's capture format: dshow, avfoundation or v4l2.</summary>
    public static string PlatformFormat =>
        OperatingSystem.IsWindows() ? "dshow" : OperatingSystem.IsMacOS() ? "avfoundation" : "v4l2";

    /// <summary>The video input devices this FFmpeg build has (v4l2, lavfi, x11grab…).</summary>
    public static IReadOnlyList<string> InputFormats()
    {
        var list = new List<string>();
        if (!FfmpegLoader.TryLoad(out _) || FfmpegLoader.Status?.DevicesAvailable != true)
            return list;
        AVInputFormat* f = null;
        while ((f = ffmpeg.av_input_video_device_next(f)) is not null)
            list.Add(FfmpegUtil.Text(f->name));
        return list;
    }

    /// <summary>The devices of a format (the platform's when null); empty when none are present or the format
    /// cannot list. With <paramref name="withFormats"/>, each device's offered formats are read too (v4l2,
    /// dshow).</summary>
    public static IReadOnlyList<CaptureDevice> List(string? format = null, bool withFormats = false)
    {
        var result = new List<CaptureDevice>();
        if (!FfmpegLoader.TryLoad(out _) || FfmpegLoader.Status?.DevicesAvailable != true)
            return result;
        var name = format ?? PlatformFormat;
        var input = ffmpeg.av_find_input_format(name);
        if (input is null)
            return result;
        AVDeviceInfoList* devices = null;
        if (ffmpeg.avdevice_list_input_sources(input, null, null, &devices) < 0 || devices is null)
        {
            ffmpeg.avdevice_free_list_devices(&devices);
            return result;
        }
        try
        {
            for (var i = 0; i < devices->nb_devices; i++)
            {
                var d = devices->devices[i];
                var types = new List<string>();
                for (var t = 0; t < d->nb_media_types; t++)
                    types.Add(ffmpeg.av_get_media_type_string(d->media_types[t]) ?? "");
                var deviceName = FfmpegUtil.Text(d->device_name);
                result.Add(new CaptureDevice(name, deviceName, FfmpegUtil.Text(d->device_description), types,
                    withFormats ? Formats(name, deviceName) : []));
            }
        }
        finally
        {
            ffmpeg.avdevice_free_list_devices(&devices);
        }
        return result;
    }

    /// <summary>The formats a device offers, as FFmpeg words them ("Raw : yuyv422 : YUYV 4:2:2 : 640x480
    /// 1280x720"), read from the device's own listing (v4l2's list_formats, dshow's list_options). Empty where the
    /// format cannot list or the device cannot be opened.</summary>
    public static IReadOnlyList<string> Formats(string format, string device)
    {
        var option = format switch
        {
            "v4l2" or "video4linux2" => ("list_formats", "all"),
            "dshow" => ("list_options", "true"),
            _ => ((string, string)?)null,
        };
        if (option is null || !FfmpegLoader.TryLoad(out _))
            return [];
        var lines = new List<string>();
        using (FfmpegLog.Capture(out var captured))
        {
            var listed = false;
            try
            {
                MediaInput.Open(device, format,
                    new Dictionary<string, string>(StringComparer.Ordinal) { [option.Value.Item1] = option.Value.Item2 }, findStreamInfo: false).Dispose();
            }
            catch (MediaOpenException e)
            {
                // Listing ends the open on purpose: FFmpeg prints the formats and exits (AVERROR_EXIT). Any other
                // failure (no such device) means there is no listing.
                listed = e.Message.Contains(FfmpegUtil.ErrorText(ffmpeg.AVERROR_EXIT), StringComparison.Ordinal);
            }
            if (!listed)
                return lines;
            foreach (var line in captured)
            {
                var text = line.Text;
                var close = text.IndexOf("] ", StringComparison.Ordinal);
                if (text.StartsWith('[') && close > 0)
                    text = text[(close + 2)..];
                text = text.Trim();
                if (text.Length > 0)
                    lines.Add(text);
            }
        }
        return lines;
    }
}
