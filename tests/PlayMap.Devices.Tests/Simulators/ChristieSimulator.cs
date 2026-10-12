using System.Globalization;

namespace PlayMap.Devices.Tests.Simulators;

/// <summary>
/// A Christie projector's serial protocol on TCP: "(CODE value)" sets answered with silence, "(CODE?)" queries
/// answered "(CODE!value)", an error frame for an unknown code (009) or a bad value (003), and an unsolicited FYI
/// frame on connect (the driver must skip it). The error frame's layout follows the public implementations' parse
/// rule ("ERR" then the code), not a quoted manual line.
/// </summary>
public sealed class ChristieSimulator : FakeTcpServer
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _values = new(StringComparer.Ordinal)
    {
        ["PWR"] = 0, ["SHU"] = 0, ["FRZ"] = 0, ["SIN"] = 1, ["ITP"] = 0, ["LPH"] = 1234,
        ["LHO"] = -120, ["LVO"] = 300, ["ZOM"] = 15, ["FCS"] = -7, ["WPS"] = 1,
    };

    public int Get(string code)
    {
        lock (_gate)
            return _values[code];
    }

    public void Set(string code, int value)
    {
        lock (_gate)
            _values[code] = value;
    }

    protected override async Task ServeAsync(Stream stream, CancellationToken ct)
    {
        await WriteAsync(stream, "(0002FYI 001 001 000 000 \"Power\")", ct);
        while (true)
        {
            var frame = await ReadUntilAsync(stream, (byte)')', ct);
            if (frame is null)
                return;
            var inner = frame[(frame.LastIndexOf('(') + 1)..].Trim();
            Received.Enqueue("(" + inner + ")");
            var answer = Answer(inner);
            if (answer.Length > 0)
                await WriteAsync(stream, answer, ct);
        }
    }

    private string Answer(string inner)
    {
        lock (_gate)
        {
            if (inner.EndsWith('?'))
            {
                var code = inner[..^1].Trim();
                return _values.TryGetValue(code, out var v)
                    ? "(" + code + "!" + v.ToString("000", CultureInfo.InvariantCulture) + ")"
                    : "(ERR009 \"" + code + ": Unknown Request\")";
            }
            var space = inner.IndexOf(' ', StringComparison.Ordinal);
            var c = space < 0 ? inner : inner[..space];
            var arg = space < 0 ? "" : inner[(space + 1)..].Trim();
            if (!_values.ContainsKey(c))
                return "(ERR009 \"" + c + ": Unknown Request\")";
            if (!int.TryParse(arg, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                || (c is "SHU" or "FRZ" or "PWR" && value is not (0 or 1)))
                return "(ERR003 \"" + c + ": Invalid Parameter\")";
            _values[c] = value;
            return "";
        }
    }
}
