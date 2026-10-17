using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Engine.Tests.Spine;
using PlayMap.Net.Companion;

namespace PlayMap.Engine.Tests.Companion;

/// <summary>tests/fixtures/state/companion.json is STATE's controllers' keys from a real scenario — a paired Companion on the web port
/// that said what it shows, one connected but not paired, an event raised — and companion.cards.json the descriptor the desk renders
/// them with. This test writes the fixture (PLAYMAP_WRITE_FIXTURES=1) and otherwise checks the committed file is what the engine writes
/// now (the index signature, which follows the show's random ids, set to a fixed value), and that every path the cards read is there.</summary>
public sealed class CompanionStateFixtureTests
{
    private static string FixturePath(string name) => Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", name);

    private static string FixtureOf(string stateJson)
    {
        var root = JsonNode.Parse(stateJson)!.AsObject();
        var index = root["project"]!["index"]!.GetValue<string>();
        Assert.Matches("^[0-9a-f]{16}$", index);
        var fixture = new JsonObject
        {
            ["clients"] = root["clients"]!.DeepClone(),
            ["events"] = root["events"]!.DeepClone(),
            ["project"] = new JsonObject { ["index"] = "9f1c2ab07d3e4410" },
        };
        return fixture.ToJsonString(FixtureJson.Indented) + "\n";
    }

    [Fact]
    public async Task TodaysCompanionStateIsTheCommittedFixtureAndItsCardsReadIt()
    {
        await using var rig = new CompanionRig();
        var registry = (IClientRegistry)rig.Engine;
        registry.Hello(CompanionRig.Paired with { SessionId = "l1" }, "ws");
        await rig.Ok("PRESENCE {\"kind\":\"companion\",\"module\":\"1.0.0\",\"live\":\"projectors\",\"page\":\"1/3\",\"thumbs\":12}",
            CompanionRig.Paired with { SessionId = "l1" });
        registry.Hello(new Core.Wire.SessionInfo("w2", "companion", "10.0.0.8", false, "Companion:Booth", false), "tcp");
        rig.Engine.RaiseEvent("item.start", rig.Engine.LeaderNowNs, w => { w.WriteString("playlist", "pl00000001"); w.WriteString("item", "it00000001"); });

        var fixture = FixtureOf(rig.Engine.State().Json);
        using (var doc = JsonDocument.Parse(fixture))
        {
            var clients = doc.RootElement.GetProperty("clients");
            Assert.Equal(2, clients.GetArrayLength());
            Assert.True(clients[0].GetProperty("paired").GetBoolean());
            Assert.False(clients[1].GetProperty("paired").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("events").GetProperty("seq").GetInt64() > 0);
        }

        var path = FixturePath("companion.json");
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
            PlayMap.Core.Storage.DurableFile.WriteAllText(path, fixture);
        Assert.Equal((PlayMap.Core.Storage.DurableFile.ReadAllText(path)).ReplaceLineEndings("\n"), fixture);

        // Every path a card reads is in the fixture: its block (each element, for a list card), its fields, its light's conditions.
        using var state = JsonDocument.Parse(fixture);
        using var cards = JsonDocument.Parse(PlayMap.Core.Storage.DurableFile.ReadAllText(FixturePath("companion.cards.json")));
        foreach (var card in cards.RootElement.EnumerateArray())
        {
            var block = state.RootElement.GetProperty(card.GetProperty("path").GetString()!);
            var each = card.TryGetProperty("each", out var e) && e.GetBoolean();
            var element = each ? block[0] : block;
            foreach (var field in card.GetProperty("fields").EnumerateArray())
            {
                var p = field.GetProperty("path").GetString()!;
                Assert.True(Resolve(p.StartsWith('/') ? state.RootElement : element, p.TrimStart('/')), $"{card.GetProperty("title")}: {p}");
            }
            foreach (var rule in card.GetProperty("light").EnumerateArray())
                if (rule.TryGetProperty("if", out var cond))
                    Assert.True(Resolve(element, cond.GetString()!), cond.GetString());
            Assert.All(card.GetProperty("actions").EnumerateArray(), a => Assert.NotNull(Core.Wire.VerbCatalog.Find(a.GetProperty("line").GetString()!)));
        }
    }

    private static bool Resolve(JsonElement from, string path)
    {
        var at = from;
        foreach (var part in path.Split('.'))
        {
            if (at.ValueKind != JsonValueKind.Object || !at.TryGetProperty(part, out at))
                return false;
        }
        return true;
    }
}
