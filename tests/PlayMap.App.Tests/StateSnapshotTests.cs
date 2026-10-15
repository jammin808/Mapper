using PlayMap.App.State;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine;

namespace PlayMap.App.Tests;

/// <summary>The desk reads STATE as a real engine writes it: STATE.air.pattern is an object (its kind and the outputs it
/// covers), null when no pattern is up; the round-2 sections are kept whole in Root for the packages' readers.</summary>
public class StateSnapshotTests
{
    private static readonly SessionInfo Desk = new("d1", "desk", "127.0.0.1", true, "PlayMapDesk", true);

    [Fact]
    public async Task ARealEnginesStateLineWithAirPatternAsAnObjectParses()
    {
        var clock = new ManualClock(5_000_000_000);
        await using var engine = new EngineCore(EngineServices.ForTests(clock));
        var none = StateSnapshot.Parse(engine.State().Json);
        Assert.Null(none.Air.Pattern);

        Assert.True(WireGrammar.TryParse("PATTERN grid", out var line, out _));
        var reply = await engine.HandleAsync(line, Desk, CancellationToken.None);
        Assert.True(reply.Ok, reply.Payload);

        // The line exactly as the node pushes it on the wire, read the way the desk's link reads it.
        var push = "STATE " + engine.State().Json;
        Assert.Contains("\"pattern\":{\"kind\":\"pattern.grid\"", push, StringComparison.Ordinal);
        var s = StateSnapshot.Parse(push["STATE ".Length..]);
        Assert.Equal("pattern.grid", s.Air.Pattern);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, s.Root.GetProperty("air").GetProperty("pattern").ValueKind);
        Assert.False(s.Air.Blackout);

        // A STATE from before round 2 (the pattern as a bare word) still reads.
        Assert.Equal("pattern.grid", StateSnapshot.Parse("{\"air\":{\"pattern\":\"pattern.grid\"}}").Air.Pattern);
        Assert.Null(StateSnapshot.Parse("{\"air\":{\"pattern\":null}}").Air.Pattern);
    }
}
