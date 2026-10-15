using PlayMap.Core.Media.Optimise;

namespace PlayMap.Core.Tests.Optimise;

/// <summary>The governor on a hand-moved clock: armed waits, playing trickles (and stays there until ten idle seconds), idle runs,
/// a frame dropped or late while a job ran pauses it for the window after the last one.</summary>
public class GovernorTests
{
    private const long S = 1_000_000_000;
    private static readonly GovernorSettings Settings = new(0.15, 50, 60, 4);

    [Fact]
    public void ArmedWaitsPlayingTricklesIdleRuns()
    {
        var g = new Governor(Settings);
        var idle = g.Step(new GovernorInputs(false, false, 0, 0, false, 0));
        Assert.Equal((GovernorState.Run, 3, 1.0, 0, true, ""), (idle.State, idle.CpuThreads, idle.GpuDuty, idle.DiskMbps, idle.HwDecode, idle.Words));
        var playing = g.Step(new GovernorInputs(false, true, 0, 0, true, 1 * S));
        Assert.Equal((GovernorState.Trickle, 1, 0.15, 50, false, "a show plays: one stream, GPU ≤ 15 %"),
            (playing.State, playing.CpuThreads, playing.GpuDuty, playing.DiskMbps, playing.HwDecode, playing.Words));
        var armed = g.Step(new GovernorInputs(true, true, 0, 0, true, 2 * S));
        Assert.Equal((GovernorState.Waiting, false, "optimise waits: the show is armed"), (armed.State, armed.MayWork, armed.Words));
    }

    [Fact]
    public void TrickleReturnsToRunOnlyAfterTenIdleSeconds()
    {
        var g = new Governor(Settings);
        g.Step(new GovernorInputs(false, true, 0, 0, true, 0));
        Assert.Equal(GovernorState.Trickle, g.Step(new GovernorInputs(false, false, 0, 0, true, 9 * S)).State);
        Assert.Equal(GovernorState.Run, g.Step(new GovernorInputs(false, false, 0, 0, true, 10 * S)).State);
    }

    [Fact]
    public void ADropWhileAJobRanPausesForTheWindowAfterTheLastOne()
    {
        var g = new Governor(Settings);
        g.Step(new GovernorInputs(false, true, 5, 2, true, 0));
        var dropped = g.Step(new GovernorInputs(false, true, 6, 2, true, 1 * S));
        Assert.Equal((GovernorState.Paused, "paused for 60 s: 1 frame dropped while it ran"), (dropped.State, dropped.Words));
        Assert.Equal(GovernorState.Paused, g.Step(new GovernorInputs(false, true, 6, 2, false, 60 * S)).State);
        // A drop while nothing worked is not the job's: the window does not move.
        Assert.Equal(GovernorState.Paused, g.Step(new GovernorInputs(false, true, 9, 2, false, 61 * S - 1)).State);
        Assert.Equal(GovernorState.Trickle, g.Step(new GovernorInputs(false, true, 9, 2, true, 61 * S)).State);
        var late = g.Step(new GovernorInputs(false, true, 9, 5, true, 62 * S));
        Assert.Equal("paused for 60 s: 3 frames late while it ran", late.Words);
    }
}
