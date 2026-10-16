using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace PlayMap.Media.Ffmpeg;

/// <summary>Why a media input could not be opened or decoded, in words an operator reads before the show.</summary>
public enum MediaProblem
{
    None,
    /// <summary>The file or device is not there or cannot be read.</summary>
    CannotOpen,
    /// <summary>The container has no stream of the kind asked for.</summary>
    NoStream,
    /// <summary>This FFmpeg build has no decoder for the codec.</summary>
    DecoderNotAvailable,
    /// <summary>The decoder exists but refused the stream or produced no picture.</summary>
    DecodeFailed,
    /// <summary>The FFmpeg libraries are missing or the wrong version.</summary>
    LibrariesMissing,
}

/// <summary>A media input could not be used: the kind of problem and FFmpeg's words.</summary>
public sealed class MediaOpenException : Exception
{
    public MediaOpenException(MediaProblem problem, string message) : base(message) => Problem = problem;

    public MediaOpenException(string message, Exception inner) : base(message, inner) => Problem = MediaProblem.CannotOpen;

    public MediaOpenException() => Problem = MediaProblem.CannotOpen;

    public MediaOpenException(string message) : base(message) => Problem = MediaProblem.CannotOpen;

    public MediaProblem Problem { get; }
}

/// <summary>A user-space library FFmpeg binds a hardware device to through stubs that abort the process when the library is
/// missing or lacks a symbol, and the newest symbol this FFmpeg calls from it (present only from <paramref name="Needs"/>).</summary>
public readonly record struct HardwareLibrary(string Library, string Export, string Needs);

/// <summary>What a <see cref="HardwareLibrary"/> is on this machine.</summary>
public enum LibraryFound
{
    Usable,
    /// <summary>The library does not load.</summary>
    Missing,
    /// <summary>It loads but lacks the symbol: an older release than this FFmpeg calls into.</summary>
    TooOld,
}

/// <summary>How a decoder is opened.</summary>
public sealed record DecoderOptions
{
    /// <summary>Try the platform's hardware decoder first (D3D11VA on Windows; VAAPI then CUDA on Linux;
    /// VideoToolbox on macOS), falling back to software.</summary>
    public bool AllowHardware { get; init; } = true;

    /// <summary>One hardware device type by FFmpeg's name ("vaapi", "cuda", "d3d11va", "videotoolbox"), or null
    /// for the platform's order.</summary>
    public string? HardwareDevice { get; init; }

    /// <summary>Decoder threads; 0 lets FFmpeg pick by core count.</summary>
    public int Threads { get; init; }

    /// <summary>What a device's library is on this machine (<see cref="StreamDecoder.HardwareLibraries"/>); null asks the
    /// runtime. A test seam for a machine without a device's libraries, or with ones too old.</summary>
    internal Func<HardwareLibrary, LibraryFound>? LibraryProbe { get; init; }

    /// <summary>Live input: slice threads only and the low-delay flag, so a frame leaves the decoder as soon as
    /// its packet arrives (frame threads hold one frame per thread).</summary>
    public bool LowDelay { get; init; }

    public static readonly DecoderOptions Default = new();
}

/// <summary>An opened demuxer: the format context, its streams, and an interrupt flag that aborts any blocking
/// read when the owner closes (FFmpeg polls it from inside its I/O). Owned by one thread.</summary>
internal sealed unsafe class MediaInput : IDisposable
{
    private AVFormatContext* _format;
    private int* _interrupt;

    private MediaInput(AVFormatContext* format, int* interrupt)
    {
        _format = format;
        _interrupt = interrupt;
    }

    public AVFormatContext* Format => _format;

    public string ContainerName => _format is null || _format->iformat is null ? "" : FfmpegUtil.Text(_format->iformat->name);

