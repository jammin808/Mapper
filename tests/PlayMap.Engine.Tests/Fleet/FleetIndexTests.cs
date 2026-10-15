using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>INDEX's eye section: NEXT and PREV PROBLEM lit by the board's worst light, the lenses, and one key per machine with its own
/// light — this node's own as "FLEET FOCUS self", the same on every node — each row's id a line of the catalogue a key sends.</summary>
public sealed class FleetIndexTests
{
    [Fact]
    public async Task TheEyeSectionHoldsTheProblemKeysAndOneKeyPerMachine()
    {
        var services = HealthTests.Services(new ManualTime()) with { FleetPeers = FleetStateFixtureTests.TwoPeers() };
        await using var engine = new EngineCore(services, Project.CreateDefault("Show"));
        Assert.True(WireGrammar.TryParse("INDEX", out var line, out _));
        var reply = await engine.HandleAsync(line, SessionInfo.Desk("test"), CancellationToken.None);
        Assert.True(reply.Ok, reply.Payload);
        using var doc = JsonDocument.Parse(reply.Payload!);
        var eye = doc.RootElement.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("section").GetString() == "eye");
        Assert.Equal("Eye", eye.GetProperty("hint").GetProperty("title").GetString());
        var rows = eye.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!, r => r);
        Assert.Equal("red", rows["FLEET NEXT"].GetProperty("light").GetString());
        Assert.Equal("3", rows["FLEET NEXT"].GetProperty("state").GetString());
        Assert.Equal("red", rows["FLEET FOCUS machine:c0ffee00"].GetProperty("light").GetString());
        Assert.Equal("amber", rows["FLEET FOCUS machine:9f8e7d6c"].GetProperty("light").GetString());
        Assert.Equal("Test node", rows["FLEET FOCUS self"].GetProperty("label").GetString());
        foreach (var id in rows.Keys)
        {
            Assert.True(WireGrammar.TryParse(id, out var key, out _), id);
            Assert.NotNull(VerbCatalog.Match(key!));
            Assert.Equal("eye", rows[id].GetProperty("parent").GetString());
        }

        // A key's line focuses what it names.
        Assert.True(WireGrammar.TryParse("FLEET FOCUS self", out var self, out _));
        Assert.True((await engine.HandleAsync(self, SessionInfo.Desk("test"), CancellationToken.None)).Ok);
        Assert.Equal("machine:0badc0de", engine.Fleet.View.Focus);
    }
}
