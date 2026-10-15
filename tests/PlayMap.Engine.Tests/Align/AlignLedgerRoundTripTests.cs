using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Engine.Align;

namespace PlayMap.Engine.Tests.Align;

/// <summary>The alignment and the calibration ledger together: ALIGN ACCEPT records each output's measured correction as a result (source
/// align) with the correction it had as its fallback (a result made by hand, source manual); the ledger's CALIBRATION FALLBACK takes
/// each output back to it, and CALIBRATION RECALL brings the alignment's result back exactly — each step published in
/// STATE.calibration, STATE.align and outputs[].measured. A result brought back is the group's score again. Armed, the show refuses
/// ACCEPT, REVERT, FALLBACK and RECALL with its own words and nothing moves.</summary>
public sealed class AlignLedgerRoundTripTests
{
    private const string Layout = "flat-screen:tile2x1:o20";

    [Fact]
    public async Task AcceptThenTheLedgersFallbackThenRecallComesBackExactly()
    {
        await using var rig = new AlignRig(AlignRig.Project(2, Layout));
        var group = await rig.NewGroup();
        var run = await rig.RunToEnd(group);
        var record = rig.Runner.Finished(run)!;
        var accepted = AlignRig.Json(await rig.Ok($"ALIGN ACCEPT {run}"));
        var applied = accepted.GetProperty("applied").EnumerateArray()
            .ToDictionary(a => a.GetProperty("output").GetString()!, a => (Snapshot: a.GetProperty("snapshot").GetString()!, Fallback: a.GetProperty("fallback").GetString()!));
        Assert.Equal(new[] { "op1", "op2" }, applied.Keys.Order());
        foreach (var o in record.Outputs)
        {
            var (snapshot, fallback) = applied[o.OutputId];
            var output = rig.Current.FindOutput(o.OutputId)!;
            Assert.Equal((snapshot, fallback, o.Measured), (output.CalibrationId, output.FallbackId, output.Measured));
            var t = Target(rig, o.OutputId);
            Assert.Equal((snapshot, fallback, "align", record.Score.Grade, record.Score.Confidence),
                (t.GetProperty("snapshot").GetString(), t.GetProperty("fallback").GetString(), t.GetProperty("source").GetString(), t.GetProperty("grade").GetString(),
                    t.GetProperty("confidence").GetDouble()));
        }
        var g = Group(rig);
        Assert.Equal((run, record.Score.Grade, "green"), (g.GetProperty("lastRun").GetString(), g.GetProperty("grade").GetString(), g.GetProperty("light").GetString()));

        // The ledger's own FALLBACK: each output back to what it had (no measured correction), recorded as a new result.
        foreach (var o in record.Outputs)
            await rig.Ok($"CALIBRATION FALLBACK {o.OutputId}");
        await rig.Engine.AlignFiles.Settled;
        var state = rig.State();
        foreach (var o in record.Outputs)
        {
            var output = rig.Current.FindOutput(o.OutputId)!;
            Assert.Null(output.Measured);
            Assert.NotEqual(applied[o.OutputId].Snapshot, output.CalibrationId);
            Assert.Null(rig.Current.FindCalibration(output.CalibrationId)!.Output);
            Assert.Equal("manual", Target(rig, o.OutputId).GetProperty("source").GetString());
            Assert.Equal(JsonValueKind.Null, OutputState(state, o.OutputId).GetProperty("measured").ValueKind);
        }
        Assert.Empty(rig.Engine.AlignMeasured.Loads);
        g = Group(rig);
        Assert.Equal(("", "grey"), (g.GetProperty("grade").GetString(), g.GetProperty("light").GetString()));

        // RECALL the alignment's results: exactly the run's corrections again, on the air and in STATE, the group graded by them.
        foreach (var o in record.Outputs)
            await rig.Ok($"CALIBRATION RECALL {applied[o.OutputId].Snapshot}");
        await rig.Engine.AlignFiles.Settled;
        state = rig.State();
        foreach (var o in record.Outputs)
        {
            var output = rig.Current.FindOutput(o.OutputId)!;
            Assert.Equal(o.Measured, output.Measured);
            var t = Target(rig, o.OutputId);
            Assert.Equal((output.CalibrationId, "align", record.Score.Grade), (t.GetProperty("snapshot").GetString(), t.GetProperty("source").GetString(),
                t.GetProperty("grade").GetString()));
            var m = OutputState(state, o.OutputId).GetProperty("measured");
            Assert.Equal((run, "ok"), (m.GetProperty("run").GetString(), m.GetProperty("files").GetString()));
            Assert.True(rig.Engine.AlignMeasured.Loads.ContainsKey(o.OutputId));
        }
        g = Group(rig);
        Assert.Equal((record.Score.Grade, "green"), (g.GetProperty("grade").GetString(), g.GetProperty("light").GetString()));

        // ALIGN REVERT goes to the same fallback the ledger keeps; the group is no longer graded by a run.
        await rig.Ok($"ALIGN REVERT {group}");
        Assert.All(record.Outputs, o => Assert.Null(rig.Current.FindOutput(o.OutputId)!.Measured));
        await rig.Ok($"CALIBRATION RECALL {applied["op1"].Snapshot}");

        // Armed: the show refuses each with its own words, and nothing moves.
        await rig.Ok("SHOW ARM");
        var revision = rig.Engine.Store.Current.Revision;
        foreach (var line in new[] { $"ALIGN ACCEPT {run}", $"ALIGN REVERT {group}", "CALIBRATION FALLBACK op1", $"CALIBRATION RECALL {applied["op2"].Snapshot}" })
            Assert.StartsWith("Not while armed: ", await rig.Refused(line), StringComparison.Ordinal);
        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
        Assert.Equal(MeasuredOf(record, "op1"), rig.Current.FindOutput("op1")!.Measured);
        await rig.Ok("SHOW DISARM");
    }

    private static MeasuredCorrection MeasuredOf(AlignRunRecord record, string outputId) => record.Outputs.Single(o => o.OutputId == outputId).Measured;

    private static JsonElement Target(AlignRig rig, string id) =>
        rig.State().GetProperty("calibration").GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("id").GetString() == id);

    private static JsonElement Group(AlignRig rig) => rig.State().GetProperty("align").GetProperty("groups")[0];

    private static JsonElement OutputState(JsonElement state, string id) =>
        state.GetProperty("outputs").EnumerateArray().Single(o => o.GetProperty("id").GetString() == id);
}
