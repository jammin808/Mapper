using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;
using PlayMap.Engine.Fx;
using PlayMap.Engine.Tests.Rendering;
using PlayMap.Engine.Tests.Spine;
using PlayMap.Render.Effects;

namespace PlayMap.Engine.Tests.Fx;

/// <summary>Effects on a running render host: a slot STATE first shows compiling becomes ok once the renderer draws it and the
/// output shows the effect; a file that does not compile is faulted in STATE at its line and the output shows its input; the
/// preflight check reds it with the line that takes it off the air; a warm frame with a chain allocates nothing on the frame
/// path; and STATE.fx from a real scenario is the committed fixture, with the card descriptor that reads it.</summary>
[Collection("render host")]
public sealed class FxHostTests
{
    private const string Broken = """
        /*{ "ISFVSN": "2", "INPUTS": [ { "NAME": "inputImage", "TYPE": "image" } ] }*/
        void main() {
            gl_FragColor = IMG_THIS_PIXEL(inputImage) * nowhere;
        }
        """;

    private static JsonElement SlotIn(EngineCore engine, string id)
    {
        using var doc = JsonDocument.Parse(engine.State().Json);
        return doc.RootElement.GetProperty("fx").GetProperty("slots").EnumerateArray().Single(s => s.GetProperty("id").GetString() == id).Clone();
    }

