using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Effects;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Optimise;
using PlayMap.Engine.ShaderWorker;
using PlayMap.Engine.Tests.Show;
using PlayMap.Engine.Tests.Spine;
using PlayMap.Render.Effects;

namespace PlayMap.Engine.Tests.Shaders;

/// <summary>wp3-shaders' STATE fixture (tests/fixtures/state/shaders.json: fx.compile with its worker, fx.quarantine) from a node
/// composed with the shader parts, its worker not yet started and one kind quarantined that a slot of the show draws; and its card
/// descriptors (shaders.cards.json), every path of which reads a value of the fixture and every action of which is a catalogue line.
/// PLAYMAP_WRITE_FIXTURES=1 writes both.</summary>
public sealed class ShadersFixtureTests
{
    private static string Folder => Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state");

    private static JsonArray Cards() =>
    [
        new JsonObject
        {
            ["title"] = "Shader worker",
            ["path"] = "fx.compile.worker",
            // A native that did not load fails every compile at its stage, whether the worker runs or not.
            ["light"] = new JsonArray(new JsonObject { ["if"] = "missing", ["light"] = "amber" }, new JsonObject { ["if"] = "running", ["light"] = "green" },
                new JsonObject { ["light"] = "grey" }),
            ["words"] = "words",
            ["fields"] = new JsonArray(
                new JsonObject { ["label"] = "Running", ["path"] = "running" },
                new JsonObject { ["label"] = "Restarts", ["path"] = "restarts" },
                new JsonObject { ["label"] = "Compiled", ["path"] = "compiled" },
                new JsonObject { ["label"] = "Failed", ["path"] = "failed" },
                new JsonObject { ["label"] = "Last compile (ms)", ["path"] = "lastMs" }),
            ["actions"] = new JsonArray(new JsonObject { ["label"] = "Kinds", ["line"] = "FX KINDS" }),
        },
        new JsonObject
        {
            ["title"] = "Quarantined effect",
            ["path"] = "fx.quarantine[]",
            ["label"] = "kind",
            ["light"] = new JsonArray(new JsonObject { ["light"] = "red" }),
            ["words"] = "words",
            ["fields"] = new JsonArray(
                new JsonObject { ["label"] = "Kind", ["path"] = "kind" },
                new JsonObject { ["label"] = "Reason", ["path"] = "reason" },
                new JsonObject { ["label"] = "Slots", ["path"] = "slots.length" },
                new JsonObject { ["label"] = "Build", ["path"] = "build" }),
            ["actions"] = new JsonArray(new JsonObject { ["label"] = "Release", ["line"] = "FX RELEASE $(hash)" }),
        },
    ];

