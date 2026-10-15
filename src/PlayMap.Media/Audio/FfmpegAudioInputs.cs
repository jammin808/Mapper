using FFmpeg.AutoGen;
using PlayMap.Core.Audio;
using PlayMap.Media.Ffmpeg;

namespace PlayMap.Media.Audio;

/// <summary>Sound capture through FFmpeg's device library: on Linux the pulse, oss and openal inputs this build offers, on Windows
/// dshow — one code path. Keys are "&lt;format&gt;:&lt;device&gt;" ("pulse:alsa_input.usb-…", "dshow:Microphone (USB Audio)").
/// FFmpeg has no WASAPI or loopback input; on Windows those are <see cref="Windows.WasapiAudioInputs"/>. A machine without a
/// device, a sound server or FFmpeg lists nothing and <see cref="Words"/> says why.</summary>
public sealed unsafe class FfmpegAudioInputs : IAudioInputs, IAudioInputsWords
{
    private volatile string _words = "";

    /// <summary>The device formats read on this platform, in order.</summary>
    public static IReadOnlyList<string> PlatformFormats =>
        OperatingSystem.IsWindows() ? ["dshow"] : OperatingSystem.IsMacOS() ? ["avfoundation"] : ["pulse", "oss", "openal"];

    public string Backend => "ffmpeg";

    public string Words => _words;

    public IReadOnlyList<AudioInputInfo> Inputs()
    {
        var list = new List<AudioInputInfo>();
        if (!FfmpegLoader.TryLoad(out var problem))
        {
            _words = "FFmpeg is not loaded: " + problem;
            return list;
        }
        if (FfmpegLoader.Status?.DevicesAvailable != true)
        {
            _words = "this FFmpeg has no device library (avdevice)";
            return list;
        }
        var notes = new List<string>();
        foreach (var format in PlatformFormats)
        {
            var input = ffmpeg.av_find_input_format(format);
            if (input is null)
            {
                notes.Add($"{format} is not in this FFmpeg");
                continue;
            }
            var found = List(format, input, list);
            if (found.Length > 0)
                notes.Add(found);
        }
        _words = list.Count > 0 ? "" : notes.Count == 0 ? "no capture device on this machine" : string.Join("; ", notes);
        return list;
    }

    /// <summary>Adds a format's audio devices to the list; the words when it lists none.</summary>
    private static string List(string format, AVInputFormat* input, List<AudioInputInfo> into)
    {
        AVDeviceInfoList* devices = null;
        using (FfmpegLog.Capture(out _))
        {
            var r = ffmpeg.avdevice_list_input_sources(input, null, null, &devices);
            if (r < 0 || devices is null)
            {
                ffmpeg.avdevice_free_list_devices(&devices);
                return $"{format} lists no capture device ({FfmpegUtil.ErrorText(r)})";
            }
        }
        try
        {
            var before = into.Count;
            for (var i = 0; i < devices->nb_devices; i++)
            {
                var d = devices->devices[i];
                var audio = d->nb_media_types == 0;
                for (var t = 0; t < d->nb_media_types; t++)
                    audio |= d->media_types[t] == AVMediaType.AVMEDIA_TYPE_AUDIO;
                if (!audio)
                    continue;
                var name = FfmpegUtil.Text(d->device_name);
                var description = FfmpegUtil.Text(d->device_description);
                into.Add(new AudioInputInfo($"{format}:{name}", description.Length > 0 ? description : name, 2, [48000], "ffmpeg")
                {
                    Kind = name.EndsWith(".monitor", StringComparison.Ordinal) ? AudioInputKind.Loopback : AudioInputKind.Capture,
                    IsDefault = i == devices->default_device,
                });
            }
            return into.Count == before ? $"{format} lists no capture device" : "";
        }
        finally
        {
            ffmpeg.avdevice_free_list_devices(&devices);
        }
    }

    public IAudioCapture Open(string key, int preferredRate, IAudioCaptureSink sink)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(sink);
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !PlatformFormats.Contains(key[..colon]))
            throw new AudioUnavailableException($"'{key}' is not an FFmpeg capture key ({string.Join(", ", PlatformFormats)}:<device>)");
        if (!FfmpegLoader.TryLoad(out var problem))
            throw new AudioUnavailableException("FFmpeg is not loaded: " + problem);
        return new FfmpegAudioCapture(key[..colon], key[(colon + 1)..], preferredRate > 0 ? preferredRate : 48000, sink);
    }
}

