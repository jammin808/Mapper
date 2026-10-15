using System.Text.Json;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;

namespace PlayMap.Assistant.Tests;

/// <summary>The assistant asks the node's Show Mode before it runs or queues a line, as MCP does: a line Show Mode refuses is
/// neither run nor queued and stops the lines after it; a line it holds for a person waits on the list even where the settings
/// would let it run; a line into the assistant's own blind draft runs.</summary>
public sealed class ShowDecisionsTests
{
    private static readonly AssistantSettings RunsReview = AssistantSettings.Default with { Enabled = true, RunReviewWithoutAsking = true };

    /// <summary>A node whose Show Mode answers by verb as the test sets it (everything else allowed).</summary>
    private sealed class ArmedNode(FakeEndpoint inner, Dictionary<string, ShowDecision> decisions) : IWireEndpoint, IShowDecisions
    {
        public FakeEndpoint Inner => inner;

        public List<string> Asked { get; } = [];

        public ShowDecision Decide(VerbSpec spec, WireLine line, SessionInfo session)
        {
            Asked.Add(line.Raw);
            return decisions.TryGetValue(spec.Name, out var d) ? d : new ShowDecision(ShowVerdict.Allow, null);
        }

        public ValueTask<CommandReply> HandleAsync(WireLine line, SessionInfo session, CancellationToken ct) => inner.HandleAsync(line, session, ct);

        public (string Json, long Revision) State() => inner.State();

        public string Context() => inner.Context();

        public (string Json, long Revision) ProjectJson() => inner.ProjectJson();

        public event Action<long>? StateChanged
        {
            add => inner.StateChanged += value;
            remove => inner.StateChanged -= value;
        }

        public event Action<long>? ProjectChanged
        {
            add => inner.ProjectChanged += value;
            remove => inner.ProjectChanged -= value;
        }
    }

    private static JsonElement Result(AssistantRequest request, string callId)
    {
        var part = request.Messages[^1].Parts.OfType<ToolResultPart>().Single(r => r.ToolCallId == callId);
        return JsonDocument.Parse(part.Content).RootElement.Clone();
    }

    [Fact]
    public async Task ALineShowModeRefusesIsNeitherRunNorQueuedAndStopsTheRest()
    {
        const string Words = "Not while armed: WARP NODE moves the mapping on the air. SHOW DISARM on the desk first.";
        var node = new ArmedNode(new FakeEndpoint(), new() { ["WARP NODE"] = new(ShowVerdict.Refuse, Words) });
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Call(ScriptedProvider.Propose("c1", "nudge it", "WARP NODE op_1 1 1 0.01 0", "BLACKOUT ON")))
            .Then(ScriptedProvider.Say("Refused."));
        var queue = new ProposalQueue(node);
        var session = new AssistantSession(provider, node, queue, RunsReview, new AssistantSessionOptions { SessionId = "t1" });

        await session.AskAsync("nudge the corner");

        Assert.Empty(node.Inner.Handled);
        Assert.Empty(queue.All);
        Assert.Equal(["WARP NODE op_1 1 1 0.01 0"], node.Asked);
        var text = Result(provider.Requests[1], "c1").GetRawText();
        Assert.Contains("\"status\":\"refused\"", text, StringComparison.Ordinal);
        Assert.Contains("Not while armed: WARP NODE", text, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"not_run\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALineShowModeHoldsForAPersonWaitsEvenWhereTheSettingsWouldRunIt()
    {
        var node = new ArmedNode(new FakeEndpoint(), new()
        {
            ["SET"] = new(ShowVerdict.Propose, "SET waits for a person while the show is armed: an assistant proposes, a person approves or runs it"),
        });
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Call(ScriptedProvider.Propose("c1", "rename it", "SET op_1 name \"Left\"")))
            .Then(ScriptedProvider.Say("Queued."));
        var queue = new ProposalQueue(node);
        var session = new AssistantSession(provider, node, queue, RunsReview, new AssistantSessionOptions { SessionId = "t1" });

        await session.AskAsync("rename the output");

        Assert.Empty(node.Inner.Handled);
        var queued = Assert.Single(queue.All);
        Assert.Equal("SET op_1 name \"Left\"", queued.Line);
        Assert.Contains("waits for a person while the show is armed", queued.Reason, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"queued\"", Result(provider.Requests[1], "c1").GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALineIntoTheAssistantsOwnBlindDraftRuns()
    {
        var node = new ArmedNode(new FakeEndpoint(), new() { ["SET"] = new(ShowVerdict.BlindOnly, null) });
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Call(ScriptedProvider.Propose("c1", "stage it", "SET op_1 name \"Left\"")))
            .Then(ScriptedProvider.Say("Staged."));
        var queue = new ProposalQueue(node);
        // Even a review line that would otherwise wait for a person runs: it lands in the draft, off the air.
        var session = new AssistantSession(provider, node, queue, AssistantSettings.Default with { Enabled = true },
            new AssistantSessionOptions { SessionId = "t1" });

        await session.AskAsync("stage the rename");

        Assert.Equal("SET op_1 name \"Left\"", Assert.Single(node.Inner.Handled).Line);
        Assert.Empty(queue.All);
    }
}
