using System.Globalization;
using System.Text;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Facade;

/// <summary>SITE POINTS and SITE CHECK through the wire: a 14-point PNEZD file imports 14 control points with the file's decimals
/// exact; SITE CHECK on the surveyed model is green, and on the model moved 0.2 m names every point "20 cm" in red; a pin and a
/// position prior bound to a control point follow it when it is re-surveyed and when the site moves.</summary>
public sealed class ControlPointVerbTests
{
    private const double North = 5_401_234.567;

    /// <summary>A georeferenced facade model (a 60 × 30 m plane at N = 5 401 234.567, OBJ, Z-up grid coordinates) imported as a
    /// survey, and the 14 points surveyed on it.</summary>
    internal static async Task<SiteRig> Surveyed()
    {
        var rig = new SiteRig();
        var obj = string.Create(CultureInfo.InvariantCulture,
            $"v 512310 {North:0.000} 100\nv 512370 {North:0.000} 100\nv 512370 {North:0.000} 130\nv 512310 {North:0.000} 130\nf 1 2 3\nf 1 3 4\n");
        await File.WriteAllTextAsync(rig.File("front.obj"), obj);
        var job = await rig.Import(rig.File("front.obj"), "CRS EPSG:25832");
        Assert.Equal("ready", job.State);
        await File.WriteAllTextAsync(rig.File("points.csv"), Points(0));
        return rig;
    }

    internal static string Points(double eastShift)
    {
        var sb = new StringBuilder("Point,Northing,Easting,Elevation,Description\n");
        for (var i = 0; i < 14; i++)
            sb.Append(CultureInfo.InvariantCulture, $"CP{i + 1},{North:0.000},{512_315.123 + i * 3.5 + (i == 0 ? eastShift : 0):0.000},{102.5 + (i % 5) * 5.25:0.000},corner\n");
        return sb.ToString();
    }

