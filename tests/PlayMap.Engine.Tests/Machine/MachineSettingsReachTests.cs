using System.Text.Json;
using PlayMap.Core.Machine;

namespace PlayMap.Engine.Tests.Machine;

/// <summary>Every saved MACHINE SET reaches the platform before anything acts on it, and STATE says what the platform made and holds,
/// never what was asked: AWAKE OFF then ON takes a hold under ON (and STATE's awake and keep-awake's claim follow the hold); TIMER's next
/// timer is the kind the platform made (none before any); MMCSS and CPUSETS reach the next thread claim; a hold the platform refuses is
/// an amber claim with its words in STATE, INDEX and the journal, and STATE says the machine is not kept awake.</summary>
public sealed class MachineSettingsReachTests
{
    private static JsonElement Platform(MachineRig rig) => rig.Machine().GetProperty("platform");

    private static JsonElement? AwakeClaim(MachineRig rig)
    {
        foreach (var c in Platform(rig).GetProperty("claims").EnumerateArray())
            if (c.GetProperty("what").GetString() == "process.awake")
                return c;
        return null;
    }

    [Fact]
    public async Task AwakeOffThenOnTakesAHoldUnderOnAndStateSaysWhatIsHeld()
    {
        // The default project has an enabled display output: keep-awake's cause.
        await using var rig = new MachineRig(settings: MachineSettings.Default with { Awake = "off" });
        Assert.Empty(rig.Platform.AwakeAskedUnder);
        Assert.False(Platform(rig).GetProperty("awake").GetBoolean());
        Assert.Null(AwakeClaim(rig));

        var on = await rig.Ok("MACHINE SET AWAKE ON");
        Assert.Equal("on", rig.Platform.Used[^1].Awake);
        // The hold was asked under ON: the platform had the setting before the machine part acted on it.
        Assert.Equal(["on"], rig.Platform.AwakeAskedUnder);
        Assert.True(Platform(rig).GetProperty("awake").GetBoolean());
        var claim = AwakeClaim(rig)!.Value;
        Assert.Equal(("on", true, "held", "green"), (claim.GetProperty("asked").GetString(), claim.GetProperty("applied").GetBoolean(),
            claim.GetProperty("readBack").GetString(), claim.GetProperty("light").GetString()));
        Assert.True(on.GetProperty("claim").GetProperty("applied").GetBoolean());
        Assert.Equal("the machine is kept awake while the project has an enabled display output: held now", on.GetProperty("words").GetString());

        var off = await rig.Ok("MACHINE SET AWAKE OFF");
        Assert.Equal(JsonValueKind.Null, off.GetProperty("claim").ValueKind);
        Assert.False(Platform(rig).GetProperty("awake").GetBoolean());
        Assert.Null(AwakeClaim(rig));
        Assert.Equal("off", rig.Platform.Used[^1].Awake);
    }

    [Fact]
    public async Task WithNoEnabledDisplayOutputNothingIsHeldAndTheReceiptSaysSo()
    {
        await using var rig = new MachineRig(MachineRig.WithoutDisplay(), settings: MachineSettings.Default with { Awake = "off" });
        var on = await rig.Ok("MACHINE SET AWAKE ON");
        Assert.Empty(rig.Platform.AwakeAskedUnder);
        Assert.Equal(JsonValueKind.Null, on.GetProperty("claim").ValueKind);
        Assert.Equal("the machine is kept awake while the project has an enabled display output: none is enabled now, so nothing is held",
            on.GetProperty("words").GetString());
        Assert.False(Platform(rig).GetProperty("awake").GetBoolean());
    }

