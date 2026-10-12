using PlayMap.Core.Wire;

namespace PlayMap.Net;

/// <summary>What the transports read beside <see cref="IWireEndpoint"/>: whether anything is playing (STATE then
/// reaches every listener at least once a second, because a receiver counts a clip down from the frames it holds)
/// and whether the node's show thread is turning over (a WebSocket screen hears <c>{"type":"beat","live":false}</c>
/// instead of a frozen time while it is not). The engine implements it beside its endpoint; an endpoint that
/// does not is taken as idle and live.</summary>
public interface INodeActivity
{
    /// <summary>True while any playlist plays or anything else moves on its own.</summary>
    bool IsAnimating { get; }

    /// <summary>False while the show thread is stuck: its STATE is a frozen picture, not a fact.</summary>
    bool IsLive => true;
}

/// <summary>One file this node can hand to a peer, addressed by its content.</summary>
/// <param name="Hash">Lowercase hex SHA-256 of the whole file (64 characters).</param>
/// <param name="Path">Where it is on this machine (never sent to a peer).</param>
/// <param name="Length">Its size in bytes.</param>
/// <param name="Name">The name a person knows it by (file name, no folder).</param>
public sealed record MediaFile(string Hash, string Path, long Length, string Name);

/// <summary>The engine's library as the network sees it: content-addressed, read-only. <c>GET /api/media</c> lists
/// <see cref="Manifest"/>; <c>GET /api/media/&lt;sha256&gt;</c> serves <see cref="Lookup"/>'s file with ranges.
/// Only files the store names are ever served — a hash is never a path.</summary>
public interface IMediaStore
{
    /// <summary>The file with this hash (lowercase hex), or null when this node does not hold it verified.</summary>
    MediaFile? Lookup(string sha256);

    /// <summary>Every file this node holds verified.</summary>
    IReadOnlyList<MediaFile> Manifest();
}

public enum ApprovalDecision
{
    /// <summary>Run it now.</summary>
    Allow,
    /// <summary>Not run: handed to a human (an approval queue); the answer carries where it waits.</summary>
    Queued,
    /// <summary>Not run, and not queued: the words say why.</summary>
    Refused,
}

/// <param name="Decision">What happens to the line.</param>
/// <param name="Words">Why, in words a person reads (empty for a plain allow).</param>
/// <param name="ProposalId">Where a queued line waits (null otherwise).</param>
public sealed record ApprovalVerdict(ApprovalDecision Decision, string Words, string? ProposalId = null)
{
    public static readonly ApprovalVerdict Allow = new(ApprovalDecision.Allow, "");
}

/// <summary>Who decides whether an assistant's line runs (Show Bus §6/§11). Low and review verbs run on a word;
/// a high-risk verb — and any line whose verb is unknown, which counts as high — is asked of this policy before
/// anything runs. The node's own fences (pairing, the endpoint's refusals) still apply after an allow.</summary>
public interface IApprovalPolicy
{
    ValueTask<ApprovalVerdict> DecideAsync(VerbSpec? verb, string line, string reason, CancellationToken ct);
}

/// <summary>Hands a line to a human: an approval queue (the node's own, or a Stagewatch desk's). Answers the
/// proposal's id, or null when it could not be queued.</summary>
public interface IProposalSink
{
    ValueTask<string?> ProposeAsync(string line, string reason, VerbRisk risk, CancellationToken ct);
}

/// <summary>The default policy: a high-risk line runs only while the node's "AI may act" switch is on; with the
/// switch off it goes to the approval queue when there is one, else it is refused with the reason.</summary>
public sealed class SwitchApprovalPolicy(Func<bool> aiMayAct, IProposalSink? queue = null) : IApprovalPolicy
{
    public async ValueTask<ApprovalVerdict> DecideAsync(VerbSpec? verb, string line, string reason, CancellationToken ct)
    {
        if (aiMayAct())
            return ApprovalVerdict.Allow;
        var name = verb?.Name ?? "an unknown verb";
        if (queue is not null && await queue.ProposeAsync(line, reason, verb?.Risk ?? VerbRisk.High, ct) is { } id)
            return new ApprovalVerdict(ApprovalDecision.Queued, $"{name} is high risk — queued for a person to approve (proposal {id})", id);
        return new ApprovalVerdict(ApprovalDecision.Refused,
            $"{name} is high risk — an assistant may not run it while \"AI may act\" is off on this node; propose it to the operator instead");
    }
}
