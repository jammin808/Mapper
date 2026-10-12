using PlayMap.Devices.Commands;
using PlayMap.Devices.Model;
using PlayMap.Devices.Transport;

namespace PlayMap.Devices.Drivers.PjLink;

/// <summary>
/// PJLink class 1 and 2 over TCP 4352. A command opens its own connection (greeting, digest when asked, the
/// command, the answer, close) — the way the spec's projectors expect to be driven, and what survives a projector
/// that serves one controller at a time; a status read sends its queries on one connection, which the spec allows
/// (the digest goes in front of the first only). A set is confirmed by a read-back on a fresh connection, so a
/// result reaches <see cref="CommandStage.Observed"/> only when the projector reads back what was asked.
/// A connection dropped before the greeting (a projector busy with another controller) is retried once.
/// </summary>
public sealed class PjLinkDriver(ProjectorLink link, ICredentialStore credentials, IDeviceLog log, DriverTimings? timings = null)
    : ProjectorDriverBase(link, credentials, log, timings)
{
    private static readonly TimeSpan BusyRetry = TimeSpan.FromMilliseconds(300);

    private int _class;

    public override ProjectorProtocol Protocol => ProjectorProtocol.PjLink;

    public override DriverProvenance Provenance => DriverProvenance.VendorDocument;

    public override ProjectorCapabilities Capabilities =>
        ProjectorCapabilities.Power | ProjectorCapabilities.Input | ProjectorCapabilities.Shutter
        | ProjectorCapabilities.Freeze | ProjectorCapabilities.Status;

    public override string Caveat(ProjectorCapabilities capability) => capability switch
    {
        ProjectorCapabilities.Freeze => "class 2 projectors only (FREZ); a class 1 projector answers ERR1",
        ProjectorCapabilities.Shutter => "AV mute (AVMT 31/30): picture and sound — the one mute every class 1 projector has",
        _ => "",
    };

    /// <summary>The class the projector reported (1 or 2); 0 until a status read asked.</summary>
    public int ProjectorClass => _class;

    protected override string InputExample => "31 (digital 1) or, on class 2, 3A";

    protected override Task<CommandResult> ConnectCoreAsync(CancellationToken ct) =>
        PjConverseAsync("connect", async (ch, s, c) =>
        {
            var reply = await AskAsync(ch, s, 1, "CLSS ?", c).ConfigureAwait(false);
            if (reply.Problem is not null)
                return reply.Problem;
            return reply.Reply is { IsError: false } r
                ? CommandResult.Accepted("connect", "connected — class " + r.Value, Line(r))
                : CommandResult.Accepted("connect", "connected", reply.Raw);
        }, ct);

    protected override async Task<CommandResult> PowerCoreAsync(bool on, CancellationToken ct)
    {
        var name = on ? "power on" : "power off";
        var result = await SetAsync(name, 1, on ? "POWR 1" : "POWR 0", ct).ConfigureAwait(false);
        if (!result.Succeeded)
            return result;
        var back = await QueryAsync(name, 1, "POWR ?", ct).ConfigureAwait(false);
        if (back is null)
            return result.ConfirmedBy(null, "power not read back");
        var p = PjLinkProtocol.Power(back.Value);
        // Warming up is the projector doing "on"; cooling down is it doing "off".
        var ok = on ? p is PowerState.On or PowerState.WarmingUp : p is PowerState.Standby or PowerState.CoolingDown;
        return result.ConfirmedBy(ok, DeviceState.PowerWord(p));
    }

    protected override async Task<CommandResult> InputCoreAsync(string input, CancellationToken ct)
    {
        var code = input.ToUpperInvariant();
        if (!PjLinkProtocol.IsInputCode(code))
            return CommandResult.Refused("input", $"'{input}' is not a PJLink input — two characters: a family 1–6 (RGB, video, digital, storage, network, internal) and a number, e.g. 31.");
        var cls = char.IsAsciiLetter(code[1]) ? 2 : 1;
        var result = await SetAsync("input", cls, "INPT " + code, ct).ConfigureAwait(false);
        if (!result.Succeeded)
            return result;
        var back = await QueryAsync("input", cls, "INPT ?", ct).ConfigureAwait(false);
        return result.ConfirmedBy(back is null ? null : back.Value.Equals(code, StringComparison.OrdinalIgnoreCase),
            back is null ? "input not read back" : PjLinkProtocol.InputWords(back.Value));
    }

    protected override async Task<CommandResult> ShutterCoreAsync(bool closed, CancellationToken ct)
    {
        var name = closed ? "shutter close" : "shutter open";
        var result = await SetAsync(name, 1, closed ? "AVMT 31" : "AVMT 30", ct).ConfigureAwait(false);
        if (!result.Succeeded)
            return result;
        var back = await QueryAsync(name, 1, "AVMT ?", ct).ConfigureAwait(false);
        var muted = back is null ? null : PjLinkProtocol.PictureMuted(back.Value);
        return result.ConfirmedBy(muted is null ? null : muted == closed,
            muted is null ? "mute not read back" : muted.Value ? "picture muted" : "picture shown");
    }

    protected override async Task<CommandResult> FreezeCoreAsync(bool frozen, CancellationToken ct)
    {
        var name = frozen ? "freeze on" : "freeze off";
        var result = await SetAsync(name, 2, frozen ? "FREZ 1" : "FREZ 0", ct).ConfigureAwait(false);
        if (!result.Succeeded)
            return result;
        var back = await QueryAsync(name, 2, "FREZ ?", ct).ConfigureAwait(false);
        return result.ConfirmedBy(back is null ? null : back.Value == (frozen ? "1" : "0"),
            back is null ? "freeze not read back" : back.Value == "1" ? "frozen" : "not frozen");
    }

    protected override Task<DriverReading> StatusCoreAsync(CancellationToken ct) =>
        PjConverseReadAsync("status", async (ch, s, c) =>
        {
            var state = Blank();
            var heard = 0;
            var notes = new List<string>();
            CommandResult? problem = null;

            async Task<string?> Ask(int cls, string body)
            {
                if (problem is not null)
                    return null;
                var r = await AskAsync(ch, s, cls, body, c).ConfigureAwait(false);
                if (r.Problem is not null)
                {
                    // Authentication failed on the first query: the projector closes; nothing more is asked.
                    problem = r.Problem;
                    return null;
                }
                heard++;
                if (r.Reply is null || r.Reply.IsError)
                    return null;
                return r.Reply.Value;
            }

            if (await Ask(1, "POWR ?").ConfigureAwait(false) is { } powr)
                state = state with { Power = PjLinkProtocol.Power(powr) };
            if (await Ask(1, "CLSS ?").ConfigureAwait(false) is { } clss && int.TryParse(clss, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var k))
                _class = k;
            if (await Ask(1, "ERST ?").ConfigureAwait(false) is { } erst && PjLinkProtocol.Faults(erst) is { } faults)
                state = state with { Faults = faults, FaultsRead = true };
            if (await Ask(1, "LAMP ?").ConfigureAwait(false) is { } lamp && PjLinkProtocol.Lamps(lamp) is { } lamps)
                state = state with { LightHours = lamps.Hours };
            if (await Ask(1, "AVMT ?").ConfigureAwait(false) is { } avmt)
                state = state with { ShutterClosed = PjLinkProtocol.PictureMuted(avmt) };
            if (await Ask(1, "INPT ?").ConfigureAwait(false) is { } inpt)
                state = state with { Input = inpt };
            if (await Ask(1, "NAME ?").ConfigureAwait(false) is { } pjName)
                state = state with { ProjectorName = pjName };
            if (await Ask(1, "INF1 ?").ConfigureAwait(false) is { } inf1)
                state = state with { Manufacturer = inf1 };
            if (await Ask(1, "INF2 ?").ConfigureAwait(false) is { } inf2)
                state = state with { Model = inf2 };
            if (_class >= 2)
            {
                if (await Ask(2, "SNUM ?").ConfigureAwait(false) is { } snum)
                    state = state with { Serial = snum };
                if (await Ask(2, "SVER ?").ConfigureAwait(false) is { } sver)
                    state = state with { Firmware = sver };
                if (await Ask(2, "IRES ?").ConfigureAwait(false) is { } ires)
                    state = state with { Signal = PjLinkProtocol.Resolution(ires) };
                if (await Ask(2, "FREZ ?").ConfigureAwait(false) is { } frez)
                    state = state with { Frozen = frez == "1" };
            }
            else
            {
                notes.Add("class 1 — serial, firmware, signal and freeze are class 2's");
            }
            if (problem is not null)
                return new DriverReading(problem with { Command = "status" }, null);
            var words = heard == 0 ? "no answer" : state.Headline() + (notes.Count > 0 ? " (" + string.Join("; ", notes) + ")" : "");
            return new DriverReading(CommandResult.Observed("status", words), state with { Online = true });
        }, ct);

    protected override Task<CommandResult> RawCoreAsync(string line, CancellationToken ct)
    {
        var text = line.Trim();
        var cls = 1;
        if (text.StartsWith('%'))
        {
            if (text.Length < 3 || !char.IsAsciiDigit(text[1]))
                return Task.FromResult(CommandResult.Refused("raw", "A PJLink line is %1 or %2 and a four-letter command: %1POWR ?"));
            cls = text[1] - '0';
            text = text[2..];
        }
        return PjConverseAsync("raw", async (ch, s, c) =>
        {
            var r = await AskAsync(ch, s, cls, text, c).ConfigureAwait(false);
            if (r.Problem is not null)
                return r.Problem;
            if (r.Reply is null)
                return CommandResult.Sent("raw", "sent; the answer was not a PJLink reply", r.Raw);
            return r.Reply.IsError
                ? CommandResult.Rejected("raw", PjLinkProtocol.ErrorWords(r.Reply.Error), r.Raw)
                : CommandResult.Accepted("raw", r.Reply.Value, r.Raw);
        }, ct);
    }

    // ---- the conversation ----

    private sealed class Session(string prefix)
    {
        /// <summary>The digest still to send in front of the next command ("" once sent, or with no auth).</summary>
        public string Prefix { get; set; } = prefix;
    }

    private sealed record Answer(PjLinkReply? Reply, string Raw, CommandResult? Problem);

    private async Task<CommandResult> SetAsync(string name, int cls, string body, CancellationToken ct) =>
        await PjConverseAsync(name, async (ch, s, c) =>
        {
            var r = await AskAsync(ch, s, cls, body, c).ConfigureAwait(false);
            if (r.Problem is not null)
                return r.Problem;
            if (r.Reply is null)
                return CommandResult.Sent(name, "sent; the answer was not a PJLink reply", r.Raw);
            if (r.Reply.IsError)
                return CommandResult.Rejected(name, PjLinkProtocol.ErrorWords(r.Reply.Error), r.Raw);
            return r.Reply.IsOk
                ? CommandResult.Accepted(name, "the projector said OK", r.Raw)
                : CommandResult.Sent(name, "the projector answered " + r.Reply.Value, r.Raw);
        }, ct).ConfigureAwait(false);

    /// <summary>A query's value on its own connection; null when it could not be read (an error is not a value).</summary>
    private async Task<PjLinkReply?> QueryAsync(string name, int cls, string body, CancellationToken ct)
    {
        PjLinkReply? value = null;
        await PjConverseAsync(name, async (ch, s, c) =>
        {
            var r = await AskAsync(ch, s, cls, body, c).ConfigureAwait(false);
            if (r.Reply is { IsError: false })
                value = r.Reply;
            return r.Problem ?? CommandResult.Accepted(name, "read");
        }, ct).ConfigureAwait(false);
        return value;
    }

    private async Task<Answer> AskAsync(TcpChannel ch, Session s, int cls, string body, CancellationToken ct)
    {
        var frame = PjLinkProtocol.Frame(cls, body);
        if (s.Prefix.Length > 0)
        {
            // The digest rides in the same write as the first command, as the spec has it.
            var prefix = s.Prefix;
            s.Prefix = "";
            await ch.WriteCommandAsync(prefix + frame, ct).ConfigureAwait(false);
        }
        else
        {
            await ch.WriteCommandAsync(frame, ct).ConfigureAwait(false);
        }
        var line = await ch.ReadUntilAsync((byte)'\r', Timings.Reply, ct).ConfigureAwait(false);
        var greeting = PjLinkProtocol.ParseGreeting(line);
        if (greeting.Kind == PjLinkGreetingKind.AuthFailed)
            return new Answer(null, "PJLINK ERRA", CommandResult.Unreached(body, PjLinkProtocol.ErrorWords("ERRA")));
        return new Answer(PjLinkProtocol.ParseReply(line), line.Trim(), null);
    }

    /// <summary>Reads the greeting and works out the digest; a problem (no password, not PJLink) as a result.</summary>
    private async Task<(CommandResult? Problem, Session Session)> GreetAsync(TcpChannel ch, string command, CancellationToken ct)
    {
        var line = await ch.ReadUntilAsync((byte)'\r', Timings.Reply, ct).ConfigureAwait(false);
        var g = PjLinkProtocol.ParseGreeting(line);
        switch (g.Kind)
        {
            case PjLinkGreetingKind.Open:
                return (null, new Session(""));
            case PjLinkGreetingKind.Protected:
                var secret = Credential?.Secret ?? "";
                if (secret.Length == 0)
                    return (CommandResult.Unreached(command, NoPasswordWords()), new Session(""));
                return (null, new Session(PjLinkProtocol.Digest(g.Seed, secret)));
            case PjLinkGreetingKind.AuthFailed:
                return (CommandResult.Unreached(command, PjLinkProtocol.ErrorWords("ERRA")), new Session(""));
            default:
                return (CommandResult.Unreached(command, $"{ch.Endpoint} does not greet as a PJLink projector"), new Session(""));
        }
    }

    private async Task<CommandResult> PjConverseAsync(string command, Func<TcpChannel, Session, CancellationToken, Task<CommandResult>> body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var greeted = false;
            var result = await ConverseAsync(command, async (ch, c) =>
            {
                var (problem, session) = await GreetAsync(ch, command, c).ConfigureAwait(false);
                greeted = true;
                return problem ?? await body(ch, session, c).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            if (result.Stage == CommandStage.Unreached && !greeted && attempt == 0)
            {
                await Task.Delay(BusyRetry, ct).ConfigureAwait(false);
                continue;
            }
            return result;
        }
    }

    private async Task<DriverReading> PjConverseReadAsync(string command, Func<TcpChannel, Session, CancellationToken, Task<DriverReading>> body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var greeted = false;
            var reading = await ConverseReadAsync(command, async (ch, c) =>
            {
                var (problem, session) = await GreetAsync(ch, command, c).ConfigureAwait(false);
                greeted = true;
                if (problem is not null)
                    return new DriverReading(problem, null);
                return await body(ch, session, c).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            if (reading.Result.Stage == CommandStage.Unreached && !greeted && attempt == 0)
            {
                await Task.Delay(BusyRetry, ct).ConfigureAwait(false);
                continue;
            }
            return reading;
        }
    }

    private static string Line(PjLinkReply r) =>
        "%" + r.Class.ToString(System.Globalization.CultureInfo.InvariantCulture) + r.Command + "=" + (r.IsError ? r.Error : r.Value);
}
