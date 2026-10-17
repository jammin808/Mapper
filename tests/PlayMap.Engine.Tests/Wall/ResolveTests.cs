using System.Text.Json;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using PlayMap.Core.Wire;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Wall;

/// <summary>Re-solving the projector that moved, from its pins: of three solved projectors one is knocked 2 cm (the simulator's
/// Bump), its pins are dragged to where the wall shows their points now, and CALIBRATION RESOLVE on it alone brings its picture
/// back within half a pixel of the truth; the other two keep their poses and results exactly. A result that fails the gate is
/// staged with words and an event, the projector untouched until a person accepts it.</summary>
public sealed class ResolveTests(ITestOutputHelper output)
{
    /// <summary>Pins dragged to where a device now lights their world points, with pixel noise σ.</summary>
    private static async Task DragPins(WallRig rig, string projectorId, SimDevice truth, double noisePx, ulong seed)
    {
        var rng = new Pcg32(seed);
        var r = await rig.Engine.EditAsync(WallRig.Desk, "drag pins", p => p.WithStage(st => st with
        {
            Projectors = st.Projectors.Update(projectorId, pj => pj with
            {
                Pins = [.. pj.Pins.Select(pin => pin with
                {
                    Pixel = CameraModel.Project(pin.World, truth.Intrinsics, truth.Lens, truth.Pose)!.Value
                        + new Vec2d(rng.NextGaussian() * noisePx, rng.NextGaussian() * noisePx),
                })],
            }),
        }));
        Assert.True(r.Changed, r.Effect);
    }

    /// <summary>RMS over a grid of the raster of how far a solved projector's picture lands from the truth's, at 5 m.</summary>
    private static double WallRmsPx(Projector solved, SimDevice truth)
    {
        double sum = 0;
        var n = 0;
        for (var y = 0; y < 12; y++)
            for (var x = 0; x < 20; x++)
            {
                var px = new Vec2d((x + 0.5) / 20 * WallRig.W, (y + 0.5) / 12 * WallRig.H);
                var dir = CameraModel.PixelRayCamera(px, truth.Intrinsics, truth.Lens);
                var world = truth.Pose.CameraToWorldPoint(dir * (5 / dir.Z));
                var seen = CameraModel.Project(world, solved.Intrinsics, solved.Lens, solved.Pose)!.Value;
                sum += (seen - px).LengthSquared;
                n++;
            }
        return Math.Sqrt(sum / n);
    }

    private static async Task<WallRig> ThreeSolved()
    {
        var rig = new WallRig(WallRig.ProjectorsWithPins(3, noisePx: 0.1));
        for (var i = 1; i <= 3; i++)
            await rig.Ok($"PROJECTOR SOLVE pj_test{i}");
        return rig;
    }

    /// <summary>A stop begun once the re-solve's edit has landed keeps the new pose and says the blend was not solved again, only
    /// when one was due: a projector whose output does not blend automatically gets no words about blends.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AStopBegunOnceTheReSolveLandedKeepsItAndSaysOnlyADueBlendWasNotSolvedAgain(bool blends)
    {
        await using var rig = await ThreeSolved();
        if (!blends)
        {
            var off = await rig.Engine.EditAsync(WallRig.Desk, "no automatic blend", p => p with
            {
                Outputs = [.. p.Outputs.Select(o => o.ProjectorId == "pj_test2" ? o with { Blend = o.Blend with { Auto3D = false } } : o)],
            });
            Assert.True(off.Changed, off.Effect);
        }
        var truth = new SimDevice(WallRig.TrueOptics, LensDistortion.None, WallRig.TruePose(1));
        await DragPins(rig, "pj_test2", ScanSimulator.Bump(truth, Vec3d.Zero, new Vec3d(0.02, 0, 0)), 0.1, 7);
        var before = rig.Project.FindProjector("pj_test2")!;
        var revision = rig.Engine.Store.Current.Revision;
        // The stop begins on the lane the moment the re-solve's edit lands, before its blend step.
        void Landed(ProjectSnapshot s)
        {
            if (s.Revision > revision)
                rig.Engine.BeginStop();
        }
        rig.Engine.Store.Changed += Landed;
        JsonElement reply;
        try
        {
            reply = WallRig.Json(await rig.Ok("CALIBRATION RESOLVE pj_test2"));
        }
        finally
        {
            rig.Engine.Store.Changed -= Landed;
        }
        Assert.True(rig.Engine.Stopping);
        Assert.True(reply.GetProperty("projectors")[0].GetProperty("applied").GetBoolean());
        Assert.NotEqual(before.CalibrationId, rig.Project.FindProjector("pj_test2")!.CalibrationId);
        var blend = reply.GetProperty("blend").GetString()!;
        Assert.Equal(blends ? "the blend was not solved again: " + EngineCore.StoppingWords : "", blend);
        if (blends)
            Assert.EndsWith("; " + blend, reply.GetProperty("words").GetString(), StringComparison.Ordinal);
        Assert.Equal("done", rig.State().GetProperty("calibration").GetProperty("task").GetProperty("state").GetString());
    }