    /// <summary>Opens a url (a path, a device name, a network url). <paramref name="formatName"/> forces a
    /// demuxer or device ("v4l2", "lavfi", "image2"); options are demuxer/device options.</summary>
    public static MediaInput Open(string url, string? formatName = null, IReadOnlyDictionary<string, string>? options = null,
        bool lowLatency = false, bool findStreamInfo = true)
    {
        FfmpegLoader.EnsureLoaded();
        AVInputFormat* inputFormat = null;
        if (!string.IsNullOrEmpty(formatName))
        {
            inputFormat = ffmpeg.av_find_input_format(formatName);
            if (inputFormat is null)
                throw new MediaOpenException(MediaProblem.CannotOpen, $"this FFmpeg build has no input format \"{formatName}\"");
        }
        var interrupt = (int*)NativeMemory.AllocZeroed(sizeof(int));
        var ctx = ffmpeg.avformat_alloc_context();
        if (ctx is null)
        {
            NativeMemory.Free(interrupt);
            throw new MediaOpenException(MediaProblem.CannotOpen, "FFmpeg could not allocate a format context");
        }
        ctx->interrupt_callback.callback = new AVIOInterruptCB_callback_func
        {
            Pointer = (nint)(delegate* unmanaged[Cdecl]<void*, int>)&OnInterrupt,
        };
        ctx->interrupt_callback.opaque = interrupt;
        if (lowLatency)
        {
            ctx->flags |= ffmpeg.AVFMT_FLAG_NOBUFFER | ffmpeg.AVFMT_FLAG_FLUSH_PACKETS;
        }

        AVDictionary* dict = null;
        if (options is not null)
        {
            foreach (var (key, value) in options)
                ffmpeg.av_dict_set(&dict, key, value, 0);
        }
        var r = ffmpeg.avformat_open_input(&ctx, url, inputFormat, &dict);
        ffmpeg.av_dict_free(&dict);
        if (r < 0)
        {
            // avformat_open_input frees the context on failure.
            NativeMemory.Free(interrupt);
            throw new MediaOpenException(MediaProblem.CannotOpen, $"cannot open {url}: {FfmpegUtil.ErrorText(r)}");
        }
        var input = new MediaInput(ctx, interrupt);
        if (findStreamInfo)
        {
            if (lowLatency)
                ctx->max_analyze_duration = 0;
            r = ffmpeg.avformat_find_stream_info(ctx, null);
            if (r < 0)
            {
                input.Dispose();
                throw new MediaOpenException(MediaProblem.CannotOpen, $"cannot read the streams of {url}: {FfmpegUtil.ErrorText(r)}");
            }
        }
        return input;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnInterrupt(void* opaque) => opaque is not null && Volatile.Read(ref *(int*)opaque) != 0 ? 1 : 0;

    /// <summary>Aborts blocking reads; safe from any thread while the input is open.</summary>
    public void Interrupt()
    {
        var flag = _interrupt;
        if (flag is not null)
            Volatile.Write(ref *flag, 1);
    }

    public bool Interrupted => _interrupt is not null && Volatile.Read(ref *_interrupt) != 0;

    /// <summary>The best stream of a kind (video ignores attached cover pictures), or −1.</summary>
    public int BestStream(AVMediaType type)
    {
        var index = ffmpeg.av_find_best_stream(_format, type, -1, -1, null, 0);
        if (index >= 0 && type == AVMediaType.AVMEDIA_TYPE_VIDEO && IsAttachedPicture(index))
        {
            index = -1;
            for (var i = 0; i < (int)_format->nb_streams; i++)
            {
                if (_format->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && !IsAttachedPicture(i))
                {
                    index = i;
                    break;
                }
            }
        }
        return index;
    }

    public bool IsAttachedPicture(int index) =>
        (_format->streams[index]->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0;

    public AVStream* Stream(int index) => _format->streams[index];

    /// <summary>Discards every stream but the ones kept, so the demuxer skips their packets cheaply.</summary>
    public void KeepOnly(int a, int b = -1)
    {
        for (var i = 0; i < (int)_format->nb_streams; i++)
            _format->streams[i]->discard = i == a || i == b ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;
    }

    /// <summary>The stream's first timestamp (its media zero), or 0 when unknown.</summary>
    public long StartTs(int index)
    {
        var s = _format->streams[index];
        if (s->start_time != ffmpeg.AV_NOPTS_VALUE)
            return s->start_time;
        if (_format->start_time != ffmpeg.AV_NOPTS_VALUE)
            return ffmpeg.av_rescale_q(_format->start_time, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE }, s->time_base);
        return 0;
    }

    /// <summary>The stream's duration in ns: its own, else the container's, else 0.</summary>
    public long DurationNs(int index)
    {
        var s = _format->streams[index];
        if (s->duration > 0 && s->duration != ffmpeg.AV_NOPTS_VALUE)
            return FfmpegUtil.DurationNs(s->duration, s->time_base);
        if (_format->duration > 0 && _format->duration != ffmpeg.AV_NOPTS_VALUE)
            return _format->duration * 1000;
        return 0;
    }

    /// <summary>The stream's frame rate: the average, else the base rate, else FFmpeg's guess.</summary>
    public AVRational FrameRate(int index)
    {
        var s = _format->streams[index];
        if (s->avg_frame_rate.num > 0 && s->avg_frame_rate.den > 0)
            return s->avg_frame_rate;
        if (s->r_frame_rate.num > 0 && s->r_frame_rate.den > 0)
            return s->r_frame_rate;
        return ffmpeg.av_guess_frame_rate(_format, s, null);
    }

    public void Dispose()
    {
        if (_format is not null)
        {
            var f = _format;
            ffmpeg.avformat_close_input(&f);
            _format = null;
        }
        if (_interrupt is not null)
        {
            NativeMemory.Free(_interrupt);
            _interrupt = null;
        }
    }
}

/// <summary>An opened decoder for one stream, hardware-accelerated when the platform's device opens and the
/// codec supports it; hardware frames are transferred to system memory (NV12 or P010) by
/// <see cref="TransferIfHardware"/>. Owned by one thread.</summary>
internal sealed unsafe class StreamDecoder : IDisposable
{
    private AVCodecContext* _ctx;
    private AVBufferRef* _hwDevice;
    private AVFrame* _transfer;

