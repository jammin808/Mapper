using PlayMap.Core.Wire;
using PlayMap.Net;

namespace PlayMap.Engine.Host;

/// <summary>Who decides an assistant's high-risk line on this node (HTTP's MCP tools ask <see cref="Policy"/> before
/// anything runs). It is Net's <see cref="SwitchApprovalPolicy"/> over a switch and a queue the assistant side supplies
/// through <see cref="Use"/> once its proposal queue exists: until then the switch is off and there is no queue, so a
/// high-risk line is refused with the reason — never run. The switch and the queue can change while the node runs.</summary>
public sealed class ApprovalHook : IProposalSink
{
    private volatile IProposalSink? _sink;
    private volatile Func<bool> _aiMayAct = static () => false;

    public ApprovalHook() => Policy = new SwitchApprovalPolicy(() => _aiMayAct(), this);

    /// <summary>The policy the web host is built with.</summary>
    public IApprovalPolicy Policy { get; }

    /// <summary>True once a queue has been supplied.</summary>
    public bool HasQueue => _sink is not null;

    /// <summary>Supplies the approval queue (null takes it away) and, when given, the node's "AI may act" switch.</summary>
    public void Use(IProposalSink? queue, Func<bool>? aiMayAct = null)
    {
        _sink = queue;
        if (aiMayAct is not null)
            _aiMayAct = aiMayAct;
    }

    /// <summary>Hands the line to the supplied queue; null (refused with the reason) while there is none.</summary>
    public ValueTask<string?> ProposeAsync(string line, string reason, VerbRisk risk, CancellationToken ct) =>
        _sink is { } sink ? sink.ProposeAsync(line, reason, risk, ct) : ValueTask.FromResult<string?>(null);
}
