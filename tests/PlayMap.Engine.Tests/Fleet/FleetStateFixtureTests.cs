using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Spine;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>tests/fixtures/state/fleet.json is the fleet's STATE keys from a real scenario — this node, FOH-B its backup dropping frames,
/// FOH-C gone quiet, the board focused on a problem through the PROBLEMS lens — and fleet.cards.json the cards the desk draws from
/// them. This test writes both (PLAYMAP_WRITE_FIXTURES=1) and otherwise checks the committed files are what the engine writes now;
/// every card's path resolves in the fixture and every action line is a line of the catalogue. It also holds the board's golden
/// as FLEET and EYE STATUS answer it, in Patterns' EyeJson shape.</summary>
public sealed class FleetStateFixtureTests
{
    private static readonly JsonSerializerOptions Indented = FixtureJson.Indented;

    private static string FixturePath(string name) => Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", name);

    private static string GoldenPath() => Path.Combine(NodeExe.RepoRoot(), "tests", "PlayMap.Engine.Tests", "Fleet", "Golden", "eye.json");

    private static JsonArray Cards() =>
    [
        new JsonObject
        {
            ["title"] = "Fleet",
            ["path"] = "fleet",
            ["light"] = new JsonArray(new JsonObject { ["from"] = "light" }),
            ["words"] = "{headline}",
            ["fields"] = new JsonArray(Field("Role", "role"), Field("Epoch", "epoch"), Field("State", "state"), Field("Backs up", "of"),
                Field("Succession", "succession"), Field("Machines", "nodes")),
            ["actions"] = new JsonArray("FLEET", "FLEET LENS PROBLEMS", "FLEET NEXT", "HEALTH"),
        },
        new JsonObject
        {
            ["title"] = "Machines",
            ["path"] = "fleet.nodes",
            ["light"] = new JsonArray(new JsonObject { ["from"] = "0.light" }),
            ["words"] = "{count} machines",
            ["fields"] = new JsonArray(Field("Name", "0.name"), Field("Light", "0.light"), Field("Headline", "0.headline"), Field("Heard", "0.heard"),
                Field("Role", "0.role"), Field("Build", "0.build")),
            ["actions"] = new JsonArray("FLEET FOCUS $(id)", "FLEET RESET"),
        },
        new JsonObject
        {
            ["title"] = "Eye",
            ["path"] = "eye",
            ["light"] = new JsonArray(new JsonObject { ["from"] = "worstLight" }),
            ["words"] = "{headline}",
            ["fields"] = new JsonArray(Field("Worst", "worstWords"), Field("Red", "counts.red"), Field("Amber", "counts.amber"),
                Field("Things", "counts.things"), Field("Lens", "lens"), Field("Focus", "focus")),
            ["actions"] = new JsonArray("FLEET NEXT", "FLEET PREV", "FLEET LENS PROBLEMS", "FLEET LENS ALL", "FLEET RESET"),
        },
    ];

    private static JsonObject Field(string label, string path) => new() { ["label"] = label, ["path"] = path };

    private static JsonNode? At(JsonNode? node, string path)
    {
        foreach (var part in path.Split('.'))
        {
            node = node switch
            {
                JsonObject o => o[part],
                JsonArray a when int.TryParse(part, out var i) && i < a.Count => a[i],
                _ => null,
            };
        }
        return node;
    }

    private static async Task<string> Run(EngineCore engine, string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        var reply = await engine.HandleAsync(parsed, SessionInfo.Desk("test"), CancellationToken.None);
        Assert.True(reply.Ok, $"{line} → {reply.Payload}");
        return reply.Payload ?? "";
    }

    internal static FleetPreflightTests.TestFleet TwoPeers()
    {
        var three = FleetFixtures.ThreeNodes();
        return new FleetPreflightTests.TestFleet(
            new FleetPeer("9f8e7d6c", "FOH-B", "10.0.0.22", three[1].Health, FleetHeard.Live, "subscribed over TLS, certificate pinned") { Pin = "pinned", SilentS = 0.4 },
            new FleetPeer("c0ffee00", "FOH-C", "10.0.0.23", three[2].Health, FleetHeard.Gone, "FOH-C's health feed ended") { Pin = "pinned", SilentS = 7 });
    }

