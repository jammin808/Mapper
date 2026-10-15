using System.Buffers;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;
using PlayMap.Engine.Wall;

namespace PlayMap.Engine.Tests.Wall;

/// <summary>Preflight's calibration rows and the INDEX section: a projector on the wall with no result to fall back to is amber with
/// the line that makes one; solved it is green; a staged result waiting is amber with the line that accepts it; an F is red with the
/// line that returns to the fallback. INDEX lists every projector with its grade's light, and its keys are catalogue lines.</summary>
public sealed class WallPreflightTests
{
    private static async Task<IReadOnlyList<PreflightRow>> Rows(WallRig rig) =>
        await new WallPreflight().RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None);

    [Fact]
    public async Task EachProjectorsGradeAndFallbackAreRowsWithAFix()
    {
        await using var rig = new WallRig(WallRig.ProjectorsWithPins(1));
        var row = Assert.Single(await Rows(rig));
        Assert.Equal(("calibration", Light.Amber, "CALIBRATION ACCEPT pj_test1"), (row.Section, row.Light, row.Fix));
        Assert.Contains("no calibration result", row.Note, StringComparison.Ordinal);

        await rig.Ok("PROJECTOR SOLVE pj_test1");
        row = Assert.Single(await Rows(rig));
        Assert.Equal(Light.Green, row.Light);
        Assert.Null(row.Fix);
        Assert.StartsWith("A ", row.Value, StringComparison.Ordinal);

        var staged = WallRig.Json(await rig.Ok("CALIBRATION RESOLVE pj_test1 STAGE")).GetProperty("projectors")[0].GetProperty("snapshot").GetString();
        row = Assert.Single(await Rows(rig));
        Assert.Equal((Light.Amber, $"CALIBRATION ACCEPT {staged}"), (row.Light, row.Fix));

        // Every fix a row offers is a line of the catalogue.
        Assert.True(WireGrammar.TryParse(row.Fix!, out var line, out _));
        Assert.NotNull(VerbCatalog.Match(line));
    }

    [Fact]
    public async Task AFailedSolveIsRedWithTheWayBack()
    {
        await using var rig = new WallRig(WallRig.ProjectorsWithPins(1));
        await rig.Ok("PROJECTOR SOLVE pj_test1");
        await rig.Ok("CALIBRATION RESOLVE pj_test1 STAGE");
        // A result recorded for a projector turned to face away from its pins grades F.
        var pj = rig.Project.FindProjector("pj_test1")!;
        var away = pj with { Pose = Core.Model.Pose.LookAt(pj.Pose.Position, pj.Pose.Position - pj.Pose.Forward, Core.Numerics.Vec3d.UnitY) };
        var snapshot = CalibrationLedger.Of(away, "", "Turned", CalibrationLedger.ScoreAsItStands(away), Core.Model.CalibrationSource.Manual, "test", 1);
        Assert.Equal("F", snapshot.Score.Grade);
        var (r, id) = await rig.Engine.CalibrationLedgerService.RecordAsync(WallRig.Desk, snapshot, apply: true, CancellationToken.None);
        Assert.True(r.Changed, r.Effect);
        Assert.Equal(id, rig.Project.FindProjector("pj_test1")!.CalibrationId);
        var row = Assert.Single(await Rows(rig));
        Assert.Equal((Light.Red, "CALIBRATION FALLBACK pj_test1"), (row.Light, row.Fix));
        var light = rig.State().GetProperty("calibration").GetProperty("targets")[0].GetProperty("light").GetString();
        Assert.Equal("red", light);
        Assert.Contains("\"calibration:pj_test1\"", rig.Engine.Context(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheIndexSectionListsEveryProjectorWithItsGrade()
    {
        await using var rig = new WallRig(WallRig.ProjectorsWithPins(2));
        await rig.Ok("PROJECTOR SOLVE pj_test1");
        var index = Assert.Single(rig.Engine.Routes.IndexSections, s => s.Section == "calibration");
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
            index.WriteRows(rig.Project, rig.Engine.Snapshot, w);
        var rows = JsonDocument.Parse(Encoding.UTF8.GetString(buffer.WrittenSpan)).RootElement;
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal(("pj_test1", "green"), (rows[0].GetProperty("id").GetString(), rows[0].GetProperty("light").GetString()));
        Assert.StartsWith("A ", rows[0].GetProperty("state").GetString(), StringComparison.Ordinal);
        Assert.Equal(("grey", "none"), (rows[1].GetProperty("light").GetString(), rows[1].GetProperty("state").GetString()));
        foreach (var key in index.Hint.Keys)
        {
            Assert.True(WireGrammar.TryParse(key.DownLine.Replace("$(id)", "pj_test1", StringComparison.Ordinal), out var line, out _));
            Assert.NotNull(VerbCatalog.Match(line));
        }
        // The engine's ledger and the services' one are the same: align and facade find it there.
        Assert.Equal("A", rig.Engine.Services.Ledger!.ScoreOf(rig.Project, "pj_test1").Grade);
    }
}
