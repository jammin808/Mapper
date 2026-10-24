using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine;

namespace PlayMap.Assistant.Tests.Quad;

/// <summary>The Oops history through the assistant, on a real engine: a scripted provider proposes UNDO TO &lt;step&gt; (a step UNDO LIST
/// names); while the show is armed it waits for a person even where review lines run without asking, and nothing is undone; disarmed it
/// runs, and UNDO LIST reads back the step and every newer one gone.</summary>
public sealed class AssistantUndoTests
{
    private static readonly SessionInfo Desk = SessionInfo.Desk("d1");

    private static readonly AssistantSettings RunsReview = AssistantSettings.Default with { Enabled = true, RunReviewWithoutAsking = true };

    private static async Task<CommandReply> Run(EngineCore engine, string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out _), text);
        return await engine.HandleAsync(line, Desk, CancellationToken.None);
    }

    private static async Task<long[]> Steps(EngineCore engine)
    {
        var reply = await Run(engine, "UNDO LIST");
        Assert.True(reply.Ok, reply.Payload);
        return [.. JsonDocument.Parse(reply.Payload!).RootElement.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("step").GetInt64())];
    }

    private static JsonElement Result(AssistantRequest request, string callId)
    {
        var part = request.Messages[^1].Parts.OfType<ToolResultPart>().Single(r => r.ToolCallId == callId);
        return JsonDocument.Parse(part.Content).RootElement.Clone();
    }

    [Fact]
    public async Task UndoToIsHeldForAPersonWhileArmedRunsDisarmedAndIsReadBackThroughUndoList()
    {
        var clock = new ManualClock(5_000_000_000);
        await using var engine = new EngineCore(EngineServices.ForTests(clock), Project.CreateDefault("Undo test"));
        var output = engine.Store.Project.Outputs[0].Id;
        // Three edits a second apart: three undo steps (the store keeps a burst of one field as one).
        foreach (var name in new[] { "One", "Two", "Three" })
        {
            Assert.True((await Run(engine, $"SET {output} name \"{name}\"")).Ok);
            clock.AdvanceMs(1000);
        }
        var steps = await Steps(engine);
        Assert.Equal(3, steps.Length);
        var undoLine = "UNDO TO " + steps[1].ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Armed: the assistant's UNDO TO waits for a person, and nothing is undone.
        Assert.True((await Run(engine, "SHOW ARM")).Ok);
        var armed = new ScriptedProvider()
            .Then(ScriptedProvider.Call(ScriptedProvider.Propose("u1", "take back the last two names", undoLine)))
            .Then(ScriptedProvider.Say("Waiting for you."));
        var queue = new ProposalQueue(engine);
        await new AssistantSession(armed, engine, queue, RunsReview, new AssistantSessionOptions { SessionId = "a1" }).AskAsync("undo the last two");
        Assert.Contains("\"status\":\"queued\"", Result(armed.Requests[1], "u1").GetRawText(), StringComparison.Ordinal);
        Assert.Equal(undoLine, Assert.Single(queue.All).Line);
        Assert.Equal(steps, await Steps(engine));
        Assert.Equal("Three", engine.Store.Project.FindOutput(output)!.Name);

        // Disarmed: it runs, and UNDO LIST reads back the step and the newer one gone, as one action.
        Assert.True((await Run(engine, "SHOW DISARM")).Ok);
        var disarmed = new ScriptedProvider()
            .Then(ScriptedProvider.Call(ScriptedProvider.Propose("u2", "take back the last two names", undoLine)))
            .Then(ScriptedProvider.Say("Undone."));
        await new AssistantSession(disarmed, engine, new ProposalQueue(engine), RunsReview, new AssistantSessionOptions { SessionId = "a2" })
            .AskAsync("undo the last two");
        Assert.Contains("\"status\":\"ran\"", Result(disarmed.Requests[1], "u2").GetRawText(), StringComparison.Ordinal);
        Assert.Equal([steps[2]], await Steps(engine));
        Assert.Equal("One", engine.Store.Project.FindOutput(output)!.Name);
    }
}
