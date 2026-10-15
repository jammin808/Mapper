using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Bus;
using PlayMap.Core.Control;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Control;
using ProjectFile = PlayMap.Core.Model.ProjectJson;

namespace PlayMap.Engine;

/// <summary>wp2-control's hooks into the engine (EngineCore.Modules.cs): the CONTROL, BIND and NUDGE verbs (and the TIMECODE verbs,
/// <see cref="RouteTimecode"/>), STATE.control and STATE.timecode, the context's control, timecode and chase items, the checks of
/// a binding, surface or source an edit adds, and the recovery facts of the chases. Every mutating verb refuses a change that
/// changes nothing, with the reason; a binding's line runs through the router and the show's gate like any other line.</summary>
public sealed partial class EngineCore
{
    private ControlRuntime? _controlInput;

    /// <summary>The bindings at run time (always present; the node's sockets and ports feed it when its control service runs).</summary>
    public ControlRuntime ControlInput => _controlInput ??= new ControlRuntime(this, Services.Settings.Control);

    partial void RouteControl(VerbRoutes routes)
    {
        routes.Lane("CONTROL STATUS", _ => CommandReply.Success(JsonOut(WriteControlState)));
        routes.OffLane("CONTROL PORTS", c => Task.FromResult(ControlPorts()));
        routes.OffLane("CONTROL MONITOR", c => Task.FromResult(ControlMonitor(c.Args)));
        routes.Lane("CONTROL PROFILES", _ => CommandReply.Success("""[{"id":"generic","name":"Generic","verified":true,"words":"any controller: values in; over OSC, values and lamps back"}]"""));
        routes.Lane("CONTROL ENABLE", c => ControlEnable(c.Args));
        routes.Lane("CONTROL SURFACE", c => ControlSurface(c.Args));
        routes.OffLane("CONTROL ALIAS", c => Task.FromResult(ControlAlias(c.Args)));
        routes.OffLane("CONTROL FEEDBACK", c => Task.FromResult(ControlFeedbackClients(c.Args)));
        routes.OffLane("CONTROL DMX", c => Task.FromResult(ControlDmx(c.Args)));
        routes.OffLane("CONTROL INJECT", ControlInjectAsync);
        routes.Lane("BIND LIST", c => BindList(c.Args));
        routes.Lane("BIND ADD", c => BindAdd(c.Args, c.Line));
        routes.OffLane("BIND LEARN", c => BindLearnAsync(c.Args, c.Line));
        routes.Lane("BIND FORGET", c => BindForget(c.Args, c.Line));
        routes.Lane("BIND ENABLE", c => BindEnable(c.Args));
        routes.Lane("NUDGE", c => Nudge(c.Args));
        RouteTimecode(routes);
    }

    partial void TickControl(long leaderNs) => TickTimecode(leaderNs);

    partial void StateControl(Utf8JsonWriter w)
    {
        w.WritePropertyName("control");
        WriteControlState(w);
        w.WritePropertyName("timecode");
        WriteTimecodeState(w);
    }

    partial void PlaylistControl(Utf8JsonWriter w, Playlist list) => WritePlaylistChase(w, list);