    [Fact]
    public async Task TheFleetsStateIsTheCommittedFixtureAndItsCardsReadIt()
    {
        var services = HealthTests.Services(new ManualTime(), sampler: new HealthTests.FakeSampler()) with { FleetPeers = TwoPeers() };
        await using var engine = new EngineCore(services, Project.CreateDefault("Show"));
        Assert.Equal("{\"focus\":\"machine:c0ffee00\",\"lens\":\"all\"}", await Run(engine, "FLEET NEXT"));
        await Run(engine, "EYE LENS PROBLEMS");

        // EYE STATUS and FLEET answer the same board; it is the golden.
        var eye = await Run(engine, "EYE STATUS");
        Assert.Equal(eye, await Run(engine, "FLEET"));
        using (var doc = JsonDocument.Parse(eye))
        {
            var golden = JsonSerializer.Serialize(doc.RootElement, Indented) + "\n";
            if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(GoldenPath())!);
                PlayMap.Core.Storage.DurableFile.WriteAllText(GoldenPath(), golden);
            }
            Assert.Equal((PlayMap.Core.Storage.DurableFile.ReadAllText(GoldenPath())).ReplaceLineEndings("\n"), golden);
        }

        using var stateDoc = JsonDocument.Parse(engine.State().Json);
        var state = stateDoc.RootElement;
        var fleet = state.GetProperty("fleet");
        Assert.Equal("solo", fleet.GetProperty("role").GetString());
        Assert.Equal(2, fleet.GetProperty("nodes").GetArrayLength());
        Assert.Equal("red", fleet.GetProperty("light").GetString());
        Assert.Equal("problems", state.GetProperty("eye").GetProperty("lens").GetString());
        Assert.Equal("machine:c0ffee00", state.GetProperty("eye").GetProperty("focus").GetString());
        Assert.Equal("machine:c0ffee00", state.GetProperty("eye").GetProperty("worst").GetString());
        // The fleet's keys follow show's and come before decode's (SPINE §9).
        var keys = state.EnumerateObject().Select(p => p.Name).ToList();
        Assert.True(keys.IndexOf("show") < keys.IndexOf("fleet") && keys.IndexOf("fleet") + 1 == keys.IndexOf("eye") && keys.IndexOf("eye") < keys.IndexOf("decode"));

        var root = new JsonObject
        {
            ["fleet"] = JsonNode.Parse(fleet.GetRawText()),
            ["eye"] = JsonNode.Parse(state.GetProperty("eye").GetRawText()),
        };
        var fixture = root.ToJsonString(Indented) + "\n";
        var cards = Cards().ToJsonString(Indented) + "\n";
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
        {
            PlayMap.Core.Storage.DurableFile.WriteAllText(FixturePath("fleet.json"), fixture);
            PlayMap.Core.Storage.DurableFile.WriteAllText(FixturePath("fleet.cards.json"), cards);
        }
        Assert.Equal((PlayMap.Core.Storage.DurableFile.ReadAllText(FixturePath("fleet.json"))).ReplaceLineEndings("\n"), fixture);
        Assert.Equal((PlayMap.Core.Storage.DurableFile.ReadAllText(FixturePath("fleet.cards.json"))).ReplaceLineEndings("\n"), cards);

        var parsed = JsonNode.Parse(fixture);
        foreach (var card in Cards())
        {
            var at = At(parsed, card!["path"]!.GetValue<string>());
            Assert.NotNull(at);
            foreach (var field in card["fields"]!.AsArray())
                Assert.True(At(at, field!["path"]!.GetValue<string>()) is not null
                    || (at is JsonObject o && o.ContainsKey(field["path"]!.GetValue<string>())), $"{card["title"]}: {field!["path"]}");
            foreach (var action in card["actions"]!.AsArray())
            {
                Assert.True(WireGrammar.TryParse(action!.GetValue<string>(), out var line, out _));
                Assert.NotNull(VerbCatalog.Match(line!));
            }
        }
    }

    [Fact]
    public async Task TheViewVerbsRefuseWhatWouldChangeNothing()
    {
        var services = HealthTests.Services(new ManualTime()) with { FleetPeers = TwoPeers() };
        await using var engine = new EngineCore(services, Project.CreateDefault("Show"));
        async Task<CommandReply> Line(string text)
        {
            Assert.True(WireGrammar.TryParse(text, out var parsed, out _));
            return await engine.HandleAsync(parsed, SessionInfo.Desk("test"), CancellationToken.None);
        }
        var reset = await Line("FLEET RESET");
        Assert.False(reset.Ok);
        Assert.Contains("own view already", reset.Payload, StringComparison.Ordinal);
        Assert.False((await Line("FLEET LENS ALL")).Ok);
        Assert.False((await Line("FLEET LENS SIDEWAYS")).Ok);
        Assert.False((await Line("FLEET FOCUS nothing-at-all")).Ok);
        Assert.True((await Line("FLEET FOCUS FOH-B")).Ok);
        Assert.Contains("already focused", (await Line("EYE FOCUS 9f8e7d6c")).Payload, StringComparison.Ordinal);
        Assert.Equal("{\"focus\":\"\",\"lens\":\"all\"}", (await Line("FLEET RESET")).Payload);
        // A view: no journal row, no project revision.
        Assert.DoesNotContain(engine.Journal.Recent(50), r => r.Text.Contains("FLEET", StringComparison.Ordinal));
        Assert.Equal(1, engine.Store.Current.Revision);
        // Paired only: an unpaired session is refused before the view moves.
        Assert.True(WireGrammar.TryParse("FLEET NEXT", out var next, out _));
        var unpaired = await engine.HandleAsync(next, new SessionInfo("w9", "wire", "10.0.0.9", false, "Somebody", false), CancellationToken.None);
        Assert.False(unpaired.Ok);
        Assert.Equal(("", Core.Fleet.FleetLens.All), engine.Fleet.View);
    }
}