    [Fact]
    public async Task TodaysShadersStateIsTheCommittedFixtureAndItsCardsReadIt()
    {
        var quarantine = new ShaderQuarantine();
        await using var worker = new ShaderWorkerHost(new ShaderWorkerOptions { Worker = WorkerLaunch.Dll(NodeExe.Locate()) }, quarantine,
            NullLogger.Instance);
        // An ISF file the show carries, written here: its hash is the same on every platform (a built-in's file is checked out
        // with the platform's line ends, and its hash with them).
        const string slot = "fx00000001";
        const string wobble = "/*{ \"DESCRIPTION\": \"Wobble\", \"INPUTS\": [ { \"NAME\": \"inputImage\", \"TYPE\": \"image\" } ] }*/\n"
            + "void main() { gl_FragColor = IMG_NORM_PIXEL(inputImage, isf_FragNormCoord + vec2(sin(TIME) * 0.01, 0.0)); }\n";
        var asset = new FxAsset("fa00000001", "Wobble", FxFormat.Isf, "wobble.fs", FxCatalog.ContentHash(wobble, null)) { Source = wobble };
        var show = ShowRig.OneScreen();
        show = show with
        {
            FxAssets = [asset],
            Outputs = [show.Outputs[0] with { Fx = new FxChain([new FxSlot(slot, FxCatalog.AssetKindId(asset.Id), "", ParamSet.Empty, false, 1, FxBlend.Normal, 1, 0)]) }],
        };
        var hash = FxCatalog.For(show.FxAssets).Source(FxCatalog.AssetKindId(asset.Id))!.Hash;
        await using var rig = new ShowRig(show, s => ShaderParts.Compose(s, quarantine, worker));
        ShaderParts.Attach(rig.Engine);
        Assert.True(quarantine.Add(new QuarantineEntry(hash, "Wobble", QuarantineReason.DeviceRemoved,
            "the GPU stopped inside this effect's pass on Output 1", 1_760_000_000_000, "0.2.0+17.abc1234")).Applied);

        var fx = JsonNode.Parse(rig.Engine.State().Json)!["fx"]!;
        var fixture = new JsonObject
        {
            ["fx"] = new JsonObject { ["compile"] = fx["compile"]!.DeepClone(), ["quarantine"] = fx["quarantine"]!.DeepClone() },
        }.ToJsonString(FixtureJson.Indented) + "\n";
        var cards = Cards().ToJsonString(FixtureJson.Indented) + "\n";
        var statePath = Path.Combine(Folder, "shaders.json");
        var cardsPath = Path.Combine(Folder, "shaders.cards.json");
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
        {
            PlayMap.Core.Storage.DurableFile.WriteAllText(statePath, fixture);
            PlayMap.Core.Storage.DurableFile.WriteAllText(cardsPath, cards);
        }
        Assert.Equal(PlayMap.Core.Storage.DurableFile.ReadAllText(statePath).ReplaceLineEndings("\n"), fixture);
        Assert.Equal(PlayMap.Core.Storage.DurableFile.ReadAllText(cardsPath).ReplaceLineEndings("\n"), cards);

        var state = JsonNode.Parse(fixture)!;
        Assert.Equal(slot, state["fx"]!["quarantine"]![0]!["slots"]![0]!.GetValue<string>());
        Assert.False(state["fx"]!["compile"]!["worker"]!["running"]!.GetValue<bool>());
        Assert.Equal(0, state["fx"]!["compile"]!["worker"]!["missing"]!.GetValue<int>());
        foreach (var card in Cards().Select(c => c!.AsObject()))
        {
            var path = card["path"]!.GetValue<string>();
            var each = path.EndsWith("[]", StringComparison.Ordinal);
            var bases = Resolve(state, each ? path[..^2] : path).SelectMany(b => each && b is JsonArray a ? a.AsEnumerable() : [b]).ToList();
            Assert.NotEmpty(bases);
            foreach (var field in card["fields"]!.AsArray())
                Assert.All(bases, b => Assert.NotEmpty(Resolve(b, field!["path"]!.GetValue<string>())));
            foreach (var rule in card["light"]!.AsArray().Where(r => r!["if"] is not null))
                Assert.All(bases, b => Assert.NotEmpty(Resolve(b, rule!["if"]!.GetValue<string>())));
            foreach (var action in card["actions"]!.AsArray())
            {
                var line = action!["line"]!.GetValue<string>().Replace("$(hash)", new string('0', 64), StringComparison.Ordinal);
                Assert.True(WireGrammar.TryParse(line, out var parsed, out _) && VerbCatalog.Match(parsed) is not null, line);
            }
        }
    }

    /// <summary>The values at a dotted path ("fx.compile.worker", "slots.length").</summary>
    private static IEnumerable<JsonNode?> Resolve(JsonNode? node, string path)
    {
        IEnumerable<JsonNode?> current = [node];
        foreach (var name in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.SelectMany(n => n switch
            {
                JsonArray a when name == "length" => [JsonValue.Create(a.Count)],
                JsonObject o when o.ContainsKey(name) => [o[name]],
                _ => Enumerable.Empty<JsonNode?>(),
            }).ToList();
        }
        return current;
    }
}
