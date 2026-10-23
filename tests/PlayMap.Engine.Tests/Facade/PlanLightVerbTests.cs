using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Facade;

/// <summary>PLAN LIGHT through the wire on a surveyed front: one 10 000 lm projector at throw ratio 1.5, 18 m off, lights its 12 × 6.75 m
/// picture to derate · Φ / area; the reply carries every field per surface; AMBIENT gives the contrast; a word out of range or a surface
/// that is not there is refused with the syntax or the words; the plan reads the session's own project and changes nothing.</summary>
public sealed class PlanLightVerbTests
{
    [Fact]
    public async Task ThePlanAnswersEverySurfacesLightAndChangesNothing()
    {
        await using var rig = new SiteRig();
        var obj = string.Create(CultureInfo.InvariantCulture,
            $"v 512330 5401230 110\nv 512350 5401230 110\nv 512350 5401230 130\nv 512330 5401230 130\nf 1 2 3\nf 1 3 4\n");
        await File.WriteAllTextAsync(rig.File("front.obj"), obj);
        Assert.Equal("ready", (await rig.Import(rig.File("front.obj"), "CRS EPSG:25832 NAME Front")).State);
        var k = Intrinsics.FromThrowRatio(1920, 1080, 1.5);
        var pj = new Projector("pj00000001", "P1", k, LensDistortion.None, Pose.LookAt(new Vec3d(0, 0, 18), Vec3d.Zero, Vec3d.UnitY), 10_000, "#FFFFFF",
            false, [], SolveLocks.None, 0);
        Assert.True((await rig.Engine.EditAsync(SiteRig.Desk, "projector", p => p.WithStage(st => st with { Projectors = [pj] }))).Changed);
        var revision = rig.Engine.Store.Current.Revision;

        var reply = SiteRig.Json(await rig.Ok("PLAN LIGHT ALL AMBIENT 5 DERATE 0.7 TARGET 50"));
        var s = Assert.Single(reply.GetProperty("surfaces").EnumerateArray());
        foreach (var key in new[] { "id", "name", "areaM2", "coverage", "luxP5", "luxP50", "luxP95", "nitsP50", "contrastP5", "pitchMmP50", "pitchMmP95", "stackDepth", "deficitLm", "words" })
            Assert.True(s.TryGetProperty(key, out _), key);
        // 0.7 × 10 000 lm over 12 × 6.75 m: 86 lux at the picture's middle, a little less off its axis.
        Assert.InRange(s.GetProperty("luxP50").GetDouble(), 80, 87);
        Assert.InRange(s.GetProperty("areaM2").GetDouble(), 399, 401);
        Assert.InRange(s.GetProperty("coverage").GetDouble(), 0.19, 0.22);
        Assert.InRange(s.GetProperty("contrastP5").GetDouble(), 14, 19);
        Assert.Equal(0, s.GetProperty("deficitLm").GetDouble());
        Assert.InRange(s.GetProperty("pitchMmP50").GetDouble(), 6.1, 6.4);
        Assert.Equal(1, s.GetProperty("stackDepth").GetInt32());

        Assert.Contains("DERATE 2: out of range", await rig.Refused("PLAN LIGHT DERATE 2"), StringComparison.Ordinal);
        Assert.Contains("no surface Nowhere", await rig.Refused("PLAN LIGHT Nowhere"), StringComparison.Ordinal);
        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
    }
}
