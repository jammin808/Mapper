using PlayMap.Core.Time;
using PlayMap.Engine.Tests.Control;

namespace PlayMap.Engine.Tests.Tc;

/// <summary>The board of the show's timecode sources follows the newest show it has been given: a reader off the lane (the control
/// service's tick) that took the show before a newer edit and reconciles after the lane has changes nothing, so a source the newer
/// show has keeps its place — an internal source located and rolling rolls on from where it was put.</summary>
public sealed class TimecodeBoardTests
{
    private const long S = 1_000_000_000;

    [Fact]
    public async Task AShowOlderThanTheOneReconciledTakesNoSourceAway()
    {
        var clock = new ManualClock(5 * S);
        await using var rig = await ControlRig.StartAsync(TimecodeEngineTests.Show(), clock: clock);
        // The show as a reader off the lane took it, before the source was added.
        var stale = rig.Engine.Store.Current;
        await rig.Ok("TIMECODE SOURCE ADD Rehearsal INTERNAL FORMAT 25");
        await rig.Ok("TIMECODE INTERNAL Rehearsal LOCATE 01:00:01:00");
        await rig.Ok("TIMECODE INTERNAL Rehearsal PLAY");

        // That reader reconciles the board now, after the lane has.
        rig.Engine.TimecodeSources.Reconcile(stale);
        clock.AdvanceMs(2000);
        await rig.Engine.StepChasesAsync();
        var source = rig.State().GetProperty("timecode").GetProperty("sources")[0];
        Assert.Equal(("rolling", "01:00:03:00"), (source.GetProperty("state").GetString(), source.GetProperty("text").GetString()));
        Assert.Equal("Rehearsal stopped at 01:00:03:00", (await rig.Ok("TIMECODE INTERNAL Rehearsal STOP")).Effect);
    }
}
