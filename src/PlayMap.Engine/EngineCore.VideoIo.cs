using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;
using PlayMap.Engine.VideoIo;
using PlayMap.Media.Video;
using PlayMap.Render.Passes;

namespace PlayMap.Engine;

/// <summary>wp2-videoio's hooks into the engine (EngineCore.Modules.cs): the live inputs and sends verbs, STATE.videoio (the NDI,
/// OMT and Spout runtimes, what discovery sees, every live input's health and tally, every send's sender, receivers and drops, the
/// bandwidth), the context's video items and links, and the check of a send or a live input.</summary>
public sealed partial class EngineCore
{
    private const int DiscoveredRows = 64;
    private VideoIoHub? _videoIo;

    /// <summary>The node's network video (made at first use from the services' provider lists).</summary>
    public VideoIoHub VideoIo
    {
        get
        {
            if (Volatile.Read(ref _videoIo) is { } hub)
                return hub;
            var made = new VideoIoHub(this);
            if (Interlocked.CompareExchange(ref _videoIo, made, null) is { } other)
            {
                made.Dispose();
                return other;
            }
            ProjectChanged += _ => made.Wake();
            return made;
        }
    }

    partial void RouteVideoIo(VerbRoutes routes)
    {
        routes.Lane("INPUT LIST", _ => CommandReply.Success(JsonOut(w => WriteInputs(w, Store.Project))));
        routes.OffLane("INPUT DISCOVER", InputDiscoverAsync);
        routes.Lane("INPUT ADD", InputAdd);
        routes.OffLane("INPUT RECONNECT", InputReconnectAsync);
        routes.Lane("SEND LIST", _ => CommandReply.Success(JsonOut(w => WriteSends(w, Store.Project))));
        routes.Lane("SEND ADD", SendAdd);
        routes.Lane("SEND ENABLE", SendEnable);
        routes.Lane("SEND REMOVE", SendRemove);
        routes.Lane("VIDEOIO STATUS", _ => CommandReply.Success(JsonOut(w => WriteVideoIo(w, Store.Project, lists: false))));
        routes.Lane("VIDEOIO CHECK", _ => CommandReply.Success(JsonOut(w => WriteChecks(w, VideoIoChecks.Rows(this)))));
        routes.Lane("VIDEOIO SET", VideoIoSet);
        routes.Index(new VideoIndex(this));
        routes.Index(new SendsIndex(this));
    }

    // ---- INPUT ----------------------------------------------------------------------------------------------------------------

