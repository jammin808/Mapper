namespace PlayMap.Core.Control.Midi;

/// <summary>A MIDI port of this machine as a backend names it.</summary>
public sealed record MidiPortInfo(string Id, string Name, bool IsInput);

/// <summary>Receives a port's bytes on the backend's own thread: never block; the span is valid only during the call.</summary>
public interface IMidiReceiver
{
    void Receive(ReadOnlySpan<byte> message, long monoNs);
}

/// <summary>An open input port; disposing it stops its bytes.</summary>
public interface IMidiInput : IDisposable;

/// <summary>An open output port.</summary>
public interface IMidiOutput : IDisposable
{
    bool Send(ReadOnlySpan<byte> message);
}

/// <summary>The machine's MIDI as the control side sees it ("winmm", "alsa-seq", "coremidi", "fake"). A backend that cannot run
/// here says why in <see cref="Unavailable"/> and lists no ports; Open throws <see cref="MidiUnavailableException"/> with words.</summary>
public interface IMidiBackend : IDisposable
{
    string Name { get; }

    /// <summary>Why the backend cannot run on this machine, or null when it can.</summary>
    string? Unavailable { get; }

    IReadOnlyList<MidiPortInfo> Ports();

    IMidiInput OpenInput(MidiPortInfo port, IMidiReceiver receiver);

    IMidiOutput OpenOutput(MidiPortInfo port);

    /// <summary>The port list changed (a controller plugged in or out).</summary>
    event Action? PortsChanged;
}

/// <summary>A MIDI port that cannot open, with the words why.</summary>
public sealed class MidiUnavailableException(string message) : Exception(message);

/// <summary>A port-name glob: '*' any run, '?' one character, case-insensitive.</summary>
public static class PortGlob
{
    public static bool Matches(string glob, string name)
    {
        ArgumentNullException.ThrowIfNull(glob);
        ArgumentNullException.ThrowIfNull(name);
        return Match(glob.AsSpan(), name.AsSpan());
    }

    private static bool Match(ReadOnlySpan<char> g, ReadOnlySpan<char> n)
    {
        while (g.Length > 0)
        {
            if (g[0] == '*')
            {
                for (var i = 0; i <= n.Length; i++)
                    if (Match(g[1..], n[i..]))
                        return true;
                return false;
            }
            if (n.Length == 0 || (g[0] != '?' && char.ToUpperInvariant(g[0]) != char.ToUpperInvariant(n[0])))
                return false;
            g = g[1..];
            n = n[1..];
        }
        return n.Length == 0;
    }
}
