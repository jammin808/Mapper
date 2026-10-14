using System.Text.Json;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Engine.Tests.Wall;

/// <summary>The calibration ledger: PROJECTOR SOLVE and SCAN APPLY record a scored result in the same edit; the first result is the
/// fallback; RECALL and FALLBACK put a result back exactly (as a new result); HISTORY gives each result's move from the one before;
/// a projector keeps CalibrationKeep loose results besides its current, fallback, staged and pinned ones; FORGET never drops one
/// of those.</summary>
public sealed class LedgerTests
{
    /// <summary>Moves a projector's pins to where a true pose moved by <paramref name="by"/> (metres, world) lights their points.</summary>
    private static async Task MovePins(WallRig rig, string projectorId, Pose truth)
    {
        var r = await rig.Engine.EditAsync(WallRig.Desk, "move pins", p => p.WithStage(st => st with
        {
            Projectors = st.Projectors.Update(projectorId, pj => pj with
            {
                Pins = [.. pj.Pins.Select(pin => pin with { Pixel = CameraModel.Project(pin.World, WallRig.TrueOptics, LensDistortion.None, truth)!.Value })],
            }),
        }));
        Assert.True(r.Changed, r.Effect);
    }

    [Fact]
    public async Task ASolveRecordsAScoredResultAndTheFirstIsTheFallback()
    {
        await using var rig = new WallRig(WallRig.ProjectorsWithPins(1));
        var first = WallRig.Json(await rig.Ok("PROJECTOR SOLVE pj_test1"));
        var id1 = first.GetProperty("snapshot").GetString()!;
        Assert.StartsWith(Ids.Calibration, id1, StringComparison.Ordinal);
        Assert.Equal("A", first.GetProperty("score").GetProperty("grade").GetString());
        var pj = rig.Project.FindProjector("pj_test1")!;
        Assert.Equal(id1, pj.CalibrationId);
        Assert.Equal(id1, pj.FallbackId);
        Assert.Null(pj.StagedId);
        var snap = rig.Project.FindCalibration(id1)!;
        Assert.Equal(CalibrationSource.Pins, snap.Source);
        Assert.Equal(CalibrationTarget.Projector, snap.Target);
        Assert.Equal(pj.Pose, snap.Projector!.Pose);
        Assert.Equal(pj.Intrinsics, snap.Projector.Intrinsics);
        Assert.NotNull(pj.Uncertainty);

        // The projector moved 2 cm: a second solve is current, the first stays the fallback; one UNDO takes both back together.
        await MovePins(rig, "pj_test1", WallRig.TruePose(0) with { Position = WallRig.TruePose(0).Position + new Vec3d(0.02, 0, 0) });
        var id2 = WallRig.Json(await rig.Ok("PROJECTOR SOLVE pj_test1")).GetProperty("snapshot").GetString()!;
        pj = rig.Project.FindProjector("pj_test1")!;
        Assert.Equal(id2, pj.CalibrationId);
        Assert.Equal(id1, pj.FallbackId);
        Assert.Equal(id1, rig.Project.FindCalibration(id2)!.ParentId);
        await rig.Ok("UNDO");
        Assert.Null(rig.Project.FindCalibration(id2));
        Assert.Equal(id1, rig.Project.FindProjector("pj_test1")!.CalibrationId);

        var target = rig.State().GetProperty("calibration").GetProperty("targets")[0];
        Assert.Equal("pj_test1", target.GetProperty("id").GetString());
        Assert.Equal("A", target.GetProperty("grade").GetString());
        Assert.Equal(id1, target.GetProperty("snapshot").GetString());
        Assert.Equal(id1, target.GetProperty("fallback").GetString());
        Assert.Equal("rms", target.GetProperty("terms")[0].GetProperty("code").GetString());
        var row = rig.State().GetProperty("stage").GetProperty("projectors")[0];
        Assert.Equal("A", row.GetProperty("grade").GetString());
    }

