using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;
using PlayMap.Engine.Fx;
using PlayMap.Render.Effects;

namespace PlayMap.Engine.Tests.Fx;

/// <summary>The FX verbs on an engine with no renderer: each answers with fields (ids, counts, JSON) and moves the project and
/// STATE.fx; each no-op is refused with the reason and leaves the revision where it was; the import and check reports carry the
/// file's issues by line; the preflight check reds a slot this node cannot draw, with the line that takes it off the air; the
/// INDEX section and the context item come from the same views.</summary>
public sealed class FxVerbTests
{
    private static readonly SessionInfo Desk = SessionInfo.Desk("t");

    internal const string Ramp = """
        /*{ "ISFVSN": "2", "DESCRIPTION": "A ramp", "INPUTS": [ { "NAME": "inputImage", "TYPE": "image" }, { "NAME": "gain", "TYPE": "float", "DEFAULT": 1.0, "MIN": 0.0, "MAX": 2.0 } ] }*/
        void main() { gl_FragColor = IMG_THIS_PIXEL(inputImage) * gain; }
        """;

    private static async Task<CommandReply> Run(EngineCore engine, string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await engine.HandleAsync(parsed, Desk, CancellationToken.None);
    }

    private static async Task<string> Ok(EngineCore engine, string line)
    {
        var r = await Run(engine, line);
        Assert.True(r.Ok, $"{line} → {r.Payload}");
        return r.Payload ?? "";
    }

    /// <summary>A refusal: the words, and the project not moved.</summary>
    private static async Task<string> Refused(EngineCore engine, string line)
    {
        var revision = engine.Store.Current.Revision;
        var r = await Run(engine, line);
        Assert.False(r.Ok, $"{line} was not refused: {r.Effect}");
        Assert.Equal(revision, engine.Store.Current.Revision);
        return r.Payload ?? "";
    }

    private static JsonElement Slots(EngineCore engine)
    {
        using var doc = JsonDocument.Parse(engine.State().Json);
        return doc.RootElement.GetProperty("fx").GetProperty("slots").Clone();
    }

    private static JsonElement Slot(EngineCore engine, string id) =>
        Slots(engine).EnumerateArray().Single(s => s.GetProperty("id").GetString() == id);

    internal static string ImportLine(string name, string fs) =>
        "FX IMPORT " + JsonSerializer.Serialize(new Dictionary<string, string> { ["name"] = name, ["fs"] = fs });

