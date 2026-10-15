using System.Collections.Immutable;

namespace PlayMap.Core.Audio;

/// <summary>The machine's sound inputs behind one door: each backend serves the keys that start with its prefix ("test:", "file:",
/// "pulse:", "wasapi:" …), a device is opened once whoever listens, and its samples fan out to every listener with that listener's
/// channel pick — the analyser on 1+2 and a timecode reader on 7 share one open. A key may carry its pick after an '@'
/// ("pulse:default@7"); without one the listener gets every channel. A show's name ("" or "default", "system", a device's name)
/// opens the input it resolves to here. The capture thread never blocks here: listeners are an array swapped whole.</summary>
public sealed class AudioInputHub : IAudioInputs, IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<(string Prefix, IAudioInputs Backend)> _backends = [];
    private readonly Dictionary<string, Device> _open = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Opens counted since the hub was made (one per device opened, not per listener).</summary>
    public int Opens { get; private set; }

    /// <summary>Adds a backend for the keys starting with <paramref name="prefix"/> (checked in the order added).</summary>
    public AudioInputHub Add(string prefix, IAudioInputs backend)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        ArgumentNullException.ThrowIfNull(backend);
        lock (_gate)
            _backends.Add((prefix, backend));
        return this;
    }

    /// <summary>Every backend's inputs, in the order the backends were added; a backend that fails to list is left out.</summary>
    public IReadOnlyList<AudioInputInfo> Inputs()
    {
        List<IAudioInputs> backends;
        lock (_gate)
            backends = [.. _backends.Select(b => b.Backend).Distinct()];
        var all = new List<AudioInputInfo>();
        foreach (var b in backends)
            all.AddRange(b.Inputs());
        return all;
    }

    /// <summary>The input a show's name stands for: "default" (the machine's default capture), "system" (its default loopback),
    /// a key, or a device's name matched by <see cref="AudioInputMatch"/>; null when nothing matches.</summary>
    public AudioInputInfo? Resolve(string name, IReadOnlyList<AudioInputInfo>? inputs = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        inputs ??= Inputs();
        if (name.Equals("default", StringComparison.OrdinalIgnoreCase))
            return inputs.FirstOrDefault(i => i.Kind == AudioInputKind.Capture && i.IsDefault)
                ?? inputs.FirstOrDefault(i => i.Kind == AudioInputKind.Capture);
        if (name.Equals("system", StringComparison.OrdinalIgnoreCase))
            return inputs.FirstOrDefault(i => i.Kind == AudioInputKind.Loopback && i.IsDefault)
                ?? inputs.FirstOrDefault(i => i.Kind == AudioInputKind.Loopback);
        return inputs.FirstOrDefault(i => string.Equals(i.Key, name, StringComparison.Ordinal))
            ?? AudioInputMatch.Find(inputs, name);
    }

    /// <summary>Opens a key ("&lt;device key&gt;[@&lt;pick&gt;]") for a listener: the device's first listener opens it, later ones
    /// join. Throws <see cref="AudioUnavailableException"/> with words when no backend serves the key or the device cannot open.</summary>
    public IAudioCapture Open(string key, int preferredRate, IAudioCaptureSink sink)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(sink);
        var (device, pick) = Split(key);
        if (!Serves(device))
        {
            // A show's name ("", "default", "system", a device's name) opens the input it stands for here.
            var named = Resolve(device.Length == 0 ? "default" : device)
                ?? throw new AudioUnavailableException(device.Length == 0
                    ? "this node has no default sound input"
                    : $"no sound backend on this node serves '{device}', and no input here is named so");
            device = named.Key;
        }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_open.TryGetValue(device, out var d))
            {
                var backend = _backends.FirstOrDefault(b => device.StartsWith(b.Prefix, StringComparison.OrdinalIgnoreCase)).Backend
                    ?? throw new AudioUnavailableException($"no sound backend on this node serves '{device}'");
                d = new Device(this, device);
                d.Capture = backend.Open(device, preferredRate, d);
                _open[device] = d;
                Opens++;
            }
            var listener = new Listener(d, pick, sink);
            d.Add(listener);
            return listener;
        }
    }

    /// <summary>True when a backend serves keys like this one.</summary>
    private bool Serves(string device)
    {
        lock (_gate)
            return device.Length > 0 && _backends.Any(b => device.StartsWith(b.Prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Device keys open now, with their listener counts.</summary>
    public IReadOnlyDictionary<string, int> OpenDevices()
    {
        lock (_gate)
            return _open.ToDictionary(kv => kv.Key, kv => kv.Value.Listeners.Length, StringComparer.Ordinal);
    }

    /// <summary>A key's device part and its pick ("all" when none is given or the text after '@' is not a pick).</summary>
    public static (string Device, ChannelPick Pick) Split(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var at = key.LastIndexOf('@');
        if (at >= 0 && ChannelPick.TryParse(key[(at + 1)..], out var pick, out _))
            return (key[..at], pick);
        return (key, ChannelPick.All);
    }

    public void Dispose()
    {
        List<Device> open;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            open = [.. _open.Values];
            _open.Clear();
        }
        foreach (var d in open)
            d.Capture?.Dispose();
    }

    private void Release(Device d, Listener l)
    {
        IAudioCapture? close = null;
        lock (_gate)
        {
            d.Remove(l);
            // The last listener closes the device, whether it is still the open one or a faulted one already let go.
            if (d.Listeners.IsEmpty)
            {
                if (_open.TryGetValue(d.Key, out var current) && ReferenceEquals(current, d))
                    _open.Remove(d.Key);
                close = d.Capture;
            }
        }
        close?.Dispose();
    }

    private void Fault(Device d)
    {
        lock (_gate)
        {
            if (_open.TryGetValue(d.Key, out var current) && ReferenceEquals(current, d))
                _open.Remove(d.Key);
        }
    }

    /// <summary>One open device and its listeners: the backend's sink, called on the capture thread.</summary>
    private sealed class Device(AudioInputHub hub, string key) : IAudioCaptureSink
    {
        private ImmutableArray<Listener> _listeners = [];

        public string Key { get; } = key;

        public IAudioCapture? Capture { get; set; }

        public ImmutableArray<Listener> Listeners => _listeners;

        public void Add(Listener l) => _listeners = _listeners.Add(l);

        public void Remove(Listener l) => _listeners = _listeners.Remove(l);

        public void Samples(ReadOnlySpan<float> interleaved, int channels, int sampleRate, long firstSampleIndex, long readMonoNs)
        {
            foreach (var l in _listeners)
                l.Deliver(interleaved, channels, sampleRate, firstSampleIndex, readMonoNs);
        }

        public void Faulted(string words)
        {
            hub.Fault(this);
            foreach (var l in _listeners)
                l.Sink.Faulted(words);
        }

        public void Release(Listener l) => hub.Release(this, l);
    }

    /// <summary>One listener of a device: its pick, its sink and the buffer its picked samples are made in.</summary>
    private sealed class Listener(Device device, ChannelPick pick, IAudioCaptureSink sink) : IAudioCapture
    {
        private float[] _picked = [];
        private int _disposed;

        public IAudioCaptureSink Sink { get; } = sink;

        public int SampleRate => device.Capture?.SampleRate ?? 0;

        public int Channels => pick.OutChannels(device.Capture?.Channels ?? 0);

        public void Deliver(ReadOnlySpan<float> interleaved, int channels, int sampleRate, long firstSampleIndex, long readMonoNs)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            if (pick.Kind == ChannelPickKind.All)
            {
                Sink.Samples(interleaved, channels, sampleRate, firstSampleIndex, readMonoNs);
                return;
            }
            var frames = interleaved.Length / Math.Max(1, channels);
            var outChannels = pick.OutChannels(channels);
            var need = frames * outChannels;
            if (_picked.Length < need)
                _picked = new float[Math.Max(need, 1024)];
            pick.Apply(interleaved, channels, _picked);
            Sink.Samples(_picked.AsSpan(0, need), outChannels, sampleRate, firstSampleIndex, readMonoNs);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                device.Release(this);
        }
    }
}