    /// <summary>Waits until STATE shows the slot past compiling (or as <paramref name="until"/> asks), counting renderer frames;
    /// answers its STATE object.</summary>
    private static async Task<JsonElement> Settled(HostRig rig, string id, Func<JsonElement, bool>? until = null)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var slot = SlotIn(rig.Engine, id);
            if (until?.Invoke(slot) ?? slot.GetProperty("state").GetString() is not ("compiling" or "idle"))
                return slot;
            Assert.True(waited.Elapsed < HostRig.Patience, $"{id} stayed {slot.GetProperty("state").GetString()}");
            await rig.Host.FramesAsync(1).WaitAsync(HostRig.Patience);
        }
    }

    [Fact]
    public async Task ASlotIsCompilingThenOkInStateAndTheOutputShowsTheEffect()
    {
        await using var rig = HostRig.Start(HostRig.WithOutputs(HostRig.Screen("op_t", SourceRef.Solid("#284664"), 64, 36)));
        await rig.Host.FramesAsync(2).WaitAsync(HostRig.Patience);
        var before = await rig.Read("op_t");
        Assert.Equal((40, 70, 100), HostRig.At(before, 64, 10, 10));
        // The lane is held open, so the renderer is seen drawing the slot's input while it compiles.
        using var gate = new ManualResetEventSlim(false);
        rig.Engine.Fx.Pass!.Lane.BeforeCompile = _ => gate.Wait();
        string id;
        try
        {
            var add = await rig.Run("FX ADD op_t fx.invert");
            Assert.True(add.Ok, add.Payload);
            Assert.EndsWith("— compiling", add.Effect, StringComparison.Ordinal);
            id = add.Payload!;
            var waited = Stopwatch.StartNew();
            while (SlotIn(rig.Engine, id).GetProperty("words").GetString() != "compiling")
            {
                Assert.Equal("compiling", SlotIn(rig.Engine, id).GetProperty("state").GetString());
                Assert.True(waited.Elapsed < HostRig.Patience, "the renderer never met the slot");
                await rig.Host.FramesAsync(1).WaitAsync(HostRig.Patience);
            }
            Assert.Equal("compiling", SlotIn(rig.Engine, id).GetProperty("state").GetString());
            Assert.Equal((40, 70, 100), HostRig.At(await rig.Read("op_t"), 64, 10, 10));
        }
        finally
        {
            gate.Set();
        }

        var slot = await Settled(rig, id);
        Assert.Equal("ok", slot.GetProperty("state").GetString());
        Assert.Equal("", slot.GetProperty("words").GetString());
        var px = await rig.ReadUntil("op_t", p => HostRig.At(p, 64, 10, 10) != (40, 70, 100));
        Assert.Equal((215, 185, 155), HostRig.At(px, 64, 10, 10));
        using (var state = JsonDocument.Parse(rig.Engine.State().Json))
        {
            Assert.Equal("shared-context", state.RootElement.GetProperty("fx").GetProperty("compile").GetProperty("lane").GetString());
            // The light follows the measured cost against the budget (llvmpipe's GPU time moves with the machine's load).
            var chain = state.RootElement.GetProperty("outputs")[0].GetProperty("fx");
            var (cost, budget) = (chain.GetProperty("costMs").GetDouble(), state.RootElement.GetProperty("fx").GetProperty("budgetMs").GetDouble());
            Assert.Equal(cost > 2 * budget ? "red" : cost > budget ? "amber" : "green", chain.GetProperty("light").GetString());
        }

        // Under blackout the renderer does not reach the chain, and STATE says so rather than keeping the last frame's word.
        await rig.Ok("BLACKOUT ON");
        var black = await Settled(rig, id, s => s.GetProperty("state").GetString() == "idle");
        Assert.Equal("not drawn: the air is black", black.GetProperty("words").GetString());
        await rig.Ok("BLACKOUT OFF");
        Assert.Equal("ok", (await Settled(rig, id, s => s.GetProperty("state").GetString() == "ok")).GetProperty("state").GetString());

        await rig.Ok($"FX BYPASS {id} ON");
        px = await rig.ReadUntil("op_t", p => HostRig.At(p, 64, 10, 10) == (40, 70, 100));
        Assert.Equal(before, px);
        Assert.Equal("bypassed", SlotIn(rig.Engine, id).GetProperty("state").GetString());
    }

    [Fact]
    public async Task AFileThatDoesNotCompileIsFaultedAtItsLineAndTheOutputShowsItsInput()
    {
        await using var rig = HostRig.Start(HostRig.WithOutputs(HostRig.Screen("op_t", SourceRef.Solid("#284664"), 64, 36)));
        await rig.Host.FramesAsync(2).WaitAsync(HostRig.Patience);
        var import = await rig.Run(FxVerbTests.ImportLine("Broken", Broken));
        Assert.True(import.Ok, import.Payload);
        using var reply = JsonDocument.Parse(import.Payload!);
        var check = reply.RootElement.GetProperty("check");
        Assert.False(check.GetProperty("ok").GetBoolean());
        Assert.False(check.GetProperty("dialects").GetProperty("glsl330").GetProperty("ok").GetBoolean());
        Assert.Contains(check.GetProperty("issues").EnumerateArray(), i => i.GetProperty("line").GetInt32() == 3
            && i.GetProperty("words").GetString()!.Contains("nowhere", StringComparison.Ordinal));
        var kind = reply.RootElement.GetProperty("kind").GetString();

        var add = await rig.Run($"FX ADD op_t {kind}");
        Assert.True(add.Ok, add.Payload);
        var slot = await Settled(rig, add.Payload!);
        Assert.Equal("faulted", slot.GetProperty("state").GetString());
        Assert.StartsWith("line 3: ", slot.GetProperty("words").GetString(), StringComparison.Ordinal);
        Assert.Equal((40, 70, 100), HostRig.At(await rig.Read("op_t"), 64, 10, 10));

        var row = Assert.Single(await new FxPreflight().RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None));
        Assert.Equal((Light.Red, "faulted", $"FX BYPASS {add.Payload} ON"), (row.Light, row.Value, row.Fix));

        // A file with issues before any compile is not compiled here, and its report says so for this node's own dialect.
        var issues = "/*{ \"INPUTS\": [ { \"NAME\": \"inputImage\", \"TYPE\": \"image\" } ] }*/\nvoid main() { gl_FragColor = IMG_PIXEL(inputImage); }\n";
        var checkIssues = await rig.Run("FX CHECK " + JsonSerializer.Serialize(new Dictionary<string, string> { ["fs"] = issues }));
        Assert.True(checkIssues.Ok, checkIssues.Payload);
        using var report = JsonDocument.Parse(checkIssues.Payload!);
        var glsl = report.RootElement.GetProperty("dialects").GetProperty("glsl330");
        Assert.Equal((JsonValueKind.Null, "not compiled: the file's issues come first"), (glsl.GetProperty("ok").ValueKind, glsl.GetProperty("words").GetString()));
        Assert.Equal("this node's context compiles the other dialect", report.RootElement.GetProperty("dialects").GetProperty("es300").GetProperty("words").GetString());
    }

    [Fact]
    public async Task AWarmFrameWithAnOutputChainAllocatesNothingOnTheFramePath()
    {
        var project = HostRig.WithOutputs(HostRig.Screen("op_t", SourceRef.Generator("gn_testcard"), 64, 36) with
        {
            Fx = new FxChain([Slot("fx00000001", "fx.blur"), Slot("fx00000002", "fx.feedback"), Slot("fx00000003", "fx.vignette") with { Scale = 0.5 }]),
        });
        await using var rig = HostRig.Start(project);
        foreach (var id in new[] { "fx00000001", "fx00000002", "fx00000003" })
            Assert.Equal("ok", (await Settled(rig, id)).GetProperty("state").GetString());
        await rig.Host.FramesAsync(40).WaitAsync(HostRig.Patience);
        var before = rig.Host.Stats;
        await rig.Host.FramesAsync(90).WaitAsync(HostRig.Patience);
        var after = rig.Host.Stats;
        Assert.True(after.Frames - before.Frames >= 90);
        Assert.Equal(before.FramePathAllocatedBytes, after.FramePathAllocatedBytes);
    }

    [Fact]
    public async Task AWarmFrameWithLayerAndSurfaceChainsAllocatesNothingOnTheFramePath()
    {
        // Two projectors on a wall showing the main canvas: its layer's chain, the wall's (a history effect, and one at half
        // scale), a layer that is not visible and an item that is not playing, both with chains that say why they are not drawn.
        var wall = BlendSolveTests.TwoProjectorsOnAWall();
        var project = wall with
        {
            Outputs = [.. wall.Outputs.Select(o => o with { Width = 320, Height = 180 })],
            Canvases = wall.Canvases.Update("cv_main", c => c with
            {
                Width = 320,
                Height = 180,
                Layers =
                [
                    .. c.Layers.Select(l => l with { Rect = new RectD(0, 0, 320, 180), Fx = new FxChain([Slot("fx00000001", "fx.invert")]) }),
                    new Layer("ly_off", "Off", SourceRef.Solid("#284664"), new RectD(0, 0, 320, 180), 1, LayerBlend.Normal, false)
                    {
                        Fx = new FxChain([Slot("fx00000004", "fx.blur")]),
                    },
                ],
            }),
            Stage = wall.Stage with
            {
                Surfaces = wall.Stage.Surfaces.Update("sf_wall", s => s with
                {
                    Content = SurfaceContent.Empty with { Source = SourceRef.Canvas("cv_main") },
                    Fx = new FxChain([Slot("fx00000002", "fx.feedback"), Slot("fx00000003", "fx.vignette") with { Scale = 0.5 }]),
                }),
            },
            Playlists =
            [
                new Playlist("pl_off", "Off", [PlaylistItem.For("it_off", SourceRef.Generator("gn_testcard"), "Off") with
                {
                    Fx = new FxChain([Slot("fx00000005", "fx.invert")]),
                }], false, null),
            ],
        };
        await using var rig = HostRig.Start(project);
        await rig.Host.Stage!.WhenSettledAsync(rig.Engine.Store.Current.Revision).WaitAsync(HostRig.Patience);
        foreach (var id in new[] { "fx00000001", "fx00000002", "fx00000003" })
            Assert.Equal("ok", (await Settled(rig, id)).GetProperty("state").GetString());
        static Func<JsonElement, bool> Says(string words) => s => s.GetProperty("words").GetString() == words;
        Assert.Equal("idle", (await Settled(rig, "fx00000004", Says("not drawn: the layer is not visible"))).GetProperty("state").GetString());
        Assert.Equal("idle", (await Settled(rig, "fx00000005", Says("not drawn: its playlist is not showing it"))).GetProperty("state").GetString());
        await rig.Host.FramesAsync(40).WaitAsync(HostRig.Patience);
        var before = rig.Host.Stats;
        await rig.Host.FramesAsync(90).WaitAsync(HostRig.Patience);
        var after = rig.Host.Stats;
        Assert.True(after.Frames - before.Frames >= 90);
        Assert.Equal(before.FramePathAllocatedBytes, after.FramePathAllocatedBytes);
    }

    private static FxSlot Slot(string id, string kind) => new(id, kind, "", ParamSet.Empty, false, 1, FxBlend.Normal, 1, 0);

    /// <summary>The fixture's show: an output chain with an effect drawing, one bypassed, a history effect and a file that does not
    /// compile; a layer chain this build keeps but does not draw.</summary>
    private static Project FixtureShow()
    {
        var asset = new FxAsset("fa00000001", "Broken", FxFormat.Isf, "", FxCatalog.ContentHash(Broken, null)) { Source = Broken };
        var output = HostRig.Screen("op_fx", SourceRef.Solid("#284664"), 64, 36) with
        {
            Name = "Stage left",
            Fx = new FxChain([Slot("fx00000001", "fx.invert"), Slot("fx00000002", "fx.blur") with { Bypass = true },
                Slot("fx00000003", "fx.feedback") with { Mix = 0.5, Blend = FxBlend.Screen }, Slot("fx00000004", "isf:fa00000001")]),
        };
        var p = HostRig.WithOutputs(output) with { FxAssets = [asset] };
        return p with
        {
            Canvases = p.Canvases.Update("cv_main", c => c with
            {
                Layers = c.Layers.Update("ly_base", l => l with { Fx = new FxChain([Slot("fx00000005", "fx.tile")]) }),
            }),
        };
    }

    /// <summary>The cards the desk draws from STATE.fx: what each reads and the lines its actions send.</summary>
    internal static JsonArray Cards() =>
    [
        new JsonObject
        {
            ["title"] = "Effects",
            ["path"] = "fx",
            ["light"] = new JsonObject { ["path"] = "fx.slots[].state", ["red"] = new JsonArray("faulted", "missing"), ["amber"] = new JsonArray("compiling"), ["green"] = new JsonArray("ok") },
            ["words"] = "fx.compile.words",
            ["fields"] = new JsonArray(
                Field("Effects", "fx.slots.length"),
                Field("Built-in kinds", "fx.library.builtin"),
                Field("Show ISF files", "fx.library.isf"),
                Field("Compile lane", "fx.compile.lane"),
                Field("Compiling", "fx.compile.pending"),
                Field("Budget (ms)", "fx.budgetMs")),
            ["actions"] = new JsonArray(Action("Effects off", "FX BYPASS ALL ON"), Action("Effects on", "FX BYPASS ALL OFF"), Action("Kinds", "FX KINDS")),
        },
        new JsonObject
        {
            ["title"] = "Effect",
            ["path"] = "fx.slots[]",
            ["light"] = new JsonObject { ["path"] = "state", ["red"] = new JsonArray("faulted", "missing"), ["amber"] = new JsonArray("compiling"), ["green"] = new JsonArray("ok") },
            ["words"] = "words",
            ["fields"] = new JsonArray(Field("Name", "name"), Field("Kind", "kind"), Field("On", "owner"), Field("GPU (ms)", "gpuMs"),
                Field("Mix", "mix"), Field("Frame-exact", "deterministic")),
            ["actions"] = new JsonArray(Action("Bypass", "FX BYPASS $(id) TOGGLE"), Action("Mix +", "FX SET $(id) mix BY 0.05"),
                Action("Mix −", "FX SET $(id) mix BY -0.05"), Action("Remove", "FX REMOVE $(id)")),
        },
        new JsonObject
        {
            ["title"] = "Output effects",
            ["path"] = "outputs[].fx",
            ["light"] = new JsonObject { ["path"] = "light" },
            ["words"] = "",
            ["fields"] = new JsonArray(Field("Effects", "slots"), Field("Cost (ms)", "costMs")),
            ["actions"] = new JsonArray(Action("Bypass all", "FX BYPASS $(id) ON"), Action("Bring back", "FX BYPASS $(id) OFF"), Action("List", "FX LIST $(id)")),
        },
    ];

    private static JsonObject Field(string label, string path) => new() { ["label"] = label, ["path"] = path };

    private static JsonObject Action(string label, string line) => new() { ["label"] = label, ["line"] = line };

    /// <summary>STATE's fx keys with what moves between runs (measured GPU time) set to 0.</summary>
    private static string FixtureOf(string stateJson)
    {
        var root = JsonNode.Parse(stateJson)!.AsObject();
        var fx = root["fx"]!.DeepClone().AsObject();
        foreach (var s in fx["slots"]!.AsArray())
            s!["gpuMs"] = 0;
        var outputs = new JsonArray();
        foreach (var o in root["outputs"]!.AsArray())
        {
            var ofx = o!["fx"]?.DeepClone();
            if (ofx is JsonObject obj)
            {
                // At cost 0 the light is the slots' own: amber with one faulted or missing, green with one drawing, else grey.
                var states = fx["slots"]!.AsArray().Where(s => s!["owner"]!.GetValue<string>() == o["id"]!.GetValue<string>())
                    .Select(s => s!["state"]!.GetValue<string>()).ToList();
                obj["costMs"] = 0;
                obj["light"] = states.Any(s => s is "faulted" or "missing") ? "amber" : states.Contains("ok") ? "green" : "grey";
            }
            outputs.Add(new JsonObject { ["id"] = o["id"]!.GetValue<string>(), ["fx"] = ofx });
        }
        var fixture = new JsonObject { ["fx"] = fx, ["outputs"] = outputs };
        return fixture.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    [Fact]
    public async Task TodaysFxStateIsTheCommittedFixtureAndItsCardsReadIt()
    {
        await using var rig = HostRig.Start(FixtureShow());
        Assert.Equal("ok", (await Settled(rig, "fx00000001")).GetProperty("state").GetString());
        Assert.Equal("ok", (await Settled(rig, "fx00000003")).GetProperty("state").GetString());
        Assert.Equal("faulted", (await Settled(rig, "fx00000004")).GetProperty("state").GetString());
        var fixture = FixtureOf(rig.Engine.State().Json);
        using (var doc = JsonDocument.Parse(fixture))
        {
            var fx = doc.RootElement.GetProperty("fx");
            var slots = fx.GetProperty("slots").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!);
            Assert.Equal(("bypassed", "faulted", "idle"), (slots["fx00000002"].GetProperty("state").GetString(), slots["fx00000004"].GetProperty("state").GetString(),
                slots["fx00000005"].GetProperty("state").GetString()));
            Assert.False(slots["fx00000003"].GetProperty("deterministic").GetBoolean());
            Assert.Equal(FxBoard.HistoryWords, slots["fx00000003"].GetProperty("words").GetString());
            Assert.Equal("amber", doc.RootElement.GetProperty("outputs")[0].GetProperty("fx").GetProperty("light").GetString());
        }
        var cards = Cards().ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        var statePath = Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", "fx.json");
        var cardsPath = Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", "fx.cards.json");
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
        {
            await File.WriteAllTextAsync(statePath, fixture);
            await File.WriteAllTextAsync(cardsPath, cards);
        }
        Assert.Equal((await File.ReadAllTextAsync(statePath)).ReplaceLineEndings("\n"), fixture);
        Assert.Equal((await File.ReadAllTextAsync(cardsPath)).ReplaceLineEndings("\n"), cards);

        // Every card path reads a value in the fixture, and every action is a line of the catalogue.
        var state = JsonNode.Parse(fixture)!;
        foreach (var card in Cards().Select(c => c!.AsObject()))
        {
            var bases = Resolve(state, card["path"]!.GetValue<string>()).ToList();
            Assert.NotEmpty(bases);
            foreach (var field in card["fields"]!.AsArray())
                Assert.All(bases.Where(b => b is not null), b => Assert.NotEmpty(Resolve(card["path"]!.GetValue<string>() == "fx" ? state : b!, field!["path"]!.GetValue<string>())));
            foreach (var action in card["actions"]!.AsArray())
            {
                var line = action!["line"]!.GetValue<string>().Replace("$(id)", "fx00000001", StringComparison.Ordinal);
                Assert.True(WireGrammar.TryParse(line, out var parsed, out _) && VerbCatalog.Match(parsed) is not null, line);
            }
        }
    }

    /// <summary>The values at a dotted path ("fx.slots[].state", "outputs[].fx", "fx.slots.length").</summary>
    private static IEnumerable<JsonNode?> Resolve(JsonNode? node, string path)
    {
        IEnumerable<JsonNode?> current = [node];
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var each = part.EndsWith("[]", StringComparison.Ordinal);
            var name = each ? part[..^2] : part;
            current = current.SelectMany(n => n switch
            {
                JsonArray a when name == "length" => [JsonValue.Create(a.Count)],
                JsonObject o when o[name] is JsonArray arr && each => arr.AsEnumerable(),
                JsonObject o when o.ContainsKey(name) => [o[name]],
                _ => Enumerable.Empty<JsonNode?>(),
            }).ToList();
        }
        return current;
    }
}
