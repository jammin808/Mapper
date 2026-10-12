using System.Text;
using System.Text.Json;

namespace PlayMap.Devices.Tests.Simulators;

/// <summary>
/// A Barco Pulse projector's JSON-RPC 2.0 API on TCP: objects with no delimiter between them, a notification
/// sent ahead of each answer (the driver must skip it), "Method not found" for an unknown method, and the
/// properties the driver uses (system.state, optics.shutter.target/position, image.window.main.source,
/// image.testpattern.show, identity).
/// </summary>
public sealed class BarcoPulseSimulator : FakeTcpServer
{
    private readonly Lock _gate = new();

    public string State { get; set; } = "standby";

    public string Shutter { get; set; } = "Open";

    public string Source { get; set; } = "L1 HDMI";

    public bool Pattern { get; set; }

    protected override async Task ServeAsync(Stream stream, CancellationToken ct)
    {
        while (true)
        {
            var json = await ReadObjectAsync(stream, ct);
            if (json is null)
                return;
            Received.Enqueue(json);
            await WriteAsync(stream, "{\"jsonrpc\":\"2.0\",\"method\":\"property.changed\",\"params\":{\"property\":\"system.time\"}}", ct);
            await WriteAsync(stream, Answer(json), ct);
        }
    }

    private string Answer(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = root.GetProperty("id").GetRawText();
        var method = root.GetProperty("method").GetString();
        var p = root.TryGetProperty("params", out var pe) ? pe : default;
        string? Prop() => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("property", out var x) ? x.GetString() : null;
        string Result(string value) => "{\"jsonrpc\":\"2.0\",\"result\":" + value + ",\"id\":" + id + "}";
        string Error(int code, string message) => "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":" + code + ",\"message\":\"" + message + "\"},\"id\":" + id + "}";
        lock (_gate)
        {
            switch (method)
            {
                case "system.poweron":
                    State = "on";
                    return Result("null");
                case "system.poweroff":
                    State = "standby";
                    return Result("null");
                case "property.get":
                    return Prop() switch
                    {
                        "system.state" => Result(JsonSerializer.Serialize(State)),
                        "optics.shutter.position" or "optics.shutter.target" => Result(JsonSerializer.Serialize(Shutter)),
                        "image.window.main.source" => Result(JsonSerializer.Serialize(Source)),
                        "image.testpattern.show" => Result(Pattern ? "true" : "false"),
                        "system.modelname" => Result("\"F80-4K12\""),
                        "system.serialnumber" => Result("\"2591234567\""),
                        "firmware.firmwareversion" => Result("\"1.11.2\""),
                        _ => Error(-32602, "Unknown property"),
                    };
                case "property.set":
                    var value = p.GetProperty("value");
                    switch (Prop())
                    {
                        case "optics.shutter.target":
                            Shutter = value.GetString() ?? Shutter;
                            return Result("null");
                        case "image.window.main.source":
                            Source = value.GetString() ?? Source;
                            return Result("null");
                        case "image.testpattern.show":
                            Pattern = value.GetBoolean();
                            return Result("null");
                        default:
                            return Error(-32602, "Unknown property");
                    }
                case "optics.zoom.stepforward" or "optics.zoom.stepreverse" or "optics.focus.stepforward" or "optics.focus.stepreverse"
                    or "optics.lensshift.horizontal.stepforward" or "optics.lensshift.horizontal.stepreverse"
                    or "optics.lensshift.vertical.stepforward" or "optics.lensshift.vertical.stepreverse":
                    return Result("null");
                default:
                    return Error(-32601, "Method not found");
            }
        }
    }

    private static async Task<string?> ReadObjectAsync(Stream s, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var depth = 0;
        var inString = false;
        var escaped = false;
        var one = new byte[1];
        while (true)
        {
            if (await s.ReadAsync(one, ct) == 0)
                return null;
            var c = (char)one[0];
            if (depth == 0 && c != '{')
                continue;
            sb.Append(c);
            if (inString)
            {
                if (escaped)
                    escaped = false;
                else if (c == '\\')
                    escaped = true;
                else if (c == '"')
                    inString = false;
                continue;
            }
            if (c == '"')
                inString = true;
            else if (c == '{')
                depth++;
            else if (c == '}' && --depth == 0)
                return sb.ToString();
        }
    }
}
