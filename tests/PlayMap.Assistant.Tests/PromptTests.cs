using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Content;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine;

namespace PlayMap.Assistant.Tests;

/// <summary>The static prompt is the same bytes every build and carries the whole vocabulary and every
/// generator; the snapshot carries the node now; the tools' schemas are closed objects.</summary>
public class PromptTests
{
    [Fact]
    public void TheSystemPromptIsByteIdenticalAcrossBuildsAndNamesEveryVerbWithItsRisk()
    {
        var a = AssistantPrompt.BuildStatic();
        var b = AssistantPrompt.BuildStatic();

        Assert.Equal(a, b);
        Assert.Equal(a, AssistantPrompt.System);
        // The prompt lists the rows the node answers: on a node that routes no round-2 row, the first round's.
        foreach (var v in VerbCatalog.All.Where(VerbAvailability.RoundOne.IsAvailable))
        {
            Assert.Contains("- " + v.Syntax + " — " + v.Words + " — risk " + PlayMap.Core.Bus.ShowContext.RiskWord(v.Risk), a, StringComparison.Ordinal);
            foreach (var alias in v.Aliases)
                Assert.Contains(alias, a, StringComparison.Ordinal);
        }
        foreach (var k in GeneratorCatalog.All)
        {
            Assert.Contains(k.Id + " — " + k.Name, a, StringComparison.Ordinal);
            foreach (var p in k.Params)
                Assert.Contains("  " + p.Name + " ", a, StringComparison.Ordinal);
        }
        Assert.Contains("number 0.2..1000000, default 1", a, StringComparison.Ordinal);
        Assert.Contains("propose_commands", a, StringComparison.Ordinal);
        Assert.Contains("Never invent an id", a, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePromptSaysWhatTheHeadsOfTheContextsRecentLinesMean()
    {
        // A node's context: a line its router refused, then a fault it met.
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)), Project.CreateDefault("Recent test"));
        Assert.True(WireGrammar.TryParse("SET sf_gone opacity 0.5", out var line, out _));
        var reply = await engine.HandleAsync(line, SessionInfo.Desk("d1"), CancellationToken.None);
        Assert.False(reply.Ok);
        engine.Journal.Add("fault", "render: InvalidOperationException: the device was lost", ok: false);
        var recent = ShowContext.Parse(engine.Context())!.Recent.Select(r => r.Text).ToList();
        Assert.Equal(["refused: " + reply.Payload, "fault: render: InvalidOperationException: the device was lost"], recent[^2..]);

        // The prompt reads those heads for what they say.
        Assert.Contains("A recent line that starts \"refused: \" is a line, an edit or a learn the node refused: it did not happen.", AssistantPrompt.System,
            StringComparison.Ordinal);
        Assert.Contains("One that starts \"fault: \" is a fault the node met.", AssistantPrompt.System, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSnapshotCarriesTheNodeNowInWordsAndAsJson()
    {
        var now = new DateTimeOffset(2026, 10, 12, 19, 42, 10, TimeSpan.FromHours(1));
        var proposal = new Proposal("ap1", "VIDEO PLAY", "VIDEO PLAY", VerbRisk.High, "go", "assistant", "s", "b1", 0, 0, ProposalState.Pending);

        var snapshot = AssistantPrompt.Snapshot(SampleShow.StateJson, SampleShow.ContextJson, [proposal], now);

        Assert.Contains("Node time: 2026-10-12 19:42:10 +01:00 (unix ms 1791830530000)", snapshot, StringComparison.Ordinal);
        Assert.Contains("Stage left is green", snapshot, StringComparison.Ordinal);
        Assert.Contains("\"headline\":\"Show · Intro 0:42 left\"", snapshot, StringComparison.Ordinal);
        Assert.Contains("ap1 VIDEO PLAY (high) — waiting for a person to approve; it has not run", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryToolSchemaIsAClosedObjectWithItsRequiredFields()
    {
        Assert.Equal(
            [AssistantTools.GetState, AssistantTools.GetContext, AssistantTools.GetProject, AssistantTools.GetEntity, AssistantTools.ListVerbs, AssistantTools.PlanRecipe, AssistantTools.ResolveTime, AssistantTools.ProposeCommands],
            AssistantTools.Specs.Select(t => t.Name));
        foreach (var t in AssistantTools.Specs)
        {
            using var doc = JsonDocument.Parse(t.InputSchemaJson);
            var r = doc.RootElement;
            Assert.Equal("object", r.GetProperty("type").GetString());
            Assert.False(r.GetProperty("additionalProperties").GetBoolean());
            var properties = r.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet();
            Assert.All(r.GetProperty("required").EnumerateArray(), q => Assert.Contains(q.GetString()!, properties));
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
        }
        using var propose = JsonDocument.Parse(AssistantTools.Specs[^1].InputSchemaJson);
        Assert.Equal(["lines", "reason"], propose.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }
}
