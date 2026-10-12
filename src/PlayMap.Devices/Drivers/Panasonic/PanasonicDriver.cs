using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PlayMap.Devices.Commands;
using PlayMap.Devices.Model;
using PlayMap.Devices.Transport;

namespace PlayMap.Devices.Drivers.Panasonic;

public sealed record NtControlGreeting(bool Valid, bool Protected, string Seed);

/// <summary>A reply with its "00" header removed; <see cref="Error"/> is the error code or "".</summary>
public sealed record NtControlReply(string Body, string Error)
{
    public bool IsError => Error.Length > 0;
}

/// <summary>
/// Panasonic NTCONTROL ("LAN control protocol 2", the projectors' own) as pure functions, from Panasonic's LAN
/// protocol document: TCP 1024; the projector greets "NTCONTROL 0" (no password) or "NTCONTROL 1 zzzzzzzz" (an
/// 8-character random number); in that protected mode every command is MD5("user:password:zzzzzzzz") + "00" +
/// command + CR; answers are "00" + data + CR or an error (ERR1–ERR5, ERRA, ER401). A control command answers
/// with its own three letters; a query answers with its value.
/// </summary>
public static class NtControlProtocol
{
    /// <summary>The default administrator user name on Panasonic projectors.</summary>
    public const string DefaultUser = "admin1";

    public static NtControlGreeting ParseGreeting(string line)
    {
        var t = (line ?? "").Trim('\r', '\n', ' ');
        if (!t.StartsWith("NTCONTROL ", StringComparison.Ordinal))
            return new NtControlGreeting(false, false, "");
        var rest = t[10..];
        if (rest == "0")
            return new NtControlGreeting(true, false, "");
        if (rest.StartsWith("1 ", StringComparison.Ordinal) && rest.Length > 2)
            return new NtControlGreeting(true, true, rest[2..].Trim());
        return new NtControlGreeting(false, false, "");
    }

    /// <summary>MD5 of "user:password:seed" in uppercase hex, as Panasonic's own monitoring software sends it.</summary>
    public static string Token(string user, string password, string seed)
    {
#pragma warning disable CA5351, S4790 // Panasonic NTCONTROL specifies MD5 for its protected-mode token — the projector's protocol, not a secret store
        var hash = MD5.HashData(Encoding.ASCII.GetBytes(user + ":" + password + ":" + seed));
#pragma warning restore CA5351, S4790
        return Convert.ToHexString(hash);
    }

    public static string Frame(string token, string command) => token + "00" + command + "\r";

    public static NtControlReply ParseReply(string line)
    {
        var t = (line ?? "").Trim('\r', '\n');
        var body = t.StartsWith("00", StringComparison.Ordinal) ? t[2..] : t;
        var code = body.ToUpperInvariant();
        if (code is "ERR1" or "ERR2" or "ERR3" or "ERR4" or "ERR5" or "ERRA" or "ER401")
            return new NtControlReply("", code);
        if (t.Equals("NTCONTROL ERRA", StringComparison.Ordinal) || t.Equals("PDPCONTROL ERRA", StringComparison.Ordinal))
            return new NtControlReply("", "ERRA");
        return new NtControlReply(body, "");
    }

    /// <summary>The meanings from Panasonic's LAN protocol document.</summary>
    public static string ErrorWords(string code) => code switch
    {
        "ERR1" => "undefined control command — this model does not have it",
        "ERR2" => "parameter out of range",
        "ERR3" => "busy, or not available now (warming up, cooling down, standby)",
        "ERR4" => "timed out, or not available now",
        "ERR5" => "invalid data length",
        "ERRA" => "password mismatch — check the link's user name and password",
        "ER401" => "the projector could not carry out the command",
        _ => code,
    };

    /// <summary>A "VXX:name=+00001"-style signed five-digit value.</summary>
    public static string Signed5(int v) => (v < 0 ? "-" : "+") + Math.Abs(v).ToString("00000", CultureInfo.InvariantCulture);

    /// <summary>The value of a "QVX:name" answer ("LNSI7=-02480" → "-02480"); null when it names something else.</summary>
    public static string? SubValue(string body, string name) =>
        body.StartsWith(name + "=", StringComparison.Ordinal) ? body[(name.Length + 1)..] : null;

