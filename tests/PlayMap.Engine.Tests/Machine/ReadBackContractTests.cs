using PlayMap.Core.Machine;

namespace PlayMap.Engine.Tests.Machine;

/// <summary>The node's composed platform answers the machine platform's contract with what the platform underneath reads back, not the
/// contract's defaults: the settings reach it, keep-awake is its claim, each timer's kind is the kind it made (asked of the counted timer
/// the composed platform hands out), and the power settings are its reading.</summary>
public sealed class ReadBackContractTests
{
    [Fact]
    public async Task ThroughTheContractTheComposedPlatformAnswersWhatThePlatformUnderneathReadsBack()
    {
        await using var rig = new MachineRig();
        IMachinePlatform composed = rig.Recorded;

        using (var high = composed.CreateTimer())
            Assert.Equal("high", composed.TimerMode(high));
        var timerDefault = MachineSettings.Default with { Timer = "default" };
        composed.Use(timerDefault);
        Assert.Same(timerDefault, rig.Platform.Used[^1]);
        using (var runtime = composed.CreateTimer())
            Assert.Equal("default", composed.TimerMode(runtime));

        // The default project has an enabled display output: keep-awake is held, and its claim is the platform's.
        Assert.Equal(new Claim("process.awake", "on", true, "held", ""), composed.AwakeClaim);
        Assert.Equal(rig.Platform.Power, composed.Power);
        Assert.Equal("Ultimate Performance", composed.Power.Plan);
    }
}
