using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Spine;

namespace PlayMap.Engine.Tests.Show;

/// <summary>tests/fixtures/state/show.json is the show's STATE keys from a real scenario — armed with a reason, an area locked,
/// a SAFE plan, a refusal, the master at 80, a panic half way through its fade, a preflight done — and show.cards.json the cards
/// the desk draws from them. This test writes both (PLAYMAP_WRITE_FIXTURES=1) and otherwise checks the committed files are what
/// the engine writes now, with the clock's numbers fixed; every card's path resolves in the fixture and every action line is a
/// line of the catalogue.</summary>
public sealed class ShowStateFixtureTest
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string FixturePath(string name) => Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", name);

    /// <summary>The cards: title, the STATE path, the light (rules in order: "if" a path is set, "from" a path's light word, or a
    /// fixed light), the words (with {path} holes), up to six fields, and the lines its buttons send.</summary>
    private static JsonArray Cards() =>
    [
        new JsonObject
        {
            ["title"] = "Show",
            ["path"] = "show",
            ["light"] = new JsonArray(Rule("if", "panic", "red"), Rule("if", "armed", "green"), new JsonObject { ["light"] = "grey" }),
            ["words"] = "{armed?ARMED by {by}:rehearsal} · MinGo {minGoMs} ms",
            ["fields"] = new JsonArray(Field("Armed", "armed"), Field("By", "by"), Field("Why", "words"), Field("MinGo (ms)", "minGoMs"),
                Field("Refused, last minute", "refused.last60s"), Field("Last refusal", "refused.last")),
            ["actions"] = new JsonArray("SHOW ARM", "SHOW DISARM", "LOCKS", "SHOW SET REMOTE-VIEW-ONLY ON"),
        },
        new JsonObject
        {
            ["title"] = "Panic",
            ["path"] = "show.panic",
            ["light"] = new JsonArray(Rule("if", "hard", "red"), Rule("if", "outcome", "red"), new JsonObject { ["light"] = "grey" }),
            ["words"] = "PANIC {outcome} by {by}",
            ["fields"] = new JsonArray(Field("Outcome", "outcome"), Field("Hard", "hard"), Field("By", "by"), Field("Since", "since"),
                Field("Outputs", "outputs"), Field("Devices", "devices")),
            ["actions"] = new JsonArray("PANIC", "PANIC CLEAR", "PANIC HARD"),
        },
        new JsonObject
        {
            ["title"] = "Master",
            ["path"] = "air.master",
            ["light"] = new JsonArray(new JsonObject { ["light"] = "green" }),
            ["words"] = "master {level}",
            ["fields"] = new JsonArray(Field("Level", "level"), Field("To", "to"), Field("Until (leader ns)", "untilLeaderNs")),
            ["actions"] = new JsonArray("MASTER 100", "MASTER 0 FADE 3000"),
        },
        new JsonObject
        {
            ["title"] = "Locks",
            ["path"] = "show.locks",
            ["light"] = new JsonArray(new JsonObject { ["light"] = "grey" }),
            ["words"] = "{count} locks",
            ["fields"] = new JsonArray(Field("Target", "0.target"), Field("Kind", "0.kind"), Field("By", "0.by"), Field("Why", "0.words"),
                Field("Implicit", "0.implicit")),
            ["actions"] = new JsonArray("LOCKS", "UNLOCK ALL", "LOCK AREA mapping"),
        },
        new JsonObject
        {
            ["title"] = "Preflight",
            ["path"] = "show.preflight",
            ["light"] = new JsonArray(new JsonObject { ["from"] = "light" }),
            ["words"] = "{green} green · {amber} amber · {red} red · show-ready {ready}",
            ["fields"] = new JsonArray(Field("Show-ready", "ready"), Field("First open", "firstOpen"), Field("Green", "green"),
                Field("Amber", "amber"), Field("Red", "red"), Field("Running", "running")),
            ["actions"] = new JsonArray("PREFLIGHT", "PREFLIGHT REPORT"),
        },
    ];

    private static JsonObject Rule(string kind, string path, string light) => new() { [kind] = path, ["light"] = light };

    private static JsonObject Field(string label, string path) => new() { ["label"] = label, ["path"] = path };

    /// <summary>The show's keys of STATE with the clock's numbers fixed.</summary>
    private static string Fixture(JsonElement state)
    {
        var show = JsonNode.Parse(state.GetProperty("show").GetRawText())!.AsObject();
        var air = JsonNode.Parse(state.GetProperty("air").GetRawText())!.AsObject();
        show["since"] = 1_760_000_000_000;
        show["panic"]!["since"] = 1_760_000_000_000;
        foreach (var l in show["locks"]!.AsArray())
            l!["at"] = 1_760_000_000_000;
        var preflight = show["preflight"]!.AsObject();
        preflight["id"] = "pf_fixture";
        preflight["at"] = 1_760_000_000_000;
        air["panic"]!["since"] = 1_760_000_000_000;
        var root = new JsonObject
        {
            ["air"] = new JsonObject { ["master"] = air["master"]!.DeepClone(), ["panic"] = air["panic"]!.DeepClone() },
            ["show"] = show,
        };
        return root.ToJsonString(Indented) + "\n";
    }

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

    [Fact]
    public async Task TheShowsStateIsTheCommittedFixtureAndItsCardsReadIt()
    {
        await using var rig = new ShowRig();
        await rig.Ok("SHOW SET EXPECT-ARMED ON");
        await rig.Ok("PANIC SET op_1 SAFE FADE 2000 SAFE color:#000000");
        await rig.Ok("LOCK AREA content show night");
        await rig.Ok("SHOW ARM doors at eight");
        await rig.Refused("SET op_1 width 1280", ShowRig.Companion);
        await rig.Ok("MASTER 80");
        await rig.Ok("PANIC BLACK FADE 1000");
        rig.Advance(500);
        var id = JsonDocument.Parse(await rig.Ok("PREFLIGHT")).RootElement.GetProperty("id").GetString();
        Assert.True(await ShowRig.Eventually(() => rig.Show().GetProperty("preflight").GetProperty("running").ValueKind == JsonValueKind.False));
        Assert.Equal(id, rig.Show().GetProperty("preflight").GetProperty("id").GetString());

        var state = rig.State();
        var show = state.GetProperty("show");
        Assert.True(show.GetProperty("armed").GetBoolean());
        Assert.Equal(0.5, show.GetProperty("panic").GetProperty("outputs")[0].GetProperty("level").GetDouble(), 1e-3);
        Assert.Equal(0.8, state.GetProperty("air").GetProperty("master").GetProperty("level").GetDouble(), 1e-3);
        Assert.Equal(1, show.GetProperty("refused").GetProperty("last60s").GetInt32());

        var fixture = Fixture(state);
        var cards = Cards().ToJsonString(Indented) + "\n";
        var write = Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1";
        if (write)
        {
            await File.WriteAllTextAsync(FixturePath("show.json"), fixture);
            await File.WriteAllTextAsync(FixturePath("show.cards.json"), cards);
        }
        Assert.Equal((await File.ReadAllTextAsync(FixturePath("show.json"))).ReplaceLineEndings("\n"), fixture);
        Assert.Equal((await File.ReadAllTextAsync(FixturePath("show.cards.json"))).ReplaceLineEndings("\n"), cards);

        var root = JsonNode.Parse(fixture);
        foreach (var card in Cards())
        {
            var at = At(root, card!["path"]!.GetValue<string>());
            Assert.NotNull(at);
            foreach (var field in card["fields"]!.AsArray())
                Assert.True(at is JsonObject or JsonArray && (At(at, field!["path"]!.GetValue<string>()) is not null
                    || at.AsObject().ContainsKey(field["path"]!.GetValue<string>())), $"{card["title"]}: {field!["path"]}");
            foreach (var action in card["actions"]!.AsArray())
            {
                Assert.True(WireGrammar.TryParse(action!.GetValue<string>(), out var line, out _));
                Assert.NotNull(VerbCatalog.Match(line!));
            }
        }
    }
}
