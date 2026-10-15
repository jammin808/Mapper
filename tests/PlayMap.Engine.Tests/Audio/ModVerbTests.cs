using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Audio;

/// <summary>The MOD verbs' fields and refusals, and the aliases (FX BIND, GENERATOR BIND, FX UNBIND, GENERATOR UNBIND) routed to
/// MOD BIND and MOD UNBIND.</summary>
public sealed class ModVerbTests
{
    private static Project WithEffect()
    {
        var show = AudioRig.Show();
        var slot = new FxSlot("fx_blur", "fx.blur", "Blur", ParamSet.Empty, false, 1, FxBlend.Normal, 1, 0);
        return show with
        {
            Canvases = show.Canvases.Update("cv_main", c => c with
            {
                Layers = c.Layers.Update("ly_base", l => l with { Fx = new FxChain([slot]) }),
            }),
        };
    }

    [Fact]
    public async Task BindWritesTheBindingWithItsFieldsAndUndoRemovesIt()
    {
        await using var rig = new AudioRig(AudioRig.Show());
        var r = await rig.Ok("MOD BIND ly_base opacity lfo:triangle/4b AMOUNT -0.25 OFFSET 0.1 MODE SCALE CURVE EASE SMOOTH 20/300 STEPS 8 INVERT");
        var b = AudioRig.Json(r);
        Assert.Equal("opacity", b.GetProperty("param").GetString());
        Assert.Equal("lfo:triangle/4b", b.GetProperty("source").GetString());
        Assert.Equal("scale", b.GetProperty("mode").GetString());
        Assert.Equal(-0.25, b.GetProperty("amount").GetDouble());
        Assert.Equal(0.1, b.GetProperty("offset").GetDouble());
        Assert.Equal("ease", b.GetProperty("curve").GetString());
        Assert.Equal(20, b.GetProperty("attackMs").GetDouble());
        Assert.Equal(300, b.GetProperty("releaseMs").GetDouble());
        Assert.Equal(8, b.GetProperty("steps").GetInt32());
        Assert.True(b.GetProperty("invert").GetBoolean());
        Assert.Equal("layer", b.GetProperty("kind").GetString());
        Assert.Contains("pure", r.Effect, StringComparison.Ordinal);
        var kept = rig.Engine.Store.Project.Canvases[0].Layers[0].Bindings.Single();
        Assert.Equal(BindingMode.Scale, kept.Mode);
        Assert.Equal(0.02, kept.AttackSeconds, 9);
        Assert.Equal(0.3, kept.ReleaseSeconds, 9);
        // The same binding again changes nothing and is refused; a new amount rebinds.
        Assert.Contains("already bound", await rig.Refused("MOD BIND ly_base opacity lfo:triangle/4b AMOUNT -0.25 OFFSET 0.1 MODE SCALE CURVE EASE SMOOTH 20/300 STEPS 8 INVERT"), StringComparison.Ordinal);
        var again = await rig.Ok("MOD BIND ly_base opacity lfo:triangle/4b AMOUNT 0.5");
        Assert.StartsWith("rebound", again.Effect, StringComparison.Ordinal);
        Assert.Single(rig.Engine.Store.Project.Canvases[0].Layers[0].Bindings);
        var state = rig.State().GetProperty("modulation");
        Assert.Equal(1, state.GetProperty("bindings").GetInt32());
        Assert.Equal(1, state.GetProperty("pure").GetInt32());
        Assert.Equal(0, state.GetProperty("live").GetInt32());
        await rig.Ok("UNDO");
        await rig.Ok("UNDO");
        Assert.Empty(rig.Engine.Store.Project.Canvases[0].Layers[0].Bindings);
    }

    [Theory]
    [InlineData("MOD BIND nothing opacity beat", "no generator, layer, surface or effect slot")]
    [InlineData("MOD BIND ly_base colour beat", "a layer modulates")]
    [InlineData("MOD BIND ly_base opacity audio:treble", "not a source")]
    [InlineData("MOD BIND ly_base opacity lfo:wobble/2", "not a source")]
    [InlineData("MOD BIND ly_base opacity beat AMOUNT 3", "AMOUNT is -1 to 1")]
    [InlineData("MOD BIND ly_base opacity beat MODE RATE", "MODE is ADD, SCALE or REPLACE")]
    [InlineData("MOD BIND ly_base opacity beat CURVE WIGGLE", "CURVE is")]
    [InlineData("MOD BIND ly_base opacity beat SMOOTH fast", "SMOOTH is")]
    [InlineData("MOD BIND ly_base opacity beat RANGE 0.2", "RANGE is")]
    [InlineData("MOD BIND ly_base opacity beat STEPS 0", "STEPS is 1 to 64")]
    [InlineData("MOD BIND ly_base opacity beat LOUDLY", "not an option of MOD BIND")]
    [InlineData("MOD BIND ly_base opacity audio:bass~10 SMOOTH 20", "not both")]
    [InlineData("MOD BIND gn_julia iterations beat", "not a continuous number")]
    [InlineData("MOD BIND fx_blur wobble beat", "has no number 'wobble'")]
    [InlineData("MOD UNBIND ly_base opacity", "has no modulation")]
    [InlineData("MOD PEEK ly_base", "has no modulation")]
    [InlineData("MOD BYPASS OFF", "already live")]
    [InlineData("MOD BYPASS SOMETIMES", "MOD BYPASS ON|OFF|TOGGLE")]
    [InlineData("MOD SOURCES midi:", "no source starts with")]
    public async Task RefusalsSayWhy(string line, string words)
    {
        await using var rig = new AudioRig(WithEffect());
        var why = await rig.Refused(line);
        Assert.Contains(words, why, StringComparison.Ordinal);
        Assert.Empty(rig.Engine.Store.Project.Canvases[0].Layers[0].Bindings);
    }

