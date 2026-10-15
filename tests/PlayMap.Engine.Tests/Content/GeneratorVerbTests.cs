using System.Text.Json;
using PlayMap.Core.Content;
using PlayMap.Core.Model;

namespace PlayMap.Engine.Tests.Content;

/// <summary>The generator verbs content adds or extends: NEW stamps the generator's own time at the leader's now (and takes a
/// PRESET), SET steps BY a share of the range, RETIME moves the origin (now or AT), INPUT names the picture a particle flow emits
/// from (refusing a loop), RESET puts parameters back, GET and KINDS tell what a generator is. Each answers with fields and moves
/// the project; each no-op is refused with words and leaves the revision where it was.</summary>
public sealed class GeneratorVerbTests
{
    [Fact]
    public async Task NewStampsItsOwnTimeAtTheLeadersNow()
    {
        await using var rig = new ContentRig();
        var first = (await rig.Ok("GENERATOR NEW particles.flow Ink")).Payload!;
        Assert.Equal(rig.Engine.LeaderNowNs, rig.Engine.Store.Project.FindGenerator(first)!.OriginNs);
        rig.Clock.AdvanceMs(5_000);
        var second = (await rig.Ok("GENERATOR NEW mandelbulb")).Payload!;
        var g = rig.Engine.Store.Project.FindGenerator(second)!;
        Assert.Equal(6_000_000_000, g.OriginNs);
        Assert.Equal("fractal.mandelbulb", g.Kind);
        Assert.Equal("Mandelbulb", g.Name);
    }

