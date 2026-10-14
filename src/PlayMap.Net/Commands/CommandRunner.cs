using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;

namespace PlayMap.Net.Commands;

/// <summary>The one path a line takes on every transport (the wire, HTTP, the WebSocket, MCP, an approved
/// proposal): STATE, CONTEXT and VERBS are answered from the endpoint's facts and the catalogue; a mutating verb
/// from a session that has not paired is refused before anything runs (fail closed); everything else — unknown
/// words included, which the endpoint refuses — goes to the endpoint; a fault is that line's refusal, counted
/// and logged without the line (a line may carry a token), and the session survives it.</summary>
public sealed class CommandRunner(IWireEndpoint endpoint, ShowWallClock wall, ILogger log)
{
    private long _faults;

    public IWireEndpoint Endpoint => endpoint;

    public ShowWallClock Wall => wall;

    /// <summary>Lines that made the endpoint fault since start.</summary>
    public long Faults => Interlocked.Read(ref _faults);

    /// <summary>Runs one parsed line. <paramref name="readAtUnixMs"/> is when the transport read it (a PING's t2).</summary>
    public async ValueTask<CommandReply> RunAsync(WireLine line, SessionInfo session, CancellationToken ct, long readAtUnixMs = 0)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(session);
        var match = VerbCatalog.Match(line);
        var bare = match is { } m && m.Words == line.Count;
        if (bare)
        {
            switch (match!.Value.Spec.Name)
            {
                case "STATE":
                    return CommandReply.Success(endpoint.State().Json);
                case "CONTEXT":
                    return CommandReply.Success(endpoint.Context());
                case "VERBS":
                    return CommandReply.Success(VerbJson.For(endpoint));
            }
        }
        if (match?.Spec.Name == "PING")
            return Pong(line, readAtUnixMs);
        if (match is { Spec: { } spec } && (spec.Mutates || spec.PairedOnly) && !session.Paired)
            return CommandReply.Fail(WireWords.NotPaired);
        return await HandleGuardedAsync(line, session, ct);
    }

    /// <summary>The endpoint's answer, or the line's fault refusal.</summary>
    public async ValueTask<CommandReply> HandleGuardedAsync(WireLine line, SessionInfo session, CancellationToken ct)
    {
        try
        {
            return await endpoint.HandleAsync(line, session, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fault(ex, session);
        }
    }

    /// <summary>A fault behind a line, counted and logged (never the line itself), as that line's refusal.</summary>
    public CommandReply Fault(Exception ex, SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentNullException.ThrowIfNull(session);
        var n = Interlocked.Increment(ref _faults);
        log.LogError(ex, "The endpoint faulted on a {Transport} line from {Address} (fault #{Fault}).", session.Transport, session.RemoteAddress, n);
        return CommandReply.Fail(WireWords.Fault(ex.GetType().Name, n));
    }

    /// <summary>"OK PONG t1 t2 t3" (Unix ms on the show clock); a bare PING is answered "OK PONG".</summary>
    public CommandReply Pong(WireLine line, long readAtUnixMs)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!TryStamp(line.Word(1), out var t1))
            return CommandReply.Success("PONG");
        var t2 = readAtUnixMs > 0 ? readAtUnixMs : wall.UnixMs;
        return CommandReply.Success(PongWords(t1, t2, wall.UnixMs));
    }

    public static string PongWords(long t1, long t2, long t3) => string.Create(CultureInfo.InvariantCulture, $"PONG {t1} {t2} {t3}");

    /// <summary>A PING's stamp: positive whole milliseconds.</summary>
    public static bool TryStamp(string word, out long t1) =>
        long.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out t1) && t1 > 0;

    /// <summary>The wire line for a reply, or null when the reply is silent. An accepted (not yet applied) verb
    /// answers <c>OK @a1 {"stage":"accepted","effect":"…"}</c> (Show Bus §3).</summary>
    public static string? WireLineFor(string? requestId, CommandReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (reply.Silent)
            return null;
        if (!reply.Ok)
            return WireGrammar.Err(requestId, reply.Payload ?? "refused");
        if (reply.Applied)
            return WireGrammar.Ok(requestId, reply.Payload);
        return WireGrammar.Ok(requestId, JsonText.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("stage", "accepted");
            w.WriteString("effect", reply.Effect);
            if (reply.Payload is { Length: > 0 } p)
                w.WriteString("payload", p);
            w.WriteEndObject();
        }));
    }

    /// <summary>The Show Bus receipt (§3) for a reply: what HTTP and the WebSocket answer, and what an approval desk
    /// is told. A reply's payload rides in refs.reply; the wire line rides in msg.</summary>
    public static string ReceiptJson(string? requestId, CommandReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var id = requestId ?? "";
        IReadOnlyDictionary<string, string>? refs = reply.Ok && reply.Payload is { Length: > 0 } p
            ? new Dictionary<string, string>(StringComparer.Ordinal) { ["reply"] = p }
            : null;
        var receipt = !reply.Ok ? Receipt.Refused(id, reply.Payload ?? "refused")
            : reply.Applied ? Receipt.Applied(id, reply.Effect, refs)
            : Receipt.Accepted(id, reply.Effect) with { Refs = refs };
        return JsonText.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("type", "receipt");
            if (requestId is null)
                w.WriteNull("id");
            else
                w.WriteString("id", requestId);
            w.WriteBoolean("ok", receipt.Ok);
            w.WriteString("stage", receipt.Stage);
            w.WriteString("effect", receipt.Effect);
            if (receipt.Error is null)
                w.WriteNull("error");
            else
                w.WriteString("error", receipt.Error);
            if (receipt.Refs is null)
            {
                w.WriteNull("refs");
            }
            else
            {
                w.WriteStartObject("refs");
                foreach (var (k, v) in receipt.Refs)
                    w.WriteString(k, v);
                w.WriteEndObject();
            }
            w.WriteString("msg", WireLineFor(requestId, reply) ?? "");
            w.WriteEndObject();
        });
    }
}

