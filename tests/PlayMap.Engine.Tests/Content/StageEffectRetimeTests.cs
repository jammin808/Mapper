using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.Tests.Rendering;
using PlayMap.Render.Engine;

namespace PlayMap.Engine.Tests.Content;

/// <summary>A surface-aware effect on a stage surface, drawn by the node's renderer through a projector: STATE.content observes it
/// drawn (ok, on the air, at its own time), GENERATOR RETIME moves the picture the projector shows and the time STATE reports, and
/// the same own time later draws the same picture again (the renderer observes that time).</summary>
[Collection("render host")]
public sealed class StageEffectRetimeTests
{
    private const int W = 160;
    private const int H = 90;

    /// <summary>A 2 × 1 m wall facing +Z showing a sweep along x (0.3 m/s, 0.2 m band, counted from the leader's epoch), a lamp
    /// 2.5 m back framing it, one projector output.</summary>
    private static Project Stage()
    {
        var wall = new MeshAsset("ms_wall", "Wall", MeshOrigin.Primitive, "", "", "plane",
            ParamSet.Empty.With("width", 2.0).With("height", 1.0).With("segments", 1), 0, 0);
        var sweep = new Generator("gn_sweep", "Sweep", "effect.sweep", ParamSet.Empty.With("speed", 0.3).With("width", 0.2)) with { OriginNs = 0 };
        var surface = new Surface("sf_wall", "Wall", "ms_wall", Transform3.Identity, [], SurfaceContent.Empty with { Source = SourceRef.Generator(sweep.Id) },
            1, [], true, false, "#ffffff", []);
        var lamp = new Projector("pj_lamp", "Lamp", Intrinsics.FromThrowRatio(W, H, 1.0), LensDistortion.None,
            Pose.LookAt(new Vec3d(0, 0, 2.5), Vec3d.Zero, Vec3d.UnitY), 0, "#ffffff", false, [], SolveLocks.None, 0);
        var output = HostRig.Screen("op_lamp", SourceRef.None, W, H) with { Mode = OutputMode.Projector, ProjectorId = lamp.Id };
        var p = Project.CreateDefault("Stage");
        return p with
        {
            Generators = p.Generators.Add(sweep),
            Stage = Core.Model.Stage.Empty with { Surfaces = [surface], Meshes = [wall], Projectors = [lamp] },
            Outputs = [output],
        };
    }

    private static JsonElement Sweep(HostRig rig) =>
        JsonDocument.Parse(rig.Engine.State().Json).RootElement.GetProperty("content").GetProperty("generators").EnumerateArray()
            .Single(g => g.GetProperty("id").GetString() == "gn_sweep").Clone();

    /// <summary>Pixels the band lights (its colour is white over black).</summary>
    private static int Lit(byte[] bgra)
    {
        var n = 0;
        for (var i = 0; i < bgra.Length; i += 4)
            if (bgra[i] + bgra[i + 1] + bgra[i + 2] > 300)
                n++;
        return n;
    }

    [Fact]
    public async Task RetimeMovesTheSweepAProjectorShowsAndStateObservesItDrawn()
    {
        await using var rig = HostRig.Start(Stage(), options: RenderHostOptions.Default with { Windows = false, Engine = new RenderEngineOptions(Dither: false) });
        // Leader 5 s, origin 0: 5 s of its own time puts the band 0.3 m right of the wall's middle (once the wall's mesh is in).
        var before = await rig.ReadUntil("op_lamp", px => Lit(px) > 0);
        Assert.True(Lit(before) > 50, $"the band lit only {Lit(before)} pixels");
        var drawn = Sweep(rig);
        Assert.Equal("ok", drawn.GetProperty("state").GetString());
        Assert.True(drawn.GetProperty("onAir").GetBoolean());
        Assert.Equal(5.0, drawn.GetProperty("timeS").GetDouble(), 3);
        Assert.Equal("", drawn.GetProperty("words").GetString());

        // RETIME at the leader's now: its own time starts again from 0, where the band is still off the wall's left edge.
        await rig.Ok("GENERATOR RETIME gn_sweep");
        var retimed = await rig.Read("op_lamp");
        Assert.Equal(0, Lit(retimed));
        var after = Sweep(rig);
        Assert.Equal(5_000_000_000, after.GetProperty("originNs").GetInt64());
        Assert.Equal(0.0, after.GetProperty("timeS").GetDouble(), 3);
        Assert.Equal("ok", after.GetProperty("state").GetString());
        Assert.True(after.GetProperty("onAir").GetBoolean());

        // Five seconds on it is 5 s into its own time again: the picture it drew before the RETIME.
        rig.Clock.NowNs += 5_000_000_000;
        var again = await rig.Read("op_lamp");
        Assert.True(before.AsSpan().SequenceEqual(again), "the same own time drew another picture");
        Assert.True(rig.Engine.Content.Frame!.Stats.TryGet("gn_sweep", out var live));
        Assert.Equal(5_000_000_000, live.TimeNs);
    }
}