    private StreamDecoder(AVCodecContext* ctx, AVBufferRef* hwDevice, AVPixelFormat hwFormat, string name, string why)
    {
        _ctx = ctx;
        _hwDevice = hwDevice;
        HardwareFormat = hwFormat;
        Name = name;
        Why = why;
        if (hwDevice is not null)
            _transfer = ffmpeg.av_frame_alloc();
    }

    public AVCodecContext* Context => _ctx;

    /// <summary>"h264", "hevc (vaapi)".</summary>
    public string Name { get; }

    public bool Hardware => _hwDevice is not null;

    /// <summary>The hardware device's type by FFmpeg's name ("vaapi", "cuda"), or "" for software.</summary>
    public string Device => _hwDevice is null ? "" : ffmpeg.av_hwdevice_get_type_name(((AVHWDeviceContext*)_hwDevice->data)->type);

    /// <summary>Why the decoder is not on a better path, in words ("vaapi skipped: libva-drm.so.2 is not on this
    /// machine; cuda: no device"); empty when the first device tried opened.</summary>
    public string Why { get; }

    public AVPixelFormat HardwareFormat { get; }

    /// <summary>The platform's hardware device types in the order they are tried.</summary>
    public static IReadOnlyList<AVHWDeviceType> PlatformDevices()
    {
        if (OperatingSystem.IsWindows())
            return [AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA];
        if (OperatingSystem.IsMacOS())
            return [AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX];
        if (OperatingSystem.IsLinux())
            return [AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA];
        return [];
    }

    /// <summary>Opens the stream's decoder; throws <see cref="MediaOpenException"/> (DecoderNotAvailable when this
    /// build has no decoder for the codec).</summary>
    public static StreamDecoder Open(AVStream* stream, DecoderOptions options) => Open(stream, options, "");

