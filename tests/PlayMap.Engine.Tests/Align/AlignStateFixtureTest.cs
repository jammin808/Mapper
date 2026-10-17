using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Spine;

namespace PlayMap.Engine.Tests.Align;

/// <summary>tests/fixtures/state/align.json is the alignment's STATE keys from a real scenario — two outputs side by side through the
/// simulated camera, a first run accepted, a second (FIT CORNERS) staged and waiting — and align.cards.json the cards the desk draws
/// from them. This test writes both (PLAYMAP_WRITE_FIXTURES=1) and otherwise checks the committed files are what the engine writes now,
/// with ids made stable; every card's path and fields resolve in the fixture and every action is a line of the catalogue.</summary>
public sealed partial class AlignStateFixtureTest
{
    private static readonly JsonSerializerOptions Indented = FixtureJson.Indented;

    private static string FixturePath(string name) => Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", name);

    private static JsonArray Cards() =>
    [
        new JsonObject
        {
            ["title"] = "Alignment run",
            ["path"] = "align.run",
            ["light"] = new JsonArray(new JsonObject { ["from"] = "light" }),
            ["words"] = "{state} · {step} · {words}",
            ["fields"] = new JsonArray(Field("Run", "id"), Field("Group", "group"), Field("State", "state"), Field("Step", "step"), Field("Progress", "progress"),
                Field("Time left (s)", "etaS"), Field("Score", "score")),
            ["actions"] = new JsonArray("ALIGN ACCEPT $(id)", "ALIGN CANCEL", "ALIGN STATUS"),
        },
        new JsonObject
        {
            ["title"] = "Alignment",
            ["path"] = "align.groups.0",
            ["light"] = new JsonArray(new JsonObject { ["from"] = "light" }),
            ["words"] = "{name}: {words}",
            ["fields"] = new JsonArray(Field("Grade", "grade"), Field("Score", "score"), Field("Outputs", "outputs"), Field("Camera", "camera"), Field("Canvas", "canvas"),
                Field("Fit", "fit"), Field("Last run", "lastRun"), Field("Staged", "staged")),
            ["actions"] = new JsonArray("ALIGN RUN $(id)", "ALIGN ACCEPT $(id)", "ALIGN REVERT $(id)", "ALIGN CLEAR $(id)"),
        },
        new JsonObject
        {
            ["title"] = "Measured correction",
            ["path"] = "outputs.0.measured",
            ["light"] = new JsonArray(new JsonObject { ["from"] = "light" }),
            ["words"] = "{run} · warp {residualPx} px · γ {gamma} · files {files}",
            ["fields"] = new JsonArray(Field("Run", "run"), Field("Warp residual (px)", "residualPx"), Field("γ", "gamma"), Field("Blend", "blend"),
                Field("Files", "files")),
            ["actions"] = new JsonArray("ALIGN STATUS"),
        },
        new JsonObject
        {
            ["title"] = "Projector's own processing",
            ["path"] = "align.devices",
            ["each"] = true,
            ["label"] = "id",
            ["light"] = "light",
            ["words"] = "words",
            ["fields"] = new JsonArray(Field("Outputs", "outputs"), Field("Read", "read"), Field("Own geometry", "geometry"), Field("Own blend", "blend"),
                Field("Fix", "fix")),
            ["actions"] = new JsonArray("DEVICE NATIVE $(id)"),
        },
    ];

    private static JsonObject Field(string label, string path) => new() { ["label"] = label, ["path"] = path };

    [GeneratedRegex(@"\b(ar|al|cs|cv|op)[0-9a-f]{8}\b")]
    private static partial Regex RandomIds();

    /// <summary>The alignment's keys of STATE, ids random per run numbered in order of appearance.</summary>
    private static string Fixture(JsonElement state)
    {
        var outputs = new JsonArray();
        foreach (var o in state.GetProperty("outputs").EnumerateArray())
            outputs.Add(new JsonObject { ["id"] = o.GetProperty("id").GetString(), ["measured"] = JsonNode.Parse(o.GetProperty("measured").GetRawText()) });
        var root = new JsonObject
        {
            ["align"] = JsonNode.Parse(state.GetProperty("align").GetRawText()),
            ["outputs"] = outputs,
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
    public async Task TheAlignmentsStateIsTheCommittedFixtureAndItsCardsReadIt()
    {
        await using var rig = new AlignRig(AlignRig.Project(2, "flat-screen:tile2x1:o20"));
        var group = await rig.NewGroup();
        await rig.RunToEnd(group);
        await rig.Ok($"ALIGN ACCEPT {group}");
        await rig.Ok($"ALIGN CORNERS {group} 0.2 0.3 0.8 0.3 0.8 0.62 0.2 0.62");
        await rig.RunToEnd(group);
        await rig.Engine.AlignFiles.Settled;

        var state = rig.State();
        Assert.Equal("staged", state.GetProperty("align").GetProperty("run").GetProperty("state").GetString());
        Assert.Equal("amber", state.GetProperty("align").GetProperty("groups")[0].GetProperty("light").GetString());
        Assert.Equal("ok", state.GetProperty("outputs")[0].GetProperty("measured").GetProperty("files").GetString());

        var fixture = Fixture(state);
        var cards = Cards().ToJsonString(Indented) + "\n";
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
        {
            PlayMap.Core.Storage.DurableFile.WriteAllText(FixturePath("align.json"), fixture);
            PlayMap.Core.Storage.DurableFile.WriteAllText(FixturePath("align.cards.json"), cards);
        }
        Assert.Equal((PlayMap.Core.Storage.DurableFile.ReadAllText(FixturePath("align.json"))).ReplaceLineEndings("\n"), fixture);
        Assert.Equal((PlayMap.Core.Storage.DurableFile.ReadAllText(FixturePath("align.cards.json"))).ReplaceLineEndings("\n"), cards);

        var root = JsonNode.Parse(fixture);
        foreach (var card in Cards())
        {
            var at = At(root, card!["path"]!.GetValue<string>());
            Assert.NotNull(at);
            // A card per element (a guarded device) reads each element's fields; a block reads its own.
            JsonObject[] blocks = card["each"]?.GetValue<bool>() == true ? [.. at.AsArray().Select(e => e!.AsObject())] : [at.AsObject()];
            foreach (var block in blocks)
            {
                foreach (var field in card["fields"]!.AsArray())
                    Assert.True(block.ContainsKey(field!["path"]!.GetValue<string>()), $"{card["title"]}: {field["path"]}");
            }
            foreach (var action in card["actions"]!.AsArray())
            {
                Assert.True(WireGrammar.TryParse(action!.GetValue<string>().Replace("$(id)", "al_fixture1", StringComparison.Ordinal), out var line, out _));
                Assert.NotNull(VerbCatalog.Match(line!));
            }
        }
    }
}
