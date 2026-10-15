using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Spine;

namespace PlayMap.Engine.Tests.Wall;

/// <summary>tests/fixtures/state/wall.json is the wall's STATE keys from a real scenario — two projectors solved from their pins, one
/// re-solved from pins dragged roughly and staged under the gate, a display lit as a third projector with a quad drawn on it, a
/// handle selected and a cursor up — and wall.cards.json the cards the desk draws from them. This test writes both
/// (PLAYMAP_WRITE_FIXTURES=1) and otherwise checks the committed files are what the engine writes now, with ids and clock numbers
/// made stable; every card's path and fields resolve in the fixture and every action is a line of the catalogue.</summary>
public sealed partial class WallStateFixtureTest
{
    private static readonly JsonSerializerOptions Indented = FixtureJson.Indented;

    private static string FixturePath(string name) => Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", name);

    private static JsonArray Cards() =>
    [
        new JsonObject
        {
            ["title"] = "Calibration",
            ["path"] = "calibration.targets.0",
            ["light"] = new JsonArray(new JsonObject { ["from"] = "light" }),
            ["words"] = "{name}: {words}",
            ["fields"] = new JsonArray(Field("Grade", "grade"), Field("Score", "score"), Field("Worst", "worst"), Field("Current", "snapshot"),
                Field("Fallback", "fallback"), Field("Staged", "staged")),
            ["actions"] = new JsonArray("CALIBRATION RESOLVE $(id)", "CALIBRATION ACCEPT $(id)", "CALIBRATION FALLBACK $(id)", "CALIBRATION HISTORY $(id)"),
        },
        new JsonObject
        {
            ["title"] = "Calibration task",
            ["path"] = "calibration.task",
            ["light"] = new JsonArray(Rule("if", "state", "amber"), new JsonObject { ["light"] = "grey" }),
            ["words"] = "{kind} {state} · {words}",
            ["fields"] = new JsonArray(Field("Task", "id"), Field("Kind", "kind"), Field("State", "state"), Field("Projector", "projector"),
                Field("Progress", "progress")),
            ["actions"] = new JsonArray("CALIBRATION CANCEL", "CALIBRATION STATUS"),
        },
        new JsonObject
        {
            ["title"] = "Edit overlay",
            ["path"] = "air.overlay",
            ["light"] = new JsonArray(Rule("if", "on", "amber"), new JsonObject { ["light"] = "grey" }),
            ["words"] = "{on?overlay on:overlay off} · {cursors} cursors",
            ["fields"] = new JsonArray(Field("On", "on"), Field("Outputs", "outputs"), Field("Layers", "layers"), Field("Handle", "handle"),
                Field("Cursors", "cursors"), Field("Armed", "armed")),
            ["actions"] = new JsonArray("OVERLAY ON", "OVERLAY OFF", "OVERLAY KEEP", "HANDLE NEXT", "HANDLE PREV"),
        },
        new JsonObject
        {
            ["title"] = "First map",
            ["path"] = "onboarding",
            ["light"] = new JsonArray(Rule("if", "contentAt", "green"), Rule("if", "litAt", "amber"), new JsonObject { ["light"] = "grey" }),
            ["words"] = "{stage} · {seconds} s",
            ["fields"] = new JsonArray(Field("Stage", "stage"), Field("Lit", "litAt"), Field("First quad", "quadAt"), Field("First content", "contentAt"),
                Field("Seconds", "seconds")),
            ["actions"] = new JsonArray("LIGHT WALL #1"),
        },
    ];

    private static JsonObject Rule(string kind, string path, string light) => new() { [kind] = path, ["light"] = light };

    private static JsonObject Field(string label, string path) => new() { ["label"] = label, ["path"] = path };

    [GeneratedRegex(@"\b(cs|kt|sf|op|pj)[0-9a-f]{8}\b")]
    private static partial Regex RandomIds();

