using System.Runtime.InteropServices;
using PlayMap.Core.Control.Midi;
using PlayMap.Core.Time;

namespace PlayMap.Devices.Midi;

/// <summary>Linux MIDI through the ALSA sequencer (libasound.so.2, loaded at run time and linked dynamically). It opens one client,
/// "PlayMap", lists every other client's readable ports as inputs, and for an opened input makes a write port of its own,
/// connects the source to it and reads events on its own thread, turning each back into MIDI bytes with the library's event
/// decoder. A machine without the library or without /dev/snd/seq gets a backend that says so and lists no ports. Output is
/// not in this build (MIDI out is next round).</summary>
public sealed class AlsaSeqBackend : IMidiBackend
{
    private const string Lib = "libasound.so.2";
    private const int OpenDuplex = 3;
    private const int NonBlock = 1;
    private const uint CapRead = 1 << 0;
    private const uint CapWrite = 1 << 1;
    private const uint CapSubsRead = 1 << 5;
    private const uint CapSubsWrite = 1 << 6;
    private const uint CapNoExport = 1 << 7;
    private const uint TypeMidiGeneric = 1 << 1;
    private const uint TypeApplication = 1 << 20;
    private const int EAgain = -11;

    private readonly Lock _gate = new();
    private readonly IMonoClock _mono;
    private readonly Dictionary<int, (IMidiReceiver Receiver, nint Decoder)> _inputs = [];
    private readonly ManualResetEventSlim _stop = new();
    private Thread? _reader;
    private nint _seq;
    private int _client = -1;

    public AlsaSeqBackend(IMonoClock? mono = null)
    {
        _mono = mono ?? MonoClock.Instance;
        if (!NativeLibrary.TryLoad(Lib, out var lib))
        {
            Unavailable = "no ALSA library (libasound.so.2) on this machine — MIDI input needs it";
            return;
        }
        NativeLibrary.Free(lib);
        if (!File.Exists("/dev/snd/seq"))
        {
            Unavailable = "no ALSA sequencer: /dev/snd/seq is missing (load the snd-seq module, or this machine has no sound devices)";
            return;
        }
        var rc = snd_seq_open(out _seq, "default", OpenDuplex, NonBlock);
        if (rc < 0 || _seq == 0)
        {
            Unavailable = $"the ALSA sequencer did not open ({Marshal.PtrToStringUTF8(snd_strerror(rc))})";
            _seq = 0;
            return;
        }
        _ = snd_seq_set_client_name(_seq, "PlayMap");
        _client = snd_seq_client_id(_seq);
    }

    public string Name => "alsa-seq";

    public string? Unavailable { get; }

    public event Action? PortsChanged;

    /// <summary>Tells listeners the port list may have changed (the service re-reads it every 2 s).</summary>
    public void Rescan() => PortsChanged?.Invoke();

    public IReadOnlyList<MidiPortInfo> Ports()
    {
        if (_seq == 0)
            return [];
        var ports = new List<MidiPortInfo>();
        lock (_gate)
        {
            _ = snd_seq_client_info_malloc(out var cinfo);
            _ = snd_seq_port_info_malloc(out var pinfo);
            try
            {
                snd_seq_client_info_set_client(cinfo, -1);
                while (snd_seq_query_next_client(_seq, cinfo) >= 0)
                {
                    var client = snd_seq_client_info_get_client(cinfo);
                    if (client == _client || client == 0)
                        continue;
                    snd_seq_port_info_set_client(pinfo, client);
                    snd_seq_port_info_set_port(pinfo, -1);
                    while (snd_seq_query_next_port(_seq, pinfo) >= 0)
                    {
                        var caps = snd_seq_port_info_get_capability(pinfo);
                        if ((caps & CapNoExport) != 0)
                            continue;
                        var port = snd_seq_port_info_get_port(pinfo);
                        var name = Marshal.PtrToStringUTF8(snd_seq_port_info_get_name(pinfo)) ?? "";
                        var id = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{client}:{port}");
                        if ((caps & (CapRead | CapSubsRead)) == (CapRead | CapSubsRead))
                            ports.Add(new MidiPortInfo(id, name, true));
                        if ((caps & (CapWrite | CapSubsWrite)) == (CapWrite | CapSubsWrite))
                            ports.Add(new MidiPortInfo(id, name, false));
                    }
                }
            }
            finally
            {
                snd_seq_port_info_free(pinfo);
                snd_seq_client_info_free(cinfo);
            }
        }
        return ports;
    }

