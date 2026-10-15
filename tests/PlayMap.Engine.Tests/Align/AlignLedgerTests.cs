using PlayMap.Core.Model;

namespace PlayMap.Engine.Tests.Align;

/// <summary>ALIGN ACCEPT through the calibration ledger: every output's measured correction is recorded as a result (source align) and
/// applied in one edit, the correction it had kept as its fallback — one undo step takes it all back, bit for bit. REVERT returns to
/// the fallbacks; a second REVERT is refused with words. The ledger's own verbs reach the results (fallback, recall). An armed show
/// refuses ACCEPT, REVERT, RUN, NEW, CORNERS and CLEAR with the gate's words and changes nothing.</summary>
public sealed class AlignLedgerTests
{
    private const string Layout = "flat-screen:tile2x1:o20";

    [Fact]
    public async Task AcceptRecordsEachOutputsResultWithWhatItHadAsTheFallbackInOneUndoStep()
    {
        await using var rig = new AlignRig(AlignRig.Project(2, Layout));
        var group = await rig.NewGroup();
        var run = await rig.RunToEnd(group);
        var state = rig.State().GetProperty("align").GetProperty("run");
        Assert.Equal((run, "staged"), (state.GetProperty("id").GetString(), state.GetProperty("state").GetString()));
        Assert.Null(rig.Current.FindOutput("op1")!.Measured);

        var before = rig.Current;
        var reply = AlignRig.Json(await rig.Ok($"ALIGN ACCEPT {run}"));
        Assert.Equal(run, reply.GetProperty("run").GetString());
        var p = rig.Current;
        var record = rig.Runner.Finished(run)!;
        Assert.Equal("accepted", record.State);
        foreach (var o in record.Outputs)
        {
            var output = p.FindOutput(o.OutputId)!;
            Assert.Equal(o.Measured, output.Measured);
            var current = p.FindCalibration(output.CalibrationId)!;
            Assert.Equal((CalibrationSource.Align, CalibrationTarget.Output, o.OutputId), (current.Source, current.Target, current.TargetId));
            Assert.Equal(o.Measured, current.Output);
            Assert.Equal("align", current.Score.Source);
            var fallback = p.FindCalibration(output.FallbackId)!;
            Assert.Null(fallback.Output);
            Assert.Equal(fallback.Id, current.ParentId);
            Assert.InRange(output.Blend.Gamma, 2.1, 2.3);
        }
        Assert.Equal(run, p.FindAlign(group)!.LastRunId);
        Assert.Equal("accepted", rig.State().GetProperty("align").GetProperty("run").GetProperty("state").GetString());
        Assert.Contains("already", await rig.Refused($"ALIGN ACCEPT {run}"), StringComparison.Ordinal);

        await rig.Ok("UNDO");
        Assert.Equal(before.Outputs, rig.Current.Outputs);
        Assert.Equal(before.Stage.Calibrations, rig.Current.Stage.Calibrations);
        Assert.Equal(before.Stage.Aligns, rig.Current.Stage.Aligns);
    }

    [Fact]
    public async Task RevertGoesBackToTheFallbackAndTheLedgersVerbsReachTheResults()
    {
        await using var rig = new AlignRig(AlignRig.Project(2, Layout));
        var group = await rig.NewGroup();
        var first = await rig.RunToEnd(group);
        await rig.Ok($"ALIGN ACCEPT {group}");
        var firstWarp = rig.Current.FindOutput("op1")!.Measured!;
        var firstSnapshot = rig.Current.FindOutput("op1")!.CalibrationId!;

        await rig.Ok($"ALIGN CORNERS {group} 0.3 0.2 0.7 0.21 0.69 0.6 0.31 0.61");
        var second = await rig.RunToEnd(group);
        Assert.NotEqual(first, second);
        await rig.Ok($"ALIGN ACCEPT {second}");
        var op1 = rig.Current.FindOutput("op1")!;
        Assert.Equal(second, op1.Measured!.RunId);
        Assert.Equal(firstSnapshot, op1.FallbackId);

        var reverted = AlignRig.Json(await rig.Ok($"ALIGN REVERT {group}"));
        Assert.Equal(2, reverted.GetProperty("applied").GetArrayLength());
        Assert.Equal(firstWarp, rig.Current.FindOutput("op1")!.Measured);
        Assert.Contains("stands on its fallback already", await rig.Refused($"ALIGN REVERT {group}"), StringComparison.Ordinal);

        // The ledger's own verbs: back to the fallback, then the second run's result recalled exactly.
        var secondSnapshot = rig.Current.Stage.Calibrations.Single(c => c.TargetId == "op1" && c.Output?.RunId == second && c.Source == CalibrationSource.Align).Id;
        await rig.Ok($"CALIBRATION RECALL {secondSnapshot}");
        Assert.Equal(second, rig.Current.FindOutput("op1")!.Measured!.RunId);
        await rig.Ok("CALIBRATION FALLBACK op1");
        Assert.Equal(first, rig.Current.FindOutput("op1")!.Measured!.RunId);

        // CLEAR keeps what it removes as the fallback: REVERT brings it back.
        await rig.Ok("ALIGN CLEAR op2");
        Assert.Null(rig.Current.FindOutput("op2")!.Measured);
        await rig.Ok($"ALIGN REVERT {group}");
        Assert.NotNull(rig.Current.FindOutput("op2")!.Measured);
    }

    [Fact]
    public async Task AnArmedShowRefusesTheAlignVerbsWithTheGatesWords()
    {
        await using var rig = new AlignRig(AlignRig.Project(2, Layout));
        var group = await rig.NewGroup();
        var run = await rig.RunToEnd(group);
        rig.Gate.IsArmed = true;
        var revision = rig.Engine.Store.Current.Revision;
        foreach (var line in new[]
        {
            $"ALIGN ACCEPT {run}", $"ALIGN REVERT {group}", $"ALIGN RUN {group}", "ALIGN NEW Other cm_sim op1", $"ALIGN CORNERS {group} CLEAR",
            "ALIGN CLEAR op1",
        })
            Assert.Equal(SwitchGate.Words, await rig.Refused(line));
        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
        Assert.Null(rig.Current.FindOutput("op1")!.Measured);
        Assert.Equal("staged", rig.State().GetProperty("align").GetProperty("run").GetProperty("state").GetString());

        rig.Gate.IsArmed = false;
        await rig.Ok($"ALIGN ACCEPT {run}");
        Assert.NotNull(rig.Current.FindOutput("op1")!.Measured);
    }
}