    /// <summary>The wall's keys of STATE, ids random per run numbered in order of appearance and the clock's numbers fixed.</summary>
    private static string Fixture(JsonElement state)
    {
        var projectors = new JsonArray();
        foreach (var pj in state.GetProperty("stage").GetProperty("projectors").EnumerateArray())
            projectors.Add(new JsonObject { ["id"] = pj.GetProperty("id").GetString(), ["grade"] = pj.GetProperty("grade").GetString(), ["quads"] = pj.GetProperty("quads").GetInt32() });
        var onboarding = JsonNode.Parse(state.GetProperty("onboarding").GetRawText())!.AsObject();
        foreach (var key in new[] { "startedAt", "litAt", "quadAt", "contentAt" })
            if (onboarding[key] is not null)
                onboarding[key] = 1_760_000_000_000;
        onboarding["seconds"] = onboarding["contentAt"] is null && onboarding["litAt"] is null ? -1 : 0;
        var root = new JsonObject
        {
            ["air"] = new JsonObject { ["overlay"] = JsonNode.Parse(state.GetProperty("air").GetProperty("overlay").GetRawText()) },
            ["calibration"] = JsonNode.Parse(state.GetProperty("calibration").GetRawText()),
            ["onboarding"] = onboarding,
            ["stage"] = new JsonObject { ["projectors"] = projectors },
        };
        var text = root.ToJsonString(Indented);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        text = RandomIds().Replace(text, m =>
        {
            if (!seen.TryGetValue(m.Value, out var stable))
            {
                stable = $"{m.Groups[1].Value}_fixture{seen.Count(kv => kv.Key.StartsWith(m.Groups[1].Value, StringComparison.Ordinal)) + 1}";
                seen[m.Value] = stable;
            }
            return stable;
        });
        return text + "\n";
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
    public async Task TheWallsStateIsTheCommittedFixtureAndItsCardsReadIt()
    {
        await using var rig = new WallRig(WallRig.ProjectorsWithPins(2, noisePx: 0.1));
        await rig.Ok("PROJECTOR SOLVE pj_test1");
        await rig.Ok("PROJECTOR SOLVE pj_test2");
        var r = await rig.Engine.EditAsync(WallRig.Desk, "drag pins roughly", p => p.WithStage(st => st with
        {
            Projectors = st.Projectors.Update("pj_test2", pj => pj with
            {
                Pins = [.. pj.Pins.Select((pin, i) => pin with { Pixel = pin.Pixel + new Core.Numerics.Vec2d(i % 2 == 0 ? 2.5 : -2.5, i % 3 == 0 ? 1.5 : -1.0) })],
            }),
        }));
        Assert.True(r.Changed);
        var staged = WallRig.Json(await rig.Ok("CALIBRATION RESOLVE pj_test2")).GetProperty("projectors")[0];
        Assert.True(staged.GetProperty("staged").GetBoolean(), staged.ToString());
        var lit = WallRig.Json(await rig.Ok("LIGHT WALL #2 AS Stage left"));
        var projector = lit.GetProperty("projector").GetString()!;
        var output = lit.GetProperty("output").GetString()!;
        var quad = WallRig.Json(await rig.Ok($"SURFACE QUAD {projector} 200 150 1700 120 1500 1000 260 1050 AS Door")).GetProperty("id").GetString()!;
        await rig.Ok($"HANDLE SELECT {quad} TL");
        await rig.Ok($"CURSOR {output} 0.5 0.4 TRAIL 0.1 0.12");

        var state = rig.State();
        Assert.Equal(3, state.GetProperty("calibration").GetProperty("targets").GetArrayLength());
        Assert.Equal("amber", state.GetProperty("calibration").GetProperty("targets")[1].GetProperty("light").GetString());
        Assert.Equal("done", state.GetProperty("calibration").GetProperty("task").GetProperty("state").GetString());
        Assert.Equal("quad", state.GetProperty("onboarding").GetProperty("stage").GetString());

        var fixture = Fixture(state);
        var cards = Cards().ToJsonString(Indented) + "\n";
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
        {
            await File.WriteAllTextAsync(FixturePath("wall.json"), fixture);
            await File.WriteAllTextAsync(FixturePath("wall.cards.json"), cards);
        }
        Assert.Equal((await File.ReadAllTextAsync(FixturePath("wall.json"))).ReplaceLineEndings("\n"), fixture);
        Assert.Equal((await File.ReadAllTextAsync(FixturePath("wall.cards.json"))).ReplaceLineEndings("\n"), cards);

        var root = JsonNode.Parse(fixture);
        foreach (var card in Cards())
        {
            var at = At(root, card!["path"]!.GetValue<string>());
            Assert.NotNull(at);
            foreach (var field in card["fields"]!.AsArray())
                Assert.True(at.AsObject().ContainsKey(field!["path"]!.GetValue<string>()), $"{card["title"]}: {field["path"]}");
            foreach (var action in card["actions"]!.AsArray())
            {
                Assert.True(WireGrammar.TryParse(action!.GetValue<string>().Replace("$(id)", "pj_test1", StringComparison.Ordinal), out var line, out _));
                Assert.NotNull(VerbCatalog.Match(line!));
            }
        }
    }
}