    [Fact]
    public async Task FourteenPointsComeInAndTheModelIsCheckedAgainstThem()
    {
        await using var rig = await Surveyed();
        var reply = SiteRig.Json(await rig.Ok($"SITE POINTS {WireGrammar.Quote(rig.File("points.csv"))}"));
        Assert.Equal(14, reply.GetProperty("added").GetInt32());
        Assert.Equal(14, reply.GetProperty("ids").GetArrayLength());
        Assert.Equal("PNEZD", reply.GetProperty("order").GetString());
        var p = rig.Engine.Store.Project;
        Assert.Equal(14, p.Stage.Controls.Length);
        Assert.Equal(512_315.123, p.Stage.Controls[0].Grid.X);
        Assert.Equal(North, p.Stage.Controls[0].Grid.Y);
        Assert.Equal(14, rig.State().GetProperty("stage").GetProperty("controls").GetInt32());
        Assert.Contains("would change nothing", await rig.Refused($"SITE POINTS {WireGrammar.Quote(rig.File("points.csv"))}"), StringComparison.Ordinal);

        var good = SiteRig.Json(await rig.Ok("SITE CHECK"));
        Assert.Equal("green", good.GetProperty("light").GetString());
        Assert.All(good.GetProperty("points").EnumerateArray(), x => Assert.True(x.GetProperty("distanceM").GetDouble() < 0.001));

        // The model moved 0.2 m (toward grid north): every point is named 20 cm and red.
        var surface = p.Stage.Surfaces[0];
        var r = await rig.Engine.EditAsync(SiteRig.Desk, "move the model", pp => pp.WithStage(st => st with
        {
            Surfaces = st.Surfaces.Update(surface.Id, s => s with { Transform = s.Transform with { Position = s.Transform.Position + new Vec3d(0, 0, -0.2) } }),
        }));
        Assert.True(r.Changed);
        var bad = SiteRig.Json(await rig.Ok("SITE CHECK"));
        Assert.Equal("red", bad.GetProperty("light").GetString());
        Assert.Equal(14, bad.GetProperty("points").GetArrayLength());
        Assert.All(bad.GetProperty("points").EnumerateArray(), x =>
        {
            Assert.Equal("red", x.GetProperty("light").GetString());
            Assert.Contains("20 cm", x.GetProperty("words").GetString(), StringComparison.Ordinal);
        });
        Assert.Equal(0.2, bad.GetProperty("fit").GetProperty("shiftM").GetDouble(), 3);
        var site = SiteRig.Json(rig.Engine.Context()).GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetString() == "site");
        Assert.Equal("red", site.GetProperty("light").GetString());
        // Any later change makes the check stale: unknown, not green.
        await rig.Ok("SITE MAINS 60");
        site = SiteRig.Json(rig.Engine.Context()).GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetString() == "site");
        Assert.Equal("grey", site.GetProperty("light").GetString());
        Assert.Contains("not checked since the last change", site.GetProperty("state").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SiteCheckFromADraftMeasuresTheDraftAndLeavesTheLiveCheckAlone()
    {
        await using var rig = await Surveyed();
        var draft = new ProjectStore(rig.Engine.Store.Project);
        rig.Engine.DraftForTests = s => s.SessionId == SiteRig.Desk.SessionId ? draft : null;
        var staged = SiteRig.Json(await rig.Ok($"SITE POINTS {WireGrammar.Quote(rig.File("points.csv"))}"));
        Assert.True(staged.GetProperty("staged").GetBoolean());
        Assert.Equal(14, draft.Project.Stage.Controls.Length);
        Assert.Empty(rig.Engine.Store.Project.Stage.Controls);

        // The draft's session measures what it staged: the draft's points on the draft's model.
        var check = SiteRig.Json(await rig.Ok("SITE CHECK"));
        Assert.Equal("green", check.GetProperty("light").GetString());
        Assert.Equal(14, check.GetProperty("points").GetArrayLength());
        // The live stage has no points: the site's context item and preflight never take the draft's measure as the stage's.
        Assert.Null(rig.Engine.LastSiteCheck);
        rig.Engine.DraftForTests = null;
        Assert.Contains("no control points", await rig.Refused("SITE CHECK"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABoundPinAndPriorFollowTheirPoint()
    {
        await using var rig = await Surveyed();
        var ids = SiteRig.Json(await rig.Ok($"SITE POINTS {WireGrammar.Quote(rig.File("points.csv"))}")).GetProperty("ids");
        var cp1 = ids[0].GetString()!;
        await rig.Ok("ADD projector {\"name\":\"P1\"}");
        var pj = rig.Engine.Store.Project.Stage.Projectors[0].Id;
        var pin = await rig.Ok($"ADD pin {pj} {{\"controlId\":\"{cp1}\",\"pixel\":{{\"x\":100,\"y\":200}}}}");
        for (var i = 1; i < 4; i++)
            await rig.Ok($"ADD pin {pj} {{\"controlId\":\"{ids[i].GetString()}\",\"pixel\":{{\"x\":{100 + i * 300},\"y\":{200 + i * 50}}}}}");
        await rig.Ok($"PROJECTOR PRIOR {pj} AT CP1 ±0.02");

        // A pin added bound but not at its point: a solve refuses it and names the line that puts it there.
        var refused = await rig.Refused($"PROJECTOR SOLVE {pj}");
        Assert.Contains("pin 1 is bound to CP1 but is not at it", refused, StringComparison.Ordinal);
        var fix = refused[(refused.IndexOf("SET ", StringComparison.Ordinal))..];
        Assert.Contains("a pin bound to CP1 sits at it", await rig.Refused($"SET {pin} world {{\"x\":1,\"y\":2,\"z\":3}}"), StringComparison.Ordinal);
        await rig.Ok(fix);
        var placed = rig.Engine.Store.Project.FindProjector(pj)!.Pins.Single(x => x.Id == pin);
        var cpNow = rig.Engine.Store.Project.Stage.Controls.Find(cp1)!;
        Assert.True((placed.World - rig.Engine.Store.Project.Stage.Site.ToStage(cpNow.Grid)).Length < 1e-9);
        // A bound point moved by hand would leave its pins behind: refused, with the verb that moves them together.
        Assert.Contains("re-survey it with SITE POINTS", await rig.Refused($"SET {cp1} grid {{\"x\":1,\"y\":2,\"z\":3}}"), StringComparison.Ordinal);

        // Re-surveyed one metre east: the pin and the prior follow in the same edit.
        await File.WriteAllTextAsync(rig.File("points2.csv"), Points(1.0));
        var moved = SiteRig.Json(await rig.Ok($"SITE POINTS {WireGrammar.Quote(rig.File("points2.csv"))}"));
        Assert.Equal(0, moved.GetProperty("added").GetInt32());
        Assert.Equal(1, moved.GetProperty("updated").GetInt32());
        var p = rig.Engine.Store.Project;
        var cp = p.Stage.Controls.Find(cp1)!;
        Assert.Equal(512_316.123, cp.Grid.X, 9);
        var stagePoint = p.Stage.Site.ToStage(cp.Grid);
        var bound = p.FindProjector(pj)!.Pins.Single(x => x.Id == pin);
        Assert.True((bound.World - stagePoint).Length < 1e-9, $"{bound.World} vs {stagePoint}");
        Assert.True((p.FindProjector(pj)!.Priors.Position!.Value - stagePoint).Length < 1e-9);
        Assert.Equal("at CP1 ±2 cm", rig.State().GetProperty("stage").GetProperty("projectors")[0].GetProperty("priors").GetString());

        // The site moves: the pin's world point moves with the stage and still is its point's.
        await rig.Ok("SITE ORIGIN 512300 5401240 100 ROTATE 10");
        p = rig.Engine.Store.Project;
        bound = p.FindProjector(pj)!.Pins.Single(x => x.Id == pin);
        Assert.True((bound.World - p.Stage.Site.ToStage(cp.Grid)).Length < 1e-9);

        // A pin naming a control point that is not there is refused.
        Assert.Contains("no control point", await rig.Refused($"SET {pin} controlId \"cp_nowhere\""), StringComparison.Ordinal);
    }
}
