using PlayMap.Core.Bus;

namespace PlayMap.Core.Wire;

/// <summary>Who is on the other end of a line: how they connected, what they said in HELLO, whether they
/// paired. The command router reads it to fence mutating verbs (fail closed) and to journal who did what.</summary>
public sealed record SessionInfo(
    string SessionId,
    /// <summary>"wire", "http", "ws", "mcp", "desk", "patterns-device", "stagewatch", "approval".</summary>
    string Transport,
    string RemoteAddress,
    bool IsLoopback,
    string ClientName,
    bool Paired)
{
    public static SessionInfo Desk(string sessionId) => new(sessionId, "desk", "127.0.0.1", true, "Desk", true);
}

/// <summary>A verb's answer. <see cref="Payload"/> is the JSON or words after OK; <see cref="Effect"/> is what
/// changed (for the receipt and the journal); <see cref="Applied"/> false means accepted but not yet
/// confirmed (a scan started, a transfer queued) — the receipt's "accepted" stage.</summary>
public sealed record CommandReply(bool Ok, string? Payload, string Effect, bool Applied = true, bool Silent = false)
{
    public static CommandReply Success(string? payload = null, string effect = "") => new(true, payload, effect);

    /// <summary>A line that is a fact, not a command (a Patterns device link's "BLACKOUT 1"): taken in,
    /// never answered — an answer would be matched to that peer's own outstanding line.</summary>
    public static CommandReply NoReply(string effect = "") => new(true, null, effect, Applied: true, Silent: true);

    public static CommandReply Accepted(string effect) => new(true, null, effect, Applied: false);

    public static CommandReply Fail(string reason) => new(false, reason, "");

    /// <summary>The wire line for this reply.</summary>
    public string ToLine(string? requestId) => Ok ? WireGrammar.Ok(requestId, Payload) : WireGrammar.Err(requestId, Payload ?? "refused");
}

/// <summary>What a transport (the TCP wire, HTTP, WebSocket, MCP, an approved proposal) hands lines to and
/// reads facts from. The engine implements it; PlayMap.Net serves it. One implementation, every transport —
/// so a verb means the same from Patterns, from Stagewatch, from an AI and from the desk.</summary>
public interface IWireEndpoint
{
    /// <summary>Runs one line. Never throws for a bad line: a refusal is a <see cref="CommandReply"/>.</summary>
    ValueTask<CommandReply> HandleAsync(WireLine line, SessionInfo session, CancellationToken ct);

    /// <summary>The node's STATE JSON (one line, compact), with its revision.</summary>
    (string Json, long Revision) State();

    /// <summary>The Show Bus §6 context JSON.</summary>
    string Context();

    /// <summary>The project JSON (compact) with its revision, for PROJECT GET and the desk's mirror.</summary>
    (string Json, long Revision) ProjectJson();

    /// <summary>Raised (on any thread) when STATE's revision moves; listeners pull <see cref="State"/>
    /// latest-wins, never a backlog.</summary>
    event Action<long>? StateChanged;

    /// <summary>Raised when the project's revision moves.</summary>
    event Action<long>? ProjectChanged;
}

/// <summary>A reply as a client sees it: OK or ERR and the text after it, and what became of the command (Show Bus 1.3 §13.12.2's
/// outcome). <see cref="Ok"/> is true exactly when the command was applied or accepted. A command written that no answer settled is
/// <see cref="PeerOutcome.Unconfirmed"/> — it may have run, so it is Ok false and never a refusal, and its text opens with
/// <see cref="UnconfirmedWord"/> — and one never written is <see cref="PeerOutcome.NotSent"/>. A reply made from OK or ERR alone is
/// applied or refused.</summary>
public sealed record WireReply(bool Ok, string Text, PeerOutcome Outcome)
{
    /// <summary>The words an unconfirmed command's text opens with, so a reader that knows only Ok and the text still reads it.</summary>
    public const string UnconfirmedWord = "unconfirmed: ";

    /// <summary>What every unconfirmed command's words end with: it may have run, and the peer's state says whether.</summary>
    public const string MayHaveRunWords = "it may have run; look at its state";

    /// <summary>A reply read from OK or ERR alone: applied when <paramref name="ok"/>, refused when not.</summary>
    public WireReply(bool ok, string text)
        : this(ok, text, ok ? PeerOutcome.Applied : PeerOutcome.Refused)
    {
    }

    /// <summary>True exactly when the command was applied or accepted. Set by a constructor only, never by a copy (<c>with</c>), so it
    /// cannot come apart from <see cref="Outcome"/>.</summary>
    public bool Ok { get; } = Ok;

    /// <summary>What became of the command. Ok and the outcome never disagree: only an applied or accepted command is Ok, a reply built
    /// otherwise throws, and neither has a setter a copy (<c>with</c>) could move one of them by.</summary>
    public PeerOutcome Outcome { get; } = Ok == (Outcome is PeerOutcome.Applied or PeerOutcome.Accepted)
        ? Outcome
        : throw new ArgumentException("a reply is Ok exactly when its command was applied or accepted", nameof(Outcome));

    /// <summary>True for a command written that no answer settled: it may have run — neither done nor refused.</summary>
    public bool IsUnconfirmed => Outcome == PeerOutcome.Unconfirmed;

    /// <summary>A command written that no answer settled (<paramref name="words"/> say why, ending with <see cref="MayHaveRunWords"/>):
    /// it may have run.</summary>
    public static WireReply Unconfirmed(string words) => new(false, UnconfirmedWord + words, PeerOutcome.Unconfirmed);

    /// <summary>A command never written (<paramref name="words"/> say why): it did not run.</summary>
    public static WireReply NotSent(string words) => new(false, words, PeerOutcome.NotSent);

    public static WireReply Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.StartsWith("OK", StringComparison.Ordinal))
            return new WireReply(true, StripId(line.Length > 2 ? line[2..].TrimStart() : ""));
        if (line.StartsWith("ERR", StringComparison.Ordinal))
            return new WireReply(false, StripId(line.Length > 3 ? line[3..].TrimStart() : ""));
        return new WireReply(false, line);
    }

    private static string StripId(string rest)
    {
        if (!rest.StartsWith('@'))
            return rest;
        var space = rest.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? "" : rest[(space + 1)..];
    }
}

public enum LinkStatus
{
    Connecting,
    Live,
    Stale,
    Down,
}

/// <summary>The desk's view of an engine — its own on this machine or a node across the network. The same
/// verbs every peer uses; the project and STATE mirrored latest-wins; previews from shared memory (local) or
/// JPEG frames (remote).</summary>
public interface IEngineLink : IAsyncDisposable
{
    LinkStatus Status { get; }

    /// <summary>The engine's node name and id once connected.</summary>
    string NodeName { get; }

    string NodeId { get; }

    /// <summary>Sends one line (a request id is added) and returns its reply with its outcome. A dead link fails at once
    /// (not-sent) — nothing is queued while a link is down; a line written that no answer settles is unconfirmed (it may have run),
    /// never refused.</summary>
    Task<WireReply> SendAsync(string line, CancellationToken ct = default);

    /// <summary>The latest STATE JSON and its revision (empty before the first).</summary>
    (string Json, long Revision) State { get; }

    /// <summary>The latest project JSON and its revision.</summary>
    (string Json, long Revision) Project { get; }

    event Action<LinkStatus>? StatusChanged;

    event Action<long>? StateChanged;

    event Action<long>? ProjectChanged;
}
