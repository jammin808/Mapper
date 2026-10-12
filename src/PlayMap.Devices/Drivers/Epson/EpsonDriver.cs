using System.Globalization;
using System.Text;
using PlayMap.Devices.Commands;
using PlayMap.Devices.Model;
using PlayMap.Devices.Transport;

namespace PlayMap.Devices.Drivers.Epson;

/// <summary>The ESC/VP.net message statuses.</summary>
public enum EscVpNetStatus : byte
{
    Request = 0x00,
    Ok = 0x20,
    BadRequest = 0x40,
    PasswordRequired = 0x41,
    PasswordWrong = 0x43,
    NotAllowed = 0x45,
    Busy = 0x53,
    VersionNotSupported = 0x55,
}

/// <summary>
/// Epson ESC/VP.net and ESC/VP21 as pure functions, from Epson's ESC/VP.net manual as implemented by the public
/// library Home Assistant uses. TCP 3629. A 16-byte header — "ESC/VP.net", version 0x10, type (3 = CONNECT), two
/// reserved bytes, status, header count — then 18-byte headers (id 1 = password: attribute 1, 16 bytes of
/// password zero-padded). After an OK the socket carries ESC/VP21 lines: "PWR ON\r" answered ":", "PWR?\r"
/// answered "PWR=01\r:", a bad one "ERR\r:".
/// </summary>
public static class EscVpProtocol
{
    public const int HeaderSize = 16;
    public const int SubHeaderSize = 18;
    private const byte Version = 0x10;
    private const byte TypeConnect = 0x03;
    private static readonly byte[] Identifier = "ESC/VP.net"u8.ToArray();

    /// <summary>The CONNECT request, with the password header when there is a password (at most 16 ASCII bytes).</summary>
    public static byte[] ConnectRequest(string? password)
    {
        var pw = string.IsNullOrEmpty(password) ? [] : Encoding.ASCII.GetBytes(password);
        var bytes = new byte[HeaderSize + (pw.Length > 0 ? SubHeaderSize : 0)];
        Identifier.CopyTo(bytes, 0);
        bytes[10] = Version;
        bytes[11] = TypeConnect;
        bytes[14] = (byte)EscVpNetStatus.Request;
        bytes[15] = (byte)(pw.Length > 0 ? 1 : 0);
        if (pw.Length > 0)
        {
            bytes[16] = 0x01;
            bytes[17] = 0x01;
            pw.AsSpan(0, Math.Min(16, pw.Length)).CopyTo(bytes.AsSpan(18));
        }
        return bytes;
    }

    /// <summary>The answer's status and how many headers follow it; null when the 16 bytes are not ESC/VP.net.</summary>
    public static (EscVpNetStatus Status, int Headers)? ParseHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize || !header[..10].SequenceEqual(Identifier))
            return null;
        return ((EscVpNetStatus)header[14], header[15]);
    }

    public static string StatusWords(EscVpNetStatus s) => s switch
    {
        EscVpNetStatus.Ok => "connected",
        EscVpNetStatus.PasswordRequired => "the projector asks for a password",
        EscVpNetStatus.PasswordWrong => "password wrong — check the link's password",
        EscVpNetStatus.Busy => "the projector is busy (another controller holds the session)",
        EscVpNetStatus.NotAllowed => "not allowed in the projector's present state",
        EscVpNetStatus.VersionNotSupported => "protocol version not supported",
        _ => "bad request",
    };

    /// <summary>"PWR=01" → "01" for PWR; null when the answer is ERR or names something else.</summary>
    public static string? Value(string answer, string command)
    {
        var t = answer.Trim('\r', '\n', ' ', ':');
        return t.StartsWith(command + "=", StringComparison.Ordinal) ? t[(command.Length + 1)..] : null;
    }

    public static bool IsError(string answer) => answer.Trim('\r', '\n', ' ', ':').Equals("ERR", StringComparison.Ordinal);

    /// <summary>PWR?: 01 on, 02 warming up, 03 cooling down, 00 and 04 standby (network off / on), 05 abnormal standby.</summary>
    public static PowerState Power(string value) => value switch
    {
        "01" => PowerState.On,
        "02" => PowerState.WarmingUp,
        "03" => PowerState.CoolingDown,
        "00" or "04" or "05" => PowerState.Standby,
        _ => PowerState.Unknown,
    };
}

