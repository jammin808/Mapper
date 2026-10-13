using PlayMap.Core.Bus;
using PlayMap.Core.Wire;

namespace PlayMap.Assistant;

/// <summary>Where a proposal is in its life (Show Bus §11): pending on this node's list or forwarded to a
/// bus approval desk; approved and running; done or failed with the node's reply; rejected; or expired
/// because nobody decided in time.</summary>
public enum ProposalState
{
    Pending,
    Forwarded,
    Running,
    Done,
    Failed,
    Rejected,
    Expired,
}

/// <summary>A line an assistant proposed that waits for a person. <see cref="PreTicked"/> is the desk's
/// default tick: only low risk is ever pre-ticked — high is never, by any assistant. The result words are the
/// node's own reply to the line once it ran: attempts are not facts.</summary>
public sealed record Proposal(
    string Id,
    string Line,
    string Verb,
    VerbRisk Risk,
    string Reason,
    /// <summary>Who proposed: "assistant (" and the model the provider asked for ")".</summary>
    string From,
    string SessionId,
    /// <summary>The proposals made by one call share a batch and run in its order.</summary>
    string Batch,
    int Order,
    long CreatedUnixMs,
    ProposalState State,
    string DecidedBy = "",
    long DecidedUnixMs = 0,
    bool? ResultOk = null,
    string ResultWords = "",
    /// <summary>The approval desk's id when it was forwarded there.</summary>
    string RemoteId = "",
    string RemoteDesk = "")
{
    public bool PreTicked => Risk == VerbRisk.Low;

    public string RiskWord => ShowContext.RiskWord(Risk);

    public bool IsOpen => State is ProposalState.Pending or ProposalState.Forwarded or ProposalState.Running;

    /// <summary>The line as a person, a desk, a log or a reply sees it: the verb's secret words replaced by a
    /// marker (<see cref="VerbCatalog.Redacted(string)"/>). Only an approval runs <see cref="Line"/> itself.</summary>
    public string Shown => VerbCatalog.Redacted(Line);

    /// <summary>One line for the model and the desk: "ap3 PLAYLIST GO pl… (high) — done: OK".</summary>
    public string Summary => State switch
    {
        ProposalState.Pending => $"{Id} {Shown} ({RiskWord}) — waiting for a person to approve; it has not run",
        ProposalState.Forwarded => $"{Id} {Shown} ({RiskWord}) — waiting on the approval desk {RemoteDesk}; it has not run",
        ProposalState.Running => $"{Id} {Shown} ({RiskWord}) — approved by {DecidedBy}, running",
        ProposalState.Done => $"{Id} {Shown} ({RiskWord}) — approved by {DecidedBy}, ran: {ResultWords}",
        ProposalState.Failed => $"{Id} {Shown} ({RiskWord}) — approved by {DecidedBy}, refused by the node: {ResultWords}",
        ProposalState.Rejected => $"{Id} {Shown} ({RiskWord}) — rejected by {DecidedBy}{(ResultWords.Length > 0 ? ": " + ResultWords : "")}; it did not run",
        _ => $"{Id} {Shown} ({RiskWord}) — expired before anyone decided; it did not run",
    };
}

/// <summary>A line to queue: what it is, why, and who is asking.</summary>
public sealed record ProposalDraft(string Line, VerbSpec Spec, string Reason, string From, string SessionId, string Batch, int Order);

/// <summary>An approval desk on the Show Bus (a Stagewatch with the approvals cap). When one is on the bus the
/// node's proposals go there instead of its own list (§11): the desk's human approves, and the node collects
/// approved lines by its pull and runs them through its own command path, then reports back with
/// <see cref="ProposalQueue.CompleteForwarded"/>.</summary>
public interface IProposalSink
{
    /// <summary>The desk's name, for the operator.</summary>
    string Name { get; }

    /// <summary>Offers a proposal. Returns the desk's id for it, or null when the desk did not take it (the
    /// proposal then stays on this node's own list).</summary>
    Task<string?> OfferAsync(Proposal proposal, CancellationToken ct);
}

