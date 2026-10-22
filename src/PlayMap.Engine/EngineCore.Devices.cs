using System.Globalization;
using System.Net;
using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Devices.Commands;
using PlayMap.Devices.Drivers;
using PlayMap.Devices.Model;
using PlayMap.Devices.Workflows;
using PlayMap.Engine.Devices;

namespace PlayMap.Engine;

/// <summary>The DEVICE verbs (docs/PROJECTORS.md §7) and the credential verbs. A projector's answer is the receipt:
/// refused, unreached or rejected is an ERR with the words; sent or accepted is an OK that is not yet a fact;
/// observed (a read-back confirmed it) is applied.</summary>
public sealed partial class EngineCore
{
    private async Task<CommandReply> DeviceVerbAsync(VerbSpec spec, List<string> args, WireLine line, SessionInfo session, CancellationToken ct)
    {
        switch (spec.Name)
        {
            case "CREDENTIAL SET":
                return CredentialSet(args, session);
            case "CREDENTIAL CLEAR":
                return CredentialClear(args, session);
            case "CREDENTIAL LIST":
                return CredentialList();
            case "DEVICE LIST":
                return CommandReply.Success(JsonOut(w =>
                {
                    w.WriteStartArray();
                    foreach (var d in Store.Project.Stage.Devices)
                        WriteDevice(w, d);
                    w.WriteEndArray();
                }));
            case "DEVICE ADD":
                return await _lane.RunAsync(() => EditsInto(session, () => DeviceAdd(args), StoppedReply)).ConfigureAwait(false);
            case "DEVICE REMOVE":
                return await _lane.RunAsync(() => EditsInto(session, () => DeviceRemove(args), StoppedReply)).ConfigureAwait(false);
            case "DEVICE DISCOVER":
                return await DeviceDiscoverAsync(args, ct).ConfigureAwait(false);
            default:
                break;
        }

        if (args.Count < 1)
            return CommandReply.Fail(spec.Syntax);
        var link = ResolveDevice(args[0]);
        if (link is null)
            return CommandReply.Fail($"no projector link {args[0]} — DEVICE LIST names them");
        if (spec.Name == "DEVICE GET")
            return CommandReply.Success(DeviceJson(link));
        if (Services.Devices is not { } devices)
            return CommandReply.Fail("projector control is not on this node");
        var (_, problem) = DeviceService.ToProjectorLink(link);
        if (problem.Length > 0)
            return CommandReply.Fail($"{link.Name}: {problem}");
        var rest = args.Skip(1).ToList();
        return spec.Name switch
        {
            "DEVICE POWER" => TryOnOff(rest.FirstOrDefault() ?? "", out var on)
                ? await Command(devices, link, on ? "power on" : "power to standby", (d, c) => d.SetPowerAsync(on, c), ct).ConfigureAwait(false)
                : CommandReply.Fail(spec.Syntax),
            "DEVICE SHUTTER" => rest.FirstOrDefault()?.ToUpperInvariant() is "OPEN" or "CLOSE" or "CLOSED"
                ? await Command(devices, link, rest[0].Equals("OPEN", StringComparison.OrdinalIgnoreCase) ? "shutter open" : "shutter closed",
                    (d, c) => d.SetShutterAsync(!rest[0].Equals("OPEN", StringComparison.OrdinalIgnoreCase), c), ct).ConfigureAwait(false)
                : CommandReply.Fail(spec.Syntax),
            "DEVICE INPUT" => rest.Count == 1
                ? await Command(devices, link, "input " + rest[0], (d, c) => d.SelectInputAsync(rest[0], c), ct).ConfigureAwait(false)
                : CommandReply.Fail(spec.Syntax),
            "DEVICE FREEZE" => TryOnOff(rest.FirstOrDefault() ?? "", out var frozen)
                ? await Command(devices, link, frozen ? "freeze on" : "freeze off", (d, c) => d.SetFreezeAsync(frozen, c), ct).ConfigureAwait(false)
                : CommandReply.Fail(spec.Syntax),
            "DEVICE PATTERN" => rest.Count == 1
                ? await Command(devices, link, IsOff(rest[0]) ? "patterns off" : "pattern " + rest[0],
                    (d, c) => d.ShowTestPatternAsync(IsOff(rest[0]) ? TestPatterns.Off : rest[0].ToLowerInvariant(), c), ct).ConfigureAwait(false)
                : CommandReply.Fail(spec.Syntax),
            "DEVICE LENS" => await LensAsync(devices, link, rest, spec, ct).ConfigureAwait(false),
            "DEVICE RAW" => line.RestAfter(3) is { Length: > 0 } raw
                ? await Command(devices, link, "raw line", (d, c) => d.RawAsync(raw, c), ct).ConfigureAwait(false)
                : CommandReply.Fail(spec.Syntax),
            "DEVICE PREPARE" => FromWorkflow(link, await devices.PrepareForScanAsync(link.Id, ct).ConfigureAwait(false)),
            "DEVICE IDENTIFY" => FromWorkflow(link, await devices.IdentifyAsync(link.Id, TimeSpan.FromSeconds(IdentifySeconds(rest)), ct).ConfigureAwait(false)),
            _ => CommandReply.Fail($"{spec.Name} is not available on this node yet"),
        };
    }

