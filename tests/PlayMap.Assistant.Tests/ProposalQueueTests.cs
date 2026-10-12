using PlayMap.Core.Wire;

namespace PlayMap.Assistant.Tests;

/// <summary>The approval list: a proposal runs only when a person approves it, and records the node's reply;
/// a rejected or expired one never runs; with a bus approval desk the proposal goes there and its decision
/// comes back.</summary>
public class ProposalQueueTests
{
    private static ProposalDraft Draft(string line) =>
        new(line, VerbCatalog.Match(Parse(line))!.Value.Spec, "because", "assistant (test)", "assistant:t1", "t1/b1", 0);

    private static WireLine Parse(string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out _));
        return parsed;
    }

    [Fact]
    public async Task ARejectedProposalNeverRunsAndCannotBeApprovedAfterwards()
    {
        var node = new FakeEndpoint();
        var queue = new ProposalQueue(node);
        var changes = new List<ProposalState>();
        queue.Changed += p => changes.Add(p.State);
        var p = await queue.EnqueueAsync(Draft("BLACKOUT ON"), CancellationToken.None);

        var rejected = queue.Reject(p.Id, "Sam", "not now");

        Assert.Equal(ProposalState.Rejected, rejected.State);
        Assert.Contains("rejected by Sam: not now; it did not run", rejected.Summary, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.ApproveAsync(p.Id, "Sam", CancellationToken.None));
        Assert.Empty(node.Handled);
        Assert.Equal([ProposalState.Pending, ProposalState.Rejected], changes);
    }

    [Fact]
    public async Task TheLinesOfOneBatchAreApprovedInTheirOrder()
    {
        var node = new FakeEndpoint();
        var queue = new ProposalQueue(node);
        var first = await queue.EnqueueAsync(Draft("BLACKOUT ON") with { Order = 0 }, CancellationToken.None);
        var second = await queue.EnqueueAsync(Draft("BLACKOUT OFF") with { Order = 1 }, CancellationToken.None);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.ApproveAsync(second.Id, "Sam", CancellationToken.None));
        Assert.Contains($"{first.Id} comes first", refused.Message, StringComparison.Ordinal);
        Assert.Empty(node.Handled);

        queue.Reject(first.Id, "Sam");
        var done = await queue.ApproveAsync(second.Id, "Sam", CancellationToken.None);

        Assert.Equal(ProposalState.Done, done.State);
        Assert.Equal(["BLACKOUT OFF"], node.Handled.Select(h => h.Line));
    }

    [Fact]
    public async Task AnApprovedLineTheNodeRefusesIsRecordedAsFailedWithTheNodesWords()
    {
        var node = new FakeEndpoint { Reply = _ => CommandReply.Fail("no playlist 'pl00000009'") };
        var queue = new ProposalQueue(node);
        var p = await queue.EnqueueAsync(Draft("VIDEO PLAY ON pl00000009"), CancellationToken.None);

        var done = await queue.ApproveAsync(p.Id, "Sam", CancellationToken.None);

        Assert.Equal(ProposalState.Failed, done.State);
        Assert.Equal(false, done.ResultOk);
        Assert.Equal("ERR no playlist 'pl00000009'", done.ResultWords);
    }

    [Fact]
    public async Task AProposalOlderThanItsLifetimeExpiresInsteadOfRunning()
    {
        var clock = new ManualTime(new DateTimeOffset(2026, 10, 12, 19, 0, 0, TimeSpan.Zero));
        var node = new FakeEndpoint();
        var queue = new ProposalQueue(node, time: clock, lifetime: TimeSpan.FromMinutes(10));
        var p = await queue.EnqueueAsync(Draft("VIDEO PLAY"), CancellationToken.None);

        clock.Now = clock.Now.AddMinutes(11);

        Assert.Empty(queue.Pending);
        Assert.Equal(ProposalState.Expired, queue.Find(p.Id)!.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.ApproveAsync(p.Id, "Sam", CancellationToken.None));
        Assert.Empty(node.Handled);
    }

    [Fact]
    public async Task WithAnApprovalDeskOnTheBusTheProposalGoesThereAndItsResultComesBack()
    {
        var node = new FakeEndpoint();
        var sink = new FakeSink();
        var queue = new ProposalQueue(node, sink);

        var p = await queue.EnqueueAsync(Draft("SURFACE SOURCE sf00000001 generator:gn5a5a5a5a"), CancellationToken.None);

        Assert.Equal(ProposalState.Forwarded, p.State);
        Assert.Equal("p17", p.RemoteId);
        Assert.Equal("high", Assert.Single(sink.Offered).RiskWord);
        Assert.Empty(queue.Pending);
        // A forwarded proposal is the desk's to decide, not this node's list.
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.ApproveAsync(p.Id, "Sam", CancellationToken.None));

        var done = queue.CompleteForwarded(p.Id, true, "OK", "Ana at Stagewatch FOH");

        Assert.Equal(ProposalState.Done, done.State);
        Assert.Contains("approved by Ana at Stagewatch FOH, ran: OK", done.Summary, StringComparison.Ordinal);
        Assert.Empty(node.Handled);
    }

    [Fact]
    public async Task ADeskThatDoesNotTakeAProposalLeavesItOnThisNodesList()
    {
        var node = new FakeEndpoint();
        var queue = new ProposalQueue(node, new FakeSink { Answer = null });

        var p = await queue.EnqueueAsync(Draft("BLACKOUT ON"), CancellationToken.None);

        Assert.Equal(ProposalState.Pending, p.State);
        Assert.Single(queue.Pending);
    }

    [Fact]
    public void TheReplyIsReportedAsTheNodeSaidIt()
    {
        Assert.Equal("OK", ProposalQueue.ReplyWords(CommandReply.Success()));
        Assert.Equal("OK gn12345678 — made a generator", ProposalQueue.ReplyWords(CommandReply.Success("gn12345678", "made a generator")));
        Assert.Equal("OK (accepted, not yet confirmed: scan started)", ProposalQueue.ReplyWords(CommandReply.Accepted("scan started")));
        Assert.Equal("ERR already playing", ProposalQueue.ReplyWords(CommandReply.Fail("already playing")));
    }
}
