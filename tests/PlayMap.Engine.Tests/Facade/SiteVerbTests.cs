using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Engine.Tests.Facade;

/// <summary>SITE, SITE ORIGIN and SITE MAINS through the wire: the first origin takes the stage's coordinates as they are; moving the
/// origin (and turning it) moves the whole stage in one undoable edit so every grid position is kept and every projector's
/// reprojection of every pin is unchanged within 1e-6 px; a verb that would change nothing is refused with the reason.</summary>
public sealed class SiteVerbTests
{
    private static Project Stage()
    {
        var k = Intrinsics.FromThrowRatio(1920, 1080, 1.5);
        var pose = Pose.LookAt(new Vec3d(2, 3, 12), new Vec3d(0, 2, 0), Vec3d.UnitY);
        var world = new List<Vec3d>();
        for (var i = 0; i < 8; i++)
            world.Add(new Vec3d(-3 + i * 0.8, 0.5 + (i % 3) * 1.1, (i % 2) * 0.7));
        var pj = SiteRig.ProjectorWithPins("pj_site1", k, pose, world);
        var surface = new Surface("sf_site1", "Wall", "ms_site1", Transform3.Identity with { Position = new Vec3d(1, 0, -2) }, [], SurfaceContent.Empty, 1, [],
            true, false, "#FFFFFF", []);
        var cam = new CameraRig("cm_site1", "Cam", CameraSourceKind.Simulated, "", k, LensDistortion.None, Pose.LookAt(new Vec3d(-4, 2, 9), Vec3d.Zero, Vec3d.UnitY), true);
        var p = Project.CreateDefault("Site test");
        return p with
        {
            Stage = p.Stage with
            {
                Projectors = [pj],
                Surfaces = [surface],
                Cameras = [cam],
                Meshes = [new MeshAsset("ms_site1", "Wall", MeshOrigin.Primitive, "", "", "plane", ParamSet.Empty, 4, 2)],
            },
        };
    }

    private static double WorstPinReprojection(Project before, Project after)
    {
        var worst = 0.0;
        foreach (var a in before.Stage.Projectors)
        {
            var b = after.FindProjector(a.Id)!;
            for (var i = 0; i < a.Pins.Length; i++)
            {
                var pa = CameraModel.Project(a.Pins[i].World, a.Intrinsics, a.Lens, a.Pose)!.Value;
                var pb = CameraModel.Project(b.Pins[i].World, b.Intrinsics, b.Lens, b.Pose)!.Value;
                worst = Math.Max(worst, (pa - pb).Length);
            }
        }
        return worst;
    }

    [Fact]
    public async Task TheFirstOriginTakesTheStageAsItIsAndAMoveKeepsEveryGridPositionAndPin()
    {
        await using var rig = new SiteRig(Stage());
        var site = SiteRig.Json(await rig.Ok("SITE"));
        Assert.False(site.GetProperty("set").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, rig.State().GetProperty("stage").GetProperty("site").ValueKind);

        var start = rig.Engine.Store.Project;
        var first = SiteRig.Json(await rig.Ok("SITE ORIGIN 512340 5401230 110 CRS EPSG:25832"));
        Assert.Equal(0, first.GetProperty("moved")[0].GetDouble());
        Assert.Equal(start.Stage.Projectors[0].Pose, rig.Engine.Store.Project.Stage.Projectors[0].Pose);
        var state = rig.State().GetProperty("stage").GetProperty("site");
        Assert.Equal("EPSG:25832", state.GetProperty("crs").GetString());
        Assert.Equal(5401230, state.GetProperty("origin")[1].GetDouble());
        Assert.Contains("would change nothing", await rig.Refused("SITE ORIGIN 512340 5401230 110"), StringComparison.Ordinal);

        var before = rig.Engine.Store.Project;
        var gridBefore = before.Stage.Site.ToGrid(before.Stage.Projectors[0].Pose.Position);
        var surfaceGrid = before.Stage.Site.ToGrid(before.Stage.Surfaces[0].Transform.Apply(new Vec3d(1, 2, 3)));
        var moved = SiteRig.Json(await rig.Ok("SITE ORIGIN 512360.5 5401221.25 108 ROTATE 15"));
        Assert.NotEqual(0, moved.GetProperty("moved")[0].GetDouble());
        var after = rig.Engine.Store.Project;
        Assert.Equal(15, after.Stage.Site.RotationDeg);
        Assert.True((after.Stage.Site.ToGrid(after.Stage.Projectors[0].Pose.Position) - gridBefore).Length < 1e-9);
        Assert.True((after.Stage.Site.ToGrid(after.Stage.Surfaces[0].Transform.Apply(new Vec3d(1, 2, 3))) - surfaceGrid).Length < 1e-9);
        Assert.True((after.Stage.Site.ToGrid(after.Stage.Cameras[0].Pose.Position) - before.Stage.Site.ToGrid(before.Stage.Cameras[0].Pose.Position)).Length < 1e-9);
        Assert.True(WorstPinReprojection(before, after) <= 1e-6, $"{WorstPinReprojection(before, after)} px");
        Assert.True((after.Stage.Projectors[0].Pose.Position - before.Stage.Projectors[0].Pose.Position).Length > 10);

        // One undo step takes the whole move back.
        await rig.Ok("UNDO");
        Assert.Equal(before.Stage, rig.Engine.Store.Project.Stage);
    }

    [Fact]
    public async Task MainsAndWordsTheSiteRefuses()
    {
        await using var rig = new SiteRig();
        Assert.Equal(50, SiteRig.Json(await rig.Ok("SITE MAINS 50")).GetProperty("mainsHz").GetInt32());
        Assert.Contains("would change nothing", await rig.Refused("SITE MAINS 50"), StringComparison.Ordinal);
        Assert.Contains("SITE MAINS 50|60", await rig.Refused("SITE MAINS 55"), StringComparison.Ordinal);
        Assert.Contains("not a coordinate system", await rig.Refused("SITE ORIGIN 1 2 3 CRS WGS84"), StringComparison.Ordinal);
        await rig.Ok("SITE ORIGIN 1 2 3 LOCAL");
        Assert.Equal("LOCAL", rig.Engine.Store.Project.Stage.Site.Crs);
        Assert.Equal(50, rig.Engine.Store.Project.Stage.Site.MainsHz);
    }
}
