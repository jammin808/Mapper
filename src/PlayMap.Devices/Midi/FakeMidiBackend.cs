using PlayMap.Core.Control.Midi;

namespace PlayMap.Devices.Midi;

/// <summary>In-memory MIDI ports: a test plugs a port in, plays bytes into it and reads what was sent out. Bytes reach the receiver
/// on the caller's thread, as a backend's own thread would deliver them.</summary>
public sealed class FakeMidiBackend : IMidiBackend
{
    private readonly Lock _gate = new();
    private readonly List<MidiPortInfo> _ports = [];
    private readonly Dictionary<string, List<IMidiReceiver>> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<byte[]>> _sent = new(StringComparer.Ordinal);

    public string Name => "fake";

    public string? Unavailable => null;

    public event Action? PortsChanged;

    public IReadOnlyList<MidiPortInfo> Ports()
    {
        lock (_gate)
            return [.. _ports];
    }

    /// <summary>Plugs a port in (an input and an output of that name).</summary>
    public void Plug(string name)
    {
        lock (_gate)
        {
            _ports.Add(new MidiPortInfo("in:" + name, name, true));
            _ports.Add(new MidiPortInfo("out:" + name, name, false));
        }
        PortsChanged?.Invoke();
    }

    public void Unplug(string name)
    {
        lock (_gate)
        {
            _ports.RemoveAll(p => p.Name == name);
            _open.Remove("in:" + name);
        }
        PortsChanged?.Invoke();
    }

    /// <summary>Bytes arriving on a port's input (to every receiver that opened it); answers how many heard them.</summary>
    public int Play(string name, ReadOnlySpan<byte> bytes, long monoNs)
    {
        List<IMidiReceiver> receivers;
        lock (_gate)
            receivers = _open.TryGetValue("in:" + name, out var list) ? [.. list] : [];
        foreach (var r in receivers)
            r.Receive(bytes, monoNs);
        return receivers.Count;
    }

    /// <summary>What was sent out of a port.</summary>
    public IReadOnlyList<byte[]> Sent(string name)
    {
        lock (_gate)
            return _sent.TryGetValue("out:" + name, out var list) ? [.. list] : [];
    }

    public IMidiInput OpenInput(MidiPortInfo port, IMidiReceiver receiver)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(receiver);
        lock (_gate)
        {
            if (!_ports.Any(p => p.Id == port.Id && p.IsInput))
                throw new MidiUnavailableException($"no MIDI input {port.Name}");
            if (!_open.TryGetValue(port.Id, out var list))
            {
                list = [];
                _open[port.Id] = list;
            }
            list.Add(receiver);
        }
        return new Opened(() =>
        {
            lock (_gate)
                if (_open.TryGetValue(port.Id, out var list))
                    list.Remove(receiver);
        });
    }

    public IMidiOutput OpenOutput(MidiPortInfo port)
    {
        ArgumentNullException.ThrowIfNull(port);
        lock (_gate)
        {
            if (!_ports.Any(p => p.Id == port.Id && !p.IsInput))
                throw new MidiUnavailableException($"no MIDI output {port.Name}");
        }
        return new Output(this, port.Id);
    }

    public void Dispose()
    {
        lock (_gate)
            _open.Clear();
    }

    private sealed class Opened(Action close) : IMidiInput
    {
        private Action? _close = close;

        public void Dispose() => Interlocked.Exchange(ref _close, null)?.Invoke();
    }

    private sealed class Output(FakeMidiBackend owner, string id) : IMidiOutput
    {
        public bool Send(ReadOnlySpan<byte> message)
        {
            lock (owner._gate)
            {
                if (!owner._sent.TryGetValue(id, out var list))
                {
                    list = [];
                    owner._sent[id] = list;
                }
                list.Add(message.ToArray());
            }
            return true;
        }

        public void Dispose()
        {
            // Nothing is held for an in-memory port.
        }
    }
}