/// <summary>
/// Epson projectors through ESC/VP.net on TCP 3629 carrying ESC/VP21: power, source, A/V mute (the shutter),
/// freeze, lens memory recall, test pattern off, lamp hours, serial and the error code. ESC/VP21 answers a good set
/// with a bare ":" — accepted — and the query that follows makes it observed.
/// </summary>
public sealed class EpsonDriver(ProjectorLink link, ICredentialStore credentials, IDeviceLog log, DriverTimings? timings = null)
    : ProjectorDriverBase(link, credentials, log, timings)
{
    public override ProjectorProtocol Protocol => ProjectorProtocol.EpsonEscVpNet;

    public override DriverProvenance Provenance => DriverProvenance.PublicImplementation;

    public override ProjectorCapabilities Capabilities =>
        ProjectorCapabilities.Power | ProjectorCapabilities.Input | ProjectorCapabilities.Shutter
        | ProjectorCapabilities.Freeze | ProjectorCapabilities.LensMemory | ProjectorCapabilities.TestPatterns
        | ProjectorCapabilities.Status;

    public override string Caveat(ProjectorCapabilities capability) => capability switch
    {
        ProjectorCapabilities.TestPatterns => "off only (TESTPATTERN 00); the pattern numbers are not in a source PlayMap was built from",
        ProjectorCapabilities.LensMemory => "POPLP (load lens position) on the motorised-lens models",
        ProjectorCapabilities.Shutter => "A/V mute (MUTE ON/OFF)",
        _ => "",
    };

    protected override string InputExample => "30 (Epson's two-digit source code, hex)";

    protected override Task<CommandResult> ConnectCoreAsync(CancellationToken ct) =>
        SessionAsync("connect", async (ch, c) =>
        {
            var v = await QueryAsync(ch, "PWR", c).ConfigureAwait(false);
            return CommandResult.Accepted("connect", "connected — " + (v is null ? "power not read" : DeviceState.PowerWord(EscVpProtocol.Power(v))));
        }, ct);

    protected override Task<CommandResult> PowerCoreAsync(bool on, CancellationToken ct) =>
        SetAndReadAsync(on ? "power on" : "power off", on ? "PWR ON" : "PWR OFF", "PWR",
            v =>
            {
                var p = EscVpProtocol.Power(v);
                return (on ? p is PowerState.On or PowerState.WarmingUp : p is PowerState.Standby or PowerState.CoolingDown, DeviceState.PowerWord(p));
            }, ct);

    protected override Task<CommandResult> InputCoreAsync(string input, CancellationToken ct)
    {
        var code = input.ToUpperInvariant();
        if (code.Length != 2 || !code.All(char.IsAsciiHexDigit))
            return Task.FromResult(CommandResult.Refused("input", $"'{input}' is not an Epson source code — two hex digits, e.g. 30."));
        return SetAndReadAsync("input", "SOURCE " + code, "SOURCE", v => (v == code, "source " + v), ct);
    }

    protected override Task<CommandResult> ShutterCoreAsync(bool closed, CancellationToken ct) =>
        SetAndReadAsync(closed ? "shutter close" : "shutter open", closed ? "MUTE ON" : "MUTE OFF", "MUTE",
            v => (v == (closed ? "ON" : "OFF"), "A/V mute " + v), ct);

    protected override Task<CommandResult> FreezeCoreAsync(bool frozen, CancellationToken ct) =>
        SetAndReadAsync(frozen ? "freeze on" : "freeze off", frozen ? "FREEZE ON" : "FREEZE OFF", "FREEZE",
            v => (v == (frozen ? "ON" : "OFF"), "freeze " + v), ct);

    protected override Task<CommandResult> LensMemoryCoreAsync(int slot, CancellationToken ct) =>
        SessionAsync("lens memory", (ch, c) =>
            SetAsync(ch, "lens memory", "POPLP " + slot.ToString("X2", CultureInfo.InvariantCulture), c), ct);

    protected override Task<CommandResult> TestPatternCoreAsync(string pattern, CancellationToken ct)
    {
        if (pattern != TestPatterns.Off)
            return Task.FromResult(CommandResult.Refused("test pattern " + pattern, "Epson's pattern numbers are not in a source PlayMap was built from — only 'off'."));
        return SessionAsync("test pattern off", (ch, c) => SetAsync(ch, "test pattern off", "TESTPATTERN 00", c), ct);
    }

    protected override Task<DriverReading> StatusCoreAsync(CancellationToken ct) =>
        ConverseReadAsync("status", async (ch, c) =>
        {
            var problem = await HandshakeAsync(ch, "status", c).ConfigureAwait(false);
            if (problem is not null)
                return new DriverReading(problem, null);
            var pwr = await QueryAsync(ch, "PWR", c).ConfigureAwait(false);
            if (pwr is null)
                return new DriverReading(CommandResult.Rejected("status", "the projector answered PWR? with ERR"), null);
            var s = Blank() with { Power = EscVpProtocol.Power(pwr), Manufacturer = "Epson" };
            if (pwr == "05")
                s = s with { Faults = [new DeviceFault("abnormal standby", FaultSeverity.Error)], FaultsRead = true };
            if (await QueryAsync(ch, "MUTE", c).ConfigureAwait(false) is { } mute)
                s = s with { ShutterClosed = mute == "ON" };
            if (await QueryAsync(ch, "FREEZE", c).ConfigureAwait(false) is { } freeze)
                s = s with { Frozen = freeze == "ON" };
            if (await QueryAsync(ch, "SOURCE", c).ConfigureAwait(false) is { } source)
                s = s with { Input = source };
            if (await QueryAsync(ch, "LAMP", c).ConfigureAwait(false) is { } lamp
                && int.TryParse(lamp, NumberStyles.None, CultureInfo.InvariantCulture, out var hours))
                s = s with { LightHours = [hours] };
            if (await QueryAsync(ch, "SNO", c).ConfigureAwait(false) is { } sno)
                s = s with { Serial = sno };
            if (await QueryAsync(ch, "ERR", c).ConfigureAwait(false) is { } err && pwr != "05")
            {
                s = err == "00"
                    ? s with { Faults = [], FaultsRead = true }
                    : s with { Faults = [new DeviceFault("error code " + err + " (see the model's manual)", FaultSeverity.Error)], FaultsRead = true };
            }
            return new DriverReading(CommandResult.Observed("status", s.Headline()), s with { Online = true });
        }, ct);

    protected override Task<CommandResult> RawCoreAsync(string line, CancellationToken ct) =>
        SessionAsync("raw", async (ch, c) =>
        {
            await ch.WriteCommandAsync(line + "\r", c).ConfigureAwait(false);
            var answer = await ch.ReadUntilAsync((byte)':', Timings.Reply, c).ConfigureAwait(false);
            return EscVpProtocol.IsError(answer)
                ? CommandResult.Rejected("raw", "the projector answered ERR", "ERR")
                : CommandResult.Accepted("raw", "answered", answer.Trim());
        }, ct);

    // ---- the conversation ----

    private Task<CommandResult> SetAndReadAsync(string name, string command, string query, Func<string, (bool Ok, string Words)> judge, CancellationToken ct) =>
        SessionAsync(name, async (ch, c) =>
        {
            var set = await SetAsync(ch, name, command, c).ConfigureAwait(false);
            if (!set.Succeeded)
                return set;
            var v = await QueryAsync(ch, query, c).ConfigureAwait(false);
            if (v is null)
                return set.ConfirmedBy(null, query + "? answered ERR");
            var (ok, words) = judge(v);
            return set.ConfirmedBy(ok, words);
        }, ct);

    private async Task<CommandResult> SetAsync(TcpChannel ch, string name, string command, CancellationToken ct)
    {
        await ch.WriteCommandAsync(command + "\r", ct).ConfigureAwait(false);
        var answer = await ch.ReadUntilAsync((byte)':', Timings.Reply, ct).ConfigureAwait(false);
        return EscVpProtocol.IsError(answer)
            ? CommandResult.Rejected(name, "the projector answered ERR (not available now, or not on this model)", "ERR")
            : CommandResult.Accepted(name, "the projector took it", ":");
    }

    private async Task<string?> QueryAsync(TcpChannel ch, string command, CancellationToken ct)
    {
        await ch.WriteCommandAsync(command + "?\r", ct).ConfigureAwait(false);
        var answer = await ch.ReadUntilAsync((byte)':', Timings.Reply, ct).ConfigureAwait(false);
        return EscVpProtocol.Value(answer, command);
    }

    private async Task<CommandResult?> HandshakeAsync(TcpChannel ch, string name, CancellationToken ct)
    {
        var secret = Credential?.Secret;
        if (secret is { Length: > 16 })
            return CommandResult.Refused(name, "An ESC/VP.net password is at most 16 characters.");
        await ch.WriteHandshakeAsync(EscVpProtocol.ConnectRequest(secret), ct).ConfigureAwait(false);
        var header = await ch.ReadExactlyAsync(EscVpProtocol.HeaderSize, Timings.Reply, ct).ConfigureAwait(false);
        var parsed = EscVpProtocol.ParseHeader(header);
        if (parsed is null)
            return CommandResult.Unreached(name, $"{ch.Endpoint} does not answer as ESC/VP.net");
        if (parsed.Value.Headers > 0)
            await ch.ReadExactlyAsync(parsed.Value.Headers * EscVpProtocol.SubHeaderSize, Timings.Reply, ct).ConfigureAwait(false);
        if (parsed.Value.Status == EscVpNetStatus.Ok)
            return null;
        if (parsed.Value.Status == EscVpNetStatus.PasswordRequired && string.IsNullOrEmpty(secret))
            return CommandResult.Unreached(name, NoPasswordWords());
        return CommandResult.Unreached(name, EscVpProtocol.StatusWords(parsed.Value.Status));
    }

    private Task<CommandResult> SessionAsync(string name, Func<TcpChannel, CancellationToken, Task<CommandResult>> body, CancellationToken ct) =>
        ConverseAsync(name, async (ch, c) => await HandshakeAsync(ch, name, c).ConfigureAwait(false) ?? await body(ch, c).ConfigureAwait(false), ct);
}
