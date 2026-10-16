using PlayMap.Core.Calibration;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Engine.Tests.Wall;

namespace PlayMap.Engine.Tests.Facade;

/// <summary>A re-solve is a solve: CALIBRATION RESOLVE from pins solves as PROJECTOR SOLVE does with no LOCK word — the projector's
/// priors as rows, the zoom locked from the lens when the pins cannot carry it (and the lock answered with the optics), a bound pin
/// away from its control point refused with the line that puts it there; CALIBRATION RESOLVE … CAMERA weighs a position prior beside
/// the dots (the optics are held, so the lens prior has nothing to weigh). Without priors both solve as before.</summary>
public sealed class ResolvePriorTests
{
    [Fact]
    public async Task AResolveFromPinsLocksTheZoomFromTheLensAsTheSolveDoes()
    {
        await using var rig = new SiteRig(LongThrowVerbTests.Facade(0));
        await rig.Ok("PROJECTOR PRIOR pj_far1 THROW 3.02 ±2");
        var reply = SiteRig.Json(await rig.Ok("CALIBRATION RESOLVE pj_far1"));
        var row = reply.GetProperty("projectors")[0];
        Assert.True(row.GetProperty("applied").GetBoolean(), row.GetProperty("why").GetString());
        Assert.Equal(["focal"], row.GetProperty("locked").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["throw"], row.GetProperty("priorsUsed").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains(LongThrowSolve.LockedWords, reply.GetProperty("words").GetString(), StringComparison.Ordinal);
        var pj = rig.Engine.Store.Project.FindProjector("pj_far1")!;
        Assert.Equal(3.02, pj.Intrinsics.ThrowRatio, 9);
        Assert.Equal(SolveLocks.Focal, pj.Locks & SolveLocks.Focal);
        // The ledger's result is the projector as re-solved, its lock with it.
        Assert.Equal(pj.Locks, rig.Engine.Store.Project.FindCalibration(pj.CalibrationId!)!.Projector!.Locks);
    }

    [Fact]
    public async Task WithoutAPriorTheResolveIsTheSolveItWasAndNamesTheWeakness()
    {
        await using var rig = new SiteRig(LongThrowVerbTests.Facade(0));
        var before = rig.Engine.Store.Project.FindProjector("pj_far1")!;
        var expected = PoseSolver.Solve(before, PoseSolveOptions.Default with { Locks = before.Locks, InitialPose = before.Pose });
        var reply = SiteRig.Json(await rig.Ok("CALIBRATION RESOLVE pj_far1"));
        var row = reply.GetProperty("projectors")[0];
        Assert.Equal(0, row.GetProperty("locked").GetArrayLength());
        Assert.Equal(0, row.GetProperty("priorsUsed").GetArrayLength());
        Assert.Contains("PROJECTOR PRIOR", reply.GetProperty("words").GetString(), StringComparison.Ordinal);
        // Applied or staged by the gate, the ledger's result is the solve's.
        var result = rig.Engine.Store.Project.FindCalibration(row.GetProperty("snapshot").GetString()!)!.Projector!;
        Assert.Equal(expected.Intrinsics, result.Intrinsics);
        Assert.Equal(expected.Pose, result.Pose);
        Assert.Equal(before.Locks, result.Locks);
    }

    [Fact]
    public async Task AResolveRefusesABoundPinAwayFromItsControlPoint()
    {
        var p = LongThrowVerbTests.Facade(2);
        var pj = p.Stage.Projectors[0];
        var w = pj.Pins[0].World;
        // The site is unset: a control point's grid (E, N, H) is the stage's (X, −Z, Y). This one is 5 cm above the pin's point.
        var cp = new ControlPoint("cp_resolve1", "CP1", new Vec3d(w.X, -w.Z, w.Y + 0.05), 0.01, "", true);
        var bound = pj with { Pins = pj.Pins.SetItem(0, pj.Pins[0] with { ControlId = cp.Id }) };
        p = p with { Stage = p.Stage with { Controls = [cp], Projectors = [bound] } };
        await using var rig = new SiteRig(p);
        var before = rig.Engine.Store.Project.FindProjector("pj_far1")!;
        var words = await rig.Refused("CALIBRATION RESOLVE pj_far1");
        Assert.Contains($"pin 1 is bound to CP1 but is not at it — SET {pj.Pins[0].Id} world", words, StringComparison.Ordinal);
        Assert.Same(before, rig.Engine.Store.Project.FindProjector("pj_far1"));
        Assert.Empty(rig.Engine.Store.Project.Stage.Calibrations);
    }

    /// <summary>Three solved projectors and the room camera, P2 knocked 0.8° and 2 cm; with <paramref name="prior"/> P2 carries that
    /// position prior. Answers P2 as re-solved from the camera and the reply's row.</summary>
    private static async Task<(Projector After, System.Text.Json.JsonElement Row)> KnockAndResolve(string? prior)
    {
        await using var rig = new WallRig(CameraStage.With(WallRig.ProjectorsWithPins(3, noisePx: 0.1)));
        for (var i = 1; i <= 3; i++)
            await rig.Ok($"PROJECTOR SOLVE pj_test{i}");
        for (var n = 0; n < 3; n++)
            rig.Engine.SimulatedStage.Knock(rig.Project.FindProjector($"pj_test{n + 1}")! with { Pose = WallRig.TruePose(n) }, Vec3d.Zero, Vec3d.Zero);
        var axis = new Vec3d(0.6, 0.8, 0).Normalized() * (0.8 * Math.PI / 180);
        rig.Engine.SimulatedStage.Knock(rig.Project.FindProjector("pj_test2")! with { Pose = WallRig.TruePose(1) }, axis, new Vec3d(0.02, 0, 0));
        if (prior is not null)
            await rig.Ok("PROJECTOR PRIOR pj_test2 AT " + prior);
        var reply = WallRig.Json(await rig.Ok("CALIBRATION RESOLVE pj_test1 pj_test2 pj_test3 CAMERA cm_sim"));
        var row = reply.GetProperty("projectors")[0];
        Assert.Equal("pj_test2", row.GetProperty("id").GetString());
        Assert.True(row.GetProperty("applied").GetBoolean(), row.GetProperty("why").GetString());
        return (rig.Project.FindProjector("pj_test2")!, row);
    }

    [Fact]
    public async Task ACameraResolveWeighsThePositionPriorBesideTheDots()
    {
        // The prior 30 cm to the side of where P2 truly stands, held to 2 mm: the dots and the prior meet between them.
        var truth = WallRig.TruePose(1).Position + new Vec3d(0.02, 0, 0);
        var at = truth + new Vec3d(0.3, 0, 0);
        var (free, freeRow) = await KnockAndResolve(null);
        var (held, heldRow) = await KnockAndResolve(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{at.X:R} {at.Y:R} {at.Z:R} ±0.002"));
        Assert.Equal(0, freeRow.GetProperty("priorsUsed").GetArrayLength());
        Assert.Equal(["position"], heldRow.GetProperty("priorsUsed").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(0, heldRow.GetProperty("locked").GetArrayLength());
        var freeGap = (free.Pose.Position - at).Length;
        var heldGap = (held.Pose.Position - at).Length;
        Assert.True(heldGap < freeGap - 0.01, $"with the prior {heldGap:0.000} m from it, without {freeGap:0.000} m");
        // The optics are held either way.
        Assert.Equal(free.Intrinsics, held.Intrinsics);
    }
}
