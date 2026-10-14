using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PlayMap.Core.Control.Midi;
using PlayMap.Core.Time;

namespace PlayMap.Devices.Midi;

/// <summary>Windows MIDI through WinMM (winmm.dll from System32): midiInGetNumDevs and midiInGetDevCapsW list the inputs;
/// midiInOpen with a callback function opens one, and its short messages (MIM_DATA: status | data1 &lt;&lt; 8 | data2 &lt;&lt; 16) are
/// handed to the receiver as bytes. The callback copies and returns; it calls no midiIn function (a WinMM rule). Windows 11 24H2
/// and later route WinMM through Windows MIDI Services, so several programs may share a port. SysEx input (MIM_LONGDATA
/// buffers) and output are not in this build.</summary>
[SupportedOSPlatform("windows")]
public sealed class WinMmBackend : IMidiBackend
{
    private const int CallbackFunction = 0x30000;
    private const uint MimData = 0x3C3;

    private static readonly MidiInProc Callback = OnMessage;
    private static readonly Lock Gate = new();
    private static readonly Dictionary<nint, (IMidiReceiver Receiver, IMonoClock Mono)> Open = [];
    private readonly IMonoClock _mono;

    public WinMmBackend(IMonoClock? mono = null)
    {
        _mono = mono ?? MonoClock.Instance;
    }

    public string Name => "winmm";

    public string? Unavailable => null;

    public event Action? PortsChanged;

    /// <summary>Tells listeners the port list may have changed (the service re-reads it every 2 s).</summary>
    public void Rescan() => PortsChanged?.Invoke();

    public IReadOnlyList<MidiPortInfo> Ports()
    {
        var ports = new List<MidiPortInfo>();
        var n = midiInGetNumDevs();
        for (uint i = 0; i < n; i++)
        {
            var caps = new MidiInCaps();
            if (midiInGetDevCapsW((nuint)i, ref caps, (uint)Marshal.SizeOf<MidiInCaps>()) == 0)
                ports.Add(new MidiPortInfo(i.ToString(System.Globalization.CultureInfo.InvariantCulture), caps.Name, true));
        }
        return ports;
    }

    public IMidiInput OpenInput(MidiPortInfo port, IMidiReceiver receiver)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(receiver);
        if (!uint.TryParse(port.Id, System.Globalization.CultureInfo.InvariantCulture, out var id))
            throw new MidiUnavailableException($"{port.Id} is no WinMM device");
        var rc = midiInOpen(out var handle, id, Marshal.GetFunctionPointerForDelegate(Callback), 0, CallbackFunction);
        if (rc != 0)
            throw new MidiUnavailableException($"WinMM refused {port.Name} (MMRESULT {rc}) — another program may hold it");
        lock (Gate)
            Open[handle] = (receiver, _mono);
        _ = midiInStart(handle);
        return new Input(handle);
    }

    public IMidiOutput OpenOutput(MidiPortInfo port) => throw new MidiUnavailableException("MIDI output is not in this build");

    public void Dispose()
    {
        // Each input closes itself; the backend holds nothing of its own.
    }

    private static void OnMessage(nint handle, uint message, nint instance, nint param1, nint param2)
    {
        if (message != MimData)
            return;
        (IMidiReceiver Receiver, IMonoClock Mono) target;
        lock (Gate)
        {
            if (!Open.TryGetValue(handle, out target))
                return;
        }
        var packed = (uint)param1;
        Span<byte> bytes = [(byte)packed, (byte)(packed >> 8), (byte)(packed >> 16)];
        var length = (bytes[0] & 0xF0) is 0xC0 or 0xD0 ? 2 : bytes[0] >= 0xF8 ? 1 : 3;
        target.Receiver.Receive(bytes[..length], target.Mono.NowNs);
    }

    private sealed class Input(nint handle) : IMidiInput
    {
        private int _closed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;
            _ = midiInStop(handle);
            _ = midiInReset(handle);
            _ = midiInClose(handle);
            lock (Gate)
                Open.Remove(handle);
        }

        [DllImport("winmm.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint midiInStop(nint handle);

        [DllImport("winmm.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint midiInReset(nint handle);

        [DllImport("winmm.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint midiInClose(nint handle);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void MidiInProc(nint handle, uint message, nint instance, nint param1, nint param2);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MidiInCaps
    {
        public ushort Mid;
        public ushort Pid;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;
        public uint Support;
    }

    [DllImport("winmm.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint midiInGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint midiInGetDevCapsW(nuint id, ref MidiInCaps caps, uint size);

    [DllImport("winmm.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint midiInOpen(out nint handle, uint id, nint callback, nint instance, int flags);

    [DllImport("winmm.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint midiInStart(nint handle);
}

/// <summary>The MIDI backend this machine runs: WinMM on Windows, the ALSA sequencer on Linux; elsewhere one that says MIDI input
/// is not in this build for the platform (CoreMIDI is next round).</summary>
public static class MidiBackends
{
    public static IMidiBackend ForThisMachine(string preferred, IMonoClock? mono = null)
    {
        if (preferred == "fake")
            return new FakeMidiBackend();
        if (OperatingSystem.IsWindows())
            return new WinMmBackend(mono);
        if (OperatingSystem.IsLinux())
            return new AlsaSeqBackend(mono);
        return new NoMidi($"MIDI input on {System.Runtime.InteropServices.RuntimeInformation.OSDescription} is not in this build");
    }

    /// <summary>A backend that cannot run here, with the words why.</summary>
    public sealed class NoMidi(string why) : IMidiBackend
    {
        public string Name => "none";

        public string? Unavailable => why;

        public event Action? PortsChanged
        {
            add
            {
                // A backend that cannot run has no ports to change.
            }
            remove
            {
                // Nothing was kept.
            }
        }

        public IReadOnlyList<MidiPortInfo> Ports() => [];

        public IMidiInput OpenInput(MidiPortInfo port, IMidiReceiver receiver) => throw new MidiUnavailableException(why);

        public IMidiOutput OpenOutput(MidiPortInfo port) => throw new MidiUnavailableException(why);

        public void Dispose()
        {
            // Nothing is open.
        }
    }
}