    private static double IdentifySeconds(List<string> rest) =>
        rest.Count > 0 && double.TryParse(rest[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? Math.Clamp(s, 1, 60) : 5;

    /// <summary>A link by id or name in <paramref name="project"/> (the live project when none is given: any thread).</summary>
    private DeviceLink? ResolveDevice(string key, Project? project = null)
    {
        var devices = (project ?? Store.Project).Stage.Devices;
        return devices.Find(key) ?? devices.FirstOrDefault(d => d.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<CommandReply> Command(DeviceService devices, DeviceLink link, string words,
        Func<IProjectorDriver, CancellationToken, Task<CommandResult>> command, CancellationToken ct)
    {
        var result = await devices.RunAsync(link.Id, words, command, ct).ConfigureAwait(false);
        return FromDevice(link, words, result);
    }

    /// <summary>A projector's answer as a receipt.</summary>
    private static CommandReply FromDevice(DeviceLink link, string words, CommandResult r)
    {
        var said = $"{link.Name}: {words}";
        switch (r.Stage)
        {
            case CommandStage.Refused:
                return CommandReply.Fail($"{said} — not sent: {r.Words}");
            case CommandStage.Unreached:
                return CommandReply.Fail($"{said} — the projector was not reached: {r.Words}");
            case CommandStage.Rejected:
                return CommandReply.Fail($"{said} — the projector said no: {r.Words}");
            default:
                var payload = JsonOut(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("device", link.Id);
                    w.WriteString("command", words);
                    w.WriteString("stage", CommandResult.StageWord(r.Stage));
                    w.WriteString("words", r.Words);
                    if (r.Reply.Length > 0)
                        w.WriteString("reply", r.Reply);
                    w.WriteEndObject();
                });
                return CommandReply.Success(payload, $"{said} ({CommandResult.StageWord(r.Stage)}: {r.Words})") with { Applied = r.Stage == CommandStage.Observed };
        }
    }

    private static CommandReply FromWorkflow(DeviceLink link, WorkflowOutcome outcome)
    {
        var payload = JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("device", link.Id);
            w.WriteString("workflow", outcome.Plan.Name);
            w.WriteBoolean("allSucceeded", outcome.AllSucceeded);
            w.WriteStartArray("steps");
            foreach (var s in outcome.Steps)
            {
                w.WriteStartObject();
                w.WriteString("step", s.Step.Words);
                w.WriteString("kind", s.Step.Kind.ToString().ToLowerInvariant());
                w.WriteString("stage", s.Step.Kind == StepKind.Instruction ? "by hand" : CommandResult.StageWord(s.Result.Stage));
                if (s.Step.Kind != StepKind.Instruction)
                    w.WriteString("words", s.Result.Words);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (outcome.Lens is { } lens)
                w.WriteString("lens", lens.Words());
            w.WriteEndObject();
        });
        return CommandReply.Success(payload, $"{link.Name}: {outcome.Plan.Name} — {DeviceService.Summary(outcome)}") with { Applied = outcome.AllSucceeded };
    }

    private static async Task<CommandReply> LensAsync(DeviceService devices, DeviceLink link, List<string> rest, VerbSpec spec, CancellationToken ct)
    {
        if (rest.Count < 2)
            return CommandReply.Fail(spec.Syntax);
        var absolute = true;
        var at = 1;
        if (rest.Count >= 3 && rest[1].ToUpperInvariant() is "TO" or "BY")
        {
            absolute = rest[1].Equals("TO", StringComparison.OrdinalIgnoreCase);
            at = 2;
        }
        if (!int.TryParse(rest[at], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return CommandReply.Fail(spec.Syntax);
        LensAxis axis;
        switch (rest[0].ToUpperInvariant())
        {
            case "MEMORY":
                return n < 1
                    ? CommandReply.Fail("a lens memory is numbered from 1, as the projector's menu counts")
                    : await Command(devices, link, "lens memory " + n.ToString(CultureInfo.InvariantCulture), (d, c) => d.RecallLensMemoryAsync(n, c), ct).ConfigureAwait(false);
            case "SHIFTH":
                axis = LensAxis.ShiftHorizontal;
                break;
            case "SHIFTV":
                axis = LensAxis.ShiftVertical;
                break;
            case "ZOOM":
                axis = LensAxis.Zoom;
                break;
            case "FOCUS":
                axis = LensAxis.Focus;
                break;
            default:
                return CommandReply.Fail(spec.Syntax);
        }
        var move = absolute ? LensMove.To(axis, n) : LensMove.By(axis, n);
        var words = string.Create(CultureInfo.InvariantCulture, $"lens {rest[0].ToLowerInvariant()} {(absolute ? "to" : "by")} {n}");
        return await Command(devices, link, words, (d, c) => d.MoveLensAsync(move, c), ct).ConfigureAwait(false);
    }

    /// <summary>DEVICE ADD: a link in the project (undoable), its credential reference its own id, so
    /// <c>CREDENTIAL SET &lt;id&gt; &lt;password&gt;</c> gives it a password. On the lane, reading and writing <see cref="Edits"/>.</summary>
    private CommandReply DeviceAdd(List<string> args)
    {
        var projectorKey = Take(args, "PROJECTOR");
        var outputKey = Take(args, "OUTPUT");
        if (args.Count < 3)
            return CommandReply.Fail("DEVICE ADD <name> <protocol> <host[:port]> [PROJECTOR <projector>] [OUTPUT <output>]");
        var name = args[0];
        if (ProjectorProtocols.Parse(args[1]) is not { } protocol)
            return CommandReply.Fail($"'{args[1]}' is not a protocol PlayMap speaks — pjlink, christie, panasonic, barco-pulse, sony-adcp, epson or nec");
        var (host, port) = HostPort(args[2]);
        if (host.Length == 0 || port < 0)
            return CommandReply.Fail("a host is a name or an address, with :port (1–65535) when the protocol's own is not used");
        var p = Edits.Project;
        var pj = projectorKey is null ? null : p.FindProjector(projectorKey) ?? p.Stage.Projectors.FirstOrDefault(x => x.Name.Equals(projectorKey, StringComparison.OrdinalIgnoreCase));
        if (projectorKey is not null && pj is null)
            return CommandReply.Fail($"no projector {projectorKey}");
        var output = outputKey is null ? null : ResolveOutput(outputKey);
        if (outputKey is not null && output is null)
            return CommandReply.Fail($"no output {outputKey}");
        if (p.Stage.Devices.Any(d => d.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && (d.Port == port || d.Port == 0 || port == 0)
            && ProjectorProtocols.Parse(d.Protocol) == protocol))
            return CommandReply.Fail($"{host} is linked already — DEVICE LIST names it");
        string id = "";
        var r = Edits.Apply($"link {name}", proj =>
        {
            id = proj.NewId(Ids.Device);
            var link = new DeviceLink(id, name, ProjectorProtocols.Word(protocol), host, port, id, pj?.Id ?? "", output?.Id ?? "", 0, false);
            return proj.WithStage(s => s with { Devices = s.Devices.Add(link) });
        });
        return r.Changed
            ? CommandReply.Success(id, $"linked {name} ({ProjectorProtocols.Label(protocol)} at {args[2]})")
            : FromEdit(r);
    }

    /// <summary>ADD device &lt;json&gt;: the generic edit's link, its credential reference its own id unless one is given.</summary>
    private (Project, string) AddDevice(Project p, string json)
    {
        var (next, id) = AddTop<DeviceLink>(p, json, Ids.Device, (pp, e) => pp.WithStage(s => s with { Devices = s.Devices.Add(e) }));
        if (next.FindDevice(id) is { CredentialRef.Length: 0 })
            next = next.WithStage(s => s with { Devices = s.Devices.Update(id, d => d with { CredentialRef = id }) });
        return (next, id);
    }

    /// <summary>A link must name a protocol PlayMap speaks, a host, a sane port and poll, and only things the project
    /// has; its credential reference is a reference, never a secret.</summary>
    private static void ValidateDevice(Project p, DeviceLink d)
    {
        if (ProjectorProtocols.Parse(d.Protocol) is null)
            throw new EditRefusedException($"'{d.Protocol}' is not a protocol PlayMap speaks — pjlink, christie, panasonic, barco-pulse, sony-adcp, epson or nec");
        if (string.IsNullOrWhiteSpace(d.Host))
            throw new EditRefusedException("a link needs the projector's host");
        if (d.Port is < 0 or > 65535)
            throw new EditRefusedException("a port is 1 to 65535 (0 = the protocol's own)");
        if (d.PollIntervalMs < 0)
            throw new EditRefusedException("the poll interval is milliseconds (0 = every 10 s)");
        if (d.CredentialRef.Length > 0 && !NodeCredentialStore.IsReference(d.CredentialRef))
            throw new EditRefusedException("a credential reference is 1–64 letters, digits, '_', '-' or '.' — the secret itself goes in with CREDENTIAL SET");
        if (d.ProjectorId.Length > 0 && p.FindProjector(d.ProjectorId) is null)
            throw new EditRefusedException($"no projector {d.ProjectorId}");
        if (d.OutputId.Length > 0 && p.FindOutput(d.OutputId) is null)
            throw new EditRefusedException($"no output {d.OutputId}");
    }

    /// <summary>DEVICE REMOVE, on the lane, reading and writing <see cref="Edits"/>.</summary>
    private CommandReply DeviceRemove(List<string> args)
    {
        if (args.Count < 1)
            return CommandReply.Fail("DEVICE REMOVE <device>");
        var link = ResolveDevice(args[0], Edits.Project);
        if (link is null)
            return CommandReply.Fail($"no projector link {args[0]}");
        return FromEdit(Edits.Apply($"unlink {link.Name}", p => p.WithStage(s => s with { Devices = s.Devices.RemoveId(link.Id) })));
    }

    private async Task<CommandReply> DeviceDiscoverAsync(List<string> args, CancellationToken ct)
    {
        if (Services.Devices is not { } devices)
            return CommandReply.Fail("projector control is not on this node");
        IPAddress? around = null;
        var sweep = Take(args, "SWEEP");
        if (sweep is not null && !IPAddress.TryParse(sweep, out around))
            return CommandReply.Fail("SWEEP takes an address of the subnet to knock on (its /24)");
        var found = await devices.DiscoverAsync(around, ct).ConfigureAwait(false);
        var payload = JsonOut(w =>
        {
            w.WriteStartArray();
            foreach (var d in found)
            {
                w.WriteStartObject();
                w.WriteString("host", d.Host);
                w.WriteString("mac", d.Mac);
                w.WriteNumber("pjlinkClass", d.PjLinkClass);
                w.WriteString("manufacturer", d.Manufacturer ?? "");
                w.WriteString("model", d.Model ?? "");
                w.WriteString("name", d.Name ?? "");
                w.WriteString("serial", d.Serial ?? "");
                w.WriteString("protocol", ProjectorProtocols.Word(d.Suggested));
                w.WriteString("why", d.Why);
                if (d.NativePortOpen is { } open)
                    w.WriteBoolean("nativePortOpen", open);
                w.WriteString("problem", d.Problem);
                w.WriteString("linked", Store.Project.Stage.Devices.FirstOrDefault(x => x.Host == d.Host)?.Id ?? "");
                w.WriteString("add", $"DEVICE ADD \"{(d.Name is { Length: > 0 } n ? n : d.Model ?? d.Host)}\" {ProjectorProtocols.Word(d.Suggested)} {d.Host}");
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
        return CommandReply.Success(payload, $"found {found.Count} projector{(found.Count == 1 ? "" : "s")}");
    }

    /// <summary>"KEY value" anywhere in the words: the value, both removed from the list.</summary>
    private static string? Take(List<string> args, string key)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (!args[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = args[i + 1];
            args.RemoveRange(i, 2);
            return value;
        }
        return null;
    }

    /// <summary>"10.0.0.7:4352" → host and port; a bare host (or an IPv6 address) → port 0, the protocol's own.</summary>
    private static (string Host, int Port) HostPort(string text)
    {
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon)
        {
            return int.TryParse(text.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
                ? (text[..colon], port)
                : ("", -1);
        }
        return (text.Trim('[', ']'), 0);
    }

    /// <summary>One link as STATE and DEVICE LIST show it: the link, then what its projector reports (power, shutter,
    /// input, light-source hours, faults), whether a secret is set for it (never the secret), and the last command.</summary>
    private void WriteDevice(Utf8JsonWriter w, DeviceLink d)
    {
        var devices = Services.Devices;
        var s = devices?.State(d.Id);
        w.WriteStartObject();
        w.WriteString("id", d.Id);
        w.WriteString("name", d.Name);
        w.WriteString("protocol", d.Protocol);
        w.WriteString("host", d.Host);
        w.WriteNumber("port", d.Port);
        w.WriteString("projector", d.ProjectorId);
        w.WriteString("output", d.OutputId);
        w.WriteString("light", Core.Bus.ShowContext.LightWord(s?.Light ?? Core.Bus.Light.Grey));
        w.WriteString("headline", Redact(devices is null ? "projector control is not on this node" : s?.Headline() ?? "not polled yet"));
        w.WriteString("liveness", (s?.Liveness ?? DeviceLiveness.Unknown).ToString().ToLowerInvariant());
        w.WriteBoolean("online", s?.Online ?? false);
        w.WriteString("power", s is null ? "unknown" : DeviceState.PowerWord(s.Power));
        if (s?.ShutterClosed is { } closed)
            w.WriteString("shutter", closed ? "closed" : "open");
        else
            w.WriteNull("shutter");
        if (s?.Input is { } input)
            w.WriteString("input", input);
        else
            w.WriteNull("input");
        if (s?.Frozen is { } frozen)
            w.WriteBoolean("frozen", frozen);
        else
            w.WriteNull("frozen");
        w.WriteStartArray("lampHours");
        foreach (var h in s?.LightHours ?? [])
            w.WriteNumberValue(h);
        w.WriteEndArray();
        w.WriteStartArray("errors");
        foreach (var f in s?.Faults ?? [])
        {
            w.WriteStartObject();
            w.WriteString("words", f.Words);
            w.WriteString("severity", f.Severity.ToString().ToLowerInvariant());
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteBoolean("faultsRead", s?.FaultsRead ?? false);
        w.WriteString("model", s?.Model ?? "");
        w.WriteString("serial", s?.Serial ?? "");
        w.WriteString("problem", Redact(s?.LastProblem ?? ""));
        w.WriteBoolean("credential", d.CredentialRef.Length > 0 && Credentials?.Find(d.CredentialRef) is not null);
        if (devices?.LastCommand(d.Id) is { } last)
        {
            w.WriteStartObject("last");
            w.WriteString("command", last.Command);
            w.WriteString("stage", CommandResult.StageWord(last.Stage));
            w.WriteString("words", Redact(last.Words));
            w.WriteNumber("at", last.AtUnixMs);
            w.WriteEndObject();
        }
        else
            w.WriteNull("last");
        w.WriteEndObject();
    }

    /// <summary>DEVICE GET: the link and its facts, what its protocol can do (and what is gated as unverified on this
    /// link), and the plan PREPARE would run.</summary>
    private string DeviceJson(DeviceLink d) => JsonOut(w =>
    {
        w.WriteStartObject();
        w.WritePropertyName("link");
        JsonSerializer.Serialize(w, d, Core.Model.ProjectJson.Compact);
        w.WritePropertyName("state");
        WriteDevice(w, d);
        var driver = Services.Devices?.Manager.Driver(d.Id);
        if (driver is not null)
        {
            w.WriteString("protocol", ProjectorProtocols.Label(driver.Protocol));
            w.WriteString("provenance", driver.Provenance.ToString().ToLowerInvariant());
            w.WriteString("capabilities", CapabilityWords.List(driver.Capabilities));
            w.WriteString("unverified", CapabilityWords.List(driver.Unverified));
            w.WriteStartArray("patterns");
            foreach (var p in driver.TestPatternNames)
                w.WriteStringValue(p);
            w.WriteEndArray();
            w.WriteStartArray("prepare");
            foreach (var step in Services.Devices?.PreparePlan(d.Id)?.Describe() ?? [])
                w.WriteStringValue(step);
            w.WriteEndArray();
        }
        w.WriteEndObject();
    });

    /// <summary>Who may hand the node a secret: a person on this machine, paired — never an assistant, never from
    /// across the network (the line carries the secret in clear, as AUTH does).</summary>
    private static string? SecretRefusal(SessionInfo session) =>
        IsAssistantTransport(session) ? "an assistant never handles a secret — the operator sets it at the node"
        : !session.Paired ? "not paired — this node asks for its pairing token first"
        : !session.IsLoopback && session.Transport != "desk" ? "a secret is set from this machine only (it rides in the line in clear)"
        : null;

    private CommandReply CredentialSet(List<string> args, SessionInfo session)
    {
        if (SecretRefusal(session) is { } why)
            return CommandReply.Fail(why);
        if (args.Count is < 2 or > 3 || !NodeCredentialStore.IsReference(args[0]))
            return CommandReply.Fail("CREDENTIAL SET <ref> [<user>] <secret> — a reference is a link's id (dv…) or \"assistant\"");
        var reference = args[0];
        var credential = new Credential(args.Count == 3 ? args[1] : "", args[^1]);
        switch (Credentials)
        {
            case NodeCredentialStore store:
                store.Set(reference, credential);
                break;
            case InMemoryCredentialStore memory:
                memory.Set(reference, credential);
                break;
            default:
                return CommandReply.Fail("this node has no credential store");
        }
        Journal.Add("credential", $"a secret was set for {reference}", session.ClientName, null, true);
        FactsChanged();
        return CommandReply.Success(effect: $"a secret is kept for {reference}");
    }

    private CommandReply CredentialClear(List<string> args, SessionInfo session)
    {
        if (SecretRefusal(session) is { } why)
            return CommandReply.Fail(why);
        if (args.Count != 1)
            return CommandReply.Fail("CREDENTIAL CLEAR <ref>");
        if (Credentials is not NodeCredentialStore store)
            return CommandReply.Fail("this node's credential store cannot forget a secret");
        if (!store.Remove(args[0]))
            return CommandReply.Fail($"no secret is kept for {args[0]}");
        Journal.Add("credential", $"the secret for {args[0]} was forgotten", session.ClientName, null, true);
        FactsChanged();
        return CommandReply.Success(effect: $"the secret for {args[0]} is forgotten");
    }

    private CommandReply CredentialList()
    {
        var store = Credentials as NodeCredentialStore;
        var refs = store?.References ?? [];
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("references");
            foreach (var r in refs)
                w.WriteStringValue(r);
            w.WriteEndArray();
            w.WriteString("problem", store?.Problem ?? (Credentials is null ? "this node has no credential store" : ""));
            w.WriteEndObject();
        }));
    }
}
