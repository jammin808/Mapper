using System.Security.Cryptography;
using System.Text;
using PlayMap.Devices.Commands;
using PlayMap.Devices.Model;
using PlayMap.Devices.Transport;

namespace PlayMap.Devices.Drivers.Sony;

/// <summary>
/// Sony ADCP as pure functions, as recalled from Sony's ADCP protocol manual for its installation projectors
/// (VPL-FHZ/GTZ/PHZ families): TCP 53595, CR LF lines; the projector greets "NOKEY" when authentication is off,
/// otherwise a random string, answered with SHA-256(random + password) in hex, then "OK" or "err_auth".
/// Commands are words with a quoted value — power "on", blank "on", input "hdmi1" — or a query — power_status ?
/// — answered "ok", a quoted value, or an "err_…" word. Not re-verified against a public source in this build:
/// the driver is <see cref="DriverProvenance.Unverified"/> and every command is gated.
/// </summary>
public static class AdcpProtocol
{
    public const string NoKey = "NOKEY";

    public static string Hash(string challenge, string password) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(challenge + password)));

    public static string Line(string command) => command + "\r\n";

    /// <summary>A quoted answer's value ("\"on\"" → "on"); the text as it is otherwise.</summary>
    public static string Unquote(string answer)
    {
        var t = answer.Trim();
        return t.Length >= 2 && t[0] == '"' && t[^1] == '"' ? t[1..^1] : t;
    }

    public static bool IsError(string answer) => answer.Trim().StartsWith("err_", StringComparison.Ordinal);

    public static string ErrorWords(string answer) => answer.Trim() switch
    {
        "err_cmd" => "unknown command",
        "err_option" => "unknown option",
        "err_inactive" => "not available now (standby, warming, cooling, or the feature is off)",
        "err_val" => "value out of range",
        "err_auth" => "authentication failed — check the link's password",
        _ => answer.Trim(),
    };

    /// <summary>power_status: "on"; "standby"/"saving_standby" off; "startup…" warming; "cooling…" cooling.</summary>
    public static PowerState Power(string status)
    {
        var s = Unquote(status);
        if (s == "on")
            return PowerState.On;
        if (s is "standby" or "saving_standby")
            return PowerState.Standby;
        if (s.StartsWith("startup", StringComparison.Ordinal))
            return PowerState.WarmingUp;
        if (s.StartsWith("cooling", StringComparison.Ordinal) || s.StartsWith("saving_cooling", StringComparison.Ordinal))
            return PowerState.CoolingDown;
        return PowerState.Unknown;
    }
}