    [Fact]
    public async Task TheAliasesRouteToBindAndUnbind()
    {
        await using var rig = new AudioRig(WithEffect());
        foreach (var alias in new[] { "FX BIND", "GENERATOR BIND" })
            Assert.Equal("MOD BIND", VerbCatalog.Find(alias)!.Name);
        Assert.Equal("MOD UNBIND", VerbCatalog.Find("FX UNBIND")!.Name);
        Assert.Equal("MOD UNBIND", VerbCatalog.Find("GENERATOR UNBIND")!.Name);
        var fx = await rig.Ok("FX BIND fx_blur mix beat:pulse");
        Assert.Equal("fx", AudioRig.Json(fx).GetProperty("kind").GetString());
        // An effect's own number binds with the range its kind declares (the blur's radius, 0 to 32, 4 by default).
        await rig.Ok("FX BIND fx_blur radius bar AMOUNT 0.25");
        var radius = AudioRig.Json(await rig.Ok("MOD PEEK fx_blur")).GetProperty("bindings").EnumerateArray()
            .Single(b => b.GetProperty("param").GetString() == "radius");
        Assert.Equal(0, radius.GetProperty("min").GetDouble());
        Assert.Equal(32, radius.GetProperty("max").GetDouble());
        Assert.Equal(4, radius.GetProperty("base").GetDouble());
        await rig.Ok("FX UNBIND fx_blur radius");
        var gen = await rig.Ok("GENERATOR BIND gn_julia speed bar AMOUNT 0.2");
        Assert.Equal("generator", AudioRig.Json(gen).GetProperty("kind").GetString());
        var p = rig.Engine.Store.Project;
        Assert.Single(p.FindFx("fx_blur")!.Value.Slot.Bindings);
        Assert.Single(p.FindGenerator("gn_julia")!.Bindings);
        await rig.Ok("FX UNBIND fx_blur mix");
        await rig.Ok($"GENERATOR UNBIND gn_julia ALL");
        Assert.Empty(rig.Engine.Store.Project.FindFx("fx_blur")!.Value.Slot.Bindings);
        Assert.Empty(rig.Engine.Store.Project.FindGenerator("gn_julia")!.Bindings);
    }

    [Fact]
    public async Task PeekSourcesAndBypassAnswerWithFields()
    {
        await using var rig = new AudioRig(AudioRig.Show());
        await rig.Ok("MOD BIND Base opacity beat AMOUNT 0.5");
        await rig.Ok("MOD BIND ly_base rect.x audio:bass");
        var peek = AudioRig.Json(await rig.Ok("MOD PEEK ly_base"));
        Assert.Equal("layer", peek.GetProperty("kind").GetString());
        Assert.Equal(2, peek.GetProperty("bindings").GetArrayLength());
        var opacity = peek.GetProperty("bindings").EnumerateArray().Single(b => b.GetProperty("param").GetString() == "opacity");
        Assert.Equal("pure", opacity.GetProperty("determinism").GetString());
        Assert.Equal(0, opacity.GetProperty("min").GetDouble());
        Assert.Equal(1, opacity.GetProperty("max").GetDouble());
        var rect = peek.GetProperty("bindings").EnumerateArray().Single(b => b.GetProperty("param").GetString() == "rect.x");
        Assert.Equal(JsonValueKind.Null, rect.GetProperty("sourceValue").ValueKind);
        Assert.Contains("missing", rect.GetProperty("words").GetString(), StringComparison.Ordinal);

        var sources = AudioRig.Json(await rig.Ok("MOD SOURCES"));
        var beat = sources.EnumerateArray().Single(s => s.GetProperty("source").GetString() == "beat");
        Assert.Equal("pure", beat.GetProperty("determinism").GetString());
        Assert.Equal(JsonValueKind.Number, beat.GetProperty("value").ValueKind);
        var bass = sources.EnumerateArray().Single(s => s.GetProperty("source").GetString() == "audio:bass");
        Assert.Equal(JsonValueKind.Null, bass.GetProperty("value").ValueKind);
        Assert.Equal("live", bass.GetProperty("determinism").GetString());
        var bpm = AudioRig.Json(await rig.Ok("MOD SOURCES bpm"))[0];
        Assert.Equal(120, bpm.GetProperty("raw").GetDouble());
        Assert.Equal(0.5, bpm.GetProperty("value").GetDouble());

        await rig.Ok("MOD BYPASS ON ly_base");
        Assert.True(AudioRig.Json(await rig.Ok("MOD PEEK ly_base")).GetProperty("held").GetBoolean());
        Assert.Contains("ly_base", rig.State().GetProperty("modulation").GetProperty("held").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("already held", await rig.Refused("MOD BYPASS ON ly_base"), StringComparison.Ordinal);
        await rig.Ok("MOD BYPASS TOGGLE ly_base");
        Assert.False(AudioRig.Json(await rig.Ok("MOD PEEK ly_base")).GetProperty("held").GetBoolean());
        await rig.Ok("MOD UNBIND ly_base rect.x");
        Assert.Contains("not modulated", await rig.Refused("MOD UNBIND ly_base rect.x"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEditThatWritesABadBindingIsRefused()
    {
        await using var rig = new AudioRig(AudioRig.Show());
        var bad = new ParamBinding("opacity", "audio:treble", 1, 0, BindingCurve.Linear, 0);
        var json = JsonSerializer.Serialize(new[] { bad }, ProjectJson.Compact);
        var why = await rig.Refused($"SET ly_base bindings {json}");
        Assert.Contains("not a source", why, StringComparison.Ordinal);
    }
}