/// <summary>One open FFmpeg capture: a thread reads the device's packets, decodes them and converts them to interleaved float at the
/// device's own rate and channel count, and hands each block to the sink stamped with the monotonic instant it was read. A device
/// that stops (unplugged, the sound server gone) is reported once through <see cref="IAudioCaptureSink.Faulted"/>.</summary>
internal sealed unsafe class FfmpegAudioCapture : IAudioCapture
{
    private readonly MediaInput _input;
    private readonly StreamDecoder _decoder;
    private readonly int _stream;
    private readonly IAudioCaptureSink _sink;
    private readonly Thread _thread;
    private volatile bool _stop;
    private int _disposed;

    public FfmpegAudioCapture(string format, string device, int rate, IAudioCaptureSink sink)
    {
        _sink = sink;
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sample_rate"] = rate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["channels"] = "2",
        };
        // Reactive pictures want blocks of about 10 ms; a device's default buffer is often half a second.
        if (format == "pulse")
            options["fragment_size"] = "1920";
        if (format == "dshow")
            options["audio_buffer_size"] = "10";
        var url = format == "dshow" ? "audio=" + device : device;
        try
        {
            _input = MediaInput.Open(url, format, options, lowLatency: true);
        }
        catch (MediaOpenException e)
        {
            throw new AudioUnavailableException($"{format} cannot open '{device}': {e.Message}");
        }
        try
        {
            _stream = _input.BestStream(AVMediaType.AVMEDIA_TYPE_AUDIO);
            if (_stream < 0)
                throw new AudioUnavailableException($"{format} '{device}' gives no sound stream");
            _input.KeepOnly(_stream);
            _decoder = StreamDecoder.Open(_input.Stream(_stream), DecoderOptions.Default with { LowDelay = true });
        }
        catch (MediaOpenException e)
        {
            _input.Dispose();
            throw new AudioUnavailableException($"{format} '{device}': {e.Message}");
        }
        catch
        {
            _input.Dispose();
            throw;
        }
        SampleRate = _decoder.Context->sample_rate > 0 ? _decoder.Context->sample_rate : rate;
        Channels = Math.Max(1, _decoder.Context->ch_layout.nb_channels);
        _thread = new Thread(Run) { IsBackground = true, Name = $"audio {format}" };
        _thread.Start();
    }

    public int SampleRate { get; }

    public int Channels { get; }

    private void Run()
    {
        var packet = ffmpeg.av_packet_alloc();
        var frame = ffmpeg.av_frame_alloc();
        SwrContext* swr = null;
        var buffer = new float[0];
        long index = 0;
        var words = "";
        try
        {
            while (!_stop)
            {
                var r = ffmpeg.av_read_frame(_input.Format, packet);
                if (r == FfmpegUtil.ErrorAgain)
                    continue;
                if (r < 0)
                {
                    words = _stop ? "" : $"the device stopped: {FfmpegUtil.ErrorText(r)}";
                    break;
                }
                var readNs = PlayMap.Core.Time.MonoClock.Instance.NowNs;
                if (packet->stream_index == _stream)
                    ffmpeg.avcodec_send_packet(_decoder.Context, packet);
                ffmpeg.av_packet_unref(packet);
                while (ffmpeg.avcodec_receive_frame(_decoder.Context, frame) == 0)
                {
                    if (swr is null)
                        swr = Converter(frame);
                    var need = frame->nb_samples * Channels;
                    if (buffer.Length < need)
                        buffer = new float[need];
                    int got;
                    fixed (float* dst = buffer)
                    {
                        var d = (byte*)dst;
                        got = ffmpeg.swr_convert(swr, &d, frame->nb_samples, frame->extended_data, frame->nb_samples);
                    }
                    ffmpeg.av_frame_unref(frame);
                    if (got <= 0)
                        continue;
                    _sink.Samples(buffer.AsSpan(0, got * Channels), Channels, SampleRate, index, readNs);
                    index += got;
                }
            }
        }
        catch (Exception e) when (e is FfmpegException or InvalidOperationException)
        {
            words = e.Message;
        }
        finally
        {
            if (swr is not null)
                ffmpeg.swr_free(&swr);
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
        }
        if (words.Length > 0)
            _sink.Faulted(words);
    }

    private SwrContext* Converter(AVFrame* frame)
    {
        var layout = frame->ch_layout.nb_channels > 0 ? frame->ch_layout : _decoder.Context->ch_layout;
        AVChannelLayout output;
        ffmpeg.av_channel_layout_default(&output, Channels);
        SwrContext* swr = null;
        var r = ffmpeg.swr_alloc_set_opts2(&swr, &output, AVSampleFormat.AV_SAMPLE_FMT_FLT, SampleRate, &layout,
            (AVSampleFormat)frame->format, frame->sample_rate, 0, null);
        if (r < 0 || ffmpeg.swr_init(swr) < 0)
        {
            ffmpeg.swr_free(&swr);
            throw new FfmpegException("audio capture converter", r < 0 ? r : ffmpeg.AVERROR_EXTERNAL);
        }
        return swr;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop = true;
        _input.Interrupt();
        if (!_thread.Join(TimeSpan.FromSeconds(5)))
            return; // A read that ignores the interrupt keeps its objects; nothing is freed under it.
        _decoder.Dispose();
        _input.Dispose();
    }
}