    private static StreamDecoder Open(AVStream* stream, DecoderOptions options, string why)
    {
        var par = stream->codecpar;
        var codec = ffmpeg.avcodec_find_decoder(par->codec_id);
        if (codec is null)
            throw new MediaOpenException(MediaProblem.DecoderNotAvailable, $"decoder not available: {ffmpeg.avcodec_get_name(par->codec_id)}");

        var ctx = ffmpeg.avcodec_alloc_context3(codec);
        if (ctx is null)
            throw new MediaOpenException(MediaProblem.DecodeFailed, "FFmpeg could not allocate a decoder context");
        AVBufferRef* device = null;
        var hwFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        try
        {
            FfmpegUtil.Check(ffmpeg.avcodec_parameters_to_context(ctx, par), "decoder parameters");
            ctx->pkt_timebase = stream->time_base;
            if (par->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && options.AllowHardware)
                device = TryHardware(codec, ctx, options, out hwFormat, out why);
            if (options.LowDelay)
            {
                ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
                ctx->thread_type = ffmpeg.FF_THREAD_SLICE;
                ctx->thread_count = options.Threads;
            }
            else if (device is not null)
            {
                // Hardware decoders keep their own pipeline; frame threads only add delay and surfaces.
                ctx->thread_count = 1;
            }
            else
            {
                ctx->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
                ctx->thread_count = options.Threads;
            }
            var r = ffmpeg.avcodec_open2(ctx, codec, null);
            if (r < 0 && device is not null)
            {
                // The device opened but the decoder refused it (an unsupported profile): software instead.
                var refused = ffmpeg.av_hwdevice_get_type_name(((AVHWDeviceContext*)device->data)->type) + " refused the stream";
                ffmpeg.av_buffer_unref(&ctx->hw_device_ctx);
                ffmpeg.av_buffer_unref(&device);
                ffmpeg.avcodec_free_context(&ctx);
                return Open(stream, options with { AllowHardware = false }, Words(why, refused));
            }
            if (r < 0)
                throw new MediaOpenException(MediaProblem.DecodeFailed, $"the {FfmpegUtil.Text(codec->name)} decoder refused the stream: {FfmpegUtil.ErrorText(r)}");
            var name = FfmpegUtil.Text(codec->name);
            if (device is not null)
                name += " (" + ffmpeg.av_hwdevice_get_type_name(((AVHWDeviceContext*)device->data)->type) + ")";
            return new StreamDecoder(ctx, device, hwFormat, name, why);
        }
        catch
        {
            if (device is not null)
                ffmpeg.av_buffer_unref(&device);
            if (ctx is not null)
                ffmpeg.avcodec_free_context(&ctx);
            throw;
        }
    }

    /// <summary>The user-space libraries FFmpeg binds a device type to through generated stubs that abort the whole process
    /// when a library is missing or a symbol it calls is not in it: this FFmpeg build's VA-API (reached through a DRM render
    /// node only, see <see cref="TryHardwareCore"/>), and QSV, whose Linux child device is VA-API. vaMapBuffer2, called when a
    /// frame is read back, is the newest libva symbol it calls. The types FFmpeg loads itself and fails with an error code
    /// (CUDA, Vulkan, OpenCL) need no entry.</summary>
    public static IReadOnlyList<HardwareLibrary> HardwareLibraries(AVHWDeviceType type) =>
        !OperatingSystem.IsLinux() ? []
        : type is AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI or AVHWDeviceType.AV_HWDEVICE_TYPE_QSV
            ? [new("libva.so.2", "vaMapBuffer2", "libva 2.21"), new("libva-drm.so.2", "vaGetDisplayDRM", "libva 2.0")]
            : [];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<HardwareLibrary, LibraryFound> Found = new();

    /// <summary>Loads the library to look for the symbol and lets it go again (FFmpeg's stub loads it itself when the device is
    /// asked for); the answer does not change while the process runs.</summary>
    private static LibraryFound Probe(HardwareLibrary library) =>
        Found.GetOrAdd(library, static l =>
        {
            if (!NativeLibrary.TryLoad(l.Library, out var handle))
                return LibraryFound.Missing;
            var has = NativeLibrary.TryGetExport(handle, l.Export, out _);
            NativeLibrary.Free(handle);
            return has ? LibraryFound.Usable : LibraryFound.TooOld;
        });

    /// <summary>Why the device type cannot be asked for on this machine, or null when every library it needs is usable.</summary>
    private static string? Unusable(AVHWDeviceType type, Func<HardwareLibrary, LibraryFound>? probe)
    {
        foreach (var library in HardwareLibraries(type))
        {
            switch ((probe ?? Probe)(library))
            {
                case LibraryFound.Missing:
                    return $"{library.Library} is not on this machine";
                case LibraryFound.TooOld:
                    return $"{library.Library} has no {library.Export} (this FFmpeg needs {library.Needs} or later)";
            }
        }
        return null;
    }

