using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Wire;

namespace PlayMap.Assistant.Tests;

/// <summary>An assistant on a node sees and proposes what that node answers: the prompt it is sent and its list_verbs hold a round-2
/// row once the node routes it and no row the node does not answer; a proposal of a row the node does not answer, or of a
/// node-to-node verb, is refused with words before anything runs or waits for a person.</summary>
public sealed class AvailabilityTests
{
    private static readonly AssistantSettings On = AssistantSettings.Default with { Enabled = true };

    /// <summary>A node that answers the first round's rows and the round-2 rows it was given.</summary>
    private sealed class RoutingNode(FakeEndpoint inner, params string[] routed) : IWireEndpoint, IVerbAvailability
    {
        public FakeEndpoint Inner => inner;

        public bool IsAvailable(VerbSpec spec) => spec.Round == 1 || routed.Contains(spec.Name, StringComparer.Ordinal);

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

    private static string PromptLine(VerbSpec v) => "- " + v.Syntax + " — " + v.Words + " — risk " + ShowContext.RiskWord(v.Risk);

    private static ToolResultPart ResultFor(AssistantRequest request, string callId) =>
        request.Messages[^1].Parts.OfType<ToolResultPart>().Single(r => r.ToolCallId == callId);

    [Fact]
    public async Task ThePromptAndListVerbsHoldTheRowsTheNodeRoutesAndNoOther()
    {
        var routed = VerbCatalog.Find("SHOW ARM")!;
        var category = routed.Category.ToString().ToLowerInvariant();
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Call(ScriptedProvider.Tool("c1", AssistantTools.ListVerbs, $$"""{"category":"{{category}}"}""")))
            .Then(ScriptedProvider.Say("Read."));
        var node = new RoutingNode(new FakeEndpoint(), routed.Name);
        var session = new AssistantSession(provider, node, new ProposalQueue(node), On, new AssistantSessionOptions { SessionId = "t1" });

        await session.AskAsync("what can you do here");

        var system = provider.Requests[0].System;
        Assert.Contains(PromptLine(routed), system, StringComparison.Ordinal);
        var unrouted = VerbCatalog.All.Where(v => v.Round == 2 && v != routed).ToList();
        Assert.NotEmpty(unrouted);
        Assert.All(unrouted, v => Assert.DoesNotContain(PromptLine(v), system, StringComparison.Ordinal));

        using var listed = JsonDocument.Parse(ResultFor(provider.Requests[1], "c1").Content);
        var names = listed.RootElement.EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(routed.Name, names);
        foreach (var v in VerbCatalog.All.Where(v => v.Category == routed.Category))
            Assert.Equal(!v.Internal && (v.Round == 1 || v == routed), names.Contains(v.Name));
    }

    [Fact]
    public async Task AProposalTheNodeWouldNeverRunIsRefusedBeforeAnythingQueues()
    {
        var unrouted = VerbCatalog.Find("PANIC")!;
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Call(ScriptedProvider.Propose("c1", "hold the outputs", "MIRROR AIR {\"rev\":1}", unrouted.Name)))
            .Then(ScriptedProvider.Say("Neither runs here."));
        var node = new RoutingNode(new FakeEndpoint(), "SHOW ARM");
        var queue = new ProposalQueue(node);
        var session = new AssistantSession(provider, node, queue, On, new AssistantSessionOptions { SessionId = "t1" });

        await session.AskAsync("hold everything");

        Assert.Empty(node.Inner.Handled);
        Assert.Empty(queue.All);
        var refused = ResultFor(provider.Requests[1], "c1").Content;
        Assert.Contains("MIRROR: this line is node-to-node only", refused, StringComparison.Ordinal);
        Assert.Contains($"{unrouted.Name} is not available on this node yet", refused, StringComparison.Ordinal);
    }
}
