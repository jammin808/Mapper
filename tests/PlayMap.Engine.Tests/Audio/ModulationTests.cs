using PlayMap.Core.Content;
using PlayMap.Core.Model;
using PlayMap.Core.Modulation;

namespace PlayMap.Engine.Tests.Audio;

/// <summary>The modulation bus end to end: a pure source gives the same live value on two engines at the same leader instants; a
/// bass-bound opacity follows a bass burst within one hop of it; the live values never write the project.</summary>
public sealed class ModulationTests
{
    [Fact]
    public async Task AnLfoBoundParameterIsTheSameOnTwoEnginesAtTheSameInstants()
    {
        await using var a = new AudioRig(AudioRig.Show());
        await using var b = new AudioRig(AudioRig.Show());
        const string param = "speed";
        foreach (var rig in new[] { a, b })
            await rig.Ok($"MOD BIND gn_julia {param} lfo:sine/2 AMOUNT 0.5");
        var spec = GeneratorCatalog.Find("fractal.julia")!.Param(param)!;
        var baseValue = ParamValues.Number(ParamSet.Empty, GeneratorCatalog.Find("fractal.julia")!, param);
        var seen = new HashSet<double>();
        for (var step = 0; step < 40; step++)
        {
            // Instants that are not on any frame grid: 37 ms apart.
            a.AdvanceMs(37);
            b.AdvanceMs(37);
            var pa = AudioRig.Json(await a.Ok("MOD PEEK gn_julia")).GetProperty("bindings")[0];
            var pb = AudioRig.Json(await b.Ok("MOD PEEK gn_julia")).GetProperty("bindings")[0];
            Assert.Equal(pa.GetProperty("value").GetDouble(), pb.GetProperty("value").GetDouble());
            Assert.Equal("pure", pa.GetProperty("determinism").GetString());
            // The value is the pure formula: base + 0.5·u·(max − min), u = ½ − ½·cos(2π·t/2 s), clamped.
            var t = a.Clock.NowNs / 1e9;
            var u = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (t / 2 - Math.Floor(t / 2)));
            var expected = Math.Clamp(baseValue + 0.5 * u * (spec.Max - spec.Min), spec.Min, spec.Max);
            Assert.Equal(Math.Round(expected, 6), pa.GetProperty("value").GetDouble(), 6);
            seen.Add(pa.GetProperty("value").GetDouble());
        }
        Assert.True(seen.Count > 20, "the LFO moves the number");
        // The plans the renderers evaluate agree too, at instants of any frame rate.
        var planA = ModulationPlan.For(a.Engine.Store.Project);
        var planB = ModulationPlan.For(b.Engine.Store.Project);
        for (var t = 6_000_000_000L; t < 8_000_000_000L; t += 6_944_444)
            Assert.Equal(a.Engine.AudioRt.Read(bus => planA.Evaluate(0, bus, t)), b.Engine.AudioRt.Read(bus => planB.Evaluate(0, bus, t)));
    }

    [Fact]
    public async Task ABassBoundOpacityFollowsABassBurstWithinOneHopAndTheProjectNeverMoves()
    {
        await using var rig = new AudioRig(AudioRig.Show());
        await rig.Ok("AUDIO LISTEN fake:desk CHANNELS 1");
        await rig.Ok("MOD BIND ly_base opacity audio:bass AMOUNT 1");
        var revision = rig.Engine.Store.Current.Revision;
        var capture = rig.Fake.Captures.Single();
        // 500 ms of silence, then a 60 Hz burst; delivered in 10 ms blocks, each read when its last sample is due.
        const int burstAt = 24_000;
        var sound = Sounds.BassBurst(48_000, burstAt);
        var start = rig.Clock.NowNs;
        for (var at = 0; at < sound.Length; at += 480)
        {
            rig.Clock.NowNs = start + (at + 480) * 1_000_000_000L / 48_000;
            capture.Push(sound.AsSpan(at, 480), rig.Clock.NowNs);
        }
        var burstNs = start + burstAt * 1_000_000_000L / 48_000;
        var plan = ModulationPlan.For(rig.Engine.Store.Project);
        double Opacity(long t) => rig.Engine.AudioRt.Read(bus => plan.Apply(bus, t, false)).Canvases[0].Layers[0].Opacity;
        var before = Opacity(burstNs - 20_000_000);
        var settled = Opacity(burstNs + 200_000_000);
        Assert.Equal(0.2, before, 3);
        Assert.True(settled > 0.6, $"the burst lifts the opacity to {settled:0.###}");
        // One hop (5 ms) after the burst the opacity has made at least a quarter of its move.
        var oneHop = Opacity(burstNs + 5_000_000);
        Assert.True(oneHop - before >= 0.25 * (settled - before), $"one hop after the burst: {oneHop:0.###} (from {before:0.###} to {settled:0.###})");
        // Evaluating never writes the show: the store keeps the base and its revision.
        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
        Assert.Equal(0.2, rig.Engine.Store.Project.Canvases[0].Layers[0].Opacity);
        Assert.Single(rig.Engine.Store.Project.Canvases[0].Layers[0].Bindings);
        // The drawn project carries no bindings (nothing downstream modulates the number again).
        Assert.Empty(rig.Engine.AudioRt.Read(bus => plan.Apply(bus, burstNs, false)).Canvases[0].Layers[0].Bindings);
        var peek = AudioRig.Json(await rig.Ok("MOD PEEK ly_base")).GetProperty("bindings")[0];
        Assert.Equal("live", peek.GetProperty("determinism").GetString());
        Assert.Equal(0.2, peek.GetProperty("base").GetDouble());
    }

    /// <summary>A generator's numbers are never rewritten in the drawn project (its renderer re-reads every number on a change: a
    /// particle field would re-seed and a flash guard forget its last flash, every frame): its parameter set is the show's own and
    /// its bindings stay on it for its own render path; held, they are taken off. STATE and MOD PEEK say it is drawn at its base.</summary>
    [Fact]
    public async Task AGeneratorKeepsItsNumbersAndItsBindingsForItsOwnRenderPath()
    {
        await using var rig = new AudioRig(AudioRig.Show());
        await rig.Ok("MOD BIND gn_julia speed lfo:sine/1 AMOUNT 0.5");
        await rig.Ok("MOD BIND ly_base opacity lfo:sine/1 AMOUNT 0.5");
        var show = rig.Engine.Store.Project;
        var plan = ModulationPlan.For(show);
        for (var t = 6_000_000_000L; t < 7_000_000_000L; t += 16_666_667)
        {
            var drawn = rig.Engine.AudioRt.Read(bus => plan.Apply(bus, t, false));
            var gen = drawn.FindGenerator("gn_julia")!;
            Assert.Same(show.FindGenerator("gn_julia")!.Params, gen.Params);
            Assert.Single(gen.Bindings);
            // The layer beside it moves.
            Assert.Empty(drawn.Canvases[0].Layers[0].Bindings);
        }
        Assert.NotEqual(rig.Engine.AudioRt.Read(bus => plan.Apply(bus, 6_000_000_000L, false)).Canvases[0].Layers[0].Opacity,
            rig.Engine.AudioRt.Read(bus => plan.Apply(bus, 6_250_000_000L, false)).Canvases[0].Layers[0].Opacity);
        Assert.Empty(rig.Engine.AudioRt.Read(bus => plan.Apply(bus, 6_000_000_000L, false, new HashSet<string> { "gn_julia" }))
            .FindGenerator("gn_julia")!.Bindings);
        Assert.Empty(rig.Engine.AudioRt.Read(bus => plan.Apply(bus, 6_000_000_000L, true)).FindGenerator("gn_julia")!.Bindings);

        var unbound = rig.State().GetProperty("modulation").GetProperty("unbound").EnumerateArray().Select(u => u.GetString()).ToList();
        Assert.Contains($"Fractal speed: {EngineCore.GeneratorDrawsBase}", unbound);
        var peek = AudioRig.Json(await rig.Ok("MOD PEEK gn_julia")).GetProperty("bindings")[0];
        Assert.Equal(EngineCore.GeneratorDrawsBase, peek.GetProperty("words").GetString());
    }

    [Fact]
    public async Task BypassHoldsTheBaseAndAMissingSourceHoldsItToo()
    {
        await using var rig = new AudioRig(AudioRig.Show());
        await rig.Ok("MOD BIND ly_base opacity audio:bass AMOUNT 1");
        // No input: the binding holds its base, and STATE names why.
        var peek = AudioRig.Json(await rig.Ok("MOD PEEK ly_base")).GetProperty("bindings")[0];
        Assert.Equal(0.2, peek.GetProperty("value").GetDouble());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, peek.GetProperty("sourceValue").ValueKind);
        var unbound = rig.State().GetProperty("modulation").GetProperty("unbound");
        Assert.Contains(unbound.EnumerateArray(), u => u.GetString() == "audio:bass (no audio on this node)");
        await rig.Ok("MOD BIND ly_base rect.x beat AMOUNT 0.5");
        await rig.Ok("MOD BYPASS ON");
        Assert.True(rig.State().GetProperty("modulation").GetProperty("bypassed").GetBoolean());
        var plan = ModulationPlan.For(rig.Engine.Store.Project);
        var held = rig.Engine.AudioRt.Read(bus => plan.Apply(bus, rig.Clock.NowNs + 123_456_789, bypassAll: true));
        Assert.Equal(0, held.Canvases[0].Layers[0].Rect.X);
        Assert.Empty(held.Canvases[0].Layers[0].Bindings);
        Assert.Contains("already", await rig.Refused("MOD BYPASS ON"), StringComparison.Ordinal);
        await rig.Ok("MOD BYPASS OFF");
        var live = rig.Engine.AudioRt.Read(bus => plan.Apply(bus, rig.Clock.NowNs + 123_456_789, bypassAll: false));
        Assert.NotEqual(0, live.Canvases[0].Layers[0].Rect.X);
    }
}