/// <summary>Sony installation projectors through ADCP on TCP 53595. Unverified — see <see cref="AdcpProtocol"/>.</summary>
public sealed class SonyAdcpDriver(ProjectorLink link, ICredentialStore credentials, IDeviceLog log, DriverTimings? timings = null)
    : ProjectorDriverBase(link, credentials, log, timings)
{
    public override ProjectorProtocol Protocol => ProjectorProtocol.SonyAdcp;

    public override DriverProvenance Provenance => DriverProvenance.Unverified;

    public override ProjectorCapabilities Capabilities =>
        ProjectorCapabilities.Power | ProjectorCapabilities.Input | ProjectorCapabilities.Shutter | ProjectorCapabilities.Status;

    protected override string InputExample => "hdmi1";

    protected override Task<CommandResult> ConnectCoreAsync(CancellationToken ct) =>
        SessionAsync("connect", async (ch, c) =>
        {
            var r = await AskAsync(ch, "power_status ?", c).ConfigureAwait(false);
            return AdcpProtocol.IsError(r)
                ? CommandResult.Rejected("connect", AdcpProtocol.ErrorWords(r), r)
                : CommandResult.Accepted("connect", "connected — " + DeviceState.PowerWord(AdcpProtocol.Power(r)), r);
        }, ct);

    protected override Task<CommandResult> PowerCoreAsync(bool on, CancellationToken ct) =>
        SetAndReadAsync(on ? "power on" : "power off", on ? "power \"on\"" : "power \"off\"", "power_status ?",
            v =>
            {
                var p = AdcpProtocol.Power(v);
                return (on ? p is PowerState.On or PowerState.WarmingUp : p is PowerState.Standby or PowerState.CoolingDown, DeviceState.PowerWord(p));
            }, ct);

    protected override Task<CommandResult> InputCoreAsync(string input, CancellationToken ct)
    {
        var name = input.ToLowerInvariant();
        if (!name.All(char.IsAsciiLetterOrDigit))
            return Task.FromResult(CommandResult.Refused("input", $"'{input}' is not an ADCP input word — hdmi1, hdmi2, dvi1, hdbaset1…"));
        return SetAndReadAsync("input", "input \"" + name + "\"", "input ?", v => (AdcpProtocol.Unquote(v) == name, "input " + AdcpProtocol.Unquote(v)), ct);
    }

    protected override Task<CommandResult> ShutterCoreAsync(bool closed, CancellationToken ct) =>
        SetAndReadAsync(closed ? "shutter close" : "shutter open", closed ? "blank \"on\"" : "blank \"off\"", "blank ?",
            v => (AdcpProtocol.Unquote(v) == (closed ? "on" : "off"), "picture blank " + AdcpProtocol.Unquote(v)), ct);

    protected override Task<DriverReading> StatusCoreAsync(CancellationToken ct) =>
        ConverseReadAsync("status", async (ch, c) =>
        {
            var problem = await GreetAsync(ch, "status", c).ConfigureAwait(false);
            if (problem is not null)
                return new DriverReading(problem, null);
            var power = await AskAsync(ch, "power_status ?", c).ConfigureAwait(false);
            if (AdcpProtocol.IsError(power))
                return new DriverReading(CommandResult.Rejected("status", AdcpProtocol.ErrorWords(power), power), null);
            var s = Blank() with { Power = AdcpProtocol.Power(power), Manufacturer = "Sony" };
            if (await AskAsync(ch, "blank ?", c).ConfigureAwait(false) is var blank && !AdcpProtocol.IsError(blank))
                s = s with { ShutterClosed = AdcpProtocol.Unquote(blank) == "on" };
            if (await AskAsync(ch, "input ?", c).ConfigureAwait(false) is var input && !AdcpProtocol.IsError(input))
                s = s with { Input = AdcpProtocol.Unquote(input) };
            if (await AskAsync(ch, "modelname ?", c).ConfigureAwait(false) is var model && !AdcpProtocol.IsError(model))
                s = s with { Model = AdcpProtocol.Unquote(model) };
            if (await AskAsync(ch, "serialnum ?", c).ConfigureAwait(false) is var serial && !AdcpProtocol.IsError(serial))
                s = s with { Serial = AdcpProtocol.Unquote(serial) };
            return new DriverReading(CommandResult.Observed("status", s.Headline()), s with { Online = true });
        }, ct);

    protected override Task<CommandResult> RawCoreAsync(string line, CancellationToken ct) =>
        SessionAsync("raw", async (ch, c) =>
        {
            var r = await AskAsync(ch, line, c).ConfigureAwait(false);
            return AdcpProtocol.IsError(r) ? CommandResult.Rejected("raw", AdcpProtocol.ErrorWords(r), r) : CommandResult.Accepted("raw", "answered", r);
        }, ct);

    // ---- the conversation ----

    private Task<CommandResult> SetAndReadAsync(string name, string command, string query, Func<string, (bool Ok, string Words)> judge, CancellationToken ct) =>
        SessionAsync(name, async (ch, c) =>
        {
            var r = await AskAsync(ch, command, c).ConfigureAwait(false);
            if (AdcpProtocol.IsError(r))
                return CommandResult.Rejected(name, AdcpProtocol.ErrorWords(r), r);
            var set = r.Trim() == "ok" ? CommandResult.Accepted(name, "the projector said ok", r) : CommandResult.Sent(name, "the projector answered " + r, r);
            var back = await AskAsync(ch, query, c).ConfigureAwait(false);
            if (AdcpProtocol.IsError(back))
                return set.ConfirmedBy(null, AdcpProtocol.ErrorWords(back));
            var (ok, words) = judge(back);
            return set.ConfirmedBy(ok, words);
        }, ct);

    private async Task<string> AskAsync(TcpChannel ch, string command, CancellationToken ct)
    {
        await ch.WriteCommandAsync(AdcpProtocol.Line(command), ct).ConfigureAwait(false);
        return (await ch.ReadUntilAsync((byte)'\n', Timings.Reply, ct).ConfigureAwait(false)).TrimEnd('\r');
    }

    /// <summary>The greeting and, when asked, the SHA-256 answer; a problem as a result.</summary>
    private async Task<CommandResult?> GreetAsync(TcpChannel ch, string name, CancellationToken ct)
    {
        var greeting = (await ch.ReadUntilAsync((byte)'\n', Timings.Reply, ct).ConfigureAwait(false)).TrimEnd('\r').Trim();
        if (greeting == AdcpProtocol.NoKey)
            return null;
        if (greeting.Length == 0 || greeting.Contains(' ', StringComparison.Ordinal))
            return CommandResult.Unreached(name, $"{ch.Endpoint} does not greet as an ADCP projector");
        var secret = Credential?.Secret ?? "";
        if (secret.Length == 0)
            return CommandResult.Unreached(name, NoPasswordWords());
        await ch.WriteHandshakeAsync(Encoding.ASCII.GetBytes(AdcpProtocol.Line(AdcpProtocol.Hash(greeting, secret))), ct).ConfigureAwait(false);
        var answer = (await ch.ReadUntilAsync((byte)'\n', Timings.Reply, ct).ConfigureAwait(false)).TrimEnd('\r').Trim();
        return answer.Equals("OK", StringComparison.OrdinalIgnoreCase) ? null : CommandResult.Unreached(name, AdcpProtocol.ErrorWords(answer));
    }

    private Task<CommandResult> SessionAsync(string name, Func<TcpChannel, CancellationToken, Task<CommandResult>> body, CancellationToken ct) =>
        ConverseAsync(name, async (ch, c) => await GreetAsync(ch, name, c).ConfigureAwait(false) ?? await body(ch, c).ConfigureAwait(false), ct);
}
