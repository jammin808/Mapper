using PlayMap.Devices.Drivers.Panasonic;

namespace PlayMap.Devices.Tests.Simulators;

/// <summary>
/// A Panasonic projector's NTCONTROL per Panasonic's LAN protocol document: greets "NTCONTROL 1 seed" (or
/// "NTCONTROL 0"), checks the 32-character MD5 token in front of every command (ERRA when it is wrong), answers a
/// control command with "00" and its three letters, a query with "00" and its value, an unknown command ERR1 and
/// a bad value ERR2.
/// </summary>
public sealed class PanasonicSimulator(string user = "", string password = "", string seed = "a1b2c3d4") : FakeTcpServer
{
    private readonly Lock _gate = new();

    public string Power { get; set; } = "000";

    public string Shutter { get; set; } = "0";

    public string Freeze { get; set; } = "0";

    public string Input { get; set; } = "HD1";

    public string Pattern { get; set; } = "00";

    public string Geometry { get; set; } = "+00001";

    public string EdgeBlend { get; set; } = "+00000";

    public int LensH { get; set; } = -120;

    public int LensV { get; set; } = 300;

    public int LensF { get; set; } = 5;

    protected override async Task ServeAsync(Stream stream, CancellationToken ct)
    {
        var protectedMode = password.Length > 0;
        await WriteAsync(stream, protectedMode ? $"NTCONTROL 1 {seed}\r" : "NTCONTROL 0\r", ct);
        var token = protectedMode ? NtControlProtocol.Token(user.Length > 0 ? user : "admin1", password, seed) : "";
        while (true)
        {
            var line = await ReadUntilAsync(stream, (byte)'\r', ct);
            if (line is null)
                return;
            if (protectedMode)
            {
                if (line.Length < 32 || !line[..32].Equals(token, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(stream, "ERRA\r", ct);
                    return;
                }
                line = line[32..];
            }
            if (!line.StartsWith("00", StringComparison.Ordinal))
            {
                await WriteAsync(stream, "ERR1\r", ct);
                continue;
            }
            var command = line[2..];
            Received.Enqueue(command);
            await WriteAsync(stream, Answer(command) + "\r", ct);
        }
    }

    private string Answer(string cmd)
    {
        lock (_gate)
        {
            switch (cmd)
            {
                case "PON":
                    Power = "001";
                    return "00PON";
                case "POF":
                    Power = "000";
                    return "00POF";
                case "QPW":
                    return "00" + Power;
                case "Q$S":
                    return Power == "001" ? "002" : "000";
                case "OSH:0" or "OSH:1":
                    Shutter = cmd[^1..];
                    return "00OSH";
                case "QSH":
                    return "00" + Shutter;
                case "OFZ:0" or "OFZ:1":
                    Freeze = cmd[^1..];
                    return "00OFZ";
                case "QFZ":
                    return "00" + Freeze;
                case "QIN":
                    return "00" + Input;
                case "QTS":
                    return "00" + Pattern;
                case "QID":
                    return "00PT-RZ120";
                case "QSN":
                    return "00SW0101234";
                case "QVX:SVRS0":
                    return "00SVRS0=1.00.01";
                case "Q$L:1":
                    return "000420";
                case "QTM:0":
                    return "000031/0088";
                case "QTM:1":
                    return "000040/0104";
                case "QTM:2":
                    return "000035/0095";
                case "QVX:NSGS1":
                    return "00NSGS1=1080/60p";
                case "QVX:GMMI0":
                    return "00GMMI0=" + Geometry;
                case "QVX:EDBI0":
                    return "00EDBI0=" + EdgeBlend;
                case "QVX:LNSI7":
                    return "00LNSI7=" + NtControlProtocol.Signed5(LensH);
                case "QVX:LNSI8":
                    return "00LNSI8=" + NtControlProtocol.Signed5(LensV);
                case "QVX:LNSI9":
                    return "00LNSI9=" + NtControlProtocol.Signed5(LensF);
            }
            if (cmd.StartsWith("IIS:", StringComparison.Ordinal))
            {
                var code = cmd[4..];
                if (code is not ("HD1" or "HD2" or "SD1" or "DVI" or "DL1"))
                    return "ERR2";
                Input = code;
                return "00IIS";
            }
            if (cmd.StartsWith("OTS:", StringComparison.Ordinal))
            {
                Pattern = cmd[4..];
                return "00OTS";
            }
            if (cmd.StartsWith("VXX:", StringComparison.Ordinal) && cmd.IndexOf('=') is var eq and > 4)
            {
                var name = cmd[4..eq];
                var value = cmd[(eq + 1)..];
                switch (name)
                {
                    case "GMMI0":
                        Geometry = value;
                        return "00VXX";
                    case "EDBI0":
                        EdgeBlend = value;
                        return "00VXX";
                    case "LNSI7":
                        LensH = NtControlProtocol.Number(value) ?? LensH;
                        return "00VXX";
                    case "LNSI8":
                        LensV = NtControlProtocol.Number(value) ?? LensV;
                        return "00VXX";
                    case "LNSI9":
                        LensF = NtControlProtocol.Number(value) ?? LensF;
                        return "00VXX";
                    case "LNMI1":
                        return "00VXX";
                }
            }
            return "ERR1";
        }
    }
}