    [Fact]
    public async Task KindsListTheLibraryWithParametersAndTraits()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        using var all = JsonDocument.Parse(await Ok(engine, "FX KINDS"));
        Assert.Equal(10, all.RootElement.GetArrayLength());
        var blur = all.RootElement.EnumerateArray().Single(k => k.GetProperty("id").GetString() == "fx.blur");
        Assert.Equal("builtin", blur.GetProperty("format").GetString());
        Assert.Equal("filter", blur.GetProperty("role").GetString());
        var radius = blur.GetProperty("params")[0];
        Assert.Equal(("radius", "number", 0.0, 32.0), (radius.GetProperty("name").GetString(), radius.GetProperty("type").GetString(),
            radius.GetProperty("min").GetDouble(), radius.GetProperty("max").GetDouble()));
        Assert.False(blur.GetProperty("traits").GetProperty("history").GetBoolean());
        using var time = JsonDocument.Parse(await Ok(engine, "FX KINDS time"));
        var feedback = Assert.Single(time.RootElement.EnumerateArray());
        Assert.True(feedback.GetProperty("traits").GetProperty("history").GetBoolean());
        Assert.Contains("no effect kind matches", await Refused(engine, "FX KINDS sparkle"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddPutsASlotInAChainAndStateShowsIt()
    {
        var clock = new Core.Time.ManualClock(7_000_000_000);
        await using var engine = new EngineCore(EngineServices.ForTests(clock));
        var blur = await Ok(engine, "FX ADD \"Output 1\" blur Soft");
        var invert = await Ok(engine, "FX ADD 1 fx.invert AT 1");
        var output = engine.Store.Project.FindOutput("op_1")!;
        Assert.Equal([invert, blur], output.Fx.Slots.Select(s => s.Id).ToArray());
        var slot = output.Fx.Slots[1];
        Assert.Equal(("fx.blur", "Soft", 1.0, FxBlend.Normal, 1.0, false), (slot.Kind, slot.Name, slot.Mix, slot.Blend, slot.Scale, slot.Bypass));
        Assert.Equal(engine.LeaderNowNs, slot.OriginNs);

        var s = Slot(engine, blur);
        Assert.Equal(("op_1", "output", 1, "fx.blur", "Soft"), (s.GetProperty("owner").GetString(), s.GetProperty("ownerKind").GetString(),
            s.GetProperty("index").GetInt32(), s.GetProperty("kind").GetString(), s.GetProperty("name").GetString()));
        Assert.Equal("idle", s.GetProperty("state").GetString());
        Assert.Equal("no renderer on this node", s.GetProperty("words").GetString());
        Assert.True(s.GetProperty("deterministic").GetBoolean());
        using (var state = JsonDocument.Parse(engine.State().Json))
        {
            var fx = state.RootElement.GetProperty("fx");
            Assert.Equal(4.0, fx.GetProperty("budgetMs").GetDouble());
            Assert.Equal(10, fx.GetProperty("library").GetProperty("builtin").GetInt32());
            Assert.Equal("none", fx.GetProperty("compile").GetProperty("lane").GetString());
            var op = state.RootElement.GetProperty("outputs")[0].GetProperty("fx");
            Assert.Equal((2, "grey"), (op.GetProperty("slots").GetInt32(), op.GetProperty("light").GetString()));
        }

        var layer = await Run(engine, "FX ADD \"Test card\" fx.feedback");
        Assert.True(layer.Ok);
        Assert.Contains("draws effects on outputs only", layer.Effect, StringComparison.Ordinal);
        var onLayer = Slot(engine, layer.Payload!);
        Assert.Equal(("ly_base", "layer"), (onLayer.GetProperty("owner").GetString(), onLayer.GetProperty("ownerKind").GetString()));
        Assert.False(onLayer.GetProperty("deterministic").GetBoolean());
        Assert.True(onLayer.GetProperty("history").GetBoolean());

        Assert.Contains("no layer, canvas, surface, output or playlist item nowhere", await Refused(engine, "FX ADD nowhere blur"), StringComparison.Ordinal);
        Assert.Contains("no effect kind 'sparkle'", await Refused(engine, "FX ADD op_1 sparkle"), StringComparison.Ordinal);
        Assert.Contains("AT is a position from 1 to 3", await Refused(engine, "FX ADD op_1 blur AT 9"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetMovesOneValueAndRefusesWhatWouldNotChange()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        var id = await Ok(engine, "FX ADD op_1 fx.blur");
        await Ok(engine, $"FX SET {id} radius 12.5");
        Assert.Equal("12.5", engine.Store.Project.FindFx(id)!.Value.Slot.Params.Get("radius"));
        Assert.Equal("radius is already 12.5", await Refused(engine, $"FX SET {id} radius 12.5"));
        await Ok(engine, $"FX SET {id} radius 99");
        Assert.Equal("32", engine.Store.Project.FindFx(id)!.Value.Slot.Params.Get("radius"));
        await Ok(engine, $"FX SET {id} radius BY -0.25");
        Assert.Equal("24", engine.Store.Project.FindFx(id)!.Value.Slot.Params.Get("radius"));
        Assert.Contains("Blur has no parameter sparkle — it has radius", await Refused(engine, $"FX SET {id} sparkle 1"), StringComparison.Ordinal);
        Assert.Equal("radius is a number", await Refused(engine, $"FX SET {id} radius lots"));

        await Ok(engine, $"FX SET {id} mix 0.5");
        await Ok(engine, $"FX SET {id} mix BY 0.25");
        await Ok(engine, $"FX SET {id} blend screen");
        await Ok(engine, $"FX SET {id} scale 0.1");
        await Ok(engine, $"FX SET {id} name Haze");
        var slot = engine.Store.Project.FindFx(id)!.Value.Slot;
        Assert.Equal((0.75, FxBlend.Screen, 0.25, "Haze"), (slot.Mix, slot.Blend, slot.Scale, slot.Name));
        Assert.Contains("mix is already 0.75", await Refused(engine, $"FX SET {id} mix 0.75"), StringComparison.Ordinal);
        Assert.Contains("blend is already screen", await Refused(engine, $"FX SET {id} blend screen"), StringComparison.Ordinal);
        Assert.Equal("blend is normal, add, multiply, screen or difference", await Refused(engine, $"FX SET {id} blend 7"));

        var mirror = await Ok(engine, "FX ADD op_1 fx.mirror");
        await Ok(engine, $"FX SET op_1#2 mode BY 1");
        Assert.Equal("Top to bottom", engine.Store.Project.FindFx(mirror)!.Value.Slot.Params.Get("mode"));
        await Ok(engine, $"FX SET {mirror} mode both");
        Assert.Equal("Both", engine.Store.Project.FindFx(mirror)!.Value.Slot.Params.Get("mode"));
        Assert.Equal("mode is already Both", await Refused(engine, $"FX SET {mirror} mode BY 3"));

        await Ok(engine, $"FX RESET {id}");
        Assert.True(engine.Store.Project.FindFx(id)!.Value.Slot.Params.Values.IsEmpty);
        Assert.Contains("already at their defaults", await Refused(engine, $"FX RESET {id}"), StringComparison.Ordinal);
        Assert.Contains("no effect fxnothing", await Refused(engine, "FX SET fxnothing mix 1"), StringComparison.Ordinal);
        Assert.Contains("Output 1 has effects 1 to 2", await Refused(engine, "FX SET op_1#3 mix 1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BypassMoveAndRemoveAnswerWhatTheyChanged()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        var a = await Ok(engine, "FX ADD op_1 fx.invert");
        var b = await Ok(engine, "FX ADD op_1 fx.blur");
        var c = await Ok(engine, "FX ADD cv_main fx.tile");
        Assert.Equal("1", await Ok(engine, $"FX BYPASS {a} ON"));
        Assert.Equal("bypassed", Slot(engine, a).GetProperty("state").GetString());
        Assert.Equal("bypassed", Slot(engine, a).GetProperty("words").GetString());
        Assert.Contains("is already bypassed", await Refused(engine, $"FX BYPASS {a} ON"), StringComparison.Ordinal);
        Assert.Equal("1", await Ok(engine, "FX BYPASS op_1 ON"));
        Assert.Equal("3", await Ok(engine, "FX BYPASS ALL TOGGLE"));
        var p = engine.Store.Project;
        Assert.Equal((false, false, true), (p.FindFx(a)!.Value.Slot.Bypass, p.FindFx(b)!.Value.Slot.Bypass, p.FindFx(c)!.Value.Slot.Bypass));
        Assert.Contains("are not bypassed", await Refused(engine, "FX BYPASS op_1 OFF"), StringComparison.Ordinal);
        Assert.Equal("FX BYPASS … ends ON, OFF or TOGGLE", await Refused(engine, $"FX BYPASS {a} MAYBE"));

        await Ok(engine, $"FX MOVE {b} 1");
        Assert.Equal([b, a], engine.Store.Project.FindOutput("op_1")!.Fx.Slots.Select(s => s.Id).ToArray());
        Assert.Contains("is already at position 1", await Refused(engine, $"FX MOVE {b} 1"), StringComparison.Ordinal);
        Assert.Contains("1 to 2", await Refused(engine, $"FX MOVE {b} 5"), StringComparison.Ordinal);

        await Ok(engine, "FX REMOVE op_1#1");
        Assert.Null(engine.Store.Project.FindFx(b));
        Assert.Contains($"no effect {b}", await Refused(engine, $"FX REMOVE {b}"), StringComparison.Ordinal);

        using var list = JsonDocument.Parse(await Ok(engine, "FX LIST ALL"));
        Assert.Equal(["op_1", "cv_main"], list.RootElement.EnumerateArray().Select(o => o.GetProperty("owner").GetString()!).ToArray());
        using var one = JsonDocument.Parse(await Ok(engine, "FX LIST cv_main"));
        var tile = one.RootElement[0].GetProperty("slots")[0];
        Assert.Equal((c, "canvas", "bypassed"), (tile.GetProperty("id").GetString(), tile.GetProperty("ownerKind").GetString(), tile.GetProperty("state").GetString()));
    }

    [Fact]
    public async Task ImportCarriesTheFileAndItsReportAndRefusesTheSameFileTwice()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        using var reply = JsonDocument.Parse(await Ok(engine, ImportLine("Ramp", Ramp)));
        var id = reply.RootElement.GetProperty("id").GetString()!;
        Assert.StartsWith("fa", id, StringComparison.Ordinal);
        Assert.Equal("isf:" + id, reply.RootElement.GetProperty("kind").GetString());
        var check = reply.RootElement.GetProperty("check");
        Assert.True(check.GetProperty("ok").GetBoolean());
        Assert.Equal("filter", check.GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, check.GetProperty("dialects").GetProperty("glsl330").GetProperty("ok").ValueKind);
        Assert.Equal("no GL context on this node", check.GetProperty("dialects").GetProperty("glsl330").GetProperty("words").GetString());
        var asset = engine.Store.Project.FxAssets.Single();
        Assert.Equal((id, "Ramp", Ramp, FxCatalog.ContentHash(Ramp, null)), (asset.Id, asset.Name, asset.Source, asset.ContentHash));

        Assert.Contains($"the show has this file already, as {id}", await Refused(engine, ImportLine("Again", Ramp)), StringComparison.Ordinal);
        using (var kinds = JsonDocument.Parse(await Ok(engine, "FX KINDS isf")))
            Assert.Equal("gain", Assert.Single(kinds.RootElement.EnumerateArray()).GetProperty("params")[0].GetProperty("name").GetString());
        var slot = await Ok(engine, $"FX ADD op_1 isf:{id}");
        await Ok(engine, $"FX SET {slot} gain 1.5");
        using (var state = JsonDocument.Parse(engine.State().Json))
            Assert.Equal(1, state.RootElement.GetProperty("fx").GetProperty("library").GetProperty("isf").GetInt32());

        var broken = await Refused(engine, ImportLine("Broken", "/*{ \"INPUTS\": [ }*/\nvoid main() {}"));
        Assert.StartsWith("the file does not read as ISF: line 1:", broken, StringComparison.Ordinal);
        Assert.Contains("needs \"fs\"", await Refused(engine, "FX IMPORT {\"name\":\"empty\"}"), StringComparison.Ordinal);
        Assert.Contains("imports grain", await Refused(engine, ImportLine("Grain", "/*{ \"IMPORTED\": { \"grain\": { \"PATH\": \"g.png\" } } }*/\nvoid main() {}")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportReadsAFileWithItsVertexFileAndPictures()
    {
        var folder = Directory.CreateTempSubdirectory("playmap-fx-").FullName;
        try
        {
            var fs = Path.Combine(folder, "Shift.fs");
            await File.WriteAllTextAsync(fs, "/*{ \"IMPORTED\": { \"card\": { \"PATH\": \"card.png\" } } }*/\nvarying vec2 left;\nvoid main() { gl_FragColor = IMG_NORM_PIXEL(card, left); }\n");
            await File.WriteAllTextAsync(Path.ChangeExtension(fs, ".vs"), "varying vec2 left;\nvoid main() { isf_vertShaderInit(); left = isf_FragNormCoord; }\n");
            await File.WriteAllBytesAsync(Path.Combine(folder, "card.png"), [0x89, 0x50, 0x4E, 0x47]);
            await using var engine = new EngineCore(EngineServices.ForTests());
            using var reply = JsonDocument.Parse(await Ok(engine, $"FX IMPORT \"{fs}\""));
            var asset = engine.Store.Project.FxAssets.Single();
            Assert.Equal(("Shift", fs), (asset.Name, asset.Path));
            Assert.Contains("isf_FragNormCoord", asset.VertexSource, StringComparison.Ordinal);
            Assert.Equal(Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]), asset.Images["card"]);
            Assert.Contains("no file", await Refused(engine, $"FX IMPORT \"{Path.Combine(folder, "none.fs")}\""), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task CheckReportsIssuesByTheFilesLines()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        var revision = engine.Store.Current.Revision;
        using (var builtin = JsonDocument.Parse(await Ok(engine, "FX CHECK fx.feedback")))
        {
            Assert.True(builtin.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(2, builtin.RootElement.GetProperty("passes").GetInt32());
            Assert.Equal(["trail"], builtin.RootElement.GetProperty("persistent").EnumerateArray().Select(e => e.GetString()!).ToArray());
        }
        var bad = "/*{ \"INPUTS\": [ { \"NAME\": \"inputImage\", \"TYPE\": \"image\" } ] }*/\nvoid main() {\n  gl_FragColor = IMG_PIXEL(inputImage);\n}\n";
        using var report = JsonDocument.Parse(await Ok(engine, "FX CHECK " + JsonSerializer.Serialize(new Dictionary<string, string> { ["fs"] = bad })));
        Assert.False(report.RootElement.GetProperty("ok").GetBoolean());
        var issue = Assert.Single(report.RootElement.GetProperty("issues").EnumerateArray());
        Assert.Equal((3, "IMG_PIXEL takes 2 or 3 values, here 1"), (issue.GetProperty("line").GetInt32(), issue.GetProperty("words").GetString()));
        Assert.Contains("no effect or asset nothing", await Refused(engine, "FX CHECK nothing"), StringComparison.Ordinal);
        Assert.Equal(revision, engine.Store.Current.Revision);
    }

    [Fact]
    public async Task AGenericEditKeepsAKindThisNodeLacksAsMissingAndRefusesAStrayParameter()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        var lacking = await Ok(engine, "ADD fx op_1 {\"kind\":\"fx.sparkle\"}");
        Assert.Equal("missing", Slot(engine, lacking).GetProperty("state").GetString());
        Assert.Contains("this node has no effect fx.sparkle", Slot(engine, lacking).GetProperty("words").GetString(), StringComparison.Ordinal);
        Assert.Contains("Blur has no parameter size", await Refused(engine, "ADD fx op_1 {\"kind\":\"fx.blur\",\"params\":{\"values\":{\"size\":\"1\"}}}"), StringComparison.Ordinal);
        Assert.StartsWith("fx", await Ok(engine, "ADD fx op_1 {\"kind\":\"fx.blur\",\"params\":{\"values\":{\"radius\":\"3\"}}}"), StringComparison.Ordinal);
    }

    /// <summary>A show whose output chain names a kind this node does not have (a file from another show, say).</summary>
    internal static Project WithAMissingKind(out string slotId)
    {
        slotId = "fx0badc0de";
        var p = Project.CreateDefault("Missing");
        var slot = new FxSlot(slotId, "isf:fa00000000", "Lost", ParamSet.Empty, false, 1, FxBlend.Normal, 1, 0);
        return p with { Outputs = p.Outputs.Update("op_1", o => o with { Fx = new FxChain([slot]) }) };
    }

    [Fact]
    public async Task PreflightRedsASlotTheNodeCannotDrawWithTheLineThatTakesItOffTheAir()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(), WithAMissingKind(out var lost));
        var check = new FxPreflight();
        var rows = await check.RunAsync(new PreflightContext(engine, PreflightDepth.Quick, false), CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.Equal(("effects", Light.Red, "missing", $"FX BYPASS {lost} ON"), (row.Section, row.Light, row.Value, row.Fix));
        Assert.Contains("this node has no effect isf:fa00000000", row.Note, StringComparison.Ordinal);
        Assert.True(VerbCatalog.Match(WireGrammar.TryParse(row.Fix!, out var fix, out _) ? fix : throw new InvalidOperationException()) is not null);

        // The fix line clears the row.
        await Ok(engine, row.Fix!);
        var after = await check.RunAsync(new PreflightContext(engine, PreflightDepth.Quick, false), CancellationToken.None);
        Assert.Equal(("none", Light.Green), (Assert.Single(after).Value, after[0].Light));

        var services = new FxExtension().Configure(EngineServices.ForTests());
        Assert.Contains(services.PreflightChecks, c => c is FxPreflight && c.Section == "effects");
    }

    [Fact]
    public async Task TheIndexSectionAndTheContextItemComeFromTheSameViews()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(), WithAMissingKind(out var lost));
        var blur = await Ok(engine, "FX ADD op_1 fx.blur");
        var section = engine.Routes.IndexSections.Single(s => s.Section == "fx");
        Assert.Equal("FX BYPASS $(id) TOGGLE", section.Hint.Keys[0].DownLine);
        Assert.All(section.Hint.Keys, k => Assert.NotNull(VerbCatalog.Match(WireGrammar.TryParse(k.DownLine.Replace("$(id)", blur, StringComparison.Ordinal), out var l, out _) ? l : throw new InvalidOperationException())));
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
            section.WriteRows(engine.Store.Project, engine.Snapshot, w);
        using var rows = JsonDocument.Parse(buffer.WrittenMemory);
        Assert.Equal([lost, blur], rows.RootElement.EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToArray());
        Assert.Equal(("red", "missing", "op_1"), (rows.RootElement[0].GetProperty("light").GetString(), rows.RootElement[0].GetProperty("state").GetString(),
            rows.RootElement[0].GetProperty("parent").GetString()));

        using var context = JsonDocument.Parse(engine.Context());
        var item = context.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "fx");
        Assert.Equal("amber", item.GetProperty("light").GetString(), ignoreCase: true);
        Assert.Contains("Lost missing", item.GetProperty("state").GetString(), StringComparison.Ordinal);
    }
}
