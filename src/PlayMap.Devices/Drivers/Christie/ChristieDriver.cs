using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PlayMap.Devices.Commands;
using PlayMap.Devices.Model;
using PlayMap.Devices.Transport;

namespace PlayMap.Devices.Drivers.Christie;

/// <summary>One Christie frame: "(PWR!001)" → PWR / 001; an error frame carries its code.</summary>
public sealed record ChristieReply(string Code, string Value, int Error)
{
    public bool IsError => Error > 0;
}

/// <summary>
/// Christie's serial protocol over TCP 3002 as pure functions. A command is a three-letter code in parentheses —
/// "(PWR 1)", "(SHU 1)", "(LHO 0120)" — a query adds "?" — "(SHU?)" — and the answer carries "!" — "(SHU!001)".
/// A good set is answered with silence; a bad one with an error frame whose code the public implementations read
/// as 001 system crash … 012 supplementary info. Unsolicited "FYI" frames may arrive at any time and are skipped.
/// </summary>
public static partial class ChristieProtocol
{
    public static string Set(string code, string value) => "(" + code + " " + value + ")";

    public static string Query(string code) => "(" + code + "?)";

    /// <summary>Every frame in <paramref name="text"/> ("(…)" each), parsed; text outside frames is ignored.</summary>
    public static IReadOnlyList<ChristieReply> ParseFrames(string text)
    {
        var frames = new List<ChristieReply>();
        var depth = 0;
        var start = -1;
        var inQuote = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' && depth > 0)
                inQuote = !inQuote;
            else if (!inQuote && c == '(' && depth++ == 0)
                start = i + 1;
            else if (!inQuote && c == ')' && depth > 0 && --depth == 0)
                frames.Add(ParseFrame(text[start..i]));
        }
        return frames;
    }

    /// <summary>The inside of one frame: "SHU!001", "LHO!-0120", "65535 00000 ERR00003 \"…\"".</summary>
    public static ChristieReply ParseFrame(string inner)
    {
        var t = inner.Trim();
        var err = ErrorCode().Match(t);
        if (err.Success)
            return new ChristieReply("ERR", t, int.Parse(err.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture));
        var bang = t.IndexOf('!', StringComparison.Ordinal);
        if (bang < 0)
            return new ChristieReply(t.Split(' ')[0], "", 0);
        var code = t[..bang].Trim();
        var rest = t[(bang + 1)..].Trim();
        var space = rest.IndexOf(' ', StringComparison.Ordinal);
        return new ChristieReply(code, space < 0 ? rest : rest[..space], 0);
    }

    [GeneratedRegex(@"ERR\s*0*(\d{1,5})", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex ErrorCode();

    /// <summary>The error codes' meanings as the public implementations read them.</summary>
    public static string ErrorWords(int code) => code switch
    {
        1 => "system crash",
        2 => "system warning",
        3 => "invalid parameter",
        4 => "too many parameters",
        5 => "too few parameters",
        6 => "source does not exist",
        7 => "could not be executed",
        8 => "checksum error",
        9 => "unknown request — this model does not have the command",
        10 => "communication error",
        11 => "receive break",
        12 => "supplementary information",
        _ => "error " + code.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>PWR's value: 0 off, 1 on; 2 booting and 11 warming, 10 cooling; 20–23 standby variants.</summary>
    public static PowerState Power(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            ? v switch
            {
                0 or 20 or 21 or 22 or 23 => PowerState.Standby,
                1 => PowerState.On,
                2 or 11 => PowerState.WarmingUp,
                10 => PowerState.CoolingDown,
                _ => PowerState.Unknown,
            }
            : PowerState.Unknown;

    public static int? Number(string value) =>
        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>A signed raw position padded to four digits as the projector's examples have it ("0120", "-0120").</summary>
    public static string Pad4(int v) =>
        (v < 0 ? "-" : "") + Math.Abs(v).ToString("0000", CultureInfo.InvariantCulture);

    public static string LensCode(LensAxis axis) => axis switch
    {
        LensAxis.ShiftHorizontal => "LHO",
        LensAxis.ShiftVertical => "LVO",
        LensAxis.Zoom => "ZOM",
        _ => "FCS",
    };
}

/// <summary>
/// Christie projectors through their serial protocol on TCP 3002 (Roadie, Roadster, Mirage, DS+/DW/HD, Matrix,
/// M-series, Boxer and the families that share it). A set is answered with silence when it works, so a set is
/// <see cref="CommandStage.Sent"/> until its query reads the asked state back (<see cref="CommandStage.Observed"/>).
/// Commands on one projector are kept 150 ms apart, as Christie's controllers need.
/// </summary>
public sealed class ChristieDriver(ProjectorLink link, ICredentialStore credentials, IDeviceLog log, DriverTimings? timings = null)
    : ProjectorDriverBase(link, credentials, log, timings)
{
    private static readonly string[] Patterns = ["grid", "grey-16", "white"];

    public override ProjectorProtocol Protocol => ProjectorProtocol.ChristieAscii;

    public override DriverProvenance Provenance => DriverProvenance.PublicImplementation;

    public override ProjectorCapabilities Capabilities =>
        ProjectorCapabilities.Power | ProjectorCapabilities.Input | ProjectorCapabilities.Shutter
        | ProjectorCapabilities.Freeze | ProjectorCapabilities.LensMotor | ProjectorCapabilities.LensPositionRead
        | ProjectorCapabilities.TestPatterns | ProjectorCapabilities.GeometryReset | ProjectorCapabilities.Status;

    public override ProjectorCapabilities Unverified => ProjectorCapabilities.GeometryReset;

    public override string Caveat(ProjectorCapabilities capability) => capability switch
    {
        ProjectorCapabilities.GeometryReset =>
            "warp select differs by family — (WRP+SLCT 0) on M-series and Boxer, (WPS 0) on others; PlayMap sends the first and falls back to the second on 'unknown request'",
        ProjectorCapabilities.TestPatterns => "pattern numbers beyond off/grid/grey-16/white differ by family",
        ProjectorCapabilities.LensMotor => "absolute motor positions (LHO/LVO ±2050, ZOM/FCS on motorised lenses)",
        _ => "",
    };

    public override IReadOnlyList<string> TestPatternNames => Patterns;

    protected override string InputExample => "1 (the input number on the projector's menu)";

    protected override Task<CommandResult> ConnectCoreAsync(CancellationToken ct) =>
        ConverseAsync("connect", async (ch, c) =>
        {
            var r = await AskAsync(ch, "PWR", c).ConfigureAwait(false);
            return r is null
                ? CommandResult.Accepted("connect", "connected (the projector did not answer PWR?)")
                : CommandResult.Accepted("connect", "connected — " + DeviceState.PowerWord(ChristieProtocol.Power(r.Value)));
        }, ct);

    protected override Task<CommandResult> PowerCoreAsync(bool on, CancellationToken ct) =>
        SetAndReadAsync(on ? "power on" : "power off", "PWR", on ? "1" : "0",
            v =>
            {
                var p = ChristieProtocol.Power(v);
                return (on ? p is PowerState.On or PowerState.WarmingUp : p is PowerState.Standby or PowerState.CoolingDown, DeviceState.PowerWord(p));
            }, ct);

    protected override Task<CommandResult> InputCoreAsync(string input, CancellationToken ct)
    {
        if (ChristieProtocol.Number(input) is not { } n || n < 1)
            return Task.FromResult(CommandResult.Refused("input", $"'{input}' is not a Christie input number — the input's number on the projector's menu, e.g. 1."));
        return SetAndReadAsync("input", "SIN", n.ToString(CultureInfo.InvariantCulture),
            v => (ChristieProtocol.Number(v) == n, "input " + v), ct);
    }

    protected override Task<CommandResult> ShutterCoreAsync(bool closed, CancellationToken ct) =>
        SetAndReadAsync(closed ? "shutter close" : "shutter open", "SHU", closed ? "1" : "0",
            v => (ChristieProtocol.Number(v) == (closed ? 1 : 0), ChristieProtocol.Number(v) == 1 ? "shutter closed" : "shutter open"), ct);

    protected override Task<CommandResult> FreezeCoreAsync(bool frozen, CancellationToken ct) =>
        SetAndReadAsync(frozen ? "freeze on" : "freeze off", "FRZ", frozen ? "1" : "0",
            v => (ChristieProtocol.Number(v) == (frozen ? 1 : 0), ChristieProtocol.Number(v) == 1 ? "frozen" : "not frozen"), ct);

    protected override Task<CommandResult> TestPatternCoreAsync(string pattern, CancellationToken ct)
    {
        var code = pattern switch
        {
            TestPatterns.Off => 0,
            TestPatterns.Identify or "grid" => 1,
            "grey-16" => 2,
            _ => 3,
        };
        return SetAndReadAsync("test pattern " + pattern, "ITP", code.ToString(CultureInfo.InvariantCulture),
            v => (ChristieProtocol.Number(v) == code, ChristieProtocol.Number(v) == 0 ? "patterns off" : "pattern " + v), ct);
    }

    protected override Task<CommandResult> LensMoveCoreAsync(LensMove move, CancellationToken ct)
    {
        if (!move.Absolute)
            return Task.FromResult(CommandResult.Refused("lens", "Christie's lens commands take absolute positions here — read the lens and move to a position."));
        var code = ChristieProtocol.LensCode(move.Axis);
        return ConverseAsync("lens", async (ch, c) =>
        {
            var set = await SetAsync(ch, "lens", code, ChristieProtocol.Pad4(move.Value), c).ConfigureAwait(false);
            // A motor takes seconds to arrive: the move is an attempt until a later lens read finds it there.
            return set.Stage == CommandStage.Sent ? set with { Words = "moving — read the lens to confirm where it stopped" } : set;
        }, ct);
    }

    protected override Task<CommandResult> GeometryResetCoreAsync(CancellationToken ct) =>
        ConverseAsync("geometry off", async (ch, c) =>
        {
            var first = await SetAsync(ch, "geometry off", "WRP+SLCT", "0", c).ConfigureAwait(false);
            if (first.Stage != CommandStage.Rejected || !first.Words.Contains("unknown request", StringComparison.Ordinal))
                return first with { Words = first.Words + " (WRP+SLCT 0)" };
            await Task.Delay(Timings.CommandGap, c).ConfigureAwait(false);
            var second = await SetAsync(ch, "geometry off", "WPS", "0", c).ConfigureAwait(false);
            return second with { Words = second.Words + " (WPS 0)" };
        }, ct);

    protected override Task<DriverReading> StatusCoreAsync(CancellationToken ct) =>
        ConverseReadAsync("status", async (ch, c) =>
        {
            var s = Blank();
            var heard = 0;
            if (await AskAsync(ch, "PWR", c).ConfigureAwait(false) is { } pwr)
            {
                heard++;
                s = s with { Power = ChristieProtocol.Power(pwr.Value) };
            }
            if (await AskAsync(ch, "SHU", c).ConfigureAwait(false) is { } shu)
            {
                heard++;
                s = s with { ShutterClosed = ChristieProtocol.Number(shu.Value) is { } v ? v == 1 : null };
            }
            if (await AskAsync(ch, "FRZ", c).ConfigureAwait(false) is { } frz)
                s = s with { Frozen = ChristieProtocol.Number(frz.Value) is { } v ? v == 1 : null };
            if (await AskAsync(ch, "SIN", c).ConfigureAwait(false) is { } sin)
                s = s with { Input = sin.Value };
            if (await AskAsync(ch, "LPH", c).ConfigureAwait(false) is { } lph && ChristieProtocol.Number(lph.Value) is { } hours)
                s = s with { LightHours = [hours] };
            return heard == 0
                ? new DriverReading(CommandResult.Sent("status", "asked; the projector did not answer PWR? or SHU?"), null)
                : new DriverReading(CommandResult.Observed("status", s.Headline()), s with { Online = true });
        }, ct);

    protected override Task<DriverReading> LensReadCoreAsync(CancellationToken ct) =>
        ConverseReadAsync("lens read", async (ch, c) =>
        {
            var lens = LensPositions.Unknown;
            foreach (var axis in Enum.GetValues<LensAxis>())
            {
                if (await AskAsync(ch, ChristieProtocol.LensCode(axis), c).ConfigureAwait(false) is { } r)
                    lens = lens.With(axis, ChristieProtocol.Number(r.Value));
            }
            return lens.Any
                ? new DriverReading(CommandResult.Observed("lens read", lens.Words()), Blank() with { Lens = lens, Online = true })
                : new DriverReading(CommandResult.Sent("lens read", "asked; no lens position came back (a fixed or unmotorised lens?)"), null);
        }, ct);

    protected override Task<CommandResult> RawCoreAsync(string line, CancellationToken ct) =>
        ConverseAsync("raw", async (ch, c) =>
        {
            var frame = line.StartsWith('(') ? line : "(" + line + ")";
            await ch.WriteCommandAsync(frame, c).ConfigureAwait(false);
            var text = await ReadWindowAsync(ch, Timings.Reply, frames => frames.Any(f => !f.Code.EndsWith("FYI", StringComparison.Ordinal)), c).ConfigureAwait(false);
            var frames = ChristieProtocol.ParseFrames(text);
            var err = frames.FirstOrDefault(f => f.IsError);
            if (err is not null)
                return CommandResult.Rejected("raw", ChristieProtocol.ErrorWords(err.Error), text.Trim());
            return text.Length == 0 ? CommandResult.Sent("raw", "sent; no answer (a good set is answered with silence)") : CommandResult.Accepted("raw", "answered", text.Trim());
        }, ct);

    // ---- the conversation ----

    /// <summary>Set, then (after the gap) query the same code on the same connection and judge the read-back.</summary>
    private Task<CommandResult> SetAndReadAsync(string name, string code, string value, Func<string, (bool Ok, string Words)> judge, CancellationToken ct) =>
        ConverseAsync(name, async (ch, c) =>
        {
            var set = await SetAsync(ch, name, code, value, c).ConfigureAwait(false);
            if (set.Stage != CommandStage.Sent)
                return set;
            var back = await AskAsync(ch, code, c).ConfigureAwait(false);
            if (back is null)
                return set.ConfirmedBy(null, "no answer to " + ChristieProtocol.Query(code));
            var (ok, words) = judge(back.Value);
            return set.ConfirmedBy(ok, words);
        }, ct);

    /// <summary>Sends a set and waits the silence window: an error frame is a rejection, silence is "sent".</summary>
    private async Task<CommandResult> SetAsync(TcpChannel ch, string name, string code, string value, CancellationToken ct)
    {
        await ch.WriteCommandAsync(ChristieProtocol.Set(code, value), ct).ConfigureAwait(false);
        var text = await ReadWindowAsync(ch, Timings.SetSilence, frames => frames.Any(f => f.IsError), ct).ConfigureAwait(false);
        var err = ChristieProtocol.ParseFrames(text).FirstOrDefault(f => f.IsError);
        if (err is not null)
            return CommandResult.Rejected(name, ChristieProtocol.ErrorWords(err.Error), text.Trim());
        await Task.Delay(Timings.CommandGap, ct).ConfigureAwait(false);
        return CommandResult.Sent(name, "sent (a Christie set is answered with silence)");
    }

    /// <summary>Everything that arrives within <paramref name="window"/>, stopping early once <paramref name="done"/>
    /// holds for the frames so far. The window is not ended by the first bytes: an unsolicited FYI frame arriving
    /// first must not pass for the silence of a good set, nor for a raw line's answer.</summary>
    private static async Task<string> ReadWindowAsync(TcpChannel ch, TimeSpan window, Func<IReadOnlyList<ChristieReply>, bool> done, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var text = new StringBuilder();
        while (clock.Elapsed < window)
        {
            var more = await ch.ReadWhatArrivesAsync(window - clock.Elapsed, ct).ConfigureAwait(false);
            if (more.Length == 0)
                break;
            text.Append(more);
            if (done(ChristieProtocol.ParseFrames(text.ToString())))
                break;
        }
        return text.ToString();
    }

    /// <summary>A query's answer frame for <paramref name="code"/>, skipping FYI frames; null for an error or no answer.</summary>
    private async Task<ChristieReply?> AskAsync(TcpChannel ch, string code, CancellationToken ct)
    {
        await ch.WriteCommandAsync(ChristieProtocol.Query(code), ct).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();
        ChristieReply? found = null;
        while (found is null && clock.Elapsed < Timings.Reply)
        {
            string frame;
            try
            {
                frame = await ch.ReadUntilAsync((byte)')', Timings.Reply - clock.Elapsed, ct).ConfigureAwait(false);
            }
            catch (DeviceIoException e) when (e.Kind == DeviceIoKind.Timeout)
            {
                break;
            }
            var open = frame.LastIndexOf('(');
            var r = ChristieProtocol.ParseFrame(open >= 0 ? frame[(open + 1)..] : frame);
            if (r.IsError)
                break;
            if (r.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
                found = r;
        }
        await Task.Delay(Timings.CommandGap, ct).ConfigureAwait(false);
        return found;
    }
}
