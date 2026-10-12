using PlayMap.Devices.Drivers.Sony;

namespace PlayMap.Devices.Tests.Simulators;

/// <summary>
/// A Sony projector's ADCP as the driver understands it: greets "NOKEY" or a challenge, wants SHA-256(challenge +
/// password) back ("OK", or "err_auth" and hang up), answers sets "ok", queries with a quoted value, and unknown
/// words "err_cmd".
/// </summary>
public sealed class SonyAdcpSimulator(string password = "", string challenge = "7f3a9c21") : FakeTcpServer
{
    private readonly Lock _gate = new();

    public string Power { get; set; } = "standby";

    public string Blank { get; set; } = "off";

    public string Input { get; set; } = "hdmi1";

    protected override async Task ServeAsync(Stream stream, CancellationToken ct)
    {
        if (password.Length == 0)
        {
            await WriteAsync(stream, "NOKEY\r\n", ct);
        }
        else
        {
            await WriteAsync(stream, challenge + "\r\n", ct);
            var hash = (await ReadUntilAsync(stream, (byte)'\n', ct))?.TrimEnd('\r');
            if (hash != AdcpProtocol.Hash(challenge, password))
            {
                await WriteAsync(stream, "err_auth\r\n", ct);
                return;
            }
            await WriteAsync(stream, "OK\r\n", ct);
        }
        while (true)
        {
            var line = (await ReadUntilAsync(stream, (byte)'\n', ct))?.TrimEnd('\r');
            if (line is null)
                return;
            Received.Enqueue(line);
            await WriteAsync(stream, Answer(line) + "\r\n", ct);
        }
    }

    private string Answer(string line)
    {
        lock (_gate)
        {
            switch (line)
            {
                case "power \"on\"":
                    Power = "on";
                    return "ok";
                case "power \"off\"":
                    Power = "standby";
                    return "ok";
                case "power_status ?":
                    return "\"" + Power + "\"";
                case "blank \"on\"" or "blank \"off\"":
                    if (Power != "on")
                        return "err_inactive";
                    Blank = line.Contains("on", StringComparison.Ordinal) ? "on" : "off";
                    return "ok";
                case "blank ?":
                    return "\"" + Blank + "\"";
                case "input ?":
                    return "\"" + Input + "\"";
                case "modelname ?":
                    return "\"VPL-FHZ91\"";
                case "serialnum ?":
                    return "\"1234567\"";
                default:
                    return "err_cmd";
            }
        }
    }
}
