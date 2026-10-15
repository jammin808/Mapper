using System.Globalization;
using System.Text;

namespace PlayMap.Core.Control;

/// <summary>Where a control input comes from.</summary>
public enum InputSource : byte { None = 0, Midi = 1, Osc = 2, ArtNet = 3, Sacn = 4 }

/// <summary>The MIDI message family an input is (none for OSC and DMX).</summary>
public enum MidiFamily : byte { None, Note, Cc, Cc14, Nrpn, Rpn, Bend, Program, Pressure, PolyPressure, Msc, Mmc }

/// <summary>One control input, as a value: its source, the surface it is on ("" = any surface), the MIDI family, channel
/// (1–16) and number, a DMX channel's fine partner (0 = 8-bit), and an OSC address (or an MSC/MMC command word). Two
/// addresses that say the same words are equal; <see cref="Parse"/> and <see cref="Format"/> round-trip.</summary>
/// <param name="Number">MIDI: note, CC, NRPN/RPN parameter, program, device id (MSC/MMC); OSC: the argument index; DMX: the
/// channel 1–512.</param>
/// <param name="Universe">DMX: the Art-Net port-address 0–32767 or the sACN universe 1–63999.</param>
public readonly record struct InputAddress(InputSource Source, string Surface, MidiFamily Family, int Channel, int Number,
    int Universe, int Fine, string Path)
{
    public static InputAddress Midi(MidiFamily family, int channel, int number, string surface = "") =>
        new(InputSource.Midi, surface, family, channel, number, 0, 0, "");

    public static InputAddress Osc(string address, int arg = 0, string surface = "") =>
        new(InputSource.Osc, surface, MidiFamily.None, 0, arg, 0, 0, address);

    public static InputAddress Dmx(InputSource source, int universe, int channel, int fine = 0, string surface = "") =>
        new(source, surface, MidiFamily.None, 0, channel, universe, fine, "");

    /// <summary>The same input on any surface.</summary>
    public InputAddress AnySurface => this with { Surface = "" };

    /// <summary>An input whose values are continuous (a fader, a knob, a DMX channel, an OSC number): bound with "*".</summary>
    public bool IsContinuous => Source switch
    {
        InputSource.Midi => Family is MidiFamily.Cc or MidiFamily.Cc14 or MidiFamily.Nrpn or MidiFamily.Rpn or MidiFamily.Bend
            or MidiFamily.Pressure or MidiFamily.PolyPressure,
        InputSource.Osc or InputSource.ArtNet or InputSource.Sacn => true,
        _ => false,
    };

    /// <summary>The words of this input: "MIDI APC40 CC 1 48", "OSC /fader/1", "ARTNET 0 101+102".</summary>
    public string Format()
    {
        var b = new StringBuilder();
        b.Append(Source switch { InputSource.Midi => "MIDI", InputSource.Osc => "OSC", InputSource.ArtNet => "ARTNET", InputSource.Sacn => "SACN", _ => "NONE" });
        if (Surface.Length > 0)
            b.Append(' ').Append(Surface);
        var inv = CultureInfo.InvariantCulture;
        switch (Source)
        {
            case InputSource.Midi:
                b.Append(' ').Append(FamilyWord(Family));
                switch (Family)
                {
                    case MidiFamily.Bend or MidiFamily.Pressure:
                        b.Append(' ').Append(Channel.ToString(inv));
                        break;
                    case MidiFamily.Msc or MidiFamily.Mmc:
                        b.Append(' ').Append(Number.ToString(inv)).Append(' ').Append(Path);
                        break;
                    default:
                        b.Append(' ').Append(Channel.ToString(inv)).Append(' ').Append(Number.ToString(inv));
                        break;
                }
                break;
            case InputSource.Osc:
                b.Append(' ').Append(Path);
                if (Number > 0)
                    b.Append(" ARG ").Append(Number.ToString(inv));
                break;
            case InputSource.ArtNet or InputSource.Sacn:
                b.Append(' ').Append(Universe.ToString(inv)).Append(' ').Append(Number.ToString(inv));
                if (Fine > 0)
                    b.Append('+').Append(Fine.ToString(inv));
                break;
        }
        return b.ToString();
    }

    public override string ToString() => Format();

    private static string FamilyWord(MidiFamily f) => f switch
    {
        MidiFamily.Note => "NOTE", MidiFamily.Cc => "CC", MidiFamily.Cc14 => "CC14", MidiFamily.Nrpn => "NRPN", MidiFamily.Rpn => "RPN",
        MidiFamily.Bend => "BEND", MidiFamily.Program => "PROGRAM", MidiFamily.Pressure => "PRESSURE", MidiFamily.PolyPressure => "POLYPRESSURE",
        MidiFamily.Msc => "MSC", MidiFamily.Mmc => "MMC", _ => "?",
    };

    private static MidiFamily FamilyOf(string word) => word.ToUpperInvariant() switch
    {
        "NOTE" => MidiFamily.Note, "CC" => MidiFamily.Cc, "CC14" => MidiFamily.Cc14, "NRPN" => MidiFamily.Nrpn, "RPN" => MidiFamily.Rpn,
        "BEND" => MidiFamily.Bend, "PROGRAM" => MidiFamily.Program, "PRESSURE" => MidiFamily.Pressure, "POLYPRESSURE" => MidiFamily.PolyPressure,
        "MSC" => MidiFamily.Msc, "MMC" => MidiFamily.Mmc, _ => MidiFamily.None,
    };

    /// <summary>The MSC commands a binding names (MIDI Show Control 1.1, command table).</summary>
    public static readonly IReadOnlyDictionary<string, byte> MscCommands = new Dictionary<string, byte>(StringComparer.Ordinal)
    {
        ["GO"] = 0x01, ["STOP"] = 0x02, ["RESUME"] = 0x03, ["TIMED_GO"] = 0x04, ["LOAD"] = 0x05, ["SET"] = 0x06, ["FIRE"] = 0x07,
        ["ALL_OFF"] = 0x08, ["RESTORE"] = 0x09, ["RESET"] = 0x0A, ["GO_OFF"] = 0x0B,
    };

    /// <summary>The MMC commands a binding names (MIDI Machine Control, command table).</summary>
    public static readonly IReadOnlyDictionary<string, byte> MmcCommands = new Dictionary<string, byte>(StringComparer.Ordinal)
    {
        ["STOP"] = 0x01, ["PLAY"] = 0x02, ["DEFERRED_PLAY"] = 0x03, ["FAST_FORWARD"] = 0x04, ["REWIND"] = 0x05, ["RECORD_STROBE"] = 0x06,
        ["RECORD_EXIT"] = 0x07, ["RECORD_PAUSE"] = 0x08, ["PAUSE"] = 0x09, ["EJECT"] = 0x0A, ["CHASE"] = 0x0B, ["RESET"] = 0x0D,
        ["LOCATE"] = 0x44,
    };

    /// <summary>Reads input words; null with the reason when they name no input. Patterns' bare rows ("NOTE 1 53", "CC 1 7") read
    /// as MIDI.</summary>
    public static InputAddress? Parse(IReadOnlyList<string> words, out string why)
    {
        ArgumentNullException.ThrowIfNull(words);
        why = "";
        if (words.Count == 0)
        {
            why = "no input words — MIDI <surface> CC 1 48, OSC /fader/1, ARTNET 0 101";
            return null;
        }
        var list = words.Select(w => w.Trim()).Where(w => w.Length > 0).ToList();
        var head = list[0].ToUpperInvariant();
        if (FamilyOf(head) != MidiFamily.None)
            list.Insert(0, "MIDI");
        head = list[0].ToUpperInvariant();
        var rest = list.Skip(1).ToList();
        // A trailing "*" (Patterns' value marker) says nothing more.
        if (rest.Count > 0 && rest[^1] == "*")
            rest.RemoveAt(rest.Count - 1);
        return head switch
        {
            "MIDI" => ParseMidi(rest, out why),
            "OSC" => ParseOsc(rest, out why),
            "ARTNET" => ParseDmx(InputSource.ArtNet, rest, 0, 32767, out why),
            "SACN" => ParseDmx(InputSource.Sacn, rest, 1, 63999, out why),
            _ => Refuse($"'{list[0]}' is no input — MIDI, OSC, ARTNET or SACN", out why),
        };
    }

    /// <summary>Reads input words from text ("MIDI APC40 CC 1 48").</summary>
    public static InputAddress? Parse(string words, out string why) =>
        Parse((words ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), out why);

    private static InputAddress? Refuse(string reason, out string why)
    {
        why = reason;
        return null;
    }

    /// <summary>A surface name: letters, digits, '-', '_', 1–32 characters.</summary>
    public static bool IsSurfaceName(string word) =>
        word.Length is > 0 and <= 32 && word.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool Int(string s, int min, int max, out int v) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v) && v >= min && v <= max;

    private static InputAddress? ParseMidi(List<string> rest, out string why)
    {
        var surface = "";
        if (rest.Count > 0 && FamilyOf(rest[0]) == MidiFamily.None)
        {
            if (!IsSurfaceName(rest[0]))
                return Refuse($"'{rest[0]}' is neither a MIDI message nor a surface name", out why);
            surface = rest[0];
            rest = rest.Skip(1).ToList();
        }
        if (rest.Count == 0)
            return Refuse("MIDI [<surface>] NOTE|CC|CC14|NRPN|RPN|BEND|PROGRAM|PRESSURE|POLYPRESSURE|MSC|MMC …", out why);
        var family = FamilyOf(rest[0]);
        var a = rest.Skip(1).ToList();
        why = "";
        switch (family)
        {
            case MidiFamily.Bend or MidiFamily.Pressure:
                if (a.Count != 1 || !Int(a[0], 1, 16, out var ch1))
                    return Refuse($"MIDI {rest[0].ToUpperInvariant()} <channel 1-16>", out why);
                return Midi(family, ch1, 0, surface);
            case MidiFamily.Msc or MidiFamily.Mmc:
            {
                var table = family == MidiFamily.Msc ? MscCommands : MmcCommands;
                if (a.Count != 2 || !Int(a[0], 0, 127, out var dev) || !table.ContainsKey(a[1].ToUpperInvariant()))
                    return Refuse($"MIDI {rest[0].ToUpperInvariant()} <device 0-127> <{string.Join('|', table.Keys)}>", out why);
                return new InputAddress(InputSource.Midi, surface, family, 0, dev, 0, 0, a[1].ToUpperInvariant());
            }
            case MidiFamily.None:
                return Refuse($"'{rest[0]}' is no MIDI message — NOTE, CC, CC14, NRPN, RPN, BEND, PROGRAM, PRESSURE, POLYPRESSURE, MSC or MMC", out why);
        }
        var max = family switch { MidiFamily.Cc14 => 31, MidiFamily.Nrpn or MidiFamily.Rpn => 16383, _ => 127 };
        if (a.Count != 2 || !Int(a[0], 1, 16, out var ch) || !Int(a[1], 0, max, out var n))
            return Refuse($"MIDI {rest[0].ToUpperInvariant()} <channel 1-16> <0-{max.ToString(CultureInfo.InvariantCulture)}>", out why);
        return Midi(family, ch, n, surface);
    }

    private static InputAddress? ParseOsc(List<string> rest, out string why)
    {
        var surface = "";
        if (rest.Count > 0 && !rest[0].StartsWith('/'))
        {
            if (!IsSurfaceName(rest[0]))
                return Refuse($"'{rest[0]}' is neither an OSC address nor a surface name", out why);
            surface = rest[0];
            rest = rest.Skip(1).ToList();
        }
        if (rest.Count == 0 || !rest[0].StartsWith('/') || rest[0].Length > 256)
            return Refuse("OSC [<surface>] </address> [ARG <n>]", out why);
        var arg = 0;
        if (rest.Count == 3 && rest[1].Equals("ARG", StringComparison.OrdinalIgnoreCase) && Int(rest[2], 0, 63, out var n))
            arg = n;
        else if (rest.Count != 1)
            return Refuse("OSC [<surface>] </address> [ARG <n>]", out why);
        why = "";
        return Osc(rest[0], arg, surface);
    }

    private static InputAddress? ParseDmx(InputSource source, List<string> rest, int minUniverse, int maxUniverse, out string why)
    {
        var word = source == InputSource.ArtNet ? "ARTNET" : "SACN";
        var surface = "";
        if (rest.Count == 3)
        {
            if (!IsSurfaceName(rest[0]))
                return Refuse($"'{rest[0]}' is no surface name", out why);
            surface = rest[0];
            rest = rest.Skip(1).ToList();
        }
        var usage = $"{word} [<surface>] <universe {minUniverse.ToString(CultureInfo.InvariantCulture)}-{maxUniverse.ToString(CultureInfo.InvariantCulture)}> <channel 1-512>[+<fine>]";
        if (rest.Count != 2 || !Int(rest[0], minUniverse, maxUniverse, out var universe))
            return Refuse(usage, out why);
        var parts = rest[1].Split('+');
        if (parts.Length > 2 || !Int(parts[0], 1, 512, out var channel))
            return Refuse(usage, out why);
        var fine = 0;
        if (parts.Length == 2 && (!Int(parts[1], 1, 512, out fine) || fine == channel))
            return Refuse(usage, out why);
        why = "";
        return Dmx(source, universe, channel, fine, surface);
    }
}

/// <summary>What an input did: pressed, released, moved to a value, turned by ticks, chose a program, or fired.</summary>
public enum InputKind : byte { Press, Release, Axis, Relative, Program, Trigger }

/// <summary>One control event, as a value: what moved, how, and where it is now. <see cref="Value"/> is normalised (0–1) for
/// presses and axes, signed ticks for a relative encoder; <see cref="Raw"/> is the integer the device sent and
/// <see cref="Bits"/> its resolution (7, 14, 8, 16; 0 for a float).</summary>
public readonly record struct InputEvent(InputAddress Address, InputKind Kind, double Value, int Raw, byte Bits, long MonoNs)
{
    /// <summary>Where it came from (a MIDI port, an OSC sender, a DMX source), for the monitor; "" = unknown.</summary>
    public string Origin { get; init; } = "";
}
