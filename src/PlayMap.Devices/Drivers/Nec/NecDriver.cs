using System.Globalization;
using PlayMap.Devices.Commands;
using PlayMap.Devices.Model;
using PlayMap.Devices.Transport;

namespace PlayMap.Devices.Drivers.Nec;

/// <summary>One NEC answer: the two command bytes, whether it is an acknowledgement or an error, and its data.</summary>
public sealed record NecReply(byte Command0, byte Command1, bool Ok, byte[] Data);

/// <summary>
/// NEC (Sharp NEC) projector binary control as pure functions, as recalled from NEC's projector control command
/// reference: TCP 7142, no authentication. A frame is the command bytes, a projector id, a model code, a data
/// length and the data, then a checksum — the low byte of the sum of every byte before it. The answer repeats the
/// two command bytes with 0x20 added to the first (acknowledged) or 0xA0 (refused, two error bytes as data).
/// Not re-verified against a public source in this build: the driver is unverified and every command is gated.
/// </summary>
public static class NecProtocol
{
    public static readonly byte[] PowerOn = Frame(0x02, 0x00, []);
    public static readonly byte[] PowerOff = Frame(0x02, 0x01, []);
    public static readonly byte[] PictureMuteOn = Frame(0x02, 0x10, []);
    public static readonly byte[] PictureMuteOff = Frame(0x02, 0x11, []);
    public static readonly byte[] FreezeOn = Frame(0x01, 0x98, [0x01]);
    public static readonly byte[] FreezeOff = Frame(0x01, 0x98, [0x02]);
    public static readonly byte[] RunningStatus = Frame(0x00, 0x85, [0x01]);