    /// <summary>The ladder's words with one more rung's.</summary>
    internal static string Words(string a, string b) => a.Length == 0 ? b : a + "; " + b;

    private static (nint Device, AVPixelFormat Format, string Why) TryHardwareCore(AVCodec* codec, AVHWDeviceType type)
    {
        var typeName = ffmpeg.av_hwdevice_get_type_name(type);
        for (var i = 0; ; i++)
        {
            var config = ffmpeg.avcodec_get_hw_config(codec, i);
            if (config is null)
                return (0, AVPixelFormat.AV_PIX_FMT_NONE, $"{typeName}: does not decode {FfmpegUtil.Text(codec->name)}");
            if (config->device_type != type || (config->methods & (int)AvCodecHwConfigMethod.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) == 0)
                continue;
            AVBufferRef* device = null;
            AVDictionary* options = null;
            // VA-API through a DRM render node only: this build binds libX11 and libva-x11 through the same aborting stubs,
            // and a show node's decoder has no business with a desktop's X connection.
            if (type == AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI && OperatingSystem.IsLinux())
                ffmpeg.av_dict_set(&options, "connection_type", "drm", 0);
            var r = ffmpeg.av_hwdevice_ctx_create(&device, type, null, options, 0);
            ffmpeg.av_dict_free(&options);
            if (r < 0)
                return (0, AVPixelFormat.AV_PIX_FMT_NONE, $"{typeName}: no device ({FfmpegUtil.ErrorText(r)})");
            return ((nint)device, config->pix_fmt, "");
        }
    }

    private static AVBufferRef* TryHardware(AVCodec* codec, AVCodecContext* ctx, DecoderOptions options, out AVPixelFormat format, out string why)
    {
        format = AVPixelFormat.AV_PIX_FMT_NONE;
        why = "";
        IReadOnlyList<AVHWDeviceType> types = PlatformDevices();
        if (!string.IsNullOrEmpty(options.HardwareDevice))
        {
            var t = ffmpeg.av_hwdevice_find_type_by_name(options.HardwareDevice);
            types = t == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE ? [] : [t];
        }
        foreach (var type in types)
        {
            var typeName = ffmpeg.av_hwdevice_get_type_name(type);
            if (Unusable(type, options.LibraryProbe) is { } unusable)
            {
                why = Words(why, $"{typeName} skipped: {unusable}");
                continue;
            }
            var (device, pixFormat, refused) = TryHardwareCore(codec, type);
            if (device == 0)
            {
                why = Words(why, refused);
                continue;
            }
            var d = (AVBufferRef*)device;
            // The codec context takes its own reference; the decoder keeps one to name the device and free it.
            ctx->hw_device_ctx = ffmpeg.av_buffer_ref(d);
            format = pixFormat;
            return d;
        }
        return null;
    }

    /// <summary>The frame itself when it is in system memory; otherwise its transfer to system memory (NV12 or
    /// P010, owned by the decoder until the next transfer), with the timing and colour properties copied.</summary>
    public AVFrame* TransferIfHardware(AVFrame* frame)
    {
        if (_transfer is null || (AVPixelFormat)frame->format != HardwareFormat)
            return frame;
        ffmpeg.av_frame_unref(_transfer);
        FfmpegUtil.Check(ffmpeg.av_hwframe_transfer_data(_transfer, frame, 0), "hardware frame transfer");
        FfmpegUtil.Check(ffmpeg.av_frame_copy_props(_transfer, frame), "frame properties");
        return _transfer;
    }

    public void Dispose()
    {
        if (_transfer is not null)
        {
            var t = _transfer;
            ffmpeg.av_frame_free(&t);
            _transfer = null;
        }
        if (_ctx is not null)
        {
            var c = _ctx;
            ffmpeg.avcodec_free_context(&c);
            _ctx = null;
        }
        if (_hwDevice is not null)
        {
            var d = _hwDevice;
            ffmpeg.av_buffer_unref(&d);
            _hwDevice = null;
        }
    }
}
