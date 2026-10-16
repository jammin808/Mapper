using PlayMap.Core.Calibration;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Engine.Tests.Facade;

/// <summary>PROJECTOR PRIOR and PROJECTOR SOLVE at 100 m through the wire: a flat facade seen head-on with a lens prior locks the zoom
/// from the lens with the words, and STATE carries the uncertainty and the prior; without the prior the solve is named weak; with
/// relief the prior joins the solve as a row; the uncertainty leads with the wall.</summary>
public sealed class LongThrowVerbTests
{
    private static Project Facade(double relief, ulong seed = 3)
    {
        var k = Intrinsics.FromThrowRatio(3840, 2160, 3);
        var truth = Pose.LookAt(new Vec3d(0, 10, 100), new Vec3d(0, 10, 0), Vec3d.UnitY);
        var world = new List<Vec3d>();
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 4; c++)
            {
                var px = new Vec2d((0.1 + 0.8 * c / 3) * k.Width, (0.1 + 0.8 * r / 2) * k.Height);
                var dir = truth.CameraToWorld * Core.Geometry.CameraModel.PixelRayCamera(px, k, LensDistortion.None);
                var z = relief * (((r * 4 + c) * 5) % 12) / 11.0;
                world.Add(truth.Position + dir * ((z - truth.Position.Z) / dir.Z));
            }
        // The solve starts from a lens 4 % off the truth, where an operator's guess would be.
        var pj = SiteRig.ProjectorWithPins("pj_far1", k, truth, world, sigmaPx: 0.8, seed: seed) with
        {
            Intrinsics = Intrinsics.FromThrowRatio(3840, 2160, 3.12),
            Pose = new Pose(truth.Position + new Vec3d(0.5, -0.3, 2), truth.Rotation),
        };
        var p = Project.CreateDefault("Long throw");
        return p with { Stage = p.Stage with { Projectors = [pj] } };
    }

    [Fact]
    public async Task AFlatFacadeWithALensPriorLocksTheZoomAndSaysSo()
    {
        await using var rig = new SiteRig(Facade(0));
        var prior = SiteRig.Json(await rig.Ok("PROJECTOR PRIOR pj_far1 THROW 3.02 ±2"));
        Assert.Equal(3.02, prior.GetProperty("priors").GetProperty("throw").GetDouble());
        Assert.Contains("would change nothing", await rig.Refused("PROJECTOR PRIOR pj_far1 THROW 3.02 ±2"), StringComparison.Ordinal);
        Assert.Equal("TR 3.02 ±2 %", rig.State().GetProperty("stage").GetProperty("projectors")[0].GetProperty("priors").GetString());

        var r = await rig.Run("PROJECTOR SOLVE pj_far1");
        Assert.True(r.Ok, r.Payload);
        var solve = SiteRig.Json(r.Payload!);
        Assert.Equal("focal", solve.GetProperty("locked")[0].GetString());
        Assert.Contains(LongThrowSolve.LockedWords, solve.GetProperty("words").GetString(), StringComparison.Ordinal);
        Assert.Equal("throw", solve.GetProperty("priorsUsed")[0].GetString());
        var u = solve.GetProperty("uncertainty");
        Assert.True(u.GetProperty("wallPx").GetDouble() > 0);
        Assert.Equal(0, u.GetProperty("focalPct").GetDouble());
        var pj = rig.Engine.Store.Project.FindProjector("pj_far1")!;
        Assert.Equal(3.02, pj.Intrinsics.ThrowRatio, 9);
        Assert.Equal(SolveLocks.Focal, pj.Locks & SolveLocks.Focal);
        var state = rig.State().GetProperty("stage").GetProperty("projectors")[0].GetProperty("uncertainty");
        Assert.Equal(pj.Uncertainty!.WallPx, state.GetProperty("wallPx").GetDouble(), 3);
        Assert.Equal(0, state.GetProperty("focalPct").GetDouble());
    }

    [Fact]
    public async Task WithoutAPriorTheSolveIsNamedWeak()
    {
        await using var rig = new SiteRig(Facade(0));
        var solve = SiteRig.Json(await rig.Ok("PROJECTOR SOLVE pj_far1"));
        Assert.Equal(0, solve.GetProperty("locked").GetArrayLength());
        Assert.Equal("zoom↔distance", solve.GetProperty("uncertainty").GetProperty("weak").GetString());
        Assert.Contains("PROJECTOR PRIOR", solve.GetProperty("words").GetString(), StringComparison.Ordinal);
        Assert.Equal("zoom↔distance", rig.State().GetProperty("stage").GetProperty("projectors")[0].GetProperty("uncertainty").GetProperty("weak").GetString());
    }

    [Fact]
    public async Task OnReliefThePriorJoinsTheSolveAndThePositionPriorNarrowsTheDistance()
    {
        await using var rig = new SiteRig(Facade(2));
        await rig.Ok("PROJECTOR PRIOR pj_far1 THROW 3 ±2");
        var withThrow = SiteRig.Json(await rig.Ok("PROJECTOR SOLVE pj_far1"));
        Assert.Equal(0, withThrow.GetProperty("locked").GetArrayLength());
        var axisWithThrow = withThrow.GetProperty("uncertainty").GetProperty("axisM").GetDouble();
        await rig.Ok("PROJECTOR PRIOR pj_far1 AT 0 10 100 ±0.05");
        var withBoth = SiteRig.Json(await rig.Ok("PROJECTOR SOLVE pj_far1"));
        var priors = withBoth.GetProperty("priorsUsed").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Equal(["throw", "position"], priors);
        var axis = withBoth.GetProperty("uncertainty").GetProperty("axisM").GetDouble();
        Assert.True(axis < axisWithThrow, $"{axis} vs {axisWithThrow}");
        Assert.True(axis < 0.1, $"{axis}");
        await rig.Ok("PROJECTOR PRIOR pj_far1 CLEAR");
        Assert.Equal("", rig.State().GetProperty("stage").GetProperty("projectors")[0].GetProperty("priors").GetString());
    }
}