    public IMidiInput OpenInput(MidiPortInfo port, IMidiReceiver receiver)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(receiver);
        if (_seq == 0)
            throw new MidiUnavailableException(Unavailable ?? "the ALSA sequencer is not open");
        var parts = port.Id.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var client)
            || !int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var source))
            throw new MidiUnavailableException($"{port.Id} is no ALSA port");
        lock (_gate)
        {
            var mine = snd_seq_create_simple_port(_seq, "PlayMap in " + port.Name, CapWrite | CapSubsWrite, TypeMidiGeneric | TypeApplication);
            if (mine < 0)
                throw new MidiUnavailableException($"ALSA refused a port for {port.Name}: {Marshal.PtrToStringUTF8(snd_strerror(mine))}");
            var rc = snd_seq_connect_from(_seq, mine, client, source);
            if (rc < 0)
            {
                _ = snd_seq_delete_simple_port(_seq, mine);
                throw new MidiUnavailableException($"ALSA could not connect {port.Name}: {Marshal.PtrToStringUTF8(snd_strerror(rc))}");
            }
            if (snd_midi_event_new(4096, out var decoder) < 0)
            {
                _ = snd_seq_delete_simple_port(_seq, mine);
                throw new MidiUnavailableException("ALSA's MIDI event decoder did not open");
            }
            snd_midi_event_no_status(decoder, 1);
            _inputs[mine] = (receiver, decoder);
            if (_reader is null)
            {
                _reader = new Thread(Loop) { IsBackground = true, Name = "PlayMap midi-in" };
                _reader.Start();
            }
            return new Input(this, mine, client, source);
        }
    }

    public IMidiOutput OpenOutput(MidiPortInfo port) =>
        throw new MidiUnavailableException("MIDI output is not in this build");

    /// <summary>The one reader: takes every event the client receives and hands it, as bytes, to the receiver of its destination
    /// port (byte 15 of snd_seq_event_t: type, flags, tag, queue, time[8], source[2], dest[2]).</summary>
    private void Loop()
    {
        var buffer = new byte[4096];
        while (!_stop.IsSet)
        {
            int rc;
            nint ev = 0;
            IMidiReceiver? receiver = null;
            long n = 0;
            lock (_gate)
            {
                rc = _seq == 0 ? -1 : snd_seq_event_input(_seq, out ev);
                if (rc > 0 && _inputs.TryGetValue(Marshal.ReadByte(ev, 15), out var input))
                {
                    receiver = input.Receiver;
                    n = snd_midi_event_decode(input.Decoder, buffer, buffer.Length, ev);
                }
            }
            if (rc == EAgain || rc == 0)
            {
                _stop.Wait(1);
                continue;
            }
            if (rc < 0)
            {
                _stop.Wait(50);
                continue;
            }
            if (receiver is not null && n > 0)
                receiver.Receive(buffer.AsSpan(0, (int)n), _mono.NowNs);
        }
    }

    private void Close(int port, int client, int source)
    {
        lock (_gate)
        {
            if (_inputs.Remove(port, out var input))
                snd_midi_event_free(input.Decoder);
            if (_seq != 0)
            {
                _ = snd_seq_disconnect_from(_seq, port, client, source);
                _ = snd_seq_delete_simple_port(_seq, port);
            }
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _reader?.Join(TimeSpan.FromSeconds(2));
        lock (_gate)
        {
            foreach (var (_, input) in _inputs)
                snd_midi_event_free(input.Decoder);
            _inputs.Clear();
            if (_seq != 0)
                _ = snd_seq_close(_seq);
            _seq = 0;
        }
        _stop.Dispose();
    }

    private sealed class Input(AlsaSeqBackend owner, int port, int client, int source) : IMidiInput
    {
        private int _closed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
                owner.Close(port, client, source);
        }
    }

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int snd_seq_open(out nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int streams, int mode);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int snd_seq_close(nint handle);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint snd_strerror(int errnum);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int snd_seq_set_client_name(nint seq, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int snd_seq_client_id(nint seq);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int snd_seq_client_info_malloc(out nint info);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void snd_seq_client_info_free(nint info);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void snd_seq_client_info_set_client(nint info, int client);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int snd_seq_client_info_get_client(nint info);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int snd_seq_query_next_client(nint seq, nint info);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int snd_seq_port_info_malloc(out nint info);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void snd_seq_port_info_free(nint info);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void snd_seq_port_info_set_client(nint info, int client);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void snd_seq_port_info_set_port(nint info, int port);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int snd_seq_query_next_port(nint seq, nint info);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint snd_seq_port_info_get_capability(nint info);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int snd_seq_port_info_get_port(nint info);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint snd_seq_port_info_get_name(nint info);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int snd_seq_create_simple_port(nint seq, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint caps, uint type);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int snd_seq_delete_simple_port(nint seq, int port);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int snd_seq_connect_from(nint seq, int myPort, int srcClient, int srcPort);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int snd_seq_disconnect_from(nint seq, int myPort, int srcClient, int srcPort);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int snd_seq_event_input(nint seq, out nint ev);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int snd_midi_event_new(nuint bufsize, out nint dev);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void snd_midi_event_free(nint dev);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void snd_midi_event_no_status(nint dev, int on);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint snd_midi_event_decode(nint dev, byte[] buf, nint count, nint ev);
}
