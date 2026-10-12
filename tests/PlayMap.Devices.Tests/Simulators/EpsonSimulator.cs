using System.Text;
using PlayMap.Devices.Drivers.Epson;

namespace PlayMap.Devices.Tests.Simulators;

/// <summary>
/// An Epson projector's ESC/VP.net: reads the 16-byte CONNECT header (and the 18-byte password header when one is
/// counted), answers OK (0x20), password required (0x41) or wrong (0x43) with the 16-byte answer header, then
/// serves ESC/VP21 lines — "PWR?" → "PWR=01\r:", a set → ":", anything unknown → "ERR\r:".
/// </summary>
public sealed class EpsonSimulator(string password = "") : FakeTcpServer
{
    private readonly Lock _gate = new();

    public string Power { get; set; } = "04";

    public string Mute { get; set; } = "OFF";

    public string Freeze { get; set; } = "OFF";

    public string Source { get; set; } = "30";

    protected override async Task ServeAsync(Stream stream, CancellationToken ct)
    {
        var header = await ReadExactlyAsync(stream, EscVpProtocol.HeaderSize, ct);
        if (header is null || Encoding.ASCII.GetString(header, 0, 10) != "ESC/VP.net" || header[11] != 0x03)
            return;
        var sent = "";
        if (header[15] == 1)
        {
            var sub = await ReadExactlyAsync(stream, EscVpProtocol.SubHeaderSize, ct);
            if (sub is null)
                return;
            sent = Encoding.ASCII.GetString(sub, 2, 16).TrimEnd('\0');
        }
        var status = password.Length == 0 || sent == password
            ? EscVpNetStatus.Ok
            : sent.Length == 0 ? EscVpNetStatus.PasswordRequired : EscVpNetStatus.PasswordWrong;
        var answer = new byte[16];
        Encoding.ASCII.GetBytes("ESC/VP.net").CopyTo(answer, 0);
        answer[10] = 0x10;
        answer[11] = 0x03;
        answer[14] = (byte)status;
        await WriteAsync(stream, answer, ct);
        if (status != EscVpNetStatus.Ok)
            return;
        while (true)
        {
            var line = await ReadUntilAsync(stream, (byte)'\r', ct);
            if (line is null)
                return;
            Received.Enqueue(line);
            await WriteAsync(stream, Answer(line.Trim()), ct);
        }
    }

    private string Answer(string line)
    {
        lock (_gate)
        {
            switch (line)
            {
                case "PWR?":
                    return "PWR=" + Power + "\r:";
                case "PWR ON":
                    Power = "01";
                    return ":";
                case "PWR OFF":
                    Power = "04";
                    return ":";
                case "MUTE?":
                    return Power == "01" ? "MUTE=" + Mute + "\r:" : "ERR\r:";
                case "MUTE ON" or "MUTE OFF":
                    if (Power != "01")
                        return "ERR\r:";
                    Mute = line[5..];
                    return ":";
                case "FREEZE?":
                    return "FREEZE=" + Freeze + "\r:";
                case "FREEZE ON" or "FREEZE OFF":
                    Freeze = line[7..];
                    return ":";
                case "SOURCE?":
                    return "SOURCE=" + Source + "\r:";
                case "LAMP?":
                    return "LAMP=812\r:";
                case "SNO?":
                    return "SNO=X4AB1234567\r:";
                case "ERR?":
                    return "ERR=00\r:";
                case "TESTPATTERN 00":
                    return ":";
            }
            if (line.StartsWith("SOURCE ", StringComparison.Ordinal))
            {
                Source = line[7..];
                return ":";
            }
            if (line.StartsWith("POPLP ", StringComparison.Ordinal))
                return ":";
            return "ERR\r:";
        }
    }
}