    [Fact]
    public async Task ResolveBringsTheKnockedProjectorBackAndTouchesNothingElse()
    {
        await using var rig = await ThreeSolved();
        var others = new[] { "pj_test1", "pj_test3" }.ToDictionary(id => id, id => rig.Project.FindProjector(id)!);
        var otherResults = rig.Project.Stage.Calibrations.Where(c => c.TargetId != "pj_test2").ToList();
        var surfaces = rig.Project.Stage.Surfaces;

        var truth = new SimDevice(WallRig.TrueOptics, LensDistortion.None, WallRig.TruePose(1));
        var knocked = ScanSimulator.Bump(truth, Vec3d.Zero, new Vec3d(0.02, 0, 0));
        Assert.Equal(20, (knocked.Pose.Position - truth.Pose.Position).Length * 1000, 6);
        var before = rig.Project.FindProjector("pj_test2")!;
        var stale = WallRmsPx(before, knocked);
        output.WriteLine($"knocked 2 cm: the old solve is {stale:0.00} px RMS off on the wall");
        Assert.True(stale > 5);

        await DragPins(rig, "pj_test2", knocked, 0.1, 7);
        var reply = WallRig.Json(await rig.Ok("CALIBRATION RESOLVE pj_test2"));
        var row = reply.GetProperty("projectors")[0];
        output.WriteLine(reply.GetProperty("words").GetString());
        Assert.True(row.GetProperty("applied").GetBoolean());
        var after = rig.Project.FindProjector("pj_test2")!;
        var error = WallRmsPx(after, knocked);
        output.WriteLine($"re-solved: {error:0.000} px RMS on the wall");
        Assert.True(error <= 0.5, $"{error} px RMS");
        Assert.Equal(row.GetProperty("snapshot").GetString(), after.CalibrationId);
        Assert.Equal(before.CalibrationId, rig.Project.FindCalibration(after.CalibrationId!)!.ParentId);
        Assert.Equal(before.FallbackId, after.FallbackId);
        Assert.Equal(CalibrationSource.Resolve, rig.Project.FindCalibration(after.CalibrationId!)!.Source);
        Assert.InRange(row.GetProperty("delta").GetProperty("mm").GetDouble(), 10, 30);

        foreach (var (id, pj) in others)
            Assert.Same(pj, rig.Project.FindProjector(id));
        Assert.Equal(otherResults, rig.Project.Stage.Calibrations.Where(c => c.TargetId != "pj_test2").ToList());
        Assert.Equal(surfaces, rig.Project.Stage.Surfaces);
        var task = rig.State().GetProperty("calibration").GetProperty("task");
        Assert.Equal("done", task.GetProperty("state").GetString());
        Assert.Equal("resolve", task.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task AResultUnderTheGateIsStagedForAPerson()
    {
        await using var rig = await ThreeSolved();
        var before = rig.Project.FindProjector("pj_test2")!;
        var truth = new SimDevice(WallRig.TrueOptics, LensDistortion.None, WallRig.TruePose(1));
        // Pins dragged roughly: 1.2 px of scatter puts the fit over the gate (C, not F).
        await DragPins(rig, "pj_test2", ScanSimulator.Bump(truth, Vec3d.Zero, new Vec3d(0.02, 0, 0)), 1.2, 9);
        var dragged = rig.Project.FindProjector("pj_test2")!;
        var reply = WallRig.Json(await rig.Ok("CALIBRATION RESOLVE pj_test2"));
        var row = reply.GetProperty("projectors")[0];
        Assert.False(row.GetProperty("applied").GetBoolean());
        Assert.True(row.GetProperty("staged").GetBoolean());
        Assert.Contains("over the gate", row.GetProperty("why").GetString(), StringComparison.Ordinal);
        var staged = row.GetProperty("snapshot").GetString()!;
        var now = rig.Project.FindProjector("pj_test2")!;
        Assert.Equal(dragged.Pose, now.Pose);
        Assert.Equal(before.CalibrationId, now.CalibrationId);
        Assert.Equal(staged, now.StagedId);
        lock (rig.Events)
            Assert.Contains(rig.Events, e => e.Kind == "calibration.staged" && e.FieldsJson.Contains(staged, StringComparison.Ordinal));
        var target = rig.State().GetProperty("calibration").GetProperty("targets")[1];
        Assert.Equal(staged, target.GetProperty("staged").GetString());
        var item = Assert.Single(rig.Engine.Context().Split("\"calibration:pj_test2\"").Skip(1));
        Assert.Contains("CALIBRATION ACCEPT", item, StringComparison.Ordinal);

        // A person accepts it: it is applied and becomes the fallback.
        await rig.Ok($"CALIBRATION ACCEPT {staged}");
        now = rig.Project.FindProjector("pj_test2")!;
        Assert.Equal(staged, now.CalibrationId);
        Assert.Equal(staged, now.FallbackId);
        Assert.Null(now.StagedId);
        Assert.Equal(rig.Project.FindCalibration(staged)!.Projector!.Pose, now.Pose);
    }

    private static JsonElement? TaskOf(WallRig rig) =>
        rig.State().GetProperty("calibration").GetProperty("task") is { ValueKind: JsonValueKind.Object } t ? t : null;

    [Fact]
    public async Task ACancelBeforeItsResultIsAppliedAppliesNothingAndTheNextOneRuns()
    {
        await using var rig = await ThreeSolved();
        var before = rig.Project.FindProjector("pj_test2")!;
        var results = rig.Project.Stage.Calibrations;
        using var hold = new ManualResetEventSlim();
        // The lane held by an edit: the re-solve's result has to wait for it once the solve is done.
        var held = rig.Engine.EditAsync(WallRig.Desk, "hold the lane", p =>
        {
            hold.Wait();
            return p;
        });
        using var call = new CancellationTokenSource();
        Assert.True(WireGrammar.TryParse("CALIBRATION RESOLVE pj_test2", out var line, out _));
        var resolving = rig.Engine.HandleAsync(line, WallRig.Desk, call.Token).AsTask();
        await WallRig.Until(() => TaskOf(rig) is { } t && t.GetProperty("progress").GetDouble() >= 1, "the solve to finish");
        await call.CancelAsync();
        hold.Set();
        await held;
        var reply = await resolving;
        Assert.False(reply.Ok);
        Assert.Equal("cancelled — nothing was applied", reply.Payload);
        Assert.Same(before, rig.Project.FindProjector("pj_test2"));
        Assert.Equal(results, rig.Project.Stage.Calibrations);
        Assert.Equal("cancelled", TaskOf(rig)!.Value.GetProperty("state").GetString());
        // The task is over: nothing is left to cancel, and the next re-solve is not held up behind it.
        Assert.Contains("no calibration task", await rig.Refused("CALIBRATION CANCEL"), StringComparison.Ordinal);
        var next = WallRig.Json(await rig.Ok("CALIBRATION RESOLVE pj_test2"));
        Assert.True(next.GetProperty("projectors")[0].GetProperty("applied").GetBoolean());
        Assert.Equal("done", TaskOf(rig)!.Value.GetProperty("state").GetString());
    }

    [Fact]
    public async Task ResolveStagesWhenAskedAndRefusesWhatItCannotDo()
    {
        await using var rig = await ThreeSolved();
        var reply = WallRig.Json(await rig.Ok("CALIBRATION RESOLVE pj_test1 STAGE"));
        Assert.Equal("STAGE asked for it", reply.GetProperty("projectors")[0].GetProperty("why").GetString());
        Assert.NotNull(rig.Project.FindProjector("pj_test1")!.StagedId);
        Assert.Contains("no projector", await rig.Refused("CALIBRATION RESOLVE pj_nobody"), StringComparison.Ordinal);
        Assert.Contains("no camera cm_x", await rig.Refused("CALIBRATION RESOLVE pj_test1 CAMERA cm_x"), StringComparison.Ordinal);
        Assert.Contains("no calibration task", await rig.Refused("CALIBRATION CANCEL"), StringComparison.Ordinal);
        // An assistant's re-solve always waits for a person.
        var assistant = WallRig.Desk with { SessionId = "a1", Transport = "mcp", ClientName = "Assistant" };
        var ai = WallRig.Json(await rig.Ok("CALIBRATION RESOLVE pj_test3", assistant));
        Assert.False(ai.GetProperty("projectors")[0].GetProperty("applied").GetBoolean());
    }
}
