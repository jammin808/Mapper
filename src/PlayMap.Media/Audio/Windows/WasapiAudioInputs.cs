using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PlayMap.Core.Audio;

namespace PlayMap.Media.Audio.Windows;

/// <summary>Windows' own sound inputs through WASAPI (NAudio.Wasapi): every active capture endpoint ("wasapi:in:&lt;id&gt;"), the
/// sound a render endpoint plays ("wasapi:loop:&lt;id&gt;", shown as "System sound — &lt;device&gt;"; the default output is the
/// "system" input), and everything but PlayMap ("wasapi:proc:exclude-self", process loopback, Windows 11 / build 20348 or later),
/// all in shared mode with a 20 ms buffer. A runner or a machine without endpoints lists nothing and says so; enumeration never
/// throws.</summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioInputs : IAudioInputs, IAudioInputsWords
{
    public const string Prefix = "wasapi:";
    public const string ExcludeSelf = "wasapi:proc:exclude-self";
    private const int BufferMs = 20;

    private volatile string _words = "";

    public string Backend => "wasapi";

    public string Words => _words;

    public IReadOnlyList<AudioInputInfo> Inputs()
    {
        var list = new List<AudioInputInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var defaultIn = DefaultId(enumerator, DataFlow.Capture);
            var defaultOut = DefaultId(enumerator, DataFlow.Render);
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (d)
                    list.Add(new AudioInputInfo($"{Prefix}in:{d.ID}", d.FriendlyName, 2, [48000], Backend) { IsDefault = d.ID == defaultIn });
            }
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (d)
                    list.Add(new AudioInputInfo($"{Prefix}loop:{d.ID}", "System sound — " + d.FriendlyName, 2, [48000], Backend)
                    {
                        Kind = AudioInputKind.Loopback,
                        IsDefault = d.ID == defaultOut,
                    });
            }
            if (list.Count > 0 && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
                list.Add(new AudioInputInfo(ExcludeSelf, "Everything but PlayMap", 2, [48000], Backend) { Kind = AudioInputKind.ProcessLoopback });
            _words = list.Count == 0 ? "Windows lists no active sound endpoint on this machine" : "";
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            _words = "WASAPI cannot list the endpoints: " + e.Message;
            list.Clear();
        }
        return list;
    }

    private static string? DefaultId(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        if (!enumerator.TryGetDefaultAudioEndpoint(flow, Role.Multimedia, out var device) || device is null)
            return null;
        using (device)
            return device.ID;
    }

    public IAudioCapture Open(string key, int preferredRate, IAudioCaptureSink sink)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(sink);
        try
        {
            if (key == ExcludeSelf)
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
                    throw new AudioUnavailableException("everything but PlayMap needs Windows 11 (build 20348) or later — System sound is the fallback");
                return WasapiRecorderCapture.ExcludingSelf(sink);
            }
            var loop = key.StartsWith(Prefix + "loop:", StringComparison.Ordinal);
            var id = loop ? key[(Prefix.Length + 5)..] : key.StartsWith(Prefix + "in:", StringComparison.Ordinal) ? key[(Prefix.Length + 3)..] : null;
            if (id is null)
                throw new AudioUnavailableException($"'{key}' is not a WASAPI key (wasapi:in:<id>, wasapi:loop:<id>, {ExcludeSelf})");
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(id);
            var builder = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode().WithEventSync().WithBufferLength(BufferMs);
            if (loop)
                builder = builder.WithLoopbackCapture();
            return new WasapiRecorderCapture(builder.Build(), device, sink);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException)
        {
            throw new AudioUnavailableException("WASAPI cannot open it: " + e.Message);
        }
    }

    /// <summary>Interleaved float samples from a WASAPI buffer: 32-bit float as it comes, 16-bit and 24-bit PCM scaled.</summary>
    internal static int ToFloat(ReadOnlySpan<byte> bytes, WaveFormat format, ref float[] into)
    {
        var bits = format.BitsPerSample;
        var samples = bytes.Length / Math.Max(1, bits / 8);
        if (into.Length < samples)
            into = new float[Math.Max(samples, 4096)];
        if (bits == 32 && format.Encoding is WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible)
        {
            MemoryMarshal.Cast<byte, float>(bytes[..(samples * 4)]).CopyTo(into);
        }
        else if (bits == 16)
        {
            var pcm = MemoryMarshal.Cast<byte, short>(bytes[..(samples * 2)]);
            for (var i = 0; i < samples; i++)
                into[i] = pcm[i] / 32768f;
        }
        else if (bits == 24)
        {
            for (var i = 0; i < samples; i++)
                into[i] = ((bytes[3 * i] << 8) | (bytes[3 * i + 1] << 16) | (bytes[3 * i + 2] << 24)) / 2147483648f;
        }
        else
        {
            return 0;
        }
        return samples;
    }
}

/// <summary>An open WASAPI recorder (an endpoint's capture, a render endpoint's loopback, or the process loopback): its data handed
/// on as float with the monotonic instant it arrived; a stop that was not asked for is reported once.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WasapiRecorderCapture : IAudioCapture
{
    private readonly WasapiRecorder _recorder;
    private readonly IDisposable? _device;
    private readonly IAudioCaptureSink _sink;
    private float[] _buffer = [];
    private long _index;
    private int _disposed;

    public WasapiRecorderCapture(WasapiRecorder recorder, IDisposable? device, IAudioCaptureSink sink)
    {
        _recorder = recorder;
        _device = device;
        _sink = sink;
        SampleRate = recorder.WaveFormat.SampleRate;
        Channels = recorder.WaveFormat.Channels;
        recorder.DataAvailable += (buffer, _, _, _) => OnData(buffer);
        recorder.RecordingStopped += OnStopped;
        recorder.StartRecording();
    }

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>Everything the machine plays except PlayMap's own process tree (activation completes on a COM worker; the open
    /// waits for it on the thread-pool thread that opens inputs, never the lane).</summary>
    [SupportedOSPlatform("windows10.0.20348.0")]
    public static WasapiRecorderCapture ExcludingSelf(IAudioCaptureSink sink)
    {
#pragma warning disable VSTHRD002 // The caller is the runtime's thread-pool open, never the lane or a UI thread.
        var recorder = new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.ExcludeTargetProcessTree)
            .WithBufferLength(20)
            .BuildAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
        return new WasapiRecorderCapture(recorder, null, sink);
    }

    private void OnData(ReadOnlySpan<byte> buffer)
    {
        var readNs = PlayMap.Core.Time.MonoClock.Instance.NowNs;
        var n = WasapiAudioInputs.ToFloat(buffer, _recorder.WaveFormat, ref _buffer);
        if (n == 0)
            return;
        _sink.Samples(_buffer.AsSpan(0, n), Channels, SampleRate, _index, readNs);
        _index += n / Channels;
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0)
            _sink.Faulted(e.Exception is { } x ? "the device stopped: " + x.Message : "the device stopped");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _recorder.RecordingStopped -= OnStopped;
        _recorder.StopRecording();
        _recorder.Dispose();
        _device?.Dispose();
    }
}
