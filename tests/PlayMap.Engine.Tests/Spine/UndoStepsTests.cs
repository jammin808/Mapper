using PlayMap.Core.Time;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>The node's undo steps say who made each edit and when: a line's step names its session, an edit a handler makes off the
/// lane names the session it was made for, the node's own edit names the node, and each step's time is the show's wall clock.</summary>
public sealed class UndoStepsTests
{
    private static readonly SessionInfo Desk = SessionInfo.Desk("t1");
    private static readonly SessionInfo Companion = new("t2", "wire", "127.0.0.1", true, "Companion", true);

    private static async Task Ok(EngineCore engine, string line, SessionInfo session)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        var reply = await engine.HandleAsync(parsed, session, CancellationToken.None);
        Assert.True(reply.Ok, $"{line} → {reply.Payload}");
    }

    [Fact]
    public async Task EachStepNamesWhoMadeItAndWhen()
    {
        var clock = new ManualClock(1_000_000_000);
        var services = EngineServices.ForTests(clock);
        await using var engine = new EngineCore(services);
        var output = engine.Store.Project.Outputs[0].Id;
        var at = new List<long>();

        // A second apart, so no two edits of one field are one step.
        clock.AdvanceMs(1_000);
        at.Add(services.Wall.UnixMs);
        await Ok(engine, $"SET {output} name Left", Desk);
        clock.AdvanceMs(1_000);
        at.Add(services.Wall.UnixMs);
        await Ok(engine, $"SET {output} name Right", Companion);
        clock.AdvanceMs(1_000);
        at.Add(services.Wall.UnixMs);
        await engine.EditAsync(Desk, "rename the show", p => p with { Name = "Off the lane" });
        clock.AdvanceMs(1_000);
        at.Add(services.Wall.UnixMs);
        await engine.Lane.RunAsync(() => engine.Store.Apply("the node's own", p => p with { Name = "Node" }));

        var steps = engine.Store.Steps;
        Assert.Equal(new[] { "PlayMap", "Desk", "Companion", "Desk" }, steps.Select(s => s.Who));
        Assert.Equal(new long[] { 4, 3, 2, 1 }, steps.Select(s => s.Number));
        Assert.Equal(Enumerable.Reverse(at), steps.Select(s => s.AtUnixMs));
    }
}
