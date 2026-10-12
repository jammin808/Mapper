using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PlayMap.Devices.Commands;
using PlayMap.Devices.Model;
using PlayMap.Devices.Transport;

namespace PlayMap.Devices.Drivers.Barco;

/// <summary>A JSON-RPC answer: the result as JSON text ("null", "\"on\"") or the error's code and message.</summary>
public sealed record PulseReply(long Id, string ResultJson, int ErrorCode, string ErrorMessage)
{
    public bool IsError => ErrorMessage.Length > 0 || ErrorCode != 0;

    /// <summary>The result as a string when it is one ("on"), else its JSON text.</summary>
    public string ResultText
    {
        get
        {
            if (ResultJson.Length > 1 && ResultJson[0] == '"')
            {
                using var doc = JsonDocument.Parse(ResultJson);
                return doc.RootElement.GetString() ?? "";
            }
            return ResultJson;
        }
    }
}

/// <summary>
/// Barco's Pulse API as pure functions: JSON-RPC 2.0 over TCP 9090, one JSON object per message with no
/// delimiter between them. "system.poweron", "property.get" {"property":"system.state"}, "property.set"
/// {"property":"optics.shutter.target","value":"Closed"}; a message without an id is a notification.
/// </summary>
public static class PulseProtocol
{
    /// <summary>A request with a fresh id; <paramref name="parameters"/> is written as the params object.</summary>
    public static string Request(long id, string method, IReadOnlyList<KeyValuePair<string, object?>>? parameters = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WriteString("method", method);
            w.WriteStartObject("params");
            foreach (var (key, value) in parameters ?? [])
            {
                switch (value)
                {
                    case null:
                        w.WriteNull(key);
                        break;
                    case bool b:
                        w.WriteBoolean(key, b);
                        break;
                    case int i:
                        w.WriteNumber(key, i);
                        break;
                    case double d:
                        w.WriteNumber(key, d);
                        break;
                    default:
                        w.WriteString(key, Convert.ToString(value, CultureInfo.InvariantCulture));
                        break;
                }
            }
            w.WriteEndObject();
            w.WriteNumber("id", id);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>A response, or null for a notification (no id) or text that is not JSON-RPC.</summary>
    public static PulseReply? ParseReply(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var idEl))
                return null;
            long id;
            if (idEl.ValueKind == JsonValueKind.Number)
                id = idEl.GetInt64();
            else if (idEl.ValueKind != JsonValueKind.String || !long.TryParse(idEl.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out id))
                return null;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                var code = err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : -1;
                var message = err.TryGetProperty("message", out var m) ? m.ToString() : "error";
                return new PulseReply(id, "", code, message.Length > 0 ? message : "error");
            }
            var result = root.TryGetProperty("result", out var r) ? r.GetRawText() : "null";
            return new PulseReply(id, result, 0, "");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>system.state: "on"; "ready", "standby", "eco" are off; "conditioning"/"boot" on the way up;
    /// "deconditioning" on the way down.</summary>
    public static PowerState Power(string state) => state.ToLowerInvariant() switch
    {
        "on" => PowerState.On,
        "ready" or "standby" or "eco" => PowerState.Standby,
        "conditioning" or "boot" => PowerState.WarmingUp,
        "deconditioning" => PowerState.CoolingDown,
        _ => PowerState.Unknown,
    };

    public static string LensStepMethod(LensAxis axis, bool forward) => axis switch
    {
        LensAxis.Zoom => forward ? "optics.zoom.stepforward" : "optics.zoom.stepreverse",
        LensAxis.Focus => forward ? "optics.focus.stepforward" : "optics.focus.stepreverse",
        LensAxis.ShiftHorizontal => forward ? "optics.lensshift.horizontal.stepforward" : "optics.lensshift.horizontal.stepreverse",
        _ => forward ? "optics.lensshift.vertical.stepforward" : "optics.lensshift.vertical.stepreverse",
    };
}