    [Fact]
    public async Task AHoldThePlatformRefusesIsAnAmberClaimAndStateSaysNotAwake()
    {
        await using var rig = new MachineRig(settings: MachineSettings.Default with { Awake = "off" });
        rig.Platform.RefuseAwake = "Windows refused the display-required request: error 5: Access is denied.";
        var on = await rig.Ok("MACHINE SET AWAKE ON");
        Assert.False(on.GetProperty("claim").GetProperty("applied").GetBoolean());
        Assert.Equal("keep-awake ON is kept, and Windows refused it now: Windows refused the display-required request: error 5: Access is denied.",
            on.GetProperty("words").GetString());

        var platform = Platform(rig);
        Assert.False(platform.GetProperty("awake").GetBoolean());
        Assert.Equal("amber", platform.GetProperty("light").GetString());
        var claim = AwakeClaim(rig)!.Value;
        Assert.Equal(("on", false, "amber"), (claim.GetProperty("asked").GetString(), claim.GetProperty("applied").GetBoolean(), claim.GetProperty("light").GetString()));
        Assert.Equal(rig.Platform.RefuseAwake, claim.GetProperty("words").GetString());

        using var index = JsonDocument.Parse(rig.Engine.Companion.Index().Json);
        var section = index.RootElement.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("section").GetString() == "machine");
        var row = section.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "claim:process.awake");
        Assert.Equal(("amber", rig.Platform.RefuseAwake), (row.GetProperty("light").GetString(), row.GetProperty("state").GetString()));
        var journal = rig.Engine.Journal.Recent(20).Where(e => e.Kind == "machine").Select(e => e.Text).ToList();
        Assert.Contains($"process.awake on was refused: {rig.Platform.RefuseAwake}", journal);
    }

    [Fact]
    public async Task TheTimerSaysTheKindTheNextTimerMadeNeverTheSetting()
    {
        await using var rig = new MachineRig();
        // No deadline timer is made yet: nothing to name.
        Assert.Equal(("none", "high"), (Platform(rig).GetProperty("timer").GetProperty("mode").GetString(),
            Platform(rig).GetProperty("settings").GetProperty("timer").GetString()));
        using (rig.Recorded.CreateTimer())
            Assert.Equal("high", Platform(rig).GetProperty("timer").GetProperty("mode").GetString());

        await rig.Ok("MACHINE SET TIMER DEFAULT");
        Assert.Equal("default", rig.Platform.Used[^1].Timer);
        // The setting applies to the next timer made; the one STATE names is still the last made.
        Assert.Equal(("high", "default"), (Platform(rig).GetProperty("timer").GetProperty("mode").GetString(),
            Platform(rig).GetProperty("settings").GetProperty("timer").GetString()));
        using (rig.Recorded.CreateTimer())
            Assert.Equal("default", Platform(rig).GetProperty("timer").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task MmcssAndCpuSetsReachTheNextThreadClaim()
    {
        await using var rig = new MachineRig();
        using (var before = rig.Recorded.ClaimThread(ThreadRole.Audio))
            Assert.Contains(before.Claims, c => c.What == "thread.audio.mmcss" && c.Asked == PlatformPolicy.ProAudio);
        await rig.Ok("MACHINE SET MMCSS OFF");
        using (var off = rig.Recorded.ClaimThread(ThreadRole.Audio))
            Assert.DoesNotContain(off.Claims, c => c.What == "thread.audio.mmcss");
        await rig.Ok("MACHINE SET MMCSS ON");
        using (var on = rig.Recorded.ClaimThread(ThreadRole.Audio))
            Assert.Contains(on.Claims, c => c.What == "thread.audio.mmcss");

        await rig.Ok("MACHINE SET CPUSETS OFF");
        Assert.Equal("off", rig.Platform.Used[^1].CpuSets);
        await rig.Ok("MACHINE SET CPUSETS AUTO");
        Assert.Equal("auto", rig.Platform.Used[^1].CpuSets);
        // MACHINE LOCK saves the node's settings too: the platform is handed them again, unchanged.
        await rig.Ok("MACHINE LOCK");
        Assert.Equal(("auto", "on"), (rig.Platform.Used[^1].CpuSets, rig.Platform.Used[^1].Mmcss));
    }

    [Fact]
    public async Task ThePowerPlanIsThePlatformsReading()
    {
        await using var rig = new MachineRig();
        var plan = Platform(rig).GetProperty("powerPlan");
        Assert.Equal(("Ultimate Performance", "ultimate"), (plan.GetProperty("name").GetString(), plan.GetProperty("kind").GetString()));
    }
}