    partial void ContextControl(List<ContextItem> items, List<ContextLink> links)
    {
        var p = Store.Project;
        var input = ControlInput;
        var service = Services.Control;
        var counts = p.Control.Bindings.Length;
        if (!input.Settings.InputEnabled && counts > 0)
            items.Add(new ContextItem("control:off", "control", "Control input", Light.Amber, $"control input is off — {Count(counts, "binding")} paused"));
        if (input.Learning is { } learning)
        {
            var left = Math.Max(0, (learning.UntilUnixMs - Services.Wall.UnixMs) / 1000);
            items.Add(new ContextItem("control:learning", "control", "Learning", Light.Amber,
                string.Create(CultureInfo.InvariantCulture, $"waiting for a control for {learning.Words} ({left} s)")));
        }
        foreach (var s in p.Control.Surfaces)
        {
            var bound = p.Control.Bindings.Where(b => SurfaceOf(b) is { } name && name.Equals(s.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            var connected = service?.Connected(s) ?? false;
            var refused = service?.RefusedFor(s);
            var (light, words) = bound.Count == 0 ? (Light.Grey, "no bindings")
                : refused is not null ? (Light.Red, refused)
                : !s.Enabled ? (Light.Grey, "off")
                : connected ? (Light.Green, $"connected, {Count(bound.Count, "binding")}")
                : (Light.Amber, $"bound to {Count(bound.Count, "control")}, not connected");
            items.Add(new ContextItem("control:" + s.Id, "control", s.Name, light, words));
            foreach (var target in bound.SelectMany(b => TargetIds(p, b.Target)).Distinct(StringComparer.Ordinal))
                links.Add(new ContextLink("control:" + s.Id, target, ContextLinkKinds.Controls, light));
        }
        ContextTimecode(items, links);
    }

    partial void ValidateControl(Project p, object entity, ref string? why)
    {
        switch (entity)
        {
            case ControlBinding b:
                // A binding being made may have no input or target yet (it fires nothing until it has both); what it has must read.
                if (b.Input.Length > 0 && InputAddress.Parse(b.Input, out var inWhy) is null)
                    why = "the binding's input: " + inWhy;
                else if (b.Target.Length > 0 && TargetLine.Parse(b.Target, out var tWhy) is null)
                    why = "the binding's target: " + tWhy;
                else if (b.Input.Length > 0 && InputAddress.Parse(b.Input, out _) is { Surface.Length: > 0 } named
                    && !p.Control.Surfaces.Any(s => s.Name.Equals(named.Surface, StringComparison.OrdinalIgnoreCase)))
                    why = $"no control surface {named.Surface} — CONTROL SURFACE ADD {named.Surface} {SourceWord(named.Source)} names it";
                else if (b.Feedback is { To.Length: > 0 } fb && FeedbackMath.ParseTo(fb.To, out _, out _, out var fbWhy) == false)
                    why = "the binding's feedback: " + fbWhy;
                break;
            case ControlSurface s:
                if (!InputAddress.IsSurfaceName(s.Name))
                    why = "a surface's name is 1–32 letters, digits, '-' or '_'";
                else if (s.Kind is not ("midi" or "osc" or "artnet" or "sacn"))
                    why = "a surface is midi, osc, artnet or sacn";
                else if (p.Control.Surfaces.Any(x => x.Id != s.Id && x.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase)))
                    why = $"there is already a surface called {s.Name}";
                else if (s.Kind == "osc" && s.FeedbackTo.Length > 0 && !FeedbackMath.TryEndpoint(s.FeedbackTo, out _))
                    why = $"an OSC surface's feedbackTo is a host:port (an IP address and a port 1–65535), not '{s.FeedbackTo}'";
                break;
            case TimecodeSource t:
                why = TimecodeSourceProblem(p, t);
                break;
        }
    }

    partial void RecoverySaveControl(IDictionary<string, string> into) => RecoverySaveTimecode(into);

    partial void RecoveryLoadControl(IReadOnlyDictionary<string, string> from) => RecoveryLoadTimecode(from);

    private static string Count(int n, string what) => n.ToString(CultureInfo.InvariantCulture) + " " + what + (n == 1 ? "" : "s");

    private static string SourceWord(InputSource s) => s switch { InputSource.Midi => "MIDI", InputSource.Osc => "OSC", InputSource.ArtNet => "ARTNET", _ => "SACN" };

    private static string? SurfaceOf(ControlBinding b) => InputAddress.Parse(b.Input, out _) is { Surface.Length: > 0 } a ? a.Surface : null;

    /// <summary>The context ids a target line acts on (surfaces, playlists, outputs, devices it names).</summary>
    private static IEnumerable<string> TargetIds(Project p, string target)
    {
        foreach (var w in target.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (p.FindSurface(w) is not null) yield return "surface:" + w;
            else if (p.FindPlaylist(w) is not null) yield return "playlist:" + w;
            else if (p.FindOutput(w) is not null) yield return "output:" + w;
        }
    }

    // ---- lines a binding fires ----

    /// <summary>Runs a line a binding fired, through the router and the show's gate like any line. A lane verb runs on the lane
    /// directly; a verb answered off the lane goes through <see cref="HandleAsync"/>. A line of a gesture is not journalled here
    /// (its gesture is, once); an edge is, with the binding as who.</summary>
    internal async Task<CommandReply> RunBindingLineAsync(string text, SessionInfo session, bool journalLine, CancellationToken ct)
    {
        if (!WireGrammar.TryParse(text, out var line, out var error))
            return CommandReply.Fail("the binding's line does not read: " + error?.Reason);
        if (Stopping)
            return RefusedStopping(line, session, journalLine);
        if (VerbCatalog.Match(line) is { } m && (_routes.TryOffLane(m.Spec.Name, out _) || IsOpsVerb(m.Spec)))
            return await HandleAsync(line, session, ct).ConfigureAwait(false);
        CommandReply reply;
        try
        {
            reply = await _lane.RunAsync(() => Route(line, session, ct)).ConfigureAwait(false);
        }
        catch (EditRefusedException refused)
        {
            reply = CommandReply.Fail(refused.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Journal.Add("fault", $"{line.Verb}: {Journal.Describe(ex)}", session.ClientName, VerbCatalog.Redacted(line), false);
            return CommandReply.Fail($"the node faulted on this line ({ex.GetType().Name}) — logged");
        }
        if (journalLine && !reply.Silent && IsJournalled(line))
            Journal.Add(reply.Ok ? "verb" : "refused", reply.Ok ? (reply.Effect.Length > 0 ? reply.Effect : VerbCatalog.Redacted(line)) : reply.Payload ?? "refused",
                session.ClientName, VerbCatalog.Redacted(line), reply.Ok);
        return reply;
    }

    /// <summary>A verb the operations edge answers (TryOps: devices, the assistant, scanning a surface), not the lane.</summary>
    private static bool IsOpsVerb(VerbSpec spec) => OpsVerbs.Contains(spec.Name);

    /// <summary>A number of an entity by its field path (takeover, encoders and NUDGE read it), or null.</summary>
    internal double? ReadNumber(string id, string path) => EntityOf(id) is { } entity ? NumberAt(entity, path) : null;

    /// <summary>The entity an id names in the current show, or null.</summary>
    internal object? EntityOf(string id) => Locate(Store.Project, id)?.Entity;

    /// <summary>The number at a field path of an entity (case-insensitive names), or null.</summary>
    internal static double? NumberAt(object entity, string path)
    {
        var node = JsonSerializer.SerializeToNode(entity, entity.GetType(), ProjectFile.Compact);
        foreach (var part in path.Split('.'))
        {
            if (node is not JsonObject obj)
                return null;
            node = obj.FirstOrDefault(kv => kv.Key.Equals(part, StringComparison.OrdinalIgnoreCase)).Value;
        }
        return node is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
    }

    /// <summary>Whether a switch target is on now, from published state (a toggle chooses ON or OFF from it); null when unknown.</summary>
    internal bool? FactOf(TargetLine target)
    {
        var p = Store.Project;
        return target.Spec.Name switch
        {
            "BLACKOUT" => _air.Blackout,
            "FREEZE" => _air.Frozen,
            "SURFACE SHOW" => p.FindSurface(target.Line.Word(target.VerbWords))?.Visible,
            "OUTPUT ENABLE" => p.FindOutput(target.Line.Word(target.VerbWords))?.Enabled,
            _ => null,
        };
    }

    /// <summary>A binding in words: "MIDI APC40 CC 1 48 → Lobby opacity".</summary>
    internal string BindingWords(Project p, ControlBinding b) => $"{b.Input} → {TargetWords(p, b.Target)}";

    /// <summary>A target line in words: SET's entity by its name and field; any other line with its ids named.</summary>
    private static string TargetWords(Project p, string target)
    {
        if (TargetLine.Parse(target, out _) is not { } t)
            return target;
        if (t.Field is { } f && t.Spec.Name is "SET" or "NUDGE")
            return $"{WordName(p, f.Id)} {f.Path}";
        return string.Join(' ', t.Line.Words.Select(w => WordName(p, w)));
    }

    private static string WordName(Project p, string word) =>
        Locate(p, word) is { } located ? Label(located.Entity) is { Length: > 0 } name ? name : word : word;

    // ---- CONTROL ----

    private CommandReply ControlPorts()
    {
        if (Services.Control is not { } service)
            return CommandReply.Success("""{"backend":"none","words":"this node runs no control input (no MIDI, OSC or DMX sockets)","inputs":[],"outputs":[]}""");
        return CommandReply.Success(JsonOut(service.WritePorts));
    }

    private CommandReply ControlMonitor(List<string> args)
    {
        var n = 16;
        if (args.Count > 0 && (!int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out n) || n is < 1 or > ControlRuntime.MonitorCapacity))
            return CommandReply.Fail("CONTROL MONITOR [<n 1-64>]");
        var rows = ControlInput.Monitor(n);
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartArray();
            foreach (var r in rows)
            {
                w.WriteStartObject();
                w.WriteNumber("at", r.AtUnixMs);
                w.WriteString("input", r.Input);
                w.WriteString("raw", r.Raw);
                w.WriteNumber("value", Math.Round(r.Value, 4));
                if (r.Binding is null)
                    w.WriteNull("binding");
                else
                    w.WriteString("binding", r.Binding);
                w.WriteString("source", r.Origin);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }));
    }

    private CommandReply ControlEnable(List<string> args)
    {
        if (args.Count != 1 || args[0].ToUpperInvariant() is not ("ON" or "OFF" or "TOGGLE"))
            return CommandReply.Fail("CONTROL ENABLE ON|OFF|TOGGLE");
        var current = ControlInput.Settings.InputEnabled;
        var on = args[0].ToUpperInvariant() switch { "ON" => true, "OFF" => false, _ => !current };
        if (on == current)
            return CommandReply.Fail(on ? "control input is already on" : "control input is already off");
        if (ControlInput.ChangeSettings(s => s with { InputEnabled = on }) is { } why)
            return CommandReply.Fail("the setting could not be kept: " + why);
        var n = Store.Project.Control.Bindings.Count(b => b.Enabled);
        return CommandReply.Success(effect: on ? $"control input on — {Count(n, "binding")} live" : $"control input off — {Count(n, "binding")} paused");
    }

    private CommandReply ControlSurface(List<string> args)
    {
        if (args.Count >= 2 && args[0].Equals("REMOVE", StringComparison.OrdinalIgnoreCase))
        {
            var p = Edits.Project;
            var s = p.FindControlSurface(args[1]) ?? p.Control.Surfaces.FirstOrDefault(x => x.Name.Equals(args[1], StringComparison.OrdinalIgnoreCase));
            if (s is null)
                return CommandReply.Fail($"no control surface {args[1]}");
            var bound = p.Control.Bindings.Count(b => string.Equals(SurfaceOf(b), s.Name, StringComparison.OrdinalIgnoreCase));
            if (bound > 0)
                return CommandReply.Fail($"{s.Name} has {Count(bound, "binding")} — BIND FORGET them first (BIND LIST {s.Name} lists them)");
            return FromEdit(Edits.Apply($"remove control surface {s.Name}", pp => pp with { Control = pp.Control with { Surfaces = pp.Control.Surfaces.RemoveId(s.Id) } }));
        }
        if (args.Count < 3 || !args[0].Equals("ADD", StringComparison.OrdinalIgnoreCase))
            return CommandReply.Fail("CONTROL SURFACE ADD <name> MIDI|OSC|ARTNET|SACN [<match>] [PROFILE <id>] | CONTROL SURFACE REMOVE <surface>");
        var name = args[1];
        var kind = args[2].ToLowerInvariant();
        if (kind is not ("midi" or "osc" or "artnet" or "sacn"))
            return CommandReply.Fail("a surface is MIDI, OSC, ARTNET or SACN");
        var rest = args.Skip(3).ToList();
        var profile = "";
        var at = rest.FindIndex(w => w.Equals("PROFILE", StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
        {
            if (at + 1 >= rest.Count)
                return CommandReply.Fail("PROFILE <id>");
            profile = rest[at + 1];
            if (profile != "generic")
                return CommandReply.Fail($"no controller profile {profile} — CONTROL PROFILES lists them");
            rest.RemoveRange(at, 2);
            profile = "";
        }
        if (rest.Count > 1)
            return CommandReply.Fail("one match word: a port-name glob, a sender address, or a DMX source address");
        var match = rest.Count == 1 ? rest[0] : "";
        var id = "";
        var r = Edits.Apply($"add control surface {name}", p =>
        {
            id = p.NewId(Ids.ControlSurface);
            var surface = new ControlSurface(id, name, kind, match, profile, "", true);
            Validate(p, surface);
            return p with { Control = p.Control with { Surfaces = p.Control.Surfaces.Add(surface) } };
        });
        if (!r.Changed)
            return CommandReply.Fail(r.Effect);
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("id", id);
            w.WriteEndObject();
        }), $"control surface {name} ({kind}) added");
    }

    private CommandReply ControlAlias(List<string> args)
    {
        if (args.Count != 2)
            return CommandReply.Fail("CONTROL ALIAS <surface> <port glob>|OFF");
        var p = Store.Project;
        var s = p.FindControlSurface(args[0]) ?? p.Control.Surfaces.FirstOrDefault(x => x.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));
        if (s is null)
            return CommandReply.Fail($"no control surface {args[0]}");
        var current = ControlInput.Settings.SurfacePorts.GetValueOrDefault(s.Name);
        var off = IsOff(args[1]);
        if (off ? current is null : string.Equals(current, args[1], StringComparison.Ordinal))
            return CommandReply.Fail(off ? $"{s.Name} has no port of its own on this machine" : $"{s.Name} is already '{args[1]}' on this machine");
        if (ControlInput.ChangeSettings(c => c with { SurfacePorts = off ? c.SurfacePorts.Remove(s.Name) : c.SurfacePorts.SetItem(s.Name, args[1]) }) is { } why)
            return CommandReply.Fail("the setting could not be kept: " + why);
        return CommandReply.Success(effect: off ? $"{s.Name} finds its port by its own match again" : $"{s.Name} is '{args[1]}' on this machine");
    }

    /// <summary>CONTROL FEEDBACK ADD|REMOVE &lt;host:port&gt;: an OSC client that hears every binding's feedback without having spoken
    /// first (kept on the node).</summary>
    private CommandReply ControlFeedbackClients(List<string> args)
    {
        if (args.Count != 2 || !(args[0].Equals("ADD", StringComparison.OrdinalIgnoreCase) || args[0].Equals("REMOVE", StringComparison.OrdinalIgnoreCase)))
            return CommandReply.Fail("CONTROL FEEDBACK ADD|REMOVE <host:port>");
        if (!FeedbackMath.TryEndpoint(args[1], out var endpoint))
            return CommandReply.Fail($"'{args[1]}' is no host:port (an IP address and a port 1–65535)");
        var to = endpoint!.ToString();
        var add = args[0].Equals("ADD", StringComparison.OrdinalIgnoreCase);
        var list = ControlInput.Settings.OscFeedback;
        var has = list.Contains(to, StringComparer.Ordinal);
        if (add && has)
            return CommandReply.Fail($"{to} already hears the node's feedback");
        if (!add && !has)
            return CommandReply.Fail($"{to} is not a feedback client — CONTROL STATUS lists them (control.osc.feedback)");
        if (add && list.Length >= 16)
            return CommandReply.Fail("16 feedback clients is the most a node keeps — CONTROL FEEDBACK REMOVE one first");
        if (add && Services.Control?.OscLocal is { } local && local.Port == endpoint.Port
            && (local.Address.Equals(endpoint.Address) || System.Net.IPAddress.IsLoopback(endpoint.Address)))
            return CommandReply.Fail($"{to} is this node's own OSC port — its feedback would come straight back");
        if (ControlInput.ChangeSettings(c => c with { OscFeedback = add ? c.OscFeedback.Add(to) : c.OscFeedback.Remove(to) }) is { } why)
            return CommandReply.Fail("the setting could not be kept: " + why);
        var open = Services.Control?.OscOpen == true;
        return CommandReply.Success(effect: add
            ? $"{to} hears the bindings' feedback{(open ? "" : " once the OSC socket is open (" + (Services.Control?.OscProblem ?? "OSC input is off") + ")")}"
            : $"{to} no longer hears the node's feedback");
    }

    private CommandReply ControlDmx(List<string> args)
    {
        const string Syntax = "CONTROL DMX ARTNET|SACN ON|OFF [<interface address>]";
        if (args.Count is < 2 or > 3 || !TryOnOff(args[1], out var on))
            return CommandReply.Fail(Syntax);
        var sacn = args[0].Equals("SACN", StringComparison.OrdinalIgnoreCase);
        if (!sacn && !args[0].Equals("ARTNET", StringComparison.OrdinalIgnoreCase))
            return CommandReply.Fail(Syntax);
        var nic = args.Count == 3 ? args[2] : null;
        if (nic is not null && !System.Net.IPAddress.TryParse(nic, out _))
            return CommandReply.Fail($"{nic} is no interface address");
        var s = ControlInput.Settings;
        var (name, enabled, iface) = sacn ? ("sACN", s.SacnEnabled, s.SacnInterface) : ("Art-Net", s.ArtNetEnabled, s.ArtNetInterface);
        if (enabled == on && (nic is null || string.Equals(nic, iface, StringComparison.Ordinal)))
            return CommandReply.Fail($"{name} control input is already {(on ? "on" : "off")}");
        if (ControlInput.ChangeSettings(c => sacn ? c with { SacnEnabled = on, SacnInterface = nic ?? c.SacnInterface }
                : c with { ArtNetEnabled = on, ArtNetInterface = nic ?? c.ArtNetInterface }) is { } why)
            return CommandReply.Fail("the setting could not be kept: " + why);
        if (!on)
            return CommandReply.Success(effect: $"{name} control input off — DMX no longer fires bindings");
        // The Art-Net socket is the node's, shared with LED output: it moves to another interface when the node starts again.
        var moves = !sacn && nic is not null && Services.ArtNet is { } art && !Equals(art.Interface, System.Net.IPAddress.Parse(nic))
            ? $" — the Art-Net socket stays on {art.Interface?.ToString() ?? "all interfaces"} until the node starts again" : "";
        var problem = sacn ? null : Services.ArtNet?.Problem;
        return CommandReply.Success(effect: $"{name} control input on{(nic is null ? "" : " at " + nic)}{moves}{(problem is null ? "" : " — " + problem)} — STATE says what it hears");
    }

    private async Task<CommandReply> ControlInjectAsync(VerbCall c)
    {
        if (c.Session.Transport is "assistant" or "mcp" or "approval")
            return CommandReply.Fail("CONTROL INJECT is a person's test of a control — never from an assistant");
        if (c.Args.Count < 2)
            return CommandReply.Fail("CONTROL INJECT <input words> <value>");
        if (!double.TryParse(c.Args[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || double.IsNaN(value))
            return CommandReply.Fail($"'{c.Args[^1]}' is no value — 0 to 1 for a fader or a button, ticks for an encoder");
        if (InputAddress.Parse(c.Args.Take(c.Args.Count - 1).ToList(), out var why) is not { } input)
            return CommandReply.Fail(why);
        var table = ControlInput.TableOf(Store.Project);
        if (!table.Match(input).Any())
            return CommandReply.Fail($"nothing is bound to {input.Format()} — BIND LIST shows what is");
        var fired = await ControlInput.InjectAsync(input, value, c.Ct).ConfigureAwait(false);
        var payload = JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("fired");
            foreach (var f in fired)
            {
                w.WriteStartObject();
                w.WriteString("binding", f.Binding);
                w.WriteString("line", f.Line);
                w.WriteBoolean("ok", f.Ok);
                w.WriteString("reply", f.Reply);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
        return CommandReply.Success(payload, fired.Count == 0 ? $"{input.Format()} moved; its bindings held (takeover or the same value)"
            : $"{input.Format()} fired {Count(fired.Count, "line")}");
    }

    // ---- BIND ----

    private CommandReply BindList(List<string> args)
    {
        var p = Edits.Project;
        var filter = args.Count > 0 ? args[0] : null;
        var surfaceName = filter is null ? null
            : (p.FindControlSurface(filter) ?? p.Control.Surfaces.FirstOrDefault(s => s.Name.Equals(filter, StringComparison.OrdinalIgnoreCase)))?.Name;
        var bindings = p.Control.Bindings.Where(b => filter is null
            || (surfaceName is not null ? string.Equals(SurfaceOf(b), surfaceName, StringComparison.OrdinalIgnoreCase)
                : b.Id == filter || b.Target.Split(' ').Contains(filter, StringComparer.Ordinal))).ToList();
        if (filter is not null && bindings.Count == 0 && surfaceName is null && Locate(p, filter) is null)
            return CommandReply.Fail($"no surface, binding or entity {filter}");
        var table = ControlInput.TableOf(Store.Project);
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartArray();
            foreach (var b in bindings)
                WriteBinding(w, p, b, table);
            w.WriteEndArray();
        }));
    }

    private void WriteBinding(Utf8JsonWriter w, Project p, ControlBinding b, ControlRuntime.Table table)
    {
        var r = table.All.FirstOrDefault(x => x.Binding.Id == b.Id);
        w.WriteStartObject();
        w.WriteString("id", b.Id);
        w.WriteString("input", b.Input);
        w.WriteString("target", b.Target);
        w.WriteString("words", BindingWords(p, b));
        w.WriteBoolean("enabled", b.Enabled);
        w.WriteString("state", BindingState(b, r, table));
        w.WriteString("behaviour", (r?.Behaviour ?? b.Shape.Behaviour).ToString().ToLowerInvariant());
        if (r?.TakeoverDirection is { } dir)
            w.WriteString("takeover", dir);
        else
            w.WriteNull("takeover");
        if (r is not null && !double.IsNaN(r.LastValue))
            w.WriteNumber("value", Math.Round(r.LastValue, 6));
        else
            w.WriteNull("value");
        if (r is not null)
            w.WriteString("feedback", FeedbackMath.RouteOf(b, r.Input, r.Target, r.Behaviour).Words);
        else
            w.WriteNull("feedback");
        if (table.Problems.TryGetValue(b.Id, out var why))
            w.WriteString("why", why);
        w.WriteEndObject();
    }

    /// <summary>"off" (it or its surface), "live" (its source can be heard now), "waiting" (no source yet) or "unplugged" (its
    /// surface's port is not here).</summary>
    private string BindingState(ControlBinding b, BindingRuntime? r, ControlRuntime.Table table)
    {
        if (r is null)
            return table.Problems.TryGetValue(b.Id, out var why) && why != "off" && !why.EndsWith(" is off", StringComparison.Ordinal) ? "unplugged" : "off";
        if (Services.Control is not { } service)
            return "waiting";
        return service.Hears(r.Input) switch { true => "live", false => "unplugged", _ => "waiting" };
    }

    private CommandReply BindAdd(List<string> args, WireLine line)
    {
        if (args.Count == 0)
            return CommandReply.Fail("BIND ADD <input words> TO <target line> | BIND ADD <json>");
        if (args[0].StartsWith('{'))
            return BindAddJson(line.RestAfter(2));
        var to = args.FindIndex(w => w.Equals("TO", StringComparison.OrdinalIgnoreCase));
        if (to <= 0 || to == args.Count - 1)
            return CommandReply.Fail("BIND ADD <input words> TO <target line>");
        if (InputAddress.Parse(args.Take(to).ToList(), out var why) is not { } input)
            return CommandReply.Fail(why);
        var targetText = line.RestAfter(2 + to + 1);
        if (TargetLine.Parse(targetText, out why) is not { } target)
            return CommandReply.Fail(why);
        var binding = new ControlBinding("", "", input.Format(), target.Text, ControlShape.Default, ControlFeedback.Default, true);
        return AddBindings([binding], $"bind {input.Format()}");
    }

    private CommandReply BindAddJson(string json)
    {
        JsonObject obj;
        try
        {
            obj = ProjectFile.ParseTree(json) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException ex)
        {
            return CommandReply.Fail("BIND ADD <json> takes a binding, a set {\"bindings\":[…]} or a personality: " + ex.Message);
        }
        string? Str(string key) => obj.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        int? Int(string key) => obj.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
        var p = Edits.Project;
        if (Str("personality") is { } personality)
        {
            if (personality != DmxPersonality.Id)
                return CommandReply.Fail($"no DMX personality {personality} — \"{DmxPersonality.Id}\" is built in");
            var source = (Str("source") ?? "artnet").ToLowerInvariant();
            if (source is not ("artnet" or "sacn"))
                return CommandReply.Fail("a personality listens on \"artnet\" or \"sacn\"");
            var sacn = source == "sacn";
            var universe = Int("universe") ?? (sacn ? 1 : 0);
            var start = Int("address") ?? Int("start") ?? 1;
            var surface = Str("surface") ?? "";
            if ((sacn ? universe is < 1 or > 63999 : universe is < 0 or > 32767) || start is < 1 or > 512)
                return CommandReply.Fail(sacn ? "a personality's sACN universe is 1–63999 and its address 1–512" : "a personality's universe is 0–32767 and its address 1–512");
            var n = 0;
            var (bindings, sheet, channels) = DmxPersonality.Build(p, sacn ? InputSource.Sacn : InputSource.ArtNet, universe, start, surface,
                () => "bd_new" + (n++).ToString(CultureInfo.InvariantCulture));
            if (channels == 0)
                return CommandReply.Fail("the show has no surface or output for a personality to drive");
            if (start + channels - 1 > 512)
                return CommandReply.Fail($"the personality needs {channels} channels — from {start} it runs past 512");
            return AddBindings(bindings, $"DMX personality at {universe}/{start}", sheet);
        }
        if (obj.FirstOrDefault(kv => kv.Key.Equals("bindings", StringComparison.OrdinalIgnoreCase)).Value is JsonArray array)
        {
            var list = new List<ControlBinding>();
            foreach (var item in array)
            {
                if (item is not JsonObject one)
                    return CommandReply.Fail("every binding of a set is an object");
                try
                {
                    list.Add(ReadOverDefault<ControlBinding>(p, one.ToJsonString()));
                }
                catch (EditRefusedException ex)
                {
                    return CommandReply.Fail(ex.Message);
                }
            }
            if (list.Count == 0)
                return CommandReply.Fail("the set has no bindings");
            return AddBindings(list, $"bind a set of {list.Count}");
        }
        try
        {
            return AddBindings([ReadOverDefault<ControlBinding>(p, json)], "bind");
        }
        catch (EditRefusedException ex)
        {
            return CommandReply.Fail(ex.Message);
        }
    }

    /// <summary>Adds bindings in one edit (one undo step): each made valid or the whole edit refused; answers their ids, their words
    /// and any binding already on the same input.</summary>
    private CommandReply AddBindings(IReadOnlyList<ControlBinding> bindings, string label, string? sheet = null)
    {
        var ids = new List<string>();
        var also = new List<string>();
        var r = Edits.Apply(label, p =>
        {
            var list = p.Control.Bindings;
            foreach (var b in bindings)
            {
                var id = Ids.NewUnique(Ids.ControlBinding, list);
                var made = b with { Id = id, Input = InputAddress.Parse(b.Input, out _)?.Format() ?? b.Input };
                Validate(p with { Control = p.Control with { Bindings = list } }, made);
                also.AddRange(list.Where(x => x.Input.Equals(made.Input, StringComparison.OrdinalIgnoreCase)).Select(x => x.Id));
                list = list.Add(made);
                ids.Add(id);
            }
            return p with { Control = p.Control with { Bindings = list } };
        });
        if (!r.Changed)
            return CommandReply.Fail(r.Effect);
        var now = Edits.Project;
        var words = ids.Count == 1 && now.Control.Bindings.Find(ids[0]) is { } one ? BindingWords(now, one) : Count(ids.Count, "binding");
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            if (ids.Count == 1)
                w.WriteString("id", ids[0]);
            w.WriteStartArray("bindings");
            foreach (var id in ids)
                w.WriteStringValue(id);
            w.WriteEndArray();
            w.WriteString("words", words);
            w.WriteStartArray("alsoBound");
            foreach (var a in also.Distinct(StringComparer.Ordinal))
                w.WriteStringValue(a);
            w.WriteEndArray();
            if (sheet is not null)
                w.WriteString("sheet", sheet);
            w.WriteEndObject();
        }), also.Count > 0 ? $"bound {words} (also bound to {string.Join(", ", also.Distinct(StringComparer.Ordinal))})" : $"bound {words}");
    }

    private async Task<CommandReply> BindLearnAsync(List<string> args, WireLine line)
    {
        if (args.Count == 1 && IsOff(args[0]))
            return await ControlInput.CancelLearnAsync().ConfigureAwait(false) ? CommandReply.Success(effect: "learn stopped — nothing was bound") : CommandReply.Fail("not learning");
        if (args.Count == 0)
            return CommandReply.Fail("BIND LEARN [FROM <surface>|MIDI|OSC|ARTNET|SACN] <target line> | BIND LEARN OFF");
        var from = "";
        var skip = 2;
        if (args[0].Equals("FROM", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count < 3)
                return CommandReply.Fail("BIND LEARN FROM <surface>|MIDI|OSC|ARTNET|SACN <target line>");
            from = args[1];
            var p = Store.Project;
            if (from.ToUpperInvariant() is not ("MIDI" or "OSC" or "ARTNET" or "SACN") && !p.Control.Surfaces.Any(s => s.Name.Equals(from, StringComparison.OrdinalIgnoreCase)))
                return CommandReply.Fail($"no control surface {from}");
            skip += 2;
        }
        var text = line.RestAfter(skip);
        if (TargetLine.Parse(text, out var why) is not { } target)
            return CommandReply.Fail(why);
        var now = Services.Mono.NowNs;
        var untilUnix = Services.Wall.UnixMs + 30_000;
        var words = TargetWords(Store.Project, target.Text);
        await ControlInput.ArmAsync(new LearnSession(target, from, now + 30_000_000_000L, untilUnix, words)).ConfigureAwait(false);
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("stage", "accepted");
            w.WriteString("effect", $"waiting for a control for {words} (30 s)");
            w.WriteNumber("until", untilUnix);
            w.WriteEndObject();
        }), $"learning: waiting for a control for {words}") with { Applied = false };
    }

    private CommandReply BindForget(List<string> args, WireLine line)
    {
        if (args.Count == 0)
            return CommandReply.Fail("BIND FORGET <target line>|<input words>");
        var p = Edits.Project;
        List<ControlBinding> gone;
        if (InputAddress.Parse(args, out _) is { } input)
        {
            gone = p.Control.Bindings.Where(b => InputAddress.Parse(b.Input, out _) is { } a && (a == input || (input.Surface.Length == 0 && a.AnySurface == input))).ToList();
        }
        else
        {
            var text = line.RestAfter(2);
            if (TargetLine.Parse(text, out var why) is not { } target)
                return CommandReply.Fail("neither input words nor a target line: " + why);
            gone = p.Control.Bindings.Where(b => string.Equals(TargetLine.Parse(b.Target, out _)?.Line.Raw.Trim(), target.Line.Raw.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (gone.Count == 0)
            return CommandReply.Fail("no binding matches — BIND LIST shows them");
        var ids = gone.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var r = Edits.Apply($"forget {Count(gone.Count, "binding")}", pp => pp with
        {
            Control = pp.Control with { Bindings = pp.Control.Bindings.RemoveAll(b => ids.Contains(b.Id)) },
        });
        return r.Changed ? CommandReply.Success(effect: $"{Count(gone.Count, "binding")} forgotten") : CommandReply.Fail(r.Effect);
    }

    private CommandReply BindEnable(List<string> args)
    {
        if (args.Count != 2 || !TryOnOff(args[1], out var on))
            return CommandReply.Fail("BIND ENABLE <binding>|<surface> ON|OFF");
        var p = Edits.Project;
        if (p.Control.Bindings.Find(args[0]) is { } b)
        {
            if (b.Enabled == on)
                return CommandReply.Fail(on ? "the binding is already on" : "the binding is already off");
            var r = FromEdit(Edits.Apply($"binding {b.Id} {(on ? "on" : "off")}", pp => pp with { Control = pp.Control with { Bindings = pp.Control.Bindings.Update(b.Id, x => x with { Enabled = on }) } }));
            ControlInput.RuntimeOf(b.Id)?.Rearm();
            return r;
        }
        var s = p.FindControlSurface(args[0]) ?? p.Control.Surfaces.FirstOrDefault(x => x.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));
        if (s is null)
            return CommandReply.Fail($"no binding or surface {args[0]}");
        if (s.Enabled == on)
            return CommandReply.Fail(on ? $"{s.Name}'s bindings are already on" : $"{s.Name}'s bindings are already off");
        return FromEdit(Edits.Apply($"{s.Name} {(on ? "on" : "off")}", pp => pp with { Control = pp.Control with { Surfaces = pp.Control.Surfaces.Update(s.Id, x => x with { Enabled = on }) } }));
    }

    private CommandReply Nudge(List<string> args)
    {
        if (args.Count != 3 || !double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var delta) || double.IsNaN(delta))
            return CommandReply.Fail("NUDGE <id> <field.path> <±delta>");
        var p = Edits.Project;
        if (Locate(p, args[0]) is not { } located)
            return CommandReply.Fail($"nothing has the id {args[0]}");
        if (ReadNumber(args[0], args[1]) is not { } current)
            return CommandReply.Fail($"{Label(located.Entity)} has no number at {args[1]}");
        var range = TargetRanges.OfField(p, args[0], args[1]);
        var lo = range.Known ? range.Min : double.NegativeInfinity;
        var hi = range.Known ? range.Max : double.PositiveInfinity;
        var next = Math.Clamp(current + delta, lo, hi);
        if (range.Integer)
            next = Math.Round(next, MidpointRounding.AwayFromZero);
        var name = Label(located.Entity);
        if (Math.Abs(next - current) < 1e-12)
            return CommandReply.Fail(delta > 0 ? $"{name} {args[1]} is already at its top ({Fmt(hi)})" : $"{name} {args[1]} is already at its bottom ({Fmt(lo)})");
        var value = next.ToString("R", CultureInfo.InvariantCulture);
        var r = Edits.Apply($"set {located.Kind} {name} {args[1]}", pp =>
        {
            var at = Locate(pp, args[0]) ?? throw new EditRefusedException($"nothing has the id {args[0]}");
            var edited = SetPathTyped(at.Entity, at.Type, args[1], value);
            Validate(pp, edited);
            return at.Replace(pp, edited);
        }, coalesceKey: $"set {args[0]} {args[1]}");
        return r.Changed ? CommandReply.Success(effect: $"{name} {args[1]} {Fmt(current)} → {Fmt(next)}") : CommandReply.Fail(r.Effect);
    }

    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // ---- STATE.control ----

    private void WriteControlState(Utf8JsonWriter w)
    {
        var p = Store.Project;
        var input = ControlInput;
        var settings = input.Settings;
        var service = Services.Control;
        var table = input.TableOf(p);
        w.WriteStartObject();
        w.WriteBoolean("enabled", settings.InputEnabled);
        w.WriteNumber("axisHz", settings.AxisHz);
        if (service is not null)
        {
            service.WriteState(w);
        }
        else
        {
            w.WriteStartObject("osc");
            w.WriteNumber("port", settings.OscPort);
            w.WriteBoolean("open", false);
            w.WriteString("words", "no control service on this node");
            w.WriteEndObject();
            w.WriteStartObject("midi");
            w.WriteString("backend", "none");
            w.WriteBoolean("open", false);
            w.WriteString("words", "no control service on this node");
            w.WriteStartArray("inputs");
            w.WriteEndArray();
            w.WriteStartArray("outputs");
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteStartObject("dmx");
            w.WriteStartObject("artnet");
            w.WriteBoolean("enabled", settings.ArtNetEnabled);
            w.WriteBoolean("open", false);
            w.WriteEndObject();
            w.WriteStartObject("sacn");
            w.WriteBoolean("enabled", settings.SacnEnabled);
            w.WriteBoolean("open", false);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteStartArray("surfaces");
        foreach (var s in p.Control.Surfaces)
        {
            var bound = p.Control.Bindings.Count(b => string.Equals(SurfaceOf(b), s.Name, StringComparison.OrdinalIgnoreCase));
            var connected = service?.Connected(s) ?? false;
            w.WriteStartObject();
            w.WriteString("id", s.Id);
            w.WriteString("name", s.Name);
            w.WriteString("kind", s.Kind);
            w.WriteBoolean("enabled", s.Enabled);
            w.WriteBoolean("connected", connected);
            if (service?.PortOf(s) is { } port)
                w.WriteString("port", port);
            else
                w.WriteNull("port");
            w.WriteString("profile", s.Profile);
            w.WriteNumber("bindings", bound);
            w.WriteString("light", bound == 0 || !s.Enabled ? "grey" : service?.RefusedFor(s) is not null ? "red" : connected ? "green" : "amber");
            w.WriteEndObject();
        }
        w.WriteEndArray();
        var states = p.Control.Bindings.Select(b => BindingState(b, table.All.FirstOrDefault(x => x.Binding.Id == b.Id), table)).ToList();
        w.WriteStartObject("bindings");
        w.WriteNumber("count", p.Control.Bindings.Length);
        w.WriteNumber("enabled", p.Control.Bindings.Count(b => b.Enabled));
        w.WriteNumber("live", states.Count(s => s == "live"));
        w.WriteNumber("waiting", states.Count(s => s == "waiting"));
        w.WriteNumber("unplugged", states.Count(s => s == "unplugged"));
        w.WriteEndObject();
        if (input.Learning is { } l && l.UntilUnixMs > Services.Wall.UnixMs)
        {
            w.WriteStartObject("learning");
            w.WriteString("target", l.Target);
            w.WriteString("words", l.Words);
            w.WriteNumber("until", l.UntilUnixMs);
            w.WriteString("from", l.From.Length == 0 ? "any" : l.From);
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("learning");
        }
        if (input.Learnt is { } learnt)
            w.WriteString("learnt", learnt);
        else
            w.WriteNull("learnt");
        if (input.Last is { } last)
        {
            w.WriteStartObject("last");
            w.WriteString("input", last.Input);
            w.WriteNumber("value", Math.Round(last.Value, 4));
            if (last.Binding is null)
                w.WriteNull("binding");
            else
                w.WriteString("binding", last.Binding);
            w.WriteNumber("at", last.AtUnixMs);
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("last");
        }
        w.WriteEndObject();
    }
}
