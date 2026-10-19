using System.Text.Json;
using PlayMap.Core.Effects;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine;
using PlayMap.Engine.ShaderWorker;
using PlayMap.Render.Effects;

namespace PlayMap.Assistant.Tests.Shaders;

/// <summary>The assistant and the quarantine through the gate every line takes, on a real node: FX RELEASE is high risk, so the
/// assistant's proposal waits for a person whatever the operator's settings run without asking, and only a person's approval lifts
/// the quarantine (read back from STATE); while the show is armed the line is Setup, refused to the assistant, neither run nor
/// queued.</summary>
public sealed class AssistantReleaseTests
{
    private static readonly AssistantSettings Everything = AssistantSettings.Default with
    {
        Enabled = true,
        RunLowWithoutAsking = true,
        RunReviewWithoutAsking = true,
    };

    /// <summary>A node composed with the shader parts (no worker) whose quarantine holds the built-in invert.</summary>
    private static (EngineCore Engine, ShaderQuarantine Quarantine) Node()
    {
        var quarantine = new ShaderQuarantine();
        var engine = new EngineCore(ShaderParts.Compose(EngineServices.ForTests(), quarantine, null), Project.CreateDefault("Assistant release"));
        ShaderParts.Attach(engine);
        Assert.True(quarantine.Add(new QuarantineEntry(FxCatalog.Builtin.Source("fx.invert")!.Hash, "Invert", QuarantineReason.GpuTime,
            "its pass held the GPU past the output's period on 30 frames", 1_760_000_000_000, "test")).Applied);
        return (engine, quarantine);
    }

    private static int Held(EngineCore engine) =>
        JsonDocument.Parse(engine.State().Json).RootElement.GetProperty("fx").TryGetProperty("quarantine", out var q) ? q.GetArrayLength() : 0;

    private static async Task Ok(EngineCore engine, string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        var reply = await engine.HandleAsync(parsed, SessionInfo.Desk("d1"), CancellationToken.None);
        Assert.True(reply.Ok, $"{line} → {reply.Payload}");
    }

    [Fact]
    public async Task TheAssistantsReleaseWaitsForAPersonAndOnlyTheApprovalLiftsTheQuarantine()
    {
        var (engine, quarantine) = Node();
        await using (engine)
        {
            var provider = new ScriptedProvider()
                .Then(ScriptedProvider.Call(ScriptedProvider.Propose("c1", "Bring the invert back", "FX RELEASE Invert")))
                .Then(ScriptedProvider.Say("It waits for you."));
            var queue = new ProposalQueue(engine);
            var session = new AssistantSession(provider, engine, queue, Everything, new AssistantSessionOptions { SessionId = "t1" });

            var result = await session.AskAsync("release the invert");

            var proposal = Assert.Single(queue.Pending);
            Assert.Equal("FX RELEASE Invert", proposal.Line);
            Assert.Equal(VerbRisk.High, proposal.Risk);
            Assert.Equal([proposal.Id], result.Queued);
            Assert.Single(quarantine.All);
            Assert.Equal(1, Held(engine));

            var approved = await queue.ApproveAsync(proposal.Id, "Sam", CancellationToken.None);
            Assert.Equal(ProposalState.Done, approved.State);
            Assert.Equal(true, approved.ResultOk);
            Assert.Empty(quarantine.All);
            Assert.Equal(0, Held(engine));
        }
    }

    [Fact]
    public async Task WhileArmedTheAssistantsReleaseIsRefusedNeitherRunNorQueued()
    {
        var (engine, quarantine) = Node();
        await using (engine)
        {
            await Ok(engine, "SHOW ARM");
            var provider = new ScriptedProvider()
                .Then(ScriptedProvider.Call(ScriptedProvider.Propose("c1", "Bring the invert back", "FX RELEASE Invert")))
                .Then(ScriptedProvider.Say("It cannot be done while armed."));
            var queue = new ProposalQueue(engine);
            var session = new AssistantSession(provider, engine, queue, Everything, new AssistantSessionOptions { SessionId = "t1" });

            var result = await session.AskAsync("release the invert");

            Assert.Empty(queue.Pending);
            Assert.Empty(result.Queued);
            Assert.Single(quarantine.All);
            Assert.Equal(1, Held(engine));
            var report = provider.Requests[1].Messages[^1].Parts.OfType<ToolResultPart>().Single(p => p.ToolCallId == "c1");
            using var doc = JsonDocument.Parse(report.Content);
            var row = doc.RootElement.GetProperty("results")[0];
            Assert.Equal("refused", row.GetProperty("status").GetString());
            Assert.Contains("SHOW DISARM first", row.GetProperty("reply").GetString(), StringComparison.Ordinal);
        }
    }
}