    /// <summary>The checksum: the low byte of the sum of the bytes.</summary>
    public static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        var sum = 0;
        foreach (var b in bytes)
            sum += b;
        return (byte)(sum & 0xFF);
    }

    /// <summary>A command frame: two command bytes, projector id 0, model code 0, length, data, checksum.</summary>
    public static byte[] Frame(byte c0, byte c1, ReadOnlySpan<byte> data)
    {
        var bytes = new byte[5 + data.Length + 1];
        bytes[0] = c0;
        bytes[1] = c1;
        bytes[4] = (byte)data.Length;
        data.CopyTo(bytes.AsSpan(5));
        bytes[^1] = Checksum(bytes.AsSpan(0, bytes.Length - 1));
        return bytes;
    }

    /// <summary>An answer to <paramref name="command"/>, or null when its bytes do not answer it or its checksum is wrong.</summary>
    public static NecReply? ParseReply(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> command)
    {
        if (frame.Length < 6 || frame[4] != frame.Length - 6 || Checksum(frame[..^1]) != frame[^1] || frame[1] != command[1])
            return null;
        var ok = frame[0] == (byte)(command[0] + 0x20);
        var refused = frame[0] == (byte)(command[0] + 0xA0);
        return ok || refused ? new NecReply(frame[0], frame[1], ok, frame[5..^1].ToArray()) : null;
    }

    public static string ErrorWords(byte[] data)
    {
        if (data.Length < 2)
            return "refused";
        return (data[0], data[1]) switch
        {
            (0x00, 0x00) => "the command is not recognised",
            (0x00, 0x01) => "this model does not support the command",
            (0x01, 0x00) => "the value is not valid",
            (0x02, 0x0D) => "the projector is off — the command cannot be accepted now",
            _ => "refused, error " + data[0].ToString("X2", CultureInfo.InvariantCulture) + " " + data[1].ToString("X2", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>The running status's power: the third data byte 00 standby / 01 on, the fourth 01 while cooling.</summary>
    public static PowerState Power(byte[] data)
    {
        if (data.Length < 4)
            return PowerState.Unknown;
        if (data[3] == 0x01)
            return PowerState.CoolingDown;
        return data[2] switch
        {
            0x00 => PowerState.Standby,
            0x01 => PowerState.On,
            _ => PowerState.Unknown,
        };
    }
}

/// <summary>NEC / Sharp NEC projectors through the binary protocol on TCP 7142. Unverified — see <see cref="NecProtocol"/>.</summary>
public sealed class NecDriver(ProjectorLink link, ICredentialStore credentials, IDeviceLog log, DriverTimings? timings = null)
    : ProjectorDriverBase(link, credentials, log, timings)
{
    public override ProjectorProtocol Protocol => ProjectorProtocol.NecBinary;

    public override DriverProvenance Provenance => DriverProvenance.Unverified;

    public override ProjectorCapabilities Capabilities =>
        ProjectorCapabilities.Power | ProjectorCapabilities.Shutter | ProjectorCapabilities.Freeze | ProjectorCapabilities.Status;

    protected override Task<CommandResult> ConnectCoreAsync(CancellationToken ct) =>
        ConverseAsync("connect", async (ch, c) =>
        {
            var r = await ExchangeAsync(ch, NecProtocol.RunningStatus, c).ConfigureAwait(false);
            return r is { Ok: true }
                ? CommandResult.Accepted("connect", "connected — " + DeviceState.PowerWord(NecProtocol.Power(r.Data)))
                : CommandResult.Accepted("connect", "connected (the running status was not read)");
        }, ct);

    protected override Task<CommandResult> PowerCoreAsync(bool on, CancellationToken ct) =>
        SetAndReadAsync(on ? "power on" : "power off", on ? NecProtocol.PowerOn : NecProtocol.PowerOff,
            p => on ? p is PowerState.On or PowerState.WarmingUp : p is PowerState.Standby or PowerState.CoolingDown, ct);

    protected override Task<CommandResult> ShutterCoreAsync(bool closed, CancellationToken ct) =>
        ConverseAsync(closed ? "shutter close" : "shutter open", (ch, c) =>
            SendAsync(ch, closed ? "picture mute on" : "picture mute off", closed ? NecProtocol.PictureMuteOn : NecProtocol.PictureMuteOff, c), ct);

    protected override Task<CommandResult> FreezeCoreAsync(bool frozen, CancellationToken ct) =>
        ConverseAsync(frozen ? "freeze on" : "freeze off", (ch, c) =>
            SendAsync(ch, frozen ? "freeze on" : "freeze off", frozen ? NecProtocol.FreezeOn : NecProtocol.FreezeOff, c), ct);

    protected override Task<DriverReading> StatusCoreAsync(CancellationToken ct) =>
        ConverseReadAsync("status", async (ch, c) =>
        {
            var r = await ExchangeAsync(ch, NecProtocol.RunningStatus, c).ConfigureAwait(false);
            if (r is null)
                return new DriverReading(CommandResult.Sent("status", "asked; the answer was not NEC's"), null);
            if (!r.Ok)
                return new DriverReading(CommandResult.Rejected("status", NecProtocol.ErrorWords(r.Data)), null);
            var s = Blank() with { Power = NecProtocol.Power(r.Data), Manufacturer = "NEC" };
            return new DriverReading(CommandResult.Observed("status", s.Headline()), s with { Online = true });
        }, ct);

    protected override Task<CommandResult> RawCoreAsync(string line, CancellationToken ct)
    {
        // Hex bytes without the checksum ("02 00 00 00 00"); the checksum is added.
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new List<byte>();
        foreach (var p in parts)
        {
            if (!byte.TryParse(p.TrimEnd('H', 'h'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                return Task.FromResult(CommandResult.Refused("raw", $"'{p}' is not a hex byte — an NEC line is hex bytes without the checksum: 02 00 00 00 00."));
            bytes.Add(b);
        }
        if (bytes.Count < 5)
            return Task.FromResult(CommandResult.Refused("raw", "An NEC command is at least five bytes: two command bytes, id, model code, length."));
        var frame = NecProtocol.Frame(bytes[0], bytes[1], bytes.Skip(5).ToArray());
        return ConverseAsync("raw", (ch, c) => SendAsync(ch, "raw", frame, c), ct);
    }

    // ---- the conversation ----

    private Task<CommandResult> SetAndReadAsync(string name, byte[] command, Func<PowerState, bool> judge, CancellationToken ct) =>
        ConverseAsync(name, async (ch, c) =>
        {
            var set = await SendAsync(ch, name, command, c).ConfigureAwait(false);
            if (!set.Succeeded)
                return set;
            var back = await ExchangeAsync(ch, NecProtocol.RunningStatus, c).ConfigureAwait(false);
            if (back is not { Ok: true })
                return set.ConfirmedBy(null, "running status not read");
            var p = NecProtocol.Power(back.Data);
            return set.ConfirmedBy(judge(p), DeviceState.PowerWord(p));
        }, ct);

    private async Task<CommandResult> SendAsync(TcpChannel ch, string name, byte[] command, CancellationToken ct)
    {
        var r = await ExchangeAsync(ch, command, ct).ConfigureAwait(false);
        if (r is null)
            return CommandResult.Sent(name, "sent; the answer was not NEC's");
        return r.Ok
            ? CommandResult.Accepted(name, "the projector acknowledged it")
            : CommandResult.Rejected(name, NecProtocol.ErrorWords(r.Data));
    }

    private async Task<NecReply?> ExchangeAsync(TcpChannel ch, byte[] command, CancellationToken ct)
    {
        await ch.WriteCommandAsync(command, ct).ConfigureAwait(false);
        var head = await ch.ReadExactlyAsync(5, Timings.Reply, ct).ConfigureAwait(false);
        var rest = await ch.ReadExactlyAsync(head[4] + 1, Timings.Reply, ct).ConfigureAwait(false);
        return NecProtocol.ParseReply([.. head, .. rest], command);
    }
}
