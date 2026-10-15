using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Wire;
using PlayMap.Engine.Companion;
using PlayMap.Net.Companion;

namespace PlayMap.Engine;

/// <summary>wp2-companion's hooks into the engine: INDEX, EVENTS, WHOAMI, PRESENCE and THUMB; the INDEX, EVENTS and THUMBS topics;
/// STATE.clients, STATE.events and project.index; a context item per Companion client. The controllers' table is filled by the line
/// sessions (<see cref="IClientRegistry"/>).</summary>
public sealed partial class EngineCore : IClientRegistry, IThumbSource
{
    private CompanionParts? _companion;

    /// <summary>The node's side of two-way control (made with the engine, in its routes).</summary>
    internal CompanionParts Companion => _companion ?? throw new InvalidOperationException("the engine is still being made");

    partial void RouteCompanion(VerbRoutes routes)
    {
        var parts = _companion = new CompanionParts(this);
        routes.OffLane("INDEX", call => Task.FromResult(call.Args.Count == 0
            ? CommandReply.Success(parts.Index().Json)
            : CommandReply.Fail("INDEX takes no words after it")));
        routes.OffLane("EVENTS", call => Task.FromResult(EventsReply(call)));
        routes.OffLane("WHOAMI", call => Task.FromResult(WhoAmI(call.Session)));
        routes.OffLane("PRESENCE", call => Task.FromResult(PresenceReply(call)));
        routes.OffLane("THUMB", ThumbVerbAsync);
        routes.Topic("INDEX", parts.OpenIndex);
        routes.Topic("EVENTS", parts.OpenEvents);
        routes.Topic("THUMBS", parts.OpenThumbs);
        EventRaised += parts.TakeEvent;
        StateChanged += _ => parts.OnStateMoved();
        ProjectChanged += _ => parts.OnProjectMoved();
    }

    partial void StateCompanion(Utf8JsonWriter w)
    {
        var parts = Companion;
        parts.Clients.Write(w);
        w.WriteStartObject("events");
        w.WriteNumber("seq", parts.Events.LastSeq);
        w.WriteEndObject();
    }

    partial void ProjectCompanion(Utf8JsonWriter w) => w.WriteString("index", Companion.Index(fresh: false).Sig);

    partial void ContextCompanion(List<ContextItem> items, List<ContextLink> links)
    {
        foreach (var c in Companion.Clients.All)
        {
            if (c.Kind != "companion")
                continue;
            var words = !c.Paired ? "connected, not paired"
                : string.Join(" · ", new[] { c.Module is null ? null : "module " + c.Module, c.Where, c.Recording ? "recording" : null }
                    .Where(x => x is not null)) is { Length: > 0 } said ? said : "paired and live";
            items.Add(new ContextItem("client:" + c.Session, "deck", c.Name, c.Paired ? Light.Green : Light.Amber, words));
        }
    }

    void IClientRegistry.Hello(SessionInfo session, string channel) => Companion.Clients.Hello(session, channel, Services.Wall.UnixMs);

    void IClientRegistry.Gone(string sessionId) => Companion.Clients.Gone(sessionId);

    ValueTask<ThumbResult> IThumbSource.ThumbAsync(ThumbRequest request, SessionInfo session, CancellationToken ct) =>
        Companion.Thumbs.ThumbAsync(request, ct);

    /// <summary>EVENTS [SINCE &lt;seq&gt;].</summary>
    private CommandReply EventsReply(VerbCall call)
    {
        var a = call.Args;
        if (a.Count == 0)
            return CommandReply.Success(Companion.Events.Reply(0));
        if (a.Count == 2 && a[0].Equals("SINCE", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(a[1], NumberStyles.None, CultureInfo.InvariantCulture, out var since))
            return CommandReply.Success(Companion.Events.Reply(since));
        return CommandReply.Fail("EVENTS takes nothing, or SINCE <seq> (a whole number)");
    }

    /// <summary>WHOAMI: this session as the node sees it — transport and channel, the client's name, paired, its Show Mode role, armed,
    /// and the verbs it may run now ("*" when nothing is gated).</summary>
    private CommandReply WhoAmI(SessionInfo s)
    {
        var gate = Services.ShowGate;
        var client = Companion.Clients.Find(s.SessionId);
        var may = gate.Allowed(s);
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("session", s.SessionId);
            w.WriteString("transport", s.Transport);
            w.WriteString("channel", client?.Channel ?? (s.Transport is "http" or "mcp" ? "http" : s.Transport == "ws" ? "ws" : "tcp"));
            w.WriteString("client", s.ClientName);
            w.WriteBoolean("paired", s.Paired);
            w.WriteString("role", gate.RoleOf(s).ToString().ToLowerInvariant());
            w.WriteBoolean("armed", gate.Armed);
            if (may is null)
            {
                w.WriteString("may", "*");
            }
            else
            {
                w.WriteStartArray("may");
                foreach (var v in may.Order(StringComparer.Ordinal))
                    w.WriteStringValue(v);
                w.WriteEndArray();
            }
            w.WriteNumber("minGoMs", gate.MinGoMs);
            w.WriteNumber("proto", 1);
            w.WriteEndObject();
        }));
    }

    /// <summary>PRESENCE &lt;json&gt;: what a controller says it is and shows (module, page, recording, how many pictures).</summary>
    private CommandReply PresenceReply(VerbCall call)
    {
        var json = call.Line.RestAfter(call.Spec.WordCount);
        if (json.Length == 0)
            return CommandReply.Fail("PRESENCE takes one JSON object: {\"kind\",\"module\",\"live\",\"page\",\"recording\",\"thumbs\",\"where\"}");
        return Companion.Clients.Presence(call.Session.SessionId, json) is { } why ? CommandReply.Fail(why) : CommandReply.Success();
    }

    /// <summary>THUMB &lt;ref&gt; [&lt;w&gt;x&lt;h&gt;] [JPEG|PNG] [IF &lt;etag&gt;].</summary>
    private async Task<CommandReply> ThumbVerbAsync(VerbCall call)
    {
        if (!ThumbArgs.TryParse(call.Args, out var request, out var why))
            return CommandReply.Fail(why!);
        var r = await Companion.Thumbs.ThumbAsync(request!, call.Ct);
        if (!r.Ok)
            return CommandReply.Fail(r.Why ?? "no picture of it");
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("ref", request!.Ref);
            w.WriteString("etag", r.Etag);
            if (r.NotModified)
            {
                w.WriteBoolean("same", true);
            }
            else
            {
                w.WriteString("mime", r.Mime);
                w.WriteNumber("w", r.Width);
                w.WriteNumber("h", r.Height);
                w.WriteBase64String("data", r.Bytes.Span);
            }
            w.WriteEndObject();
        }));
    }
}