    public static int? Number(string? value) =>
        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>QPW: "001" on, "000" standby.</summary>
    public static PowerState Power(string value) => value switch
    {
        "001" => PowerState.On,
        "000" => PowerState.Standby,
        _ => PowerState.Unknown,
    };

    /// <summary>Q$S (lamp/light control status): 0 off, 1 turning on, 2 on, 3 cooling.</summary>
    public static PowerState LightStatus(string value) => value switch
    {
        "0" => PowerState.Standby,
        "1" => PowerState.WarmingUp,
        "2" => PowerState.On,
        "3" => PowerState.CoolingDown,
        _ => PowerState.Unknown,
    };

    /// <summary>QTM: "CCCC/FFFF" — Celsius then Fahrenheit; the Celsius reading, or null.</summary>
    public static double? Celsius(string value)
    {
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        var c = slash > 0 ? value[..slash] : value;
        return double.TryParse(c, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static string LensName(LensAxis axis, bool absolute) => (axis, absolute) switch
    {
        (LensAxis.ShiftHorizontal, true) => "LNSI7",
        (LensAxis.ShiftVertical, true) => "LNSI8",
        (LensAxis.Focus, true) => "LNSI9",
        (LensAxis.ShiftHorizontal, false) => "LNSI2",
        (LensAxis.ShiftVertical, false) => "LNSI3",
        (LensAxis.Focus, false) => "LNSI4",
        (LensAxis.Zoom, false) => "LNSI5",
        _ => "",
    };
}

/// <summary>
/// Panasonic projectors through NTCONTROL on TCP 1024 (the protocol Geometry Manager Pro and Panasonic's own
/// monitoring software use). A command and its read-back share one connection (the projector keeps it open 30 s);
/// the token is sent in front of each command, as the protected mode has it. Commands come from Panasonic's
/// published control-command tables (PT-RZ570, PT-DZ21K2 series); a model without one answers ERR1, which reads
/// as "this model does not have it".
/// </summary>
public sealed class PanasonicDriver(ProjectorLink link, ICredentialStore credentials, IDeviceLog log, DriverTimings? timings = null)
    : ProjectorDriverBase(link, credentials, log, timings)
{
    private static readonly string[] Patterns = ["white", "black", "window", "colour-bars"];

    public override ProjectorProtocol Protocol => ProjectorProtocol.PanasonicNtControl;

    public override DriverProvenance Provenance => DriverProvenance.VendorDocument;

    public override ProjectorCapabilities Capabilities =>
        ProjectorCapabilities.Power | ProjectorCapabilities.Input | ProjectorCapabilities.Shutter
        | ProjectorCapabilities.Freeze | ProjectorCapabilities.LensMotor | ProjectorCapabilities.LensPositionRead
        | ProjectorCapabilities.LensMemory | ProjectorCapabilities.TestPatterns | ProjectorCapabilities.GeometryReset
        | ProjectorCapabilities.EdgeBlend | ProjectorCapabilities.BlackLevel | ProjectorCapabilities.ColorGains
        | ProjectorCapabilities.Status;

    public override ProjectorCapabilities Unverified => ProjectorCapabilities.BlackLevel;

    public override string Caveat(ProjectorCapabilities capability) => capability switch
    {
        ProjectorCapabilities.BlackLevel =>
            "Panasonic's own table writes the non-overlapped black level both as 000.000.000.000 and 000,000,000,000",
        ProjectorCapabilities.LensMotor or ProjectorCapabilities.LensPositionRead =>
            "absolute positions (LNSI7/8/9) are in the PT-DZ21K2-series table, not the older series; zoom moves by steps only",
        ProjectorCapabilities.GeometryReset => "GEOMETRY OFF (VXX:GMMI0=+00000) from the PT-DZ21K2-series table",
        ProjectorCapabilities.EdgeBlend => "EDGE BLENDING (VXX:EDBI0) and the per-side widths from the PT-DZ21K2-series table",
        ProjectorCapabilities.TestPatterns => "pattern numbers from the PT-RZ570 table; other models list others",
        _ => "",
    };

    public override IReadOnlyList<string> TestPatternNames => Patterns;

    protected override string InputExample => "HD1, SD1, DVI, DL1 (Panasonic's three-letter input code)";

    protected override Task<CommandResult> ConnectCoreAsync(CancellationToken ct) =>
        SessionAsync("connect", async (ch, token, c) =>
        {
            var r = await AskAsync(ch, token, "QPW", c).ConfigureAwait(false);
            return r.IsError
                ? Problem("connect", r)
                : CommandResult.Accepted("connect", "connected — " + DeviceState.PowerWord(NtControlProtocol.Power(r.Body)), r.Body);
        }, ct);

    protected override Task<CommandResult> PowerCoreAsync(bool on, CancellationToken ct) =>
        SessionAsync(on ? "power on" : "power off", async (ch, token, c) =>
        {
            var name = on ? "power on" : "power off";
            var set = await ControlAsync(ch, token, name, on ? "PON" : "POF", c).ConfigureAwait(false);
            if (!set.Succeeded)
                return set;
            var back = await AskAsync(ch, token, "QPW", c).ConfigureAwait(false);
            var p = back.IsError ? PowerState.Unknown : NtControlProtocol.Power(back.Body);
            return set.ConfirmedBy(p == PowerState.Unknown ? null : p == (on ? PowerState.On : PowerState.Standby), DeviceState.PowerWord(p));
        }, ct);

    protected override Task<CommandResult> InputCoreAsync(string input, CancellationToken ct)
    {
        var code = input.ToUpperInvariant();
        if (code.Length < 3 || code.Length > 7 || !code.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == ':'))
            return Task.FromResult(CommandResult.Refused("input", $"'{input}' is not a Panasonic input code — HD1, SD1, DVI, RG1, DL1…"));
        return SetAndReadAsync("input", "IIS:" + code, "QIN", v => (v.Equals(code, StringComparison.Ordinal), "input " + v), ct);
    }

    protected override Task<CommandResult> ShutterCoreAsync(bool closed, CancellationToken ct) =>
        SetAndReadAsync(closed ? "shutter close" : "shutter open", closed ? "OSH:1" : "OSH:0", "QSH",
            v => (v == (closed ? "1" : "0"), v == "1" ? "shutter closed" : "shutter open"), ct);

    protected override Task<CommandResult> FreezeCoreAsync(bool frozen, CancellationToken ct) =>
        SetAndReadAsync(frozen ? "freeze on" : "freeze off", frozen ? "OFZ:1" : "OFZ:0", "QFZ",
            v => (v == (frozen ? "1" : "0"), v == "1" ? "frozen" : "not frozen"), ct);

    protected override Task<CommandResult> TestPatternCoreAsync(string pattern, CancellationToken ct)
    {
        var code = pattern switch
        {
            TestPatterns.Off => "00",
            "white" => "01",
            "black" => "02",
            TestPatterns.Identify or "window" => "05",
            _ => "08",
        };
        return SetAndReadAsync("test pattern " + pattern, "OTS:" + code, "QTS",
            v => (v == code, v == "00" ? "patterns off" : "pattern " + v), ct);
    }

    protected override Task<CommandResult> LensMoveCoreAsync(LensMove move, CancellationToken ct)
    {
        var name = NtControlProtocol.LensName(move.Axis, move.Absolute);
        if (name.Length == 0)
            return Task.FromResult(CommandResult.Refused("lens", "Panasonic's tables give zoom by steps only — move zoom by steps."));
        if (move.Absolute)
            return SetAndReadAsync("lens", "VXX:" + name + "=" + NtControlProtocol.Signed5(move.Value), "QVX:" + name,
                v => (NtControlProtocol.Number(NtControlProtocol.SubValue(v, name)) == move.Value, "at " + (NtControlProtocol.SubValue(v, name) ?? v)), ct);
        // A relative move is the table's SLOW nudge (+00000 one way, +00001 the other) once per step.
        var nudge = move.Value >= 0 ? "+00000" : "+00001";
        var steps = Math.Min(Math.Abs(move.Value), 50);
        return SessionAsync("lens", async (ch, token, c) =>
        {
            var last = CommandResult.Refused("lens", "No steps asked.");
            for (var i = 0; i < steps; i++)
            {
                last = await ControlAsync(ch, token, "lens", "VXX:" + name + "=" + nudge, c).ConfigureAwait(false);
                if (!last.Succeeded)
                    return last;
            }
            return last with { Words = $"{steps.ToString(CultureInfo.InvariantCulture)} steps accepted — read the lens to see where it is" };
        }, ct);
    }

    protected override Task<CommandResult> LensMemoryCoreAsync(int slot, CancellationToken ct) =>
        SessionAsync("lens memory", (ch, token, c) =>
            ControlAsync(ch, token, "lens memory", "VXX:LNMI1=" + NtControlProtocol.Signed5(slot - 1), c), ct);

    protected override Task<CommandResult> GeometryResetCoreAsync(CancellationToken ct) =>
        SetAndReadAsync("geometry off", "VXX:GMMI0=+00000", "QVX:GMMI0",
            v => (NtControlProtocol.SubValue(v, "GMMI0") == "+00000", "geometry " + (NtControlProtocol.SubValue(v, "GMMI0") == "+00000" ? "off" : v)), ct);

    protected override Task<CommandResult> EdgeBlendCoreAsync(EdgeBlendSettings blend, CancellationToken ct) =>
        SessionAsync("edge blend", async (ch, token, c) =>
        {
            var mode = blend.Enabled ? "+00001" : "+00000";
            var set = await ControlAsync(ch, token, "edge blend", "VXX:EDBI0=" + mode, c).ConfigureAwait(false);
            if (!set.Succeeded || !blend.Enabled)
                return await ConfirmAsync(ch, token, set, "QVX:EDBI0", v => (NtControlProtocol.SubValue(v, "EDBI0") == mode, "edge blending " + v), c).ConfigureAwait(false);
            foreach (var (side, onOff, width, value) in new[]
            {
                ("left", "VGL", "ELWI0", blend.Left), ("right", "VGR", "ERWI0", blend.Right),
                ("top", "VGU", "EUWI0", blend.Top), ("bottom", "VGB", "EBWI0", blend.Bottom),
            })
            {
                var r = await ControlAsync(ch, token, "edge blend " + side, onOff + ":" + (value > 0 ? "1" : "0"), c).ConfigureAwait(false);
                if (!r.Succeeded)
                    return r;
                if (value <= 0)
                    continue;
                r = await ControlAsync(ch, token, "edge blend " + side, "VXX:" + width + "=" + NtControlProtocol.Signed5(value), c).ConfigureAwait(false);
                if (!r.Succeeded)
                    return r;
            }
            return await ConfirmAsync(ch, token, set, "QVX:EDBI0", v => (NtControlProtocol.SubValue(v, "EDBI0") == mode, "edge blending " + v), c).ConfigureAwait(false);
        }, ct);

    protected override Task<CommandResult> BlackLevelCoreAsync(BlackLevelSettings level, CancellationToken ct)
    {
        static string C(int v) => Math.Clamp(v, 0, 255).ToString("000", CultureInfo.InvariantCulture);
        var value = level.Enabled ? $"000.{C(level.Red)}.{C(level.Green)}.{C(level.Blue)}" : "000.000.000.000";
        return SessionAsync("black level", (ch, token, c) => ControlAsync(ch, token, "black level", "VJI:" + value, c), ct);
    }

    protected override Task<CommandResult> ColorGainsCoreAsync(ColorGains gains, CancellationToken ct) =>
        SessionAsync("colour gains", async (ch, token, c) =>
        {
            static string C(int v) => Math.Clamp(v, 0, 255).ToString("000", CultureInfo.InvariantCulture);
            CommandResult last = CommandResult.Refused("colour gains", "nothing sent");
            foreach (var (cmd, q, v) in new[] { ("VHR", "QHR", gains.Red), ("VHG", "QHG", gains.Green), ("VHB", "QHB", gains.Blue) })
            {
                last = await ControlAsync(ch, token, "colour gains", cmd + ":" + C(v), c).ConfigureAwait(false);
                if (!last.Succeeded)
                    return last;
                last = await ConfirmAsync(ch, token, last, q, back => (back == C(v), q + " " + back), c).ConfigureAwait(false);
                if (!last.IsFact)
                    return last;
            }
            return last with { Words = $"white balance high R {C(gains.Red)} G {C(gains.Green)} B {C(gains.Blue)} — confirmed" };
        }, ct);

    protected override Task<DriverReading> StatusCoreAsync(CancellationToken ct) =>
        SessionReadAsync("status", async (ch, token, c) =>
        {
            var s = Blank();
            async Task<string?> Q(string cmd)
            {
                var r = await AskAsync(ch, token, cmd, c).ConfigureAwait(false);
                if (r.Error == "ERRA")
                    throw new DeviceIoException(DeviceIoKind.Protocol, NtControlProtocol.ErrorWords("ERRA"));
                return r.IsError ? null : r.Body;
            }

            var qpw = await Q("QPW").ConfigureAwait(false);
            if (qpw is null)
                return new DriverReading(CommandResult.Rejected("status", "the projector did not answer its power query"), null);
            var power = NtControlProtocol.Power(qpw);
            if (await Q("Q$S").ConfigureAwait(false) is { } light && NtControlProtocol.LightStatus(light) is var ls and (PowerState.WarmingUp or PowerState.CoolingDown))
                power = ls;
            s = s with { Power = power, Manufacturer = "Panasonic" };
            if (await Q("QSH").ConfigureAwait(false) is { } qsh)
                s = s with { ShutterClosed = qsh == "1" };
            if (await Q("QFZ").ConfigureAwait(false) is { } qfz)
                s = s with { Frozen = qfz == "1" };
            if (await Q("QIN").ConfigureAwait(false) is { } qin)
                s = s with { Input = qin };
            if (await Q("QID").ConfigureAwait(false) is { } qid)
                s = s with { Model = qid };
            if (await Q("QSN").ConfigureAwait(false) is { } qsn)
                s = s with { Serial = qsn };
            if (await Q("QVX:SVRS0").ConfigureAwait(false) is { } fw && NtControlProtocol.SubValue(fw, "SVRS0") is { } version)
                s = s with { Firmware = version };
            if (await Q("Q$L:1").ConfigureAwait(false) is { } q1 && NtControlProtocol.Number(q1) is { } hours)
                s = s with { LightHours = [hours] };
            var temps = ImmutableArray.CreateBuilder<DeviceTemperature>();
            foreach (var (cmd, sensor) in new[] { ("QTM:0", "intake"), ("QTM:1", "exhaust"), ("QTM:2", "optics module") })
            {
                if (await Q(cmd).ConfigureAwait(false) is { } t && NtControlProtocol.Celsius(t) is { } celsius)
                    temps.Add(new DeviceTemperature(sensor, celsius));
            }
            s = s with { Temperatures = temps.ToImmutable() };
            if (await Q("QVX:NSGS1").ConfigureAwait(false) is { } sig && NtControlProtocol.SubValue(sig, "NSGS1") is { Length: > 0 } signal)
                s = s with { Signal = new SignalInfo(0, 0, 0, signal) };
            return new DriverReading(CommandResult.Observed("status", s.Headline()), s with { Online = true });
        }, ct);

    protected override Task<DriverReading> LensReadCoreAsync(CancellationToken ct) =>
        SessionReadAsync("lens read", async (ch, token, c) =>
        {
            var lens = LensPositions.Unknown;
            foreach (var axis in new[] { LensAxis.ShiftHorizontal, LensAxis.ShiftVertical, LensAxis.Focus })
            {
                var name = NtControlProtocol.LensName(axis, true);
                var r = await AskAsync(ch, token, "QVX:" + name, c).ConfigureAwait(false);
                if (!r.IsError)
                    lens = lens.With(axis, NtControlProtocol.Number(NtControlProtocol.SubValue(r.Body, name)));
            }
            return lens.Any
                ? new DriverReading(CommandResult.Observed("lens read", lens.Words() + " (zoom has no position query)"), Blank() with { Lens = lens, Online = true })
                : new DriverReading(CommandResult.Rejected("lens read", "this model answered no lens position query"), null);
        }, ct);

    protected override Task<CommandResult> RawCoreAsync(string line, CancellationToken ct) =>
        SessionAsync("raw", async (ch, token, c) =>
        {
            var command = line.StartsWith("00", StringComparison.Ordinal) ? line[2..] : line;
            var r = await AskAsync(ch, token, command, c).ConfigureAwait(false);
            return r.IsError ? Problem("raw", r) : CommandResult.Accepted("raw", "answered", "00" + r.Body);
        }, ct);

    // ---- the conversation ----

    private static CommandResult Problem(string name, NtControlReply r) =>
        r.Error == "ERRA"
            ? CommandResult.Unreached(name, NtControlProtocol.ErrorWords("ERRA"))
            : CommandResult.Rejected(name, NtControlProtocol.ErrorWords(r.Error), r.Error);

    private Task<CommandResult> SetAndReadAsync(string name, string command, string query, Func<string, (bool Ok, string Words)> judge, CancellationToken ct) =>
        SessionAsync(name, async (ch, token, c) =>
        {
            var set = await ControlAsync(ch, token, name, command, c).ConfigureAwait(false);
            return set.Succeeded ? await ConfirmAsync(ch, token, set, query, judge, c).ConfigureAwait(false) : set;
        }, ct);

    private async Task<CommandResult> ConfirmAsync(TcpChannel ch, string token, CommandResult set, string query, Func<string, (bool Ok, string Words)> judge, CancellationToken ct)
    {
        var back = await AskAsync(ch, token, query, ct).ConfigureAwait(false);
        if (back.IsError)
            return set.ConfirmedBy(null, query + " answered " + back.Error);
        var (ok, words) = judge(back.Body);
        return set.ConfirmedBy(ok, words);
    }

    /// <summary>A control command: the projector echoes its three letters when it takes it.</summary>
    private async Task<CommandResult> ControlAsync(TcpChannel ch, string token, string name, string command, CancellationToken ct)
    {
        var r = await AskAsync(ch, token, command, ct).ConfigureAwait(false);
        if (r.IsError)
            return Problem(name, r);
        var head = command.Length >= 3 ? command[..3] : command;
        return r.Body.StartsWith(head, StringComparison.Ordinal)
            ? CommandResult.Accepted(name, "the projector took it", "00" + r.Body)
            : CommandResult.Sent(name, "the projector answered " + r.Body, "00" + r.Body);
    }

    private async Task<NtControlReply> AskAsync(TcpChannel ch, string token, string command, CancellationToken ct)
    {
        await ch.WriteCommandAsync(NtControlProtocol.Frame(token, command), ct).ConfigureAwait(false);
        return NtControlProtocol.ParseReply(await ch.ReadUntilAsync((byte)'\r', Timings.Reply, ct).ConfigureAwait(false));
    }

    /// <summary>Reads the greeting and works out the token; a problem as a result (no password, not NTCONTROL).</summary>
    private async Task<(CommandResult? Problem, string Token)> GreetAsync(TcpChannel ch, string name, CancellationToken ct)
    {
        var g = NtControlProtocol.ParseGreeting(await ch.ReadUntilAsync((byte)'\r', Timings.Reply, ct).ConfigureAwait(false));
        if (!g.Valid)
            return (CommandResult.Unreached(name, $"{ch.Endpoint} does not greet as an NTCONTROL projector"), "");
        if (!g.Protected)
            return (null, "");
        var cred = Credential;
        if (cred is null || cred.Secret.Length == 0)
            return (CommandResult.Unreached(name, NoPasswordWords()), "");
        var user = cred.User.Length > 0 ? cred.User : NtControlProtocol.DefaultUser;
        return (null, NtControlProtocol.Token(user, cred.Secret, g.Seed));
    }

    private Task<CommandResult> SessionAsync(string name, Func<TcpChannel, string, CancellationToken, Task<CommandResult>> body, CancellationToken ct) =>
        ConverseAsync(name, async (ch, c) =>
        {
            var (problem, token) = await GreetAsync(ch, name, c).ConfigureAwait(false);
            return problem ?? await body(ch, token, c).ConfigureAwait(false);
        }, ct);

    private Task<DriverReading> SessionReadAsync(string name, Func<TcpChannel, string, CancellationToken, Task<DriverReading>> body, CancellationToken ct) =>
        ConverseReadAsync(name, async (ch, c) =>
        {
            var (problem, token) = await GreetAsync(ch, name, c).ConfigureAwait(false);
            if (problem is not null)
                return new DriverReading(problem, null);
            try
            {
                return await body(ch, token, c).ConfigureAwait(false);
            }
            catch (DeviceIoException e) when (e.Kind == DeviceIoKind.Protocol)
            {
                return new DriverReading(CommandResult.Unreached(name, e.Message), null);
            }
        }, ct);
}
