using System.Text.Json;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine;

namespace PlayMap.Assistant.Tests.Quad;

/// <summary>The four-click quad through the assistant, on a real engine: a scripted provider proposes SURFACE QUAD &lt;projector&gt; … PX
/// and SURFACE CORNER &lt;sf&gt; TR BY 1 0 PX; the gate decides each by its class and the operator's settings (a review line waits for a
/// person unless review lines run without asking), and while the show is armed every assistant line waits; what ran is read back from
/// STATE (the projector's quads) and the project (the corner a pixel to the right).</summary>
public sealed class AssistantQuadTests
{
    private static readonly SessionInfo Desk = SessionInfo.Desk("d1");

    private static readonly AssistantSettings RunsReview = AssistantSettings.Default with { Enabled = true, RunReviewWithoutAsking = true };

    /// <summary>One projector, pj_a, on op_1 (1920 × 1080) in projector mode.</summary>
    private static Project OneProjector()
    {
        var p = Project.CreateDefault("Quad test");
        var pj = new Projector("pj_a", "Front", Intrinsics.FromThrowRatio(1920, 1080, 1.4), LensDistortion.None,
            Pose.LookAt(new Vec3d(0, 1.5, 5), new Vec3d(0, 1.5, 0), Vec3d.UnitY), 0, "#3C9AF2", false, [], SolveLocks.None, 0);
        return p.WithStage(s => s with { Projectors = [pj] }) with
        {
            Outputs = [p.Outputs[0] with { Mode = OutputMode.Projector, ProjectorId = "pj_a", Width = 1920, Height = 1080, Enabled = true }],
        };
    }

    private static JsonElement Result(AssistantRequest request, string callId)
    {
        var part = request.Messages[^1].Parts.OfType<ToolResultPart>().Single(r => r.ToolCallId == callId);
        return JsonDocument.Parse(part.Content).RootElement.Clone();
    }

    private static async Task<CommandReply> Run(EngineCore engine, string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out _), text);
        return await engine.HandleAsync(line, Desk, CancellationToken.None);
    }

    private static int Quads(EngineCore engine) =>
        JsonDocument.Parse(engine.State().Json).RootElement.GetProperty("stage").GetProperty("projectors")[0].GetProperty("quads").GetInt32();

    [Fact]
    public async Task TheQuadAndItsCornerAreDecidedByTheGateAndReadBackFromTheNode()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)), OneProjector());
        const string QuadLine = "SURFACE QUAD pj_a 480 270 1440 270 1440 810 480 810 PX";

        // The operator's default settings: a review line waits for a person.
        var held = new ScriptedProvider()
            .Then(ScriptedProvider.Call(ScriptedProvider.Propose("c1", "draw the wall's quad", QuadLine)))
            .Then(ScriptedProvider.Say("Waiting for you."));
        var queue = new ProposalQueue(engine);
        await new AssistantSession(held, engine, queue, AssistantSettings.Default with { Enabled = true }, new AssistantSessionOptions { SessionId = "a1" })
            .AskAsync("draw the quad");
        Assert.Contains("\"status\":\"queued\"", Result(held.Requests[1], "c1").GetRawText(), StringComparison.Ordinal);
        Assert.Equal(QuadLine, Assert.Single(queue.All).Line);
        Assert.Equal(0, Quads(engine));

        // Review lines that run without asking: the quad is drawn, and STATE counts it on its projector.
        var runs = new ScriptedProvider()
            .Then(ScriptedProvider.Call(ScriptedProvider.Propose("c2", "draw the wall's quad", QuadLine)))
            .Then(ScriptedProvider.Say("Drawn."));
        await new AssistantSession(runs, engine, new ProposalQueue(engine), RunsReview, new AssistantSessionOptions { SessionId = "a2" })
            .AskAsync("draw the quad");
        Assert.Contains("\"status\":\"ran\"", Result(runs.Requests[1], "c2").GetRawText(), StringComparison.Ordinal);
        Assert.Equal(1, Quads(engine));
        var quad = engine.Store.Project.Stage.Surfaces.Single(s => s.Pixel is not null);
        var tr = quad.Pixel!.TopRight;

        // The corner by one pixel, proposed while the show is armed: it waits for a person even where review lines run, and nothing moves.
        Assert.True((await Run(engine, "SHOW ARM")).Ok);
        var cornerLine = $"SURFACE CORNER {quad.Id} TR BY 1 0 PX";
        var armed = new ScriptedProvider()
            .Then(ScriptedProvider.Call(ScriptedProvider.Propose("c3", "one pixel right", cornerLine)))
            .Then(ScriptedProvider.Say("Queued."));
        var armedQueue = new ProposalQueue(engine);
        await new AssistantSession(armed, engine, armedQueue, RunsReview, new AssistantSessionOptions { SessionId = "a3" })
            .AskAsync("nudge the top right corner");
        Assert.Contains("\"status\":\"queued\"", Result(armed.Requests[1], "c3").GetRawText(), StringComparison.Ordinal);
        Assert.Equal(cornerLine, Assert.Single(armedQueue.All).Line);
        Assert.Equal(tr, engine.Store.Project.FindSurface(quad.Id)!.Pixel!.TopRight);

        // Disarmed, the same line from the assistant runs: the corner is one wall pixel to the right.
        Assert.True((await Run(engine, "SHOW DISARM")).Ok);
        var nudge = new ScriptedProvider()
            .Then(ScriptedProvider.Call(ScriptedProvider.Propose("c4", "one pixel right", cornerLine)))
            .Then(ScriptedProvider.Say("Moved."));
        await new AssistantSession(nudge, engine, new ProposalQueue(engine), RunsReview, new AssistantSessionOptions { SessionId = "a4" })
            .AskAsync("nudge the top right corner");
        Assert.Contains("\"status\":\"ran\"", Result(nudge.Requests[1], "c4").GetRawText(), StringComparison.Ordinal);
        var moved = engine.Store.Project.FindSurface(quad.Id)!.Pixel!.TopRight;
        Assert.Equal(tr.X + (1 / 1920.0), moved.X, 6);
        Assert.Equal(tr.Y, moved.Y, 6);
        Assert.Equal(1, Quads(engine));
    }
}