    [Fact]
    public async Task NewTakesAPresetItsKindHas()
    {
        await using var rig = new ContentRig();
        var r = await rig.Ok("GENERATOR NEW particles.field Falling PRESET snow");
        var g = rig.Engine.Store.Project.FindGenerator(r.Payload!)!;
        Assert.Equal("Falling", g.Name);
        Assert.Equal("Snow", g.Params.Get("preset"));
        Assert.Contains("from Snow", r.Effect, StringComparison.Ordinal);
        var made = await rig.Ok("GENERATOR NEW julia PRESET Julia dragon");
        var julia = rig.Engine.Store.Project.FindGenerator(made.Payload);
        Assert.True(julia is not null, $"{made.Payload} / {made.Effect}");
        Assert.Equal("Julia dragon", julia.Params.Get("preset"));
        Assert.Contains("no particle preset 'Sleet'", await rig.Refused("GENERATOR NEW particles.field PRESET Sleet"), StringComparison.Ordinal);
        Assert.Contains("has no presets", await rig.Refused("GENERATOR NEW particles.flow PRESET Snow"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetStepsByAShareOfTheRange()
    {
        await using var rig = new ContentRig();
        var id = (await rig.Ok("GENERATOR NEW particles.flow")).Payload!;
        await rig.Ok($"GENERATOR SET {id} curl BY 2");
        // curl runs 0–5: a step is 0.05, two steps from 0.6.
        Assert.Equal(0.7, double.Parse(rig.Engine.Store.Project.FindGenerator(id)!.Params.Get("curl")!, System.Globalization.CultureInfo.InvariantCulture), 9);
        await rig.Ok($"GENERATOR SET {id} emitter BY 1");
        Assert.Equal("point", rig.Engine.Store.Project.FindGenerator(id)!.Params.Get("emitter"));
        await rig.Ok($"GENERATOR SET {id} emitter BY -2");
        Assert.Equal("picture", rig.Engine.Store.Project.FindGenerator(id)!.Params.Get("emitter"));
        await rig.Ok($"GENERATOR SET {id} curlOctaves BY 1");
        Assert.Equal("3", rig.Engine.Store.Project.FindGenerator(id)!.Params.Get("curlOctaves"));
        Assert.Contains("is already 3", await rig.Refused($"GENERATOR SET {id} curlOctaves BY 1"), StringComparison.Ordinal);
        Assert.Contains("only numbers and choices step", await rig.Refused($"GENERATOR SET {id} glow BY 1"), StringComparison.Ordinal);
        Assert.Contains("other than 0", await rig.Refused($"GENERATOR SET {id} curl BY 0"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetimeMovesTheOriginNowOrAtAnInstant()
    {
        await using var rig = new ContentRig();
        var id = (await rig.Ok("GENERATOR NEW reactive.scene Waves")).Payload!;
        rig.Clock.AdvanceMs(3_000);
        var r = await rig.Ok($"GENERATOR RETIME {id}");
        Assert.Equal(4_000_000_000, rig.Engine.Store.Project.FindGenerator(id)!.OriginNs);
        var json = ContentRig.Json(r);
        Assert.Equal(4_000_000_000, json.GetProperty("originNs").GetInt64());
        Assert.Equal(id, json.GetProperty("generators")[0].GetString());
        Assert.Contains("already counts its time from that instant", await rig.Refused($"GENERATOR RETIME {id}"), StringComparison.Ordinal);
        await rig.Ok($"GENERATOR RETIME Waves AT +250");
        Assert.Equal(4_250_000_000, rig.Engine.Store.Project.FindGenerator(id)!.OriginNs);
        var other = (await rig.Ok("GENERATOR NEW julia")).Payload!;
        var all = ContentRig.Json(await rig.Ok("GENERATOR RETIME ALL AT +1000"));
        Assert.Equal(rig.Engine.Store.Project.Generators.Length, all.GetProperty("generators").GetArrayLength());
        Assert.All(rig.Engine.Store.Project.Generators, g => Assert.Equal(5_000_000_000, g.OriginNs));
        Assert.Contains(other, all.GetProperty("generators").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("no generator Fog", await rig.Refused("GENERATOR RETIME Fog"), StringComparison.Ordinal);
        Assert.Contains("AT", await rig.Refused($"GENERATOR RETIME {id} AT soon"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InputNamesThePictureAFlowEmitsFromAndRefusesALoop()
    {
        await using var rig = new ContentRig(ContentRig.Show());
        var r = await rig.Ok("GENERATOR INPUT Sparks source canvas:cv_plate");
        Assert.Equal(SourceRef.Canvas("cv_plate"), rig.Engine.Store.Project.FindGenerator("gn_sparks")!.Inputs["source"]);
        Assert.Contains("as its source", r.Effect, StringComparison.Ordinal);
        Assert.Contains("already reads", await rig.Refused("GENERATOR INPUT gn_sparks source canvas:cv_plate"), StringComparison.Ordinal);
        // A layer of Main shows the field: reading Main would be a loop, as would reading itself.
        Assert.Contains("a loop", await rig.Refused("GENERATOR INPUT gn_sparks source canvas:cv_main"), StringComparison.Ordinal);
        Assert.Contains("a loop", await rig.Refused("GENERATOR INPUT gn_sparks source generator:gn_sparks"), StringComparison.Ordinal);
        await rig.Ok("GENERATOR INPUT gn_sparks source NONE");
        Assert.Empty(rig.Engine.Store.Project.FindGenerator("gn_sparks")!.Inputs);
        Assert.Contains("reads no source", await rig.Refused("GENERATOR INPUT gn_sparks source NONE"), StringComparison.Ordinal);
        var julia = (await rig.Ok("GENERATOR NEW julia")).Payload!;
        Assert.Contains("reads no picture", await rig.Refused($"GENERATOR INPUT {julia} source canvas:cv_plate"), StringComparison.Ordinal);
        Assert.Contains("not matte", await rig.Refused("GENERATOR INPUT gn_sparks matte canvas:cv_plate"), StringComparison.Ordinal);
        Assert.NotEmpty(await rig.Refused("GENERATOR INPUT gn_sparks source canvas:cv_nowhere"));
    }

    [Fact]
    public async Task ResetPutsParametersBack()
    {
        await using var rig = new ContentRig();
        var id = (await rig.Ok("GENERATOR NEW julia")).Payload!;
        Assert.Contains("already at its defaults", await rig.Refused($"GENERATOR RESET {id}"), StringComparison.Ordinal);
        await rig.Ok($"GENERATOR SET {id} zoom 3");
        await rig.Ok($"GENERATOR SET {id} speed 2");
        await rig.Ok($"GENERATOR RESET {id} zoom");
        var g = rig.Engine.Store.Project.FindGenerator(id)!;
        Assert.Null(g.Params.Get("zoom"));
        Assert.Equal("2", g.Params.Get("speed"));
        Assert.Contains("already its default", await rig.Refused($"GENERATOR RESET {id} zoom"), StringComparison.Ordinal);
        await rig.Ok($"GENERATOR RESET {id} ALL");
        Assert.Empty(rig.Engine.Store.Project.FindGenerator(id)!.Params.Values);
        Assert.Contains("no parameter colour", await rig.Refused($"GENERATOR RESET {id} colour"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAndKindsTellWhatAGeneratorIs()
    {
        await using var rig = new ContentRig();
        var id = (await rig.Ok("GENERATOR NEW particles.flow Ink")).Payload!;
        var get = ContentRig.Json(await rig.Ok("GENERATOR GET Ink"));
        Assert.Equal(id, get.GetProperty("id").GetString());
        Assert.Equal("262144", get.GetProperty("effective").GetProperty("count").GetString());
        Assert.Equal("bounded", get.GetProperty("traits").GetProperty("time").GetString());
        Assert.Equal(4.0, get.GetProperty("traits").GetProperty("maxLifeSeconds").GetDouble());
        Assert.Equal(1_000_000_000, get.GetProperty("originNs").GetInt64());
        Assert.Equal("idle", get.GetProperty("state").GetString());
        Assert.Equal("no renderer on this node", get.GetProperty("words").GetString());
        Assert.Contains("no generator Fog", await rig.Refused("GENERATOR GET Fog"), StringComparison.Ordinal);

        using var kinds = JsonDocument.Parse((await rig.Ok("GENERATOR KINDS")).Payload!);
        var flow = kinds.RootElement.EnumerateArray().Single(k => k.GetProperty("id").GetString() == "particles.flow");
        Assert.Equal("bounded", flow.GetProperty("traits").GetProperty("time").GetString());
        Assert.Equal(["source"], flow.GetProperty("inputs").EnumerateArray().Select(i => i.GetString()));
        var bulb = kinds.RootElement.EnumerateArray().Single(k => k.GetProperty("id").GetString() == "fractal.mandelbulb");
        Assert.True(bulb.GetProperty("traits").GetProperty("readsAudio").GetBoolean());
        Assert.Contains(bulb.GetProperty("params").EnumerateArray(), p => p.GetProperty("name").GetString() == "sound"
            && p.GetProperty("default").GetString() == "0");
    }

    [Fact]
    public async Task PresetsListTheCuratedAndSavedLooksOfAKindOrAFamily()
    {
        await using var rig = new ContentRig();
        using (var bulbs = JsonDocument.Parse((await rig.Ok("GENERATOR PRESETS mandelbulb")).Payload!))
        {
            var rows = bulbs.RootElement.EnumerateArray().ToList();
            Assert.Equal(4, rows.Count);
            Assert.All(rows, r => Assert.Equal("fractal.mandelbulb", r.GetProperty("kind").GetString()));
            Assert.All(rows, r => Assert.True(r.GetProperty("curated").GetBoolean()));
            Assert.Contains(rows, r => r.GetProperty("name").GetString() == "Golden bulb" && r.GetProperty("pack").GetString() == "Bulbs");
        }
        using (var particles = JsonDocument.Parse((await rig.Ok("GENERATOR PRESETS particles")).Payload!))
        {
            var kinds = particles.RootElement.EnumerateArray().Select(r => r.GetProperty("kind").GetString()).Distinct().ToList();
            Assert.Equal(["particles.field", "particles.flow", "particles.image"], kinds);
        }
        Assert.Contains("no generator kind or family 'clouds'", await rig.Refused("GENERATOR PRESETS clouds"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecallWritesALookWholeInOneUndoStepAndSaveKeepsOne()
    {
        await using var rig = new ContentRig();
        var id = (await rig.Ok("GENERATOR NEW particles.flow Sparks")).Payload!;
        await rig.Ok($"GENERATOR SET {id} curl 2");
        var before = rig.Engine.Store.Project.FindGenerator(id)!.Params;
        var r = await rig.Ok($"GENERATOR RECALL {id} Galaxy swirl");
        Assert.Equal("Sparks → Galaxy swirl", r.Effect);
        var g = rig.Engine.Store.Project.FindGenerator(id)!;
        Assert.Equal(GeneratorLooks.Find("particles.flow", "Galaxy swirl")!.Params, g.Params);
        Assert.Contains("already looks like Galaxy swirl", await rig.Refused($"GENERATOR RECALL {id} galaxy swirl"), StringComparison.Ordinal);
        Assert.Contains("is a look for Mandelbulb, not Particle flow", await rig.Refused($"GENERATOR RECALL {id} Golden bulb"), StringComparison.Ordinal);
        Assert.Contains("no look 'Haze' for Particle flow", await rig.Refused($"GENERATOR RECALL {id} Haze"), StringComparison.Ordinal);
        // One undo step back to what it was.
        await rig.Ok("UNDO");
        Assert.Equal(before, rig.Engine.Store.Project.FindGenerator(id)!.Params);

        // SAVE keeps the look whole, as a show entity; RECALL takes it by name or id; a second look of one name is refused.
        var saved = (await rig.Ok($"GENERATOR SAVE {id} Hot sparks")).Payload!;
        var preset = rig.Engine.Store.Project.FindGeneratorPreset(saved)!;
        Assert.StartsWith("gp", saved, StringComparison.Ordinal);
        Assert.Equal(("Hot sparks", "particles.flow"), (preset.Name, preset.Kind));
        Assert.Equal("2", preset.Params.Get("curl"));
        Assert.Equal(GeneratorCatalog.Find("particles.flow")!.Params.Count, preset.Params.Values.Count);
        Assert.Contains("already keeps a Particle flow look named Hot sparks", await rig.Refused($"GENERATOR SAVE {id} hot sparks"), StringComparison.Ordinal);
        await rig.Ok($"GENERATOR RECALL {id} Embers");
        await rig.Ok($"GENERATOR RECALL {id} Hot sparks");
        Assert.Equal("2", rig.Engine.Store.Project.FindGenerator(id)!.Params.Get("curl"));
        await rig.Ok($"GENERATOR RECALL {id} Embers");
        await rig.Ok($"GENERATOR RECALL {id} {saved}");
        Assert.Equal(preset.Params, rig.Engine.Store.Project.FindGenerator(id)!.Params);
        using (var list = JsonDocument.Parse((await rig.Ok("GENERATOR PRESETS particles.flow")).Payload!))
            Assert.Contains(list.RootElement.EnumerateArray(), x => x.GetProperty("id").GetString() == saved && !x.GetProperty("curated").GetBoolean());

        // A kind whose looks ride its preset parameter recalls by setting it.
        var field = (await rig.Ok("GENERATOR NEW particles.field")).Payload!;
        await rig.Ok($"GENERATOR RECALL {field} Snow");
        Assert.Equal("Snow", rig.Engine.Store.Project.FindGenerator(field)!.Params.Get("preset"));
        var julia = (await rig.Ok("GENERATOR NEW julia")).Payload!;
        Assert.Contains("is a look for Particle flow, not Julia", await rig.Refused($"GENERATOR RECALL {julia} Hot sparks"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGeneratorsInputsAreCheckedOnEveryEdit()
    {
        await using var rig = new ContentRig();
        var stray = new Generator("gn_stray", "Stray", "fractal.julia", ParamSet.Empty)
        {
            Inputs = System.Collections.Immutable.ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal,
                [new KeyValuePair<string, SourceRef>("source", SourceRef.Solid("#FFFFFF"))]),
        };
        var json = JsonSerializer.Serialize(stray, Core.Model.ProjectJson.Compact);
        Assert.Contains("reads no picture", await rig.Refused("ADD generator " + json), StringComparison.Ordinal);
        Assert.True(GeneratorTraits.Of("particles.flow").DrawsBindings);
    }
}
