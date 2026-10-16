namespace PlayMap.Engine.Tests.Align;

/// <summary>ALIGN ACCEPT writes each output's measured γ into its blend γ; the results the ledger keeps carry the blend γ with them, so
/// ALIGN REVERT to a fallback from before the run (no measured correction) puts the operator's own γ back, and RECALL of the run's
/// result brings the measured one again.</summary>
public sealed class AlignGammaRevertTests
{
    [Fact]
    public async Task ARevertPutsTheOperatorsBlendGammaBack()
    {
        var project = AlignRig.Project(2, "flat-screen:tile2x1");
        project = project with { Outputs = [.. project.Outputs.Select(o => o with { Blend = o.Blend with { Gamma = 1.8 } })] };
        await using var rig = new AlignRig(project);
        var group = await rig.NewGroup();
        var run = await rig.RunToEnd(group);
        var accepted = AlignRig.Json(await rig.Ok($"ALIGN ACCEPT {run}"));
        var measured = rig.Runner.Finished(run)!.Outputs.Select(o => Math.Round(o.Gamma, 3)).ToList();
        Assert.Equal(measured, rig.Current.Outputs.Select(o => o.Blend.Gamma));
        Assert.All(measured, g => Assert.NotEqual(1.8, g));

        await rig.Ok($"ALIGN REVERT {group}");
        Assert.All(rig.Current.Outputs, o => Assert.Null(o.Measured));
        Assert.All(rig.Current.Outputs, o => Assert.Equal(1.8, o.Blend.Gamma));

        foreach (var a in accepted.GetProperty("applied").EnumerateArray())
            await rig.Ok($"CALIBRATION RECALL {a.GetProperty("snapshot").GetString()}");
        Assert.Equal(measured, rig.Current.Outputs.Select(o => o.Blend.Gamma));
    }
}