/// <summary>"file:&lt;path&gt;": a sound file played as an input, looped, at the pace of the monotonic clock (rehearsal with the
/// show's own track; LTC from a WAV). Decoded by FFmpeg to 48 kHz stereo.</summary>
public sealed class FileAudioInputs : IAudioInputs
{
    public const string Prefix = "file:";

    /// <summary>Files are not listed (any path is a key).</summary>
    public IReadOnlyList<AudioInputInfo> Inputs() => [];

    public IAudioCapture Open(string key, int preferredRate, IAudioCaptureSink sink)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(sink);
        if (!key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            throw new AudioUnavailableException($"'{key}' is not a file input (file:<path>)");
        var path = key[Prefix.Length..];
        if (!File.Exists(path))
            throw new AudioUnavailableException($"no file {path}");
        try
        {
            return new FileAudioCapture(new FfmpegAudioDecoder(path), sink);
        }
        catch (Exception e) when (e is MediaOpenException or FfmpegUnavailableException)
        {
            throw new AudioUnavailableException($"{Path.GetFileName(path)}: {e.Message}");
        }
    }
}

/// <summary>A looped file delivered in 10 ms blocks as the monotonic clock passes them.</summary>
internal sealed class FileAudioCapture : IAudioCapture
{
    private const int Block = FfmpegAudioDecoder.SampleRate / 100;

    private readonly FfmpegAudioDecoder _decoder;
    private readonly IAudioCaptureSink _sink;
    private readonly Lock _gate = new();
    private readonly Timer _timer;
    private readonly float[] _buffer = new float[Block * FfmpegAudioDecoder.Channels];
    private readonly long _startNs = PlayMap.Core.Time.MonoClock.Instance.NowNs;
    private long _made;
    private bool _closed;
    private bool _decoderDisposed;

    public FileAudioCapture(FfmpegAudioDecoder decoder, IAudioCaptureSink sink)
    {
        _decoder = decoder;
        _sink = sink;
        _timer = new Timer(_ => Deliver(), null, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10));
    }

    public int SampleRate => FfmpegAudioDecoder.SampleRate;

    public int Channels => FfmpegAudioDecoder.Channels;

    private void Deliver()
    {
        lock (_gate)
        {
            if (_closed)
                return;
            var due = (PlayMap.Core.Time.MonoClock.Instance.NowNs - _startNs) * (long)SampleRate / 1_000_000_000;
            if (due - _made > SampleRate)
                _made = due - SampleRate;
            try
            {
                while (_made + Block <= due)
                {
                    var n = _decoder.Read(_buffer);
                    if (n < Block)
                    {
                        // The end: the file starts again, and the rest of the block comes from its start.
                        _decoder.Seek(0);
                        n += _decoder.Read(_buffer.AsSpan(n * Channels));
                        if (n == 0)
                        {
                            _closed = true;
                            _sink.Faulted("the file holds no sound");
                            return;
                        }
                    }
                    _made += n;
                    _sink.Samples(_buffer.AsSpan(0, n * Channels), Channels, SampleRate, _made - n,
                        _startNs + _made * 1_000_000_000 / SampleRate);
                }
            }
            catch (FfmpegException e)
            {
                _closed = true;
                _sink.Faulted("the file stopped: " + e.Message);
            }
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate)
        {
            if (_decoderDisposed)
                return;
            _closed = true;
            _decoderDisposed = true;
            _decoder.Dispose();
        }
    }
}
