using PlayMap.Core.Bus;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Presentation;

namespace PlayMap.Engine.Tests.Presentation;

/// <summary>The present fuse: armed before a DXGI start, each start one attempt more; a fuse left at the next start makes it OpenGL
/// with words naming the attempts and how to try again; STATE says clear, armed or blown; a blown fuse is the preflight's amber row with
/// PRESENT RESET, which clears it (observed in STATE and in the row).</summary>
public sealed class PresentFuseTests
{
    [Fact]
    public async Task TheFuseFileArmsCountsAndClears()
    {
        await using var rig = new PathRig();
        Assert.Null(rig.Fuse.Read());
        Assert.False(rig.Fuse.Clear());
        var first = rig.Fuse.Arm("dxgi");
        Assert.Equal(new FuseState("dxgi", 1, 1_760_000_123_000), first);
        Assert.Equal(first, rig.Fuse.Read());
        Assert.Equal(2, rig.Fuse.Arm("dxgi").Attempt);
        Assert.Equal(Path.Combine(rig.Folder, "playmap.present.fuse"), rig.Fuse.FilePath);
        Assert.True(rig.Fuse.Clear());
        Assert.Null(rig.Fuse.Read());

        // A file a start wrote but nobody can read still counts as one attempt.
        await File.WriteAllTextAsync(rig.Fuse.FilePath, "{not json");
        Assert.Equal(new FuseState("dxgi", 1, 0), rig.Fuse.Read());
    }

    [Fact]
    public void TheChoiceFollowsTheFuseOnlyForDxgi()
    {
        Assert.Equal(("dxgi", ""), PresentChoice.Decide("DXGI", null));
        Assert.Equal(("auto", ""), PresentChoice.Decide("auto", new FuseState("dxgi", 3, 0)));
        Assert.Equal(("gl", ""), PresentChoice.Decide(" gl ", new FuseState("dxgi", 1, 0)));
        Assert.Equal(("auto", ""), PresentChoice.Decide("metal", null));
        var (path, words) = PresentChoice.Decide("dxgi", new FuseState("dxgi", 2, 1_760_000_123_000));
        Assert.Equal("gl", path);
        Assert.Equal("the DXGI path did not reach its first frames last start (2 attempts, at 2025-10-09 08:55 UTC); running OpenGL. PRESENT PATH DXGI or PRESENT RESET tries it again", words);
        Assert.Contains("(1 attempt, at an unknown time)", PresentChoice.Decide("dxgi", new FuseState("dxgi", 1, 0)).Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABlownFuseIsAmberWithPresentResetWhichClearsIt()
    {
        await using var rig = new PathRig("dxgi");
        rig.Outputs.MachineView = rig.Outputs.MachineView with { Path = "gl", Why = PresentChoice.Decide("dxgi", new FuseState("dxgi", 1, 0)).Words };
        var clear = rig.PresentPath().GetProperty("fuse");
        Assert.Equal("clear", clear.GetProperty("state").GetString());
        Assert.Equal(0, clear.GetProperty("attempt").GetInt32());

        // The fault: the last DXGI start left its fuse.
        rig.Fuse.Arm("dxgi");
        rig.Node.RefreshFuse();
        var blown = rig.PresentPath().GetProperty("fuse");
        Assert.Equal("blown", blown.GetProperty("state").GetString());
        Assert.Equal(1, blown.GetProperty("attempt").GetInt32());
        Assert.Equal(1_760_000_123_000, blown.GetProperty("at").GetInt64());
        var row = await rig.PathRow();
        Assert.Equal(Light.Amber, row.Light);
        Assert.Equal("fuse blown", row.Value);
        Assert.Equal("PRESENT RESET", row.Fix);
        Assert.StartsWith("the DXGI path did not reach its first frames last start (1 attempt", row.Note, StringComparison.Ordinal);

        // The fix: nothing presented yet, so the reset is the fuse's alone.
        var reset = await rig.Run("PRESENT RESET");
        Assert.True(reset.Ok, reset.Payload);
        Assert.Equal("the present fuse is cleared: the DXGI path is tried again at the render host's next start", reset.Effect);
        Assert.Null(rig.Fuse.Read());
        Assert.Equal("clear", rig.PresentPath().GetProperty("fuse").GetProperty("state").GetString());
        row = await rig.PathRow();
        Assert.NotEqual("fuse blown", row.Value);
        Assert.Null(row.Fix);

        // Nothing left to reset: refused.
        Assert.False((await rig.Run("PRESENT RESET")).Ok);
    }

    [Fact]
    public async Task AFuseThisStartArmedIsArmedUntilItsOutputsAreUpThenTheLaneClearsIt()
    {
        await using var rig = new PathRig("dxgi");
        rig.Fuse.Arm("dxgi");
        rig.Outputs.MachineView = rig.Outputs.MachineView with { Path = "dxgi", Experimental = true, FuseArmed = true };
        Assert.Equal("armed", rig.PresentPath().GetProperty("fuse").GetProperty("state").GetString());
        Assert.NotEqual(Light.Amber, (await rig.PathRow()).Light);
        // PRESENT RESET does not clear a fuse this start is still waiting on.
        _ = await rig.Run("PRESENT RESET");
        Assert.NotNull(rig.Fuse.Read());

        // The outputs reached their first settled frames: the lane's next tick (every 10 ms) lets the fuse go and STATE says clear.
        rig.Outputs.MachineView = rig.Outputs.MachineView with { FuseArmed = false, FirstFrames = true };
        rig.Outputs.FirstFramesUp = () => rig.Fuse.Clear();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!rig.Engine.Journal.Recent(20).Any(r => r.Text.Contains("its fuse is cleared", StringComparison.Ordinal)) && DateTime.UtcNow < deadline)
            await Task.Delay(5);
        Assert.Null(rig.Fuse.Read());
        Assert.Equal("clear", rig.PresentPath().GetProperty("fuse").GetProperty("state").GetString());
        Assert.Contains(rig.Engine.Journal.Recent(20), r => r.Text.Contains("its fuse is cleared", StringComparison.Ordinal));
    }
}