    private CommandReply InputAdd(VerbCall call)
    {
        var args = call.Args;
        if (args.Count < 1)
            return CommandReply.Fail("INPUT ADD <uri> [<name>] [PROXY] [COLD]");
        var uri = args[0];
        var proxy = false;
        var cold = false;
        var words = new List<string>();
        foreach (var a in args.Skip(1))
        {
            if (a.Equals("PROXY", StringComparison.OrdinalIgnoreCase))
                proxy = true;
            else if (a.Equals("COLD", StringComparison.OrdinalIgnoreCase))
                cold = true;
            else
                words.Add(a);
        }
        if (!LiveUri.TryParse(uri, out var protocol, out var address, out var byUrl))
            return CommandReply.Fail($"\"{uri}\" is no live input: ndi:HOST (Name), ndi://ip:port, omt:HOST (Name), omt://host:port, spout:Name, or an FFmpeg URL (rtsp://…)");
        if (protocol != LiveProtocol.Ffmpeg && VideoIo.Unavailable(protocol, sending: false) is { } why)
            return CommandReply.Fail(why);
        var project = Edits.Project;
        var location = LiveUri.Format(protocol, address);
        foreach (var m in project.Media)
        {
            if (m.Kind == MediaKind.Stream && LiveUri.TryParse(m.Location, out var p2, out var a2, out _) && p2 == protocol
                && string.Equals(a2, address, StringComparison.OrdinalIgnoreCase))
                return CommandReply.Fail($"{location} is already in the library as {m.Id} ({m.Name})");
        }
        var name = words.Count > 0 ? string.Join(' ', words) : DefaultName(protocol, address, byUrl);
        var settings = LiveSettings.Default with { Proxy = proxy, Warm = !cold };
        var id = "";
        var r = Edits.Apply($"add live input {name}", pp =>
        {
            id = pp.NewId(Ids.Media);
            var item = new MediaItem(id, name, MediaKind.Stream, location, "", 0, MediaInfo.Unknown, ["live"])
            {
                Live = settings == LiveSettings.Default ? null : settings,
            };
            return pp with { Media = pp.Media.Add(item) };
        });
        if (!r.Changed)
            return FromEdit(r);
        VideoIo.Reconcile(Store.Project);
        var input = VideoIo.InputOf(id);
        var state = protocol == LiveProtocol.Ffmpeg ? "read by FFmpeg when it plays"
            : input?.Problem is { Length: > 0 } problem ? problem
            : cold ? "cold: it connects when something shows it"
            : "connecting";
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("id", id);
            w.WriteString("uri", location);
            w.WriteString("proto", ProtoWord(protocol));
            w.WriteString("state", input?.Source is { } s ? LiveSourceState(s.Live.State) : protocol == LiveProtocol.Ffmpeg ? "ffmpeg" : "failed");
            w.WriteEndObject();
        }), $"added {name} ({location}) — {state}");
    }

    private static string DefaultName(LiveProtocol protocol, string address, bool byUrl)
    {
        if (byUrl || protocol is LiveProtocol.Ffmpeg)
            return address;
        var (host, name) = LiveUri.Split(address);
        return name.Length > 0 ? name : host;
    }

    private async Task<CommandReply> InputDiscoverAsync(VerbCall call)
    {
        LiveProtocol? only = null;
        var waitMs = 0;
        for (var i = 0; i < call.Args.Count; i++)
        {
            var a = call.Args[i];
            if (a.Equals("NDI", StringComparison.OrdinalIgnoreCase))
                only = LiveProtocol.Ndi;
            else if (a.Equals("OMT", StringComparison.OrdinalIgnoreCase))
                only = LiveProtocol.Omt;
            else if (a.Equals("SPOUT", StringComparison.OrdinalIgnoreCase))
                only = LiveProtocol.Spout;
            else if (a.Equals("ALL", StringComparison.OrdinalIgnoreCase))
                only = null;
            else if (a.Equals("WAIT", StringComparison.OrdinalIgnoreCase) && i + 1 < call.Args.Count
                     && int.TryParse(call.Args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var ms) && ms is >= 0 and <= 5000)
            {
                waitMs = ms;
                i++;
            }
            else
                return CommandReply.Fail($"INPUT DISCOVER takes NDI, OMT, SPOUT or ALL and WAIT <0-5000 ms>, not '{a}'");
        }
        VideoIo.Refresh(only);
        if (waitMs > 0)
            await Task.Delay(waitMs, call.Ct).ConfigureAwait(false);
        var project = Store.Project;
        var found = VideoIo.Discovered(project, only);
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("ndi", ProviderState(LiveProtocol.Ndi));
            w.WriteString("omt", ProviderState(LiveProtocol.Omt));
            w.WriteString("spout", ProviderState(LiveProtocol.Spout));
            w.WriteStartArray("sources");
            foreach (var e in found)
                WriteEndpoint(w, e);
            w.WriteEndArray();
            w.WriteEndObject();
        }));
    }

    private async Task<CommandReply> InputReconnectAsync(VerbCall call)
    {
        if (call.Args.Count != 1)
            return CommandReply.Fail("INPUT RECONNECT <input>|ALL");
        var project = Store.Project;
        var all = call.Args[0].Equals("ALL", StringComparison.OrdinalIgnoreCase);
        var targets = new List<LiveInput>();
        foreach (var input in VideoIo.InputsOf(project))
        {
            if (all ? input.Source?.Live.State is not LiveState.Ok : input.Id == call.Args[0] || string.Equals(input.Item.Name, call.Args[0], StringComparison.Ordinal))
                targets.Add(input);
        }
        if (!all && targets.Count == 0)
            return project.FindMedia(call.Args[0]) is { } m
                ? CommandReply.Fail($"{m.Name} is not a live input (INPUT RECONNECT reconnects NDI, OMT and Spout inputs)")
                : CommandReply.Fail($"no live input {call.Args[0]}");
        if (targets.Count == 0)
            return CommandReply.Fail("every live input delivers — nothing to reconnect");
        if (!all && targets[0].Source is { } healthy && healthy.Live is { State: LiveState.Ok } live)
            return CommandReply.Fail(string.Create(CultureInfo.InvariantCulture,
                $"{targets[0].Item.Name} delivers {live.Fps:0.##} fps ({live.AgeMs} ms old) — nothing to reconnect"));
        if (!all && targets[0].Source is null && targets[0].Problem.Length > 0)
            return CommandReply.Fail(targets[0].Problem);
        foreach (var t in targets)
            VideoIo.Reconnect(t);
        Journal.Add("videoio", $"reconnecting {string.Join(", ", targets.Select(t => t.Item.Name))}", ok: true);
        // Observed, not attempted: answered at the first frame from the new connection, or with the words that none came yet.
        var deadline = Services.Mono.NowNs + 3_000_000_000L;
        var real = DateTimeOffset.UtcNow.AddSeconds(3);
        while (targets.Any(t => t.Source?.Live.Received is not > 0))
        {
            if (Services.Mono.NowNs >= deadline || DateTimeOffset.UtcNow >= real)
                break;
            await Task.Delay(20, call.Ct).ConfigureAwait(false);
        }
        var waiting = targets.Where(t => t.Source?.Live.Received is not > 0).Select(t => t.Item.Name).ToList();
        return waiting.Count == 0
            ? CommandReply.Success(effect: $"reconnected {string.Join(", ", targets.Select(t => t.Item.Name))}: frames are arriving")
            : CommandReply.Accepted($"reconnecting {string.Join(", ", waiting)}: no frame yet — STATE says when the first lands");
    }

    // ---- SEND -----------------------------------------------------------------------------------------------------------------

    private CommandReply SendAdd(VerbCall call)
    {
        var args = call.Args;
        if (args.Count < 3)
            return CommandReply.Fail("SEND ADD NDI|OMT|SPOUT <name> <tap> [<w>x<h>] [@<fps>] [UYVY|UYVA|BGRA8] [QUALITY LOW|MEDIUM|HIGH] [ALWAYS]");
        SendProtocol protocol;
        if (args[0].Equals("NDI", StringComparison.OrdinalIgnoreCase))
            protocol = SendProtocol.Ndi;
        else if (args[0].Equals("OMT", StringComparison.OrdinalIgnoreCase))
            protocol = SendProtocol.Omt;
        else if (args[0].Equals("SPOUT", StringComparison.OrdinalIgnoreCase))
            protocol = SendProtocol.Spout;
        else
            return CommandReply.Fail($"SEND ADD takes NDI, OMT or SPOUT first, not '{args[0]}'");
        var name = args[1];
        var tap = args[2];
        int w = 0, h = 0;
        double fps = 0;
        var format = SendFormat.Auto;
        var quality = "default";
        var always = false;
        for (var i = 3; i < args.Count; i++)
        {
            var a = args[i];
            if (a.StartsWith('@'))
            {
                if (!double.TryParse(a[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out fps) || fps is < 1 or > 240)
                    return CommandReply.Fail($"@<fps> is a rate from 1 to 240, not '{a}'");
            }
            else if (a.Contains('x', StringComparison.OrdinalIgnoreCase) && TrySize(a, out var sw, out var sh))
            {
                (w, h) = (sw, sh);
            }
            else if (a.Equals("UYVY", StringComparison.OrdinalIgnoreCase))
                format = SendFormat.Uyvy;
            else if (a.Equals("UYVA", StringComparison.OrdinalIgnoreCase))
                format = SendFormat.Uyva;
            else if (a.Equals("BGRA8", StringComparison.OrdinalIgnoreCase))
                format = SendFormat.Bgra8;
            else if (a.Equals("QUALITY", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count
                     && args[i + 1].ToLowerInvariant() is "low" or "medium" or "high")
            {
                quality = args[++i].ToLowerInvariant();
            }
            else if (a.Equals("ALWAYS", StringComparison.OrdinalIgnoreCase))
                always = true;
            else
                return CommandReply.Fail($"SEND ADD does not take '{a}' — <w>x<h>, @<fps>, UYVY, UYVA, BGRA8, QUALITY LOW|MEDIUM|HIGH or ALWAYS");
        }
        var live = VideoIoHub.ProtocolOf(protocol);
        if (VideoIo.Unavailable(live, sending: true) is { } why)
            return CommandReply.Fail(why);
        if (protocol == SendProtocol.Ndi && quality != "default")
            return CommandReply.Fail("NDI chooses its own quality: QUALITY is OMT's");
        var project = Edits.Project;
        foreach (var s in project.Sends)
            if (s.Protocol == protocol && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                return CommandReply.Fail($"{VideoIoHub.Name(live)} already sends \"{s.Name}\" from this node ({s.Id})");
        var send = new VideoSend("", name, protocol, tap, w, h, fps, format, true) { Quality = quality, OnlyWhenWatched = !always };
        if (SendShape.Resolve(project, send, out var shapeWords) is not { } shape)
            return CommandReply.Fail(shapeWords);
        var est = EstimateMbps(protocol, shape, quality);
        var (limit, limitWords) = SendLimitMbps();
        var others = 0.0;
        foreach (var s in project.Sends)
            if (s.Enabled && SendShape.Resolve(project, s, out _) is { } os)
                others += EstimateMbps(s.Protocol, os, s.Quality);
        if (limit > 0 && others + est > limit)
            return CommandReply.Fail(string.Create(CultureInfo.InvariantCulture,
                $"{project.Sends.Count(s => s.Enabled) + 1} sends ≈ {others + est:0} Mb/s > {limit:0} Mb/s ({limitWords}) — VIDEOIO SET MAX-SEND-MBPS <n> to allow more"));
        var id = "";
        var r = Edits.Apply($"add {VideoIoHub.Name(live)} send {name}", pp =>
        {
            id = pp.NewId(Ids.Send);
            return pp with { Sends = pp.Sends.Add(send with { Id = id }) };
        });
        if (!r.Changed)
            return FromEdit(r);
        VideoIo.Reconcile(Store.Project);
        var slot = VideoIo.SendOf(id);
        var fullName = slot?.Sink?.FullName ?? "";
        var effect = slot?.Problem is { Length: > 0 } problem
            ? $"added {name}, but it sends nothing: {problem}"
            : $"sending {fullName} from {tap}" + (shapeWords.Length > 0 ? $" ({shapeWords})" : "");
        return CommandReply.Success(JsonOut(jw =>
        {
            jw.WriteStartObject();
            jw.WriteString("id", id);
            jw.WriteString("fullName", fullName);
            jw.WriteNumber("w", shape.Width);
            jw.WriteNumber("h", shape.Height);
            jw.WriteNumber("estMbps", Math.Round(est));
            jw.WriteEndObject();
        }), effect);
    }

    private static bool TrySize(string text, out int w, out int h)
    {
        w = h = 0;
        var x = text.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        return x > 0 && int.TryParse(text[..x], NumberStyles.None, CultureInfo.InvariantCulture, out w)
            && int.TryParse(text[(x + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out h) && w is >= 2 and <= 16384 && h is >= 1 and <= 16384;
    }

    /// <summary>A send's compressed rate estimate: OMT's VMX at its quality, NDI's SpeedHQ (bits a pixel at the rate).</summary>
    internal static double EstimateMbps(SendProtocol protocol, SendShape shape, string quality)
    {
        var bitsPerPixel = protocol switch
        {
            SendProtocol.Omt => quality switch { "low" => 0.8, "high" => 2.4, _ => 1.6 },
            SendProtocol.Ndi => 1.0,
            _ => 0,
        };
        return shape.Width * (double)shape.Height * shape.FpsN / shape.FpsD * bitsPerPixel / 1e6;
    }

    /// <summary>The send limit (Mb/s, 0 = none known) and its words: the setting, or 70 % of the link the NIC reports.</summary>
    private (double Limit, string Words) SendLimitMbps()
    {
        var settings = VideoIo.Settings;
        if (settings.MaxSendMbps > 0)
            return (settings.MaxSendMbps, "this node's MAX-SEND-MBPS");
        var nic = NicInfo.Read(settings.Interface);
        return nic.LinkMbps > 0
            ? (nic.LinkMbps * 0.7, string.Create(CultureInfo.InvariantCulture, $"70 % of {nic.Name}'s {nic.LinkMbps} Mb/s"))
            : (0, "");
    }

    private CommandReply SendEnable(VerbCall call)
    {
        if (call.Args.Count != 2 || call.Args[1].ToUpperInvariant() is not ("ON" or "OFF"))
            return CommandReply.Fail("SEND ENABLE <send> ON|OFF");
        var send = Edits.Project.FindSend(call.Args[0]);
        if (send is null)
            return CommandReply.Fail($"no send {call.Args[0]}");
        var on = call.Args[1].Equals("ON", StringComparison.OrdinalIgnoreCase);
        if (send.Enabled == on)
            return CommandReply.Fail($"{send.Name} is already {(on ? "on" : "off")}");
        var watchers = VideoIo.SendOf(send.Id)?.Sink?.Connections ?? 0;
        var r = Edits.Apply($"{(on ? "start" : "stop")} send {send.Name}", pp => pp with { Sends = pp.Sends.Update(send.Id, s => s with { Enabled = on }) });
        if (!r.Changed)
            return FromEdit(r);
        VideoIo.Reconcile(Store.Project);
        var slot = VideoIo.SendOf(send.Id);
        if (on)
            return slot?.Problem is { Length: > 0 } problem
                ? CommandReply.Success(effect: $"{send.Name} is on, but sends nothing: {problem}")
                : CommandReply.Success(effect: $"{send.Name} sends as {slot?.Sink?.FullName}");
        return CommandReply.Success(effect: watchers > 0
            ? $"{send.Name} stopped — {watchers} receiver{(watchers == 1 ? "" : "s")} lost this picture"
            : $"{send.Name} stopped");
    }

    private CommandReply SendRemove(VerbCall call)
    {
        if (call.Args.Count != 1)
            return CommandReply.Fail("SEND REMOVE <send>");
        var send = Edits.Project.FindSend(call.Args[0]);
        if (send is null)
            return CommandReply.Fail($"no send {call.Args[0]}");
        var watchers = VideoIo.SendOf(send.Id)?.Sink?.Connections ?? 0;
        var r = Edits.Apply($"remove send {send.Name}", pp => pp with { Sends = pp.Sends.RemoveId(send.Id) });
        if (!r.Changed)
            return FromEdit(r);
        VideoIo.Reconcile(Store.Project);
        return CommandReply.Success(effect: watchers > 0
            ? $"removed {send.Name} — {watchers} receiver{(watchers == 1 ? "" : "s")} lost this picture"
            : $"removed {send.Name}");
    }

    // ---- VIDEOIO SET ----------------------------------------------------------------------------------------------------------

    private CommandReply VideoIoSet(VerbCall call)
    {
        var args = call.Args;
        if (args.Count < 2)
            return CommandReply.Fail("VIDEOIO SET NDI ON|OFF | NDI-GROUPS <groups> | NDI-DISCOVERY <host,…>|OFF | OMT ON|OFF | OMT-DISCOVERY <host[:port]>|OFF | SPOUT ON|OFF | MAX-SEND-MBPS <0-100000>");
        var s = VideoIo.Settings;
        var key = args[0].ToUpperInvariant();
        var value = string.Join(' ', args.Skip(1));
        bool? onOff = value.ToUpperInvariant() switch { "ON" => true, "OFF" => false, _ => null };
        VideoIoSettings next;
        string applies;
        string effect;
        switch (key)
        {
            case "NDI" when onOff is { } on:
                next = s with { NdiEnabled = on };
                (applies, effect) = ("next start", $"NDI {(on ? "on" : "off")}");
                break;
            case "NDI-GROUPS":
                var groups = value.Equals("OFF", StringComparison.OrdinalIgnoreCase) || value == "\"\"" ? "" : value;
                next = s with { NdiGroups = groups };
                (applies, effect) = ("now", groups.Length > 0 ? $"NDI groups {groups}" : "NDI groups: public");
                break;
            case "NDI-DISCOVERY":
                var servers = onOff == false ? "" : value;
                if (servers.Length > 0 && servers.Split(',').Any(h => h.Trim().Length == 0 || h.Contains(' ', StringComparison.Ordinal)))
                    return CommandReply.Fail($"NDI-DISCOVERY is a list of hosts separated by commas, or OFF — not '{value}'");
                next = s with { NdiDiscovery = servers };
                (applies, effect) = ("next start", servers.Length > 0 ? $"NDI discovery server {servers}" : "NDI discovery by mDNS");
                break;
            case "OMT" when onOff is { } on:
                next = s with { OmtEnabled = on };
                (applies, effect) = ("next start", $"OMT {(on ? "on" : "off")}");
                break;
            case "OMT-DISCOVERY":
                var server = onOff == false ? "" : value;
                if (server.Length > 0 && server.Contains(' ', StringComparison.Ordinal))
                    return CommandReply.Fail($"OMT-DISCOVERY is host[:port] or OFF — not '{value}'");
                next = s with { OmtDiscovery = server };
                (applies, effect) = ("next start", server.Length > 0 ? $"OMT discovery server {server}" : "OMT discovery by mDNS");
                break;
            case "SPOUT" when onOff is { } on:
                next = s with { SpoutEnabled = on };
                (applies, effect) = ("next start", $"Spout {(on ? "on" : "off")}");
                break;
            case "MAX-SEND-MBPS" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var mbps) && mbps is >= 0 and <= 100_000:
                next = s with { MaxSendMbps = mbps };
                (applies, effect) = ("now", mbps > 0 ? $"sends limited to {mbps} Mb/s" : "sends limited to 70 % of the link");
                break;
            default:
                return CommandReply.Fail($"VIDEOIO SET does not take '{string.Join(' ', args)}' — NDI ON|OFF, NDI-GROUPS, NDI-DISCOVERY, OMT ON|OFF, OMT-DISCOVERY, SPOUT ON|OFF or MAX-SEND-MBPS");
        }
        if (next == s)
            return CommandReply.Fail($"video I/O already has {effect}");
        VideoIo.SetSettings(next);
        Journal.Add("videoio", effect + (applies == "now" ? "" : " (applies at the next engine start)"), ok: true);
        FactsChanged();
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("applies", applies);
            w.WriteEndObject();
        }), applies == "now" ? effect : effect + " — applies at the next engine start");
    }

    // ---- STATE ----------------------------------------------------------------------------------------------------------------

    partial void StateVideoIo(Utf8JsonWriter w) => WriteVideoIo(w, Store.Project, lists: true, named: true);

    private void WriteVideoIo(Utf8JsonWriter w, Project project, bool lists, bool named = false)
    {
        if (named)
            w.WriteStartObject("videoio");
        else
            w.WriteStartObject();
        WriteNdi(w);
        WriteOmt(w);
        WriteSpout(w);
        var discovered = VideoIo.Discovered(project);
        w.WriteNumber("discoveredCount", discovered.Count);
        if (lists)
        {
            w.WriteStartArray("discovered");
            foreach (var e in discovered.Take(DiscoveredRows))
                WriteEndpoint(w, e);
            w.WriteEndArray();
            w.WritePropertyName("inputs");
            WriteInputs(w, project);
            w.WritePropertyName("sends");
            WriteSends(w, project);
        }
        WriteBandwidth(w, project);
        w.WriteEndObject();
    }

    private void WriteNdi(Utf8JsonWriter w)
    {
        var provider = VideoIo.InputProvider(LiveProtocol.Ndi);
        var settings = VideoIo.Settings;
        var status = !settings.NdiEnabled ? new ProviderStatus("off", "", "", "NDI is off on this node", "https://ndi.video")
            : provider?.Status ?? new ProviderStatus("absent", "", "", VideoIoHub.Unregistered(LiveProtocol.Ndi), "https://ndi.video");
        w.WriteStartObject("ndi");
        w.WriteString("state", status.State);
        w.WriteString("version", status.Version);
        w.WriteString("path", status.Path);
        w.WriteString("words", status.Words);
        w.WriteString("link", status.Link.Length > 0 ? status.Link : "https://ndi.video");
        w.WriteString("groups", settings.NdiGroups);
        w.WriteString("discovery", settings.NdiDiscovery);
        w.WriteBoolean("advertisers", provider is IVideoRuntimeFacts { Advertising: true });
        w.WriteEndObject();
    }

    private void WriteOmt(Utf8JsonWriter w)
    {
        var provider = VideoIo.InputProvider(LiveProtocol.Omt);
        var settings = VideoIo.Settings;
        var status = !settings.OmtEnabled ? new ProviderStatus("off", "", "", "OMT is off on this node", "")
            : provider?.Status ?? new ProviderStatus("failed", "", "", VideoIoHub.Unregistered(LiveProtocol.Omt), "");
        var omt = provider as IVideoRuntimeFacts;
        w.WriteStartObject("omt");
        w.WriteString("state", status.State);
        w.WriteString("version", status.Version);
        w.WriteString("vmx", omt is { Vmx.Length: > 0 } ? omt.Vmx : "missing");
        w.WriteString("mdns", omt is { Mdns.Length: > 0 } ? omt.Mdns : "none");
        w.WriteString("server", settings.OmtDiscovery);
        w.WriteString("words", status.Words);
        w.WriteEndObject();
    }

    private void WriteSpout(Utf8JsonWriter w)
    {
        var status = VideoIo.InputProvider(LiveProtocol.Spout)?.Status ?? VideoIo.GpuSendProvider(SendProtocol.Spout)?.Status;
        w.WriteStartObject("spout");
        w.WriteString("state", status?.State ?? "unavailable");
        w.WriteString("version", status?.Version ?? "");
        w.WriteNumber("senders", VideoIo.InputProvider(LiveProtocol.Spout)?.Endpoints.Count ?? 0);
        w.WriteString("words", status?.Words ?? VideoIoHub.Unregistered(LiveProtocol.Spout));
        w.WriteEndObject();
    }

    private static void WriteEndpoint(Utf8JsonWriter w, LiveEndpoint e)
    {
        w.WriteStartObject();
        w.WriteString("uri", e.Uri);
        w.WriteString("proto", ProtoWord(e.Protocol));
        w.WriteString("name", e.Name);
        w.WriteString("host", e.Host);
        w.WriteString("address", e.Address);
        w.WriteString("added", e.InLibrary);
        w.WriteEndObject();
    }

    private void WriteInputs(Utf8JsonWriter w, Project project)
    {
        w.WriteStartArray();
        foreach (var input in VideoIo.InputsOf(project))
        {
            var settings = input.Item.Live ?? LiveSettings.Default;
            var live = input.Source?.Live;
            var (state, words) = InputState(input);
            w.WriteStartObject();
            w.WriteString("id", input.Id);
            w.WriteString("name", input.Item.Name);
            w.WriteString("uri", input.Item.Location);
            w.WriteString("proto", ProtoWord(input.Protocol));
            w.WriteString("state", state);
            w.WriteString("words", words);
            w.WriteNumber("w", live?.Width ?? 0);
            w.WriteNumber("h", live?.Height ?? 0);
            w.WriteNumber("fps", live?.Fps ?? 0);
            w.WriteNumber("ageMs", live?.AgeMs ?? -1);
            w.WriteNumber("received", live?.Received ?? 0);
            w.WriteNumber("dropped", live?.Dropped ?? 0);
            w.WriteNumber("repeated", live?.Repeated ?? 0);
            w.WriteString("using", input.Using);
            w.WriteString("tally", input.Tally);
            w.WriteBoolean("onAir", input.Tally == "program");
            w.WriteString("onLoss", settings.OnLoss.ToString().ToLowerInvariant());
            w.WriteString("format", live?.Format ?? "");
            w.WriteBoolean("alpha", live?.Alpha ?? false);
            w.WriteString("pacing", "latest");
            w.WriteBoolean("warm", settings.Warm);
            w.WriteNumber("mbps", live?.Mbps ?? 0);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static (string State, string Words) InputState(LiveInput input)
    {
        if (input.Source is not { } source)
            return input.Problem.Length > 0 ? ("failed", input.Problem) : ("connecting", "cold: it connects when something shows it");
        var live = source.Live;
        return (LiveSourceState(live.State), live.Words);
    }

    private static string LiveSourceState(LiveState state) => LiveSource.StateWord(state);

    private void WriteSends(Utf8JsonWriter w, Project project)
    {
        w.WriteStartArray();
        var slots = VideoIo.SendsOf(project).ToDictionary(s => s.Send.Id, StringComparer.Ordinal);
        foreach (var send in project.Sends)
        {
            slots.TryGetValue(send.Id, out var slot);
            var sink = slot?.Sink;
            var plan = slot?.Plan;
            var stats = sink?.Stats;
            var (state, words) = SendState(send, slot);
            w.WriteStartObject();
            w.WriteString("id", send.Id);
            w.WriteString("name", send.Name);
            w.WriteString("proto", send.Protocol.ToString().ToLowerInvariant());
            w.WriteString("fullName", sink?.FullName ?? "");
            w.WriteString("tap", send.Tap);
            w.WriteNumber("w", plan?.Width ?? send.Width);
            w.WriteNumber("h", plan?.Height ?? send.Height);
            w.WriteNumber("fps", plan is { FpsD: > 0 } ? Math.Round(plan.FpsN / (double)plan.FpsD, 3) : send.Fps);
            w.WriteString("format", (plan?.Format ?? send.Format) switch
            {
                SendFormat.Uyvy => "uyvy",
                SendFormat.Uyva => "uyva",
                SendFormat.Bgra8 => "bgra8",
                var f => f.ToString().ToLowerInvariant(),
            });
            w.WriteString("state", state);
            w.WriteString("words", words);
            w.WriteNumber("connections", sink?.Connections ?? 0);
            w.WriteStartObject("tally");
            w.WriteBoolean("program", sink?.Tally.Program ?? false);
            w.WriteBoolean("preview", sink?.Tally.Preview ?? false);
            w.WriteEndObject();
            w.WriteNumber("sent", stats?.Sent ?? 0);
            w.WriteNumber("dropped", (stats?.Dropped ?? 0) + (plan?.Busy ?? 0));
            w.WriteBoolean("enabled", send.Enabled);
            w.WriteNumber("skippedUnwatched", plan?.SkippedUnwatched ?? 0);
            w.WriteNumber("mbps", stats?.Mbps ?? 0);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static (string State, string Words) SendState(VideoSend send, SendSlot? slot)
    {
        if (!send.Enabled)
            return ("off", "");
        if (slot is null)
            return ("starting", "");
        if (slot.Problem.Length > 0)
            return ("failed", slot.Problem);
        if (slot.Sink is not { } sink)
            return ("starting", "");
        if (sink.Words.Length > 0)
            return ("failed", sink.Words);
        if (send.OnlyWhenWatched && sink.Connections == 0)
            return ("idle", "nobody watches: nothing is packed until a receiver connects");
        return ("sending", slot.Fitted);
    }

    private void WriteBandwidth(Utf8JsonWriter w, Project project)
    {
        var settings = VideoIo.Settings;
        // The link is read only while the show uses video I/O: a show without it says nothing of this machine's network.
        var used = project.Sends.Any(s => s.Enabled) || project.Media.Any(IsLiveInput);
        var nic = used ? NicInfo.Read(settings.Interface) : new NicInfo("", -1);
        var inMbps = VideoIo.InputsOf(project).Sum(i => i.Source?.Live.Mbps ?? 0);
        var outMbps = VideoIo.SendsOf(project).Sum(s => s.Sink?.Stats.Mbps ?? 0);
        var (limit, _) = used ? SendLimitMbps() : (0, "");
        w.WriteStartObject("bandwidth");
        w.WriteString("nic", nic.Name);
        w.WriteNumber("linkMbps", nic.LinkMbps);
        w.WriteNumber("inMbps", Math.Round(inMbps, 1));
        w.WriteNumber("outMbps", Math.Round(outMbps, 1));
        w.WriteNumber("limitMbps", Math.Round(limit));
        w.WriteString("light", limit <= 0 ? "grey" : outMbps >= limit ? "red" : outMbps >= limit * 0.7 ? "amber" : "green");
        w.WriteEndObject();
    }

    private static bool IsLiveInput(MediaItem m) =>
        m.Kind == MediaKind.Stream && LiveUri.TryParse(m.Location, out var p, out _, out _) && p != LiveProtocol.Ffmpeg;

    private string ProviderState(LiveProtocol protocol) => VideoIo.Unavailable(protocol, sending: false) is null ? "ready"
        : VideoIo.InputProvider(protocol)?.Status.State ?? (protocol == LiveProtocol.Spout ? "unavailable" : "absent");

    private static string ProtoWord(LiveProtocol protocol) => protocol switch
    {
        LiveProtocol.Ndi => "ndi",
        LiveProtocol.Omt => "omt",
        LiveProtocol.Spout => "spout",
        LiveProtocol.Syphon => "syphon",
        LiveProtocol.DeckLink => "decklink",
        _ => "ffmpeg",
    };

    private static void WriteChecks(Utf8JsonWriter w, IReadOnlyList<PreflightRow> rows)
    {
        w.WriteStartArray();
        foreach (var r in rows)
        {
            w.WriteStartObject();
            w.WriteString("id", r.Id);
            w.WriteString("item", r.Item);
            w.WriteString("light", r.Light.ToString().ToLowerInvariant());
            w.WriteString("value", r.Value);
            w.WriteString("words", r.Note);
            w.WriteString("fix", r.Fix ?? "");
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    // ---- the context ----------------------------------------------------------------------------------------------------------

    partial void ContextVideoIo(List<ContextItem> items, List<ContextLink> links)
    {
        var project = Store.Project;
        var inputs = VideoIo.InputsOf(project);
        if (inputs.Count == 0 && project.Sends.IsEmpty)
            return;
        var (light, words) = VideoIoLight(project, inputs);
        items.Add(new ContextItem("videoio", "videoio", "Video I/O", light, words));
        foreach (var input in inputs)
        {
            var (state, stateWords) = InputState(input);
            var onAir = input.Tally == "program";
            if (!onAir && state == "ok")
                continue;
            var inLight = state switch
            {
                "ok" => Light.Green,
                "lost" or "failed" => onAir ? Light.Red : Light.Amber,
                "connecting" => Light.Grey,
                _ => Light.Amber,
            };
            items.Add(new ContextItem("input:" + input.Id, "input", input.Item.Name, inLight,
                stateWords.Length > 0 ? $"{state} — {stateWords}" : onAir ? "on air" : state));
            foreach (var s in project.Stage.Surfaces)
                if (s.Content.Source.Kind is SourceKind.Live or SourceKind.Media && s.Content.Source.Id == input.Id)
                    links.Add(new ContextLink("input:" + input.Id, "surface:" + s.Id, ContextLinkKinds.Feeds, inLight));
            foreach (var o in project.Outputs)
                if (o.Mode == OutputMode.Source && o.Source.Kind is SourceKind.Live or SourceKind.Media && o.Source.Id == input.Id)
                    links.Add(new ContextLink("input:" + input.Id, "output:" + o.Id, ContextLinkKinds.Feeds, inLight));
        }
        foreach (var slot in VideoIo.SendsOf(project))
        {
            if (!slot.Send.Enabled)
                continue;
            var (state, stateWords) = SendState(slot.Send, slot);
            var sendLight = state switch { "failed" => Light.Red, "sending" or "idle" => Light.Green, _ => Light.Grey };
            var receivers = slot.Sink?.Connections ?? 0;
            items.Add(new ContextItem("send:" + slot.Send.Id, "send", slot.Send.Name, sendLight,
                state == "failed" ? stateWords : $"{receivers} receiver{(receivers == 1 ? "" : "s")}" + (slot.Sink?.Tally.Program == true ? " · on program" : "")));
            if (SendShape.TryTap(slot.Send.Tap, out var kind, out var target, out _, out _) && kind == SendTapKind.Output)
                links.Add(new ContextLink("output:" + target, "send:" + slot.Send.Id, ContextLinkKinds.Sends, sendLight));
        }
    }

    /// <summary>The videoio item's light: red when the show uses a runtime that is absent or an input on air is lost; amber when an
    /// input is stale, lost or failed or a send drops more than 1 %; green otherwise.</summary>
    private (Light Light, string Words) VideoIoLight(Project project, IReadOnlyList<LiveInput> inputs)
    {
        var usesNdi = inputs.Any(i => i.Protocol == LiveProtocol.Ndi) || project.Sends.Any(s => s.Enabled && s.Protocol == SendProtocol.Ndi);
        if (usesNdi && VideoIo.Unavailable(LiveProtocol.Ndi, sending: false) is { } ndi)
            return (Light.Red, ndi);
        foreach (var input in inputs)
        {
            var (state, words) = InputState(input);
            if (state is "lost" or "failed" && input.Tally == "program")
                return (Light.Red, $"{input.Item.Name} {state} on air — {words}");
        }
        foreach (var input in inputs)
        {
            var (state, words) = InputState(input);
            if (state is "stale" or "lost" or "failed")
                return (Light.Amber, $"{input.Item.Name} {state} — {words}");
        }
        var sends = VideoIo.SendsOf(project);
        foreach (var slot in sends)
        {
            if (slot.Problem.Length > 0 && slot.Send.Enabled)
                return (Light.Amber, $"{slot.Send.Name}: {slot.Problem}");
            if (slot.Sink?.Stats is { Sent: > 100 } st && st.Dropped * 100 > st.Sent)
                return (Light.Amber, string.Create(CultureInfo.InvariantCulture, $"{slot.Send.Name} drops {st.Dropped * 100.0 / st.Sent:0.#} % of its frames"));
        }
        var ok = inputs.Count(i => InputState(i).State == "ok");
        var receivers = sends.Sum(s => s.Sink?.Connections ?? 0);
        var on = sends.Count(s => s.Send.Enabled);
        return (Light.Green, string.Create(CultureInfo.InvariantCulture,
            $"{ok} input{(ok == 1 ? "" : "s")} ok, {on} send{(on == 1 ? "" : "s")} to {receivers} receiver{(receivers == 1 ? "" : "s")}"));
    }

    // ---- checks ---------------------------------------------------------------------------------------------------------------

    partial void ValidateVideoIo(Project p, object entity, ref string? why)
    {
        if (why is not null)
            return;
        switch (entity)
        {
            case VideoSend send:
                why = ValidateSend(p, send);
                break;
            case MediaItem { Kind: MediaKind.Stream } item when item.Live is { } live:
                why = ValidateLive(item, live);
                break;
        }
    }

    private static string? ValidateSend(Project p, VideoSend send)
    {
        if (string.IsNullOrWhiteSpace(send.Name) || send.Name.Length > 48 || send.Name.Any(char.IsControl))
            return $"a send's name is 1–48 printable characters, not \"{send.Name}\"";
        if (send.Tap.Length > 0 && !SendShape.TryTap(send.Tap, out _, out _, out _, out var tapWhy))
            return tapWhy;
        if (send.Fps is < 0 or > 240 || !double.IsFinite(send.Fps))
            return string.Create(CultureInfo.InvariantCulture, $"{send.Name}: fps is 0 (the tap's) or 1–240, not {send.Fps}");
        if (send.Width < 0 || send.Height < 0 || send.Width > 16384 || send.Height > 16384 || (send.Width == 0) != (send.Height == 0))
            return string.Create(CultureInfo.InvariantCulture, $"{send.Name}: the size is 0×0 (the tap's) or up to 16384×16384, not {send.Width}×{send.Height}");
        if (send.Quality is not ("default" or "low" or "medium" or "high"))
            return $"{send.Name}: quality is default, low, medium or high, not \"{send.Quality}\"";
        if (send.Protocol is SendProtocol.Ndi or SendProtocol.Omt && send.Format is not (SendFormat.Auto or SendFormat.Uyvy or SendFormat.Uyva or SendFormat.Bgra8))
            return $"{send.Name}: {SendShape.Word(send.Format)} sends are next round — UYVY, UYVA or BGRA8";
        foreach (var other in p.Sends)
            if (other.Id != send.Id && other.Protocol == send.Protocol && string.Equals(other.Name, send.Name, StringComparison.OrdinalIgnoreCase))
                return $"{send.Name}: this node already sends a {send.Protocol.ToString().ToUpperInvariant()} picture of that name ({other.Id})";
        return null;
    }

    private static string? ValidateLive(MediaItem item, LiveSettings live)
    {
        if (live.LostMs is < 100 or > 60_000)
            return string.Create(CultureInfo.InvariantCulture, $"{item.Name}: live.lostMs is 100–60000, not {live.LostMs}");
        if (live.StaleMs < 0 || live.StaleMs > live.LostMs)
            return string.Create(CultureInfo.InvariantCulture, $"{item.Name}: live.staleMs is 0 (three periods) up to live.lostMs, not {live.StaleMs}");
        if (live.RecoverMs is < 0 or > 600_000)
            return string.Create(CultureInfo.InvariantCulture, $"{item.Name}: live.recoverMs is 0–600000, not {live.RecoverMs}");
        if (live.Quality is not ("default" or "low" or "medium" or "high"))
            return $"{item.Name}: live.quality is default, low, medium or high, not \"{live.Quality}\"";
        if (live.Backup.Length > 0 && (!LiveUri.TryParse(live.Backup, out var bp, out _, out _) || bp == LiveProtocol.Ffmpeg))
            return $"{item.Name}: live.backup is a live input (ndi:, omt:, spout:), not \"{live.Backup}\"";
        if (live.Fallback.Length > 0 && (!SourceRef.TryParse(live.Fallback, out var fallback) || fallback.Kind is SourceKind.Live))
            return $"{item.Name}: live.fallback is a colour, a generator, a canvas or a media item, never a live input — not \"{live.Fallback}\"";
        return null;
    }
}