    [Fact]
    public async Task RecallPutsAResultBackExactlyAndHistoryGivesTheMoves()
    {
        await using var rig = new WallRig(WallRig.ProjectorsWithPins(1));
        var id1 = WallRig.Json(await rig.Ok("PROJECTOR SOLVE pj_test1")).GetProperty("snapshot").GetString()!;
        var at1 = rig.Project.FindProjector("pj_test1")!;
        await MovePins(rig, "pj_test1", WallRig.TruePose(0) with { Position = WallRig.TruePose(0).Position + new Vec3d(0.02, 0, 0) });
        var id2 = WallRig.Json(await rig.Ok("PROJECTOR SOLVE pj_test1")).GetProperty("snapshot").GetString()!;

        var history = WallRig.Json(await rig.Ok("CALIBRATION HISTORY pj_test1"));
        var rows = history.GetProperty("snapshots");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal(id2, rows[0].GetProperty("id").GetString());
        Assert.True(rows[0].GetProperty("current").GetBoolean());
        Assert.True(rows[1].GetProperty("fallback").GetBoolean());
        var delta = rows[0].GetProperty("delta");
        Assert.InRange(delta.GetProperty("mm").GetDouble(), 15, 25);
        Assert.InRange(delta.GetProperty("deg").GetDouble(), 0, 0.2);
        Assert.InRange(delta.GetProperty("wallPx").GetDouble(), 3, 30);
        Assert.Equal(-1, rows[1].GetProperty("delta").GetProperty("mm").GetDouble());

        var recalled = WallRig.Json(await rig.Ok($"CALIBRATION RECALL {id1}"));
        var id3 = recalled.GetProperty("recorded").GetString()!;
        var now = rig.Project.FindProjector("pj_test1")!;
        Assert.Equal(at1.Pose, now.Pose);
        Assert.Equal(at1.Intrinsics, now.Intrinsics);
        Assert.Equal(at1.Lens, now.Lens);
        Assert.Equal(at1.Locks, now.Locks);
        Assert.Equal(at1.Pins, now.Pins);
        Assert.Equal(id3, now.CalibrationId);
        var made = rig.Project.FindCalibration(id3)!;
        Assert.Equal(CalibrationSource.Revert, made.Source);
        Assert.Equal(id2, made.ParentId);
        Assert.Equal(rig.Project.FindCalibration(id1)!.Score, made.Score);
        Assert.Contains("stands as", await rig.Refused($"CALIBRATION RECALL {id1}"), StringComparison.Ordinal);
        Assert.Equal(3, WallRig.Json(await rig.Ok("CALIBRATION HISTORY pj_test1")).GetProperty("count").GetInt32());

        // FALLBACK is the first result exactly; on it already, it is refused.
        await rig.Ok($"CALIBRATION RECALL {id2}");
        await rig.Ok("CALIBRATION FALLBACK pj_test1");
        Assert.Equal(at1.Pose, rig.Project.FindProjector("pj_test1")!.Pose);
        Assert.Contains("stands as", await rig.Refused("CALIBRATION FALLBACK pj_test1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptMakesTheFallbackAndForgetKeepsWhatIsInUse()
    {
        await using var rig = new WallRig(WallRig.ProjectorsWithPins(1));
        var id1 = WallRig.Json(await rig.Ok("PROJECTOR SOLVE pj_test1")).GetProperty("snapshot").GetString()!;
        Assert.Contains("fallback already", await rig.Refused("CALIBRATION ACCEPT pj_test1"), StringComparison.Ordinal);
        await MovePins(rig, "pj_test1", WallRig.TruePose(0) with { Position = WallRig.TruePose(0).Position + new Vec3d(0, 0.01, 0) });
        var id2 = WallRig.Json(await rig.Ok("PROJECTOR SOLVE pj_test1")).GetProperty("snapshot").GetString()!;
        Assert.Contains("current result", await rig.Refused($"CALIBRATION FORGET {id2}"), StringComparison.Ordinal);
        Assert.Contains("fallback result", await rig.Refused($"CALIBRATION FORGET {id1}"), StringComparison.Ordinal);
        var accepted = WallRig.Json(await rig.Ok("CALIBRATION ACCEPT pj_test1"));
        Assert.Equal(id2, accepted.GetProperty("fallbacks")[0].GetProperty("fallback").GetString());
        Assert.Equal(id2, rig.Project.FindProjector("pj_test1")!.FallbackId);
        await rig.Ok($"CALIBRATION FORGET {id1}");
        Assert.Null(rig.Project.FindCalibration(id1));
        // A kept result is pinned and outlives the retention.
        var kept = WallRig.Json(await rig.Ok("CALIBRATION SAVE pj_test1 AS Opening night")).GetProperty("snapshots")[0].GetString()!;
        Assert.True(rig.Project.FindCalibration(kept)!.Pinned);
        Assert.Equal("Opening night", rig.Project.FindCalibration(kept)!.Name);
        Assert.Contains("kept already", await rig.Refused("CALIBRATION SAVE pj_test1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetentionKeepsTheNewestBesidesCurrentFallbackAndPinned()
    {
        var project = WallRig.ProjectorsWithPins(1);
        project = project with { Settings = project.Settings with { CalibrationKeep = 3 } };
        await using var rig = new WallRig(project);
        var first = WallRig.Json(await rig.Ok("PROJECTOR SOLVE pj_test1")).GetProperty("snapshot").GetString()!;
        var pinned = WallRig.Json(await rig.Ok("CALIBRATION SAVE pj_test1")).GetProperty("snapshots")[0].GetString()!;
        var ids = new List<string>();
        for (var i = 0; i < 7; i++)
            ids.Add(WallRig.Json(await rig.Ok("PROJECTOR SOLVE pj_test1")).GetProperty("snapshot").GetString()!);
        var mine = rig.Project.Stage.Calibrations.Where(c => c.TargetId == "pj_test1").Select(c => c.Id).ToHashSet();
        // The fallback (the first), the pinned one, the current (the newest) and the three newest loose ones before it.
        Assert.Equal(6, mine.Count);
        Assert.Contains(first, mine);
        Assert.Contains(pinned, mine);
        Assert.Contains(ids[^1], mine);
        Assert.Contains(ids[^2], mine);
        Assert.Contains(ids[^4], mine);
        Assert.DoesNotContain(ids[^5], mine);
        Assert.DoesNotContain(ids[0], mine);
    }

    [Fact]
    public async Task ScanApplyRecordsAResultWithTheScansScore()
    {
        var p = Project.CreateDefault("Scan ledger test");
        Projector Make(string id, string name, Vec3d eye) => new(id, name, Intrinsics.FromThrowRatio(256, 160, 1.4), LensDistortion.None,
            Pose.LookAt(eye, new Vec3d(0, 0.2, 0), Vec3d.UnitY), 0, "#FF8800", false, [], SolveLocks.None, 0);
        p = p.WithStage(s => s with { Projectors = [Make("pj00000001", "Left", new Vec3d(0.1, 1.5, 2.3))] });
        var rig = new OpsRig(project: p);
        try
        {
            Assert.True((await rig.Run("SCAN START sim MODE FAST")).Ok);
            Assert.True(await OpsRig.Eventually(() => rig.Scanner.Current?.State is "done" or "failed" or "cancelled", 120_000), "the scan never ended");
            Assert.Equal("done", rig.Scanner.Current!.State);
            var record = Assert.Single(rig.Engine.Store.Project.Stage.Scans);
            var reply = await rig.Run($"SCAN APPLY {record.Id}");
            Assert.True(reply.Ok, reply.Payload);
            var placed = JsonDocument.Parse(reply.Payload!).RootElement.GetProperty("projectors")[0];
            var snapshotId = placed.GetProperty("snapshot").GetString()!;
            var pj = rig.Engine.Store.Project.FindProjector("pj00000001")!;
            Assert.Equal(snapshotId, pj.CalibrationId);
            Assert.Equal(snapshotId, pj.FallbackId);
            var snap = rig.Engine.Store.Project.FindCalibration(snapshotId)!;
            Assert.Equal(CalibrationSource.Scan, snap.Source);
            Assert.Equal(record.Id, snap.ScanId);
            Assert.Equal("scan", snap.Score.Source);
            Assert.Equal(ScoreTermCodes.DenseRms, snap.Score.Terms[0].Code);
            Assert.Equal(pj.Pose, snap.Projector!.Pose);
            Assert.Equal(placed.GetProperty("score").GetProperty("grade").GetString(), snap.Score.Grade);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }
}
