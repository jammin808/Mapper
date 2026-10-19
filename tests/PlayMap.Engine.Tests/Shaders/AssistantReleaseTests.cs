using System.Text.Json;
using PlayMap.Assistant;
using PlayMap.Assistant.Tests;
using PlayMap.Core.Effects;
using PlayMap.Core.Wire;
using PlayMap.Engine.ShaderWorker;
using PlayMap.Engine.Tests.Show;
using PlayMap.Render.Effects;

namespace PlayMap.Engine.Tests.Shaders;

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

    private static (ShowRig Rig, ShaderQuarantine Quarantine) Node()
    {
        var quarantine = new ShaderQuarantine();
        var rig = new ShowRig(services: s => ShaderParts.Compose(s, quarantine, null));
        ShaderParts.Attach(rig.Engine);
        quarantine.Add(new QuarantineEntry(FxCatalog.Builtin.Source("fx.invert")!.Hash, "Invert", QuarantineReason.GpuTime,
            "its pass held the GPU past the output's period on 30 frames", 1_760_000_000_000, "test"));
        return (rig, quarantine);
    }

    private static int Held(ShowRig rig) =>
        rig.State().GetProperty("fx").TryGetProperty("quarantine", out var q) ? q.GetArrayLength() : 0;

    [Fact]
    public async Task TheAssistantsReleaseWaitsForAPersonAndOnlyTheApprovalLiftsTheQuarantine()
    {
        var (rig, quarantine) = Node();
        await using (rig)
        {
            var provider = new ScriptedProvider()
                .Then(ScriptedProvider.Call(ScriptedProvider.Propose("c1", "Bring the invert back", "FX RELEASE Invert")))
                .Then(ScriptedProvider.Say("It waits for you."));
            var queue = new ProposalQueue(rig.Engine);
            var session = new AssistantSession(provider, rig.Engine, queue, Everything, new AssistantSessionOptions { SessionId = "t1" });

            var result = await session.AskAsync("release the invert");

            var proposal = Assert.Single(queue.Pending);
            Assert.Equal("FX RELEASE Invert", proposal.Line);
            Assert.Equal(VerbRisk.High, proposal.Risk);
            Assert.Equal([proposal.Id], result.Queued);
            Assert.Single(quarantine.All);
            Assert.Equal(1, Held(rig));

            var approved = await queue.ApproveAsync(proposal.Id, "Sam", CancellationToken.None);
            Assert.Equal(ProposalState.Done, approved.State);
            Assert.Equal(true, approved.ResultOk);
            Assert.Empty(quarantine.All);
            Assert.Equal(0, Held(rig));
        }
    }

    [Fact]
    public async Task WhileArmedTheAssistantsReleaseIsRefusedNeitherRunNorQueued()
    {
        var (rig, quarantine) = Node();
        await using (rig)
        {
            await rig.Ok("SHOW ARM");
            var provider = new ScriptedProvider()
                .Then(ScriptedProvider.Call(ScriptedProvider.Propose("c1", "Bring the invert back", "FX RELEASE Invert")))
                .Then(ScriptedProvider.Say("It cannot be done while armed."));
            var queue = new ProposalQueue(rig.Engine);
            var session = new AssistantSession(provider, rig.Engine, queue, Everything, new AssistantSessionOptions { SessionId = "t1" });

            var result = await session.AskAsync("release the invert");

            Assert.Empty(queue.Pending);
            Assert.Empty(result.Queued);
            Assert.Single(quarantine.All);
            var report = provider.Requests[1].Messages[^1].Parts.OfType<ToolResultPart>().Single(p => p.ToolCallId == "c1");
            using var doc = JsonDocument.Parse(report.Content);
            var row = doc.RootElement.GetProperty("results")[0];
            Assert.Equal("refused", row.GetProperty("status").GetString());
            Assert.Contains("SHOW DISARM first", row.GetProperty("reply").GetString(), StringComparison.Ordinal);
        }
    }
}