/// <summary>The vocabulary as JSON — VERBS on the wire, <c>GET /api/verbs</c>, the MCP list_verbs tool: one table. Node-to-node
/// verbs are left out; "available" says whether this node answers the row now.</summary>
public static class VerbJson
{
    public static readonly string All = Build(VerbAvailability.RoundOne);

    /// <summary>The rows an assistant may use on a node that routes no round-2 row: the first round's.</summary>
    public static readonly string AllAvailable = Build(VerbAvailability.RoundOne, onlyAvailable: true);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IVerbAvailability, string> MadeAll = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IVerbAvailability, string> MadeAvailable = new();

    /// <summary>VERBS as an endpoint answers it: every row, each marked by the endpoint's own availability when it says which rows it
    /// routes, else by the first round's. An endpoint's routes are fixed once it is built, so its table is made once.</summary>
    public static string For(IWireEndpoint endpoint) =>
        endpoint is IVerbAvailability availability ? MadeAll.GetValue(availability, a => Build(a)) : All;

    /// <summary>What an assistant is shown (MCP's list_verbs): only the rows the endpoint answers now.</summary>
    public static string AvailableFor(IWireEndpoint endpoint) =>
        endpoint is IVerbAvailability availability ? MadeAvailable.GetValue(availability, a => Build(a, onlyAvailable: true)) : AllAvailable;

    public static string Build(IVerbAvailability availability, bool onlyAvailable = false)
    {
        ArgumentNullException.ThrowIfNull(availability);
        return JsonText.Write(w =>
        {
            w.WriteStartArray();
            foreach (var v in VerbCatalog.All)
            {
                if (v.Internal)
                    continue;
                var available = availability.IsAvailable(v);
                if (available || !onlyAvailable)
                    Write(w, v, available);
            }
            w.WriteEndArray();
        });
    }

    public static void Write(Utf8JsonWriter w, VerbSpec v, bool available)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(v);
        w.WriteStartObject();
        w.WriteString("name", v.Name);
        w.WriteString("syntax", v.Syntax);
        w.WriteString("words", v.Words);
        w.WriteString("category", v.Category.ToString().ToLowerInvariant());
        w.WriteString("risk", ShowContext.RiskWord(v.Risk));
        w.WriteBoolean("mutates", v.Mutates);
        w.WriteStartArray("aliases");
        foreach (var a in v.Aliases)
            w.WriteStringValue(a);
        w.WriteEndArray();
        w.WriteString("show", JsonNamingPolicy.CamelCase.ConvertName(v.Show.ToString()));
        w.WriteStartArray("riskWhen");
        foreach (var r in v.RiskWhen)
        {
            w.WriteStartObject();
            w.WriteNumber("arg", r.Arg);
            w.WriteString("word", r.Word);
            w.WriteString("risk", ShowContext.RiskWord(r.Risk));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteBoolean("ephemeral", v.Ephemeral);
        w.WriteBoolean("gesture", v.Gesture);
        w.WriteBoolean("pairedOnly", v.PairedOnly);
        w.WriteStartArray("forms");
        foreach (var f in VerbSyntax.Alternatives(v))
            w.WriteStringValue(f);
        w.WriteEndArray();
        w.WriteBoolean("available", available);
        w.WriteEndObject();
    }
}

/// <summary>Patterns' Interactive fact lines — "KEY VALUE" a Patterns desk sends a "Lines" device on every change
/// (BLACKOUT 1, LIVE 0, LOOK Keynote, DECK 3 12…). They are facts, never commands: never answered (an answer would
/// be matched to that desk's own outstanding line), and taken in only from a paired peer.</summary>
public static class FactLines
{
    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    {
        "BLACKOUT", "LIVE", "DUCK", "FROZEN", "REVIEW", "LOOK", "PROGRAM", "STINGER", "LOWERTHIRD",
        "VOL", "MUSICVOL", "LOOKEDITED", "ARMED", "HOLD", "CUE", "DECK", "STEP",
    };

    /// <summary>True for a fact line. BLACKOUT is also PlayMap's verb: only its 0/1 form is a fact.</summary>
    public static bool IsFact(WireLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.RequestId is not null || line.Count == 0)
            return false;
        var key = line.Words[0];
        if (!Keys.Contains(key))
            return false;
        if (key == "BLACKOUT")
            return line.Count == 2 && line.Words[1] is "0" or "1";
        return true;
    }
}