/// <summary>The node's approval list — one per node, shared by every assistant session. A proposal runs only
/// when a person approves it, through the same endpoint every peer uses, and its result is the node's reply.
/// High risk is never pre-ticked and has no bulk approval. The lines of one batch are approved in their order
/// (a later one waits while an earlier one is undecided). A proposal older than its lifetime expires rather
/// than run on a show that has moved on; expiry is applied whenever the list is read or a proposal approved.</summary>
public sealed class ProposalQueue
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);

    private readonly IWireEndpoint _endpoint;
    private readonly TimeProvider _time;
    private readonly TimeSpan _lifetime;
    private readonly Lock _gate = new();
    private readonly List<Proposal> _items = [];
    private long _next;

    public ProposalQueue(IWireEndpoint endpoint, IProposalSink? sink = null, TimeProvider? time = null, TimeSpan? lifetime = null)
    {
        _endpoint = endpoint;
        Sink = sink;
        _time = time ?? TimeProvider.System;
        _lifetime = lifetime is { } l && l > TimeSpan.Zero ? l : DefaultLifetime;
    }

    /// <summary>The bus approval desk, when one is heard; null keeps proposals on this node's own list. The
    /// engine sets and clears it as desks come and go.</summary>
    public IProposalSink? Sink { get; set; }

    /// <summary>Raised (on any thread) when a proposal is added or changes state.</summary>
    public event Action<Proposal>? Changed;

    /// <summary>Raised when a sink or a listener fails; the queue carries on.</summary>
    public event Action<string>? Logged;

    /// <summary>Every proposal, oldest first (a snapshot).</summary>
    public IReadOnlyList<Proposal> All
    {
        get
        {
            ExpireStale();
            lock (_gate)
                return [.. _items];
        }
    }

    /// <summary>What waits for a person on this node's own list.</summary>
    public IReadOnlyList<Proposal> Pending => All.Where(p => p.State == ProposalState.Pending).ToList();

    public Proposal? Find(string id)
    {
        lock (_gate)
            return _items.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
    }

    /// <summary>Queues a line for a person. When a bus approval desk is set the proposal is offered there
    /// first; a desk that does not take it (or fails) leaves it on this node's list.</summary>
    public async Task<Proposal> EnqueueAsync(ProposalDraft draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Proposal proposal;
        lock (_gate)
        {
            _next++;
            // The line's own risk: some words make a verb's line high (ASSISTANT SET enabled).
            var risk = WireGrammar.TryParse(draft.Line, out var parsed, out _) ? VerbCatalog.RiskOf(parsed) : draft.Spec.Risk;
            proposal = new Proposal("ap" + _next.ToString(System.Globalization.CultureInfo.InvariantCulture), draft.Line, draft.Spec.Name,
                risk, draft.Reason, draft.From, draft.SessionId, draft.Batch, draft.Order, Now(), ProposalState.Pending);
            _items.Add(proposal);
        }
        Raise(proposal);
        if (Sink is not { } sink)
            return proposal;
        string? remoteId = null;
        try
        {
            remoteId = await sink.OfferAsync(proposal, ct);
        }
        catch (Exception ex)
        {
            // A desk that fails or a cancelled offer leaves the proposal where it already is: on this node's list.
            Logged?.Invoke($"assistant: the approval desk {sink.Name} did not take {proposal.Id} ({ex.Message}); it stays on this node's list");
        }
        if (string.IsNullOrEmpty(remoteId))
            return proposal;
        return Transition(proposal.Id, ProposalState.Pending, p => p with { State = ProposalState.Forwarded, RemoteId = remoteId, RemoteDesk = sink.Name })
            ?? proposal;
    }

    /// <summary>A person approves: the line runs through the node's endpoint and the proposal records the
    /// node's reply. Only a pending proposal on this node's list can be approved here (a forwarded one is the
    /// bus desk's to decide).</summary>
    public async Task<Proposal> ApproveAsync(string id, string approvedBy, CancellationToken ct)
    {
        ExpireStale();
        var who = string.IsNullOrWhiteSpace(approvedBy) ? "the operator" : approvedBy.Trim();
        if (EarlierOpen(id) is { } earlier)
            throw new InvalidOperationException($"{earlier.Id} comes first: the lines of one proposal run in the order they were proposed — approve or reject {earlier.Id} before {id}");
        var running = Transition(id, ProposalState.Pending, p => p with { State = ProposalState.Running, DecidedBy = who, DecidedUnixMs = Now() })
            ?? throw new InvalidOperationException(Explain(id, "approved"));
        var check = CommandGate.Check(running.Line);
        if (!check.Ok || check.Parsed is null)
            return Finish(running, false, check.Reason);
        var session = new SessionInfo("approval:" + running.Id, "approval", "127.0.0.1", true, who, true);
        CommandReply reply;
        try
        {
            reply = await _endpoint.HandleAsync(check.Parsed, session, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Whether the node applied it before the cancel is not known: say so rather than guess.
            return Finish(running, null, "cancelled while running — whether it applied is not known; check STATE");
        }
        catch (Exception ex)
        {
            return Finish(running, null, "the node faulted running it (" + ex.Message + ") — whether it applied is not known; check STATE");
        }
        return Finish(running, reply.Ok, ReplyWords(reply));
    }

    /// <summary>A person rejects a pending (or forwarded) proposal: it never runs.</summary>
    public Proposal Reject(string id, string rejectedBy, string? why = null)
    {
        var who = string.IsNullOrWhiteSpace(rejectedBy) ? "the operator" : rejectedBy.Trim();
        Proposal? done;
        lock (_gate)
        {
            var i = IndexOf(id);
            if (i < 0 || _items[i].State is not (ProposalState.Pending or ProposalState.Forwarded))
                done = null;
            else
            {
                done = _items[i] with { State = ProposalState.Rejected, DecidedBy = who, DecidedUnixMs = Now(), ResultWords = (why ?? "").Trim() };
                _items[i] = done;
            }
        }
        if (done is null)
            throw new InvalidOperationException(Explain(id, "rejected"));
        Raise(done);
        return done;
    }

    /// <summary>The bus desk's decision came back for a forwarded proposal: the node ran the approved line
    /// through its own command path (<paramref name="ok"/> and its reply words), or the desk rejected it
    /// (<paramref name="ok"/> null, <paramref name="decidedBy"/> says who).</summary>
    public Proposal CompleteForwarded(string id, bool? ok, string words, string decidedBy = "")
    {
        var state = ok switch { true => ProposalState.Done, false => ProposalState.Failed, null => ProposalState.Rejected };
        var who = string.IsNullOrWhiteSpace(decidedBy) ? "the approval desk" : decidedBy.Trim();
        return Transition(id, ProposalState.Forwarded, p => p with { State = state, ResultOk = ok, ResultWords = words ?? "", DecidedBy = who, DecidedUnixMs = Now() })
            ?? throw new InvalidOperationException(Explain(id, "completed from the approval desk"));
    }

    /// <summary>The proposals one assistant session made, oldest first.</summary>
    public IReadOnlyList<Proposal> ForSession(string sessionId) =>
        All.Where(p => string.Equals(p.SessionId, sessionId, StringComparison.Ordinal)).ToList();

    /// <summary>The reply as words: "OK", "OK {payload}", "ERR reason"; "accepted: effect" when the node has
    /// taken the line but not yet confirmed it (a scan started).</summary>
    public static string ReplyWords(CommandReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (!reply.Ok)
            return "ERR " + (reply.Payload ?? "refused");
        var words = string.IsNullOrEmpty(reply.Payload) ? "OK" : "OK " + reply.Payload;
        if (!reply.Applied)
            words += " (accepted, not yet confirmed" + (reply.Effect.Length > 0 ? ": " + reply.Effect : "") + ")";
        else if (reply.Effect.Length > 0)
            words += " — " + reply.Effect;
        return words;
    }

    private Proposal Finish(Proposal running, bool? ok, string words)
    {
        var state = ok == true ? ProposalState.Done : ProposalState.Failed;
        return Transition(running.Id, ProposalState.Running, p => p with { State = state, ResultOk = ok, ResultWords = words }) ?? running;
    }

    /// <summary>Moves a proposal from <paramref name="from"/> to what <paramref name="edit"/> makes of it;
    /// null when it is not in that state (someone else decided first).</summary>
    private Proposal? Transition(string id, ProposalState from, Func<Proposal, Proposal> edit)
    {
        Proposal next;
        lock (_gate)
        {
            var i = IndexOf(id);
            if (i < 0 || _items[i].State != from)
                return null;
            next = edit(_items[i]);
            _items[i] = next;
        }
        Raise(next);
        return next;
    }

    private void ExpireStale()
    {
        var cutoff = Now() - (long)_lifetime.TotalMilliseconds;
        List<Proposal>? expired = null;
        lock (_gate)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (_items[i].State != ProposalState.Pending || _items[i].CreatedUnixMs > cutoff)
                    continue;
                _items[i] = _items[i] with { State = ProposalState.Expired, DecidedUnixMs = Now() };
                (expired ??= []).Add(_items[i]);
            }
        }
        foreach (var p in expired ?? [])
            Raise(p);
    }

    /// <summary>An earlier line of the same batch that is still undecided or running; null when none is.</summary>
    private Proposal? EarlierOpen(string id)
    {
        lock (_gate)
        {
            var i = IndexOf(id);
            if (i < 0)
                return null;
            var p = _items[i];
            return _items.FirstOrDefault(q => string.Equals(q.Batch, p.Batch, StringComparison.Ordinal) && q.Order < p.Order && q.IsOpen);
        }
    }

    private string Explain(string id, string verb)
    {
        var p = Find(id);
        return p is null ? $"no proposal '{id}'" : $"{id} cannot be {verb}: it is {p.State.ToString().ToLowerInvariant()}";
    }

    private int IndexOf(string id) => _items.FindIndex(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    private long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private void Raise(Proposal p)
    {
        try
        {
            Changed?.Invoke(p);
        }
        catch (Exception ex)
        {
            Logged?.Invoke($"assistant: a listener failed on {p.Id} ({ex.Message})");
        }
    }
}