/// <summary>
/// Barco projectors on the Pulse platform through its JSON-RPC API on TCP 9090. Power, source, shutter, test
/// pattern on/off, lens steps and identity come from public implementations used against these projectors; the
/// warp, blend and absolute lens properties Barco documents for Pulse are not in a source this was built from, so
/// they are refused rather than guessed (see docs/PROJECTORS.md).
/// </summary>
public sealed class BarcoPulseDriver(ProjectorLink link, ICredentialStore credentials, IDeviceLog log, DriverTimings? timings = null)
    : ProjectorDriverBase(link, credentials, log, timings)
{
    private long _nextId;

    public override ProjectorProtocol Protocol => ProjectorProtocol.BarcoPulse;

    public override DriverProvenance Provenance => DriverProvenance.PublicImplementation;

    public override ProjectorCapabilities Capabilities =>
        ProjectorCapabilities.Power | ProjectorCapabilities.Input | ProjectorCapabilities.Shutter
        | ProjectorCapabilities.LensMotor | ProjectorCapabilities.TestPatterns | ProjectorCapabilities.Status;

    public override string Caveat(ProjectorCapabilities capability) => capability switch
    {
        ProjectorCapabilities.LensMotor => "steps only (optics.*.stepforward/stepreverse)",
        ProjectorCapabilities.TestPatterns => "on/off only (image.testpattern.show); the pattern shown is the one chosen on the projector",
        ProjectorCapabilities.Input => "Pulse source names, e.g. 'L1 HDMI', 'L1 Displayport'",
        _ => "",
    };

    protected override string InputExample => "L1 HDMI";

    protected override Task<CommandResult> ConnectCoreAsync(CancellationToken ct) =>
        ConverseAsync("connect", async (ch, c) =>
        {
            var r = await CallAsync(ch, "property.get", [new("property", "system.state")], c).ConfigureAwait(false);
            return r.IsError
                ? CommandResult.Rejected("connect", r.ErrorMessage)
                : CommandResult.Accepted("connect", "connected — " + DeviceState.PowerWord(PulseProtocol.Power(r.ResultText)), r.ResultJson);
        }, ct);

    protected override Task<CommandResult> PowerCoreAsync(bool on, CancellationToken ct) =>
        ConverseAsync(on ? "power on" : "power off", async (ch, c) =>
        {
            var name = on ? "power on" : "power off";
            var set = await MethodAsync(ch, name, on ? "system.poweron" : "system.poweroff", [], c).ConfigureAwait(false);
            if (!set.Succeeded)
                return set;
            var back = await CallAsync(ch, "property.get", [new("property", "system.state")], c).ConfigureAwait(false);
            if (back.IsError)
                return set.ConfirmedBy(null, back.ErrorMessage);
            var p = PulseProtocol.Power(back.ResultText);
            return set.ConfirmedBy(on ? p is PowerState.On or PowerState.WarmingUp : p is PowerState.Standby or PowerState.CoolingDown, back.ResultText);
        }, ct);

    protected override Task<CommandResult> InputCoreAsync(string input, CancellationToken ct) =>
        SetPropertyAsync("input", "image.window.main.source", input, v => v.Equals(input, StringComparison.OrdinalIgnoreCase), ct);

    protected override Task<CommandResult> ShutterCoreAsync(bool closed, CancellationToken ct) =>
        SetPropertyAsync(closed ? "shutter close" : "shutter open", "optics.shutter.target", closed ? "Closed" : "Open",
            v => v.Equals(closed ? "Closed" : "Open", StringComparison.OrdinalIgnoreCase), ct, readBack: "optics.shutter.position");

    protected override Task<CommandResult> TestPatternCoreAsync(string pattern, CancellationToken ct) =>
        SetPropertyAsync("test pattern " + pattern, "image.testpattern.show", pattern != TestPatterns.Off,
            v => v.Equals(pattern != TestPatterns.Off ? "true" : "false", StringComparison.OrdinalIgnoreCase), ct);

    protected override Task<CommandResult> LensMoveCoreAsync(LensMove move, CancellationToken ct)
    {
        if (move.Absolute)
            return Task.FromResult(CommandResult.Refused("lens", "Pulse's absolute lens positions are not in a source PlayMap was built from — move by steps."));
        if (move.Value == 0)
            return Task.FromResult(CommandResult.Refused("lens", "No steps asked."));
        var steps = Math.Min(Math.Abs(move.Value), 100);
        return ConverseAsync("lens", (ch, c) =>
            MethodAsync(ch, "lens", PulseProtocol.LensStepMethod(move.Axis, move.Value > 0), [new("steps", steps)], c), ct);
    }

    protected override Task<DriverReading> StatusCoreAsync(CancellationToken ct) =>
        ConverseReadAsync("status", async (ch, c) =>
        {
            async Task<string?> Get(string property)
            {
                var r = await CallAsync(ch, "property.get", [new("property", property)], c).ConfigureAwait(false);
                return r.IsError ? null : r.ResultText;
            }

            var state = await Get("system.state").ConfigureAwait(false);
            if (state is null)
                return new DriverReading(CommandResult.Rejected("status", "the projector did not answer system.state"), null);
            var s = Blank() with { Power = PulseProtocol.Power(state), Manufacturer = "Barco" };
            if (await Get("optics.shutter.position").ConfigureAwait(false) is { } shutter)
                s = s with { ShutterClosed = shutter.Equals("Closed", StringComparison.OrdinalIgnoreCase) ? true : shutter.Equals("Open", StringComparison.OrdinalIgnoreCase) ? false : null };
            if (await Get("image.window.main.source").ConfigureAwait(false) is { } source)
                s = s with { Input = source };
            if (await Get("system.modelname").ConfigureAwait(false) is { } model)
                s = s with { Model = model };
            if (await Get("system.serialnumber").ConfigureAwait(false) is { } serial)
                s = s with { Serial = serial };
            if (await Get("firmware.firmwareversion").ConfigureAwait(false) is { } fw)
                s = s with { Firmware = fw };
            return new DriverReading(CommandResult.Observed("status", s.Headline()), s with { Online = true });
        }, ct);

    protected override Task<CommandResult> RawCoreAsync(string line, CancellationToken ct) =>
        ConverseAsync("raw", async (ch, c) =>
        {
            // "method" or "method {json params}"; a whole JSON-RPC object is sent as typed.
            string request;
            if (line.StartsWith('{'))
            {
                request = line;
            }
            else
            {
                var space = line.IndexOf(' ', StringComparison.Ordinal);
                var method = space < 0 ? line : line[..space];
                var json = space < 0 ? "{}" : line[(space + 1)..].Trim();
                request = "{\"jsonrpc\":\"2.0\",\"method\":" + JsonSerializer.Serialize(method) + ",\"params\":" + json + ",\"id\":" + NextId().ToString(CultureInfo.InvariantCulture) + "}";
            }
            await ch.WriteCommandAsync(request, c).ConfigureAwait(false);
            var answer = await ReadReplyAsync(ch, c).ConfigureAwait(false);
            return answer.IsError
                ? CommandResult.Rejected("raw", answer.ErrorMessage, answer.ResultJson)
                : CommandResult.Accepted("raw", "answered", answer.ResultJson);
        }, ct);

    // ---- the conversation ----

    private long NextId() => Interlocked.Increment(ref _nextId);

    private async Task<CommandResult> MethodAsync(TcpChannel ch, string name, string method, KeyValuePair<string, object?>[] parameters, CancellationToken ct)
    {
        var r = await CallAsync(ch, method, parameters, ct).ConfigureAwait(false);
        return r.IsError
            ? CommandResult.Rejected(name, "the projector refused it — " + r.ErrorMessage, r.ResultJson)
            : CommandResult.Accepted(name, "the projector took it", r.ResultJson);
    }

    private Task<CommandResult> SetPropertyAsync(string name, string property, object value, Func<string, bool> matches, CancellationToken ct, string? readBack = null) =>
        ConverseAsync(name, async (ch, c) =>
        {
            var set = await MethodAsync(ch, name, "property.set", [new("property", property), new("value", value)], c).ConfigureAwait(false);
            if (!set.Succeeded)
                return set;
            var back = await CallAsync(ch, "property.get", [new("property", readBack ?? property)], c).ConfigureAwait(false);
            return back.IsError ? set.ConfirmedBy(null, back.ErrorMessage) : set.ConfirmedBy(matches(back.ResultText), back.ResultText);
        }, ct);

    private async Task<PulseReply> CallAsync(TcpChannel ch, string method, KeyValuePair<string, object?>[] parameters, CancellationToken ct)
    {
        var id = NextId();
        await ch.WriteCommandAsync(PulseProtocol.Request(id, method, parameters), ct).ConfigureAwait(false);
        while (true)
        {
            // Notifications (no id) and answers to earlier ids are skipped; the reply timeout bounds the wait.
            var reply = await ReadReplyAsync(ch, ct).ConfigureAwait(false);
            if (reply.Id == id)
                return reply;
        }
    }

    /// <summary>The next JSON-RPC answer, skipping notifications; the reply timeout ends the wait with a DeviceIoException.</summary>
    private async Task<PulseReply> ReadReplyAsync(TcpChannel ch, CancellationToken ct)
    {
        while (true)
        {
            var json = await ch.ReadJsonObjectAsync(Timings.Reply, ct).ConfigureAwait(false);
            var reply = PulseProtocol.ParseReply(json);
            if (reply is not null)
                return reply;
        }
    }
}
