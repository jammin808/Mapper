using PlayMap.Core.Machine;

namespace PlayMap.Engine.Tests.Machine;

/// <summary>MACHINE, MACHINE SET and MACHINE LOCK on a fake Windows platform: REALTIME without the privilege is refused naming it;
/// armed, the Setup class refuses both changes; a setting at its value is refused; the locked set again and CLEAR with none are refused
/// with the reason and nothing is written; a change is kept on the node, applied now where it is the process's (the claim's answer in
/// the receipt and in STATE) or from the next start, saying which.</summary>
public sealed class MachineVerbTests
{
    [Fact]
    public async Task RealtimeWithoutThePrivilegeIsRefusedNamingItAndWithItIsKeptAndClaimed()
    {
        await using var rig = new MachineRig();
        var words = await rig.Refused("MACHINE SET GPU-PRIORITY REALTIME");
        Assert.Equal(PlatformPolicy.RealtimeWithoutPrivilegeWords, words);
        Assert.Empty(rig.Saved);
        Assert.Equal("normal", rig.Machine().GetProperty("platform").GetProperty("settings").GetProperty("gpuPriority").GetString());

        rig.Platform.Privileged = true;
        var receipt = await rig.Ok("MACHINE SET GPU-PRIORITY REALTIME");
        Assert.Equal(("GPU-PRIORITY", "realtime", "normal", "now"), (receipt.GetProperty("word").GetString(), receipt.GetProperty("asked").GetString(),
            receipt.GetProperty("was").GetString(), receipt.GetProperty("applies").GetString()));
        Assert.True(receipt.GetProperty("claim").GetProperty("applied").GetBoolean());
        Assert.EndsWith("the direct queue asks for global realtime at the device's next start", receipt.GetProperty("words").GetString(), StringComparison.Ordinal);
        Assert.Equal("realtime", Assert.Single(rig.Saved).GpuPriority);
        var claim = rig.Machine().GetProperty("platform").GetProperty("claims").EnumerateArray().Single(c => c.GetProperty("what").GetString() == "process.gpuPriority");
        Assert.Equal(("realtime", true, "realtime"), (claim.GetProperty("asked").GetString(), claim.GetProperty("applied").GetBoolean(), claim.GetProperty("readBack").GetString()));
    }

    [Fact]
    public async Task ArmedTheSetupClassRefusesBothChangesAndTheStatusStillAnswers()
    {
        await using var rig = new MachineRig();
        await rig.Ok("SHOW ARM");
        Assert.StartsWith("Not while armed: ", await rig.Refused("MACHINE SET QOS AUTO"), StringComparison.Ordinal);
        Assert.StartsWith("Not while armed: ", await rig.Refused("MACHINE LOCK"), StringComparison.Ordinal);
        Assert.Empty(rig.Saved);
        Assert.Contains("every one applied", (await rig.Ok("MACHINE STATUS")).GetProperty("words").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASettingAtItsValueIsRefusedAndNothingIsWritten()
    {
        await using var rig = new MachineRig();
        Assert.Equal("QoS is already high", await rig.Refused("MACHINE SET QOS HIGH"));
        Assert.Equal("MACHINE SET QOS HIGH|AUTO", await rig.Refused("MACHINE SET QOS LOW"));
        Assert.Equal(PlatformPolicy.Syntax, await rig.Refused("MACHINE SET TURBO ON"));
        Assert.Empty(rig.Saved);
        Assert.Single(rig.Platform.Asked);
    }

    [Fact]
    public async Task AProcessClaimAppliesNowWithItsAnswerAndAThreadsFromTheNextStart()
    {
        await using var rig = new MachineRig();
        rig.Platform.Refuse.Add("process.qos");
        var qos = await rig.Ok("MACHINE SET QOS AUTO");
        Assert.Equal("now", qos.GetProperty("applies").GetString());
        Assert.False(qos.GetProperty("claim").GetProperty("applied").GetBoolean());
        Assert.Equal("QoS auto is kept, and Windows refused it now: Windows refused it here", qos.GetProperty("words").GetString());
        Assert.Equal("auto", rig.Platform.Asked[^1].Qos);
        var platform = rig.Machine().GetProperty("platform");
        Assert.Equal("amber", platform.GetProperty("light").GetString());
        Assert.Equal("auto", platform.GetProperty("settings").GetProperty("qos").GetString());

        var mmcss = await rig.Ok("MACHINE SET MMCSS OFF");
        Assert.Equal(("next start", "MMCSS off is kept and applies from each claimed thread's next start"),
            (mmcss.GetProperty("applies").GetString(), mmcss.GetProperty("words").GetString()));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, mmcss.GetProperty("claim").ValueKind);
        Assert.Equal(["auto", "off"], [rig.Saved[0].Qos, rig.Saved[1].Mmcss]);
        Assert.Equal("off", rig.Machine().GetProperty("platform").GetProperty("settings").GetProperty("mmcss").GetString());
        var journal = rig.Engine.Journal.Recent(10).Where(e => e.Kind == "machine").Select(e => e.Text).ToList();
        Assert.Contains("machine qos high → auto (now)", journal);
        Assert.Contains("machine mmcss on → off (next start)", journal);
    }

    [Fact]
    public async Task TheLockedSetAgainAndClearWithNoneAreRefusedAndNothingIsWritten()
    {
        await using var rig = new MachineRig();
        Assert.Equal("no driver set is locked", await rig.Refused("MACHINE LOCK CLEAR"));
        Assert.Empty(rig.Saved);

        var locked = await rig.Ok("MACHINE LOCK");
        Assert.True(locked.GetProperty("locked").GetBoolean());
        Assert.Equal("582.16", locked.GetProperty("set").GetProperty("adapters")[0].GetProperty("driver").GetString());
        Assert.Equal("26100.4061", Assert.Single(rig.Saved).Locked!.Os);
        Assert.Equal("this machine's drivers are already the locked set", await rig.Refused("MACHINE LOCK"));
        Assert.Single(rig.Saved);

        // A driver that changed is drift in STATE until it is locked again.
        rig.Platform.Ident = MachineRig.Identity("591.44");
        var drift = Assert.Single(rig.Machine().GetProperty("locked").GetProperty("drift").EnumerateArray());
        Assert.Equal(("582.16", "591.44"), (drift.GetProperty("locked").GetString(), drift.GetProperty("now").GetString()));
        var again = await rig.Ok("MACHINE LOCK");
        Assert.StartsWith("this machine's driver set is locked again, taking NVIDIA RTX PRO 4000 Blackwell driver 582.16 → 591.44", again.GetProperty("words").GetString(), StringComparison.Ordinal);
        Assert.Empty(rig.Machine().GetProperty("locked").GetProperty("drift").EnumerateArray());

        Assert.False((await rig.Ok("MACHINE LOCK CLEAR")).GetProperty("locked").GetBoolean());
        Assert.Null(rig.Saved[^1].Locked);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, rig.Machine().GetProperty("locked").ValueKind);
        Assert.Equal("no driver set is locked", await rig.Refused("MACHINE LOCK CLEAR"));
        Assert.Equal(3, rig.Saved.Count);
        Assert.Equal("MACHINE LOCK [CLEAR]", await rig.Refused("MACHINE LOCK NOW"));
    }

    [Fact]
    public async Task ANodeWithNoHomeOrASettingsFileThatRefusesKeepsNothing()
    {
        await using (var rig = new MachineRig(home: false))
        {
            Assert.StartsWith("this node keeps no settings", await rig.Refused("MACHINE SET QOS AUTO"), StringComparison.Ordinal);
            Assert.StartsWith("this node keeps no settings", await rig.Refused("MACHINE LOCK"), StringComparison.Ordinal);
        }
        await using var refusing = new MachineRig(save: _ => throw new UnauthorizedAccessException("Access to the path 'playmap.node.json' is denied."));
        Assert.Equal("the node's settings could not be written: Access to the path 'playmap.node.json' is denied.", await refusing.Refused("MACHINE SET AWAKE OFF"));
        Assert.Equal("on", refusing.Node.Saved.Awake);
        Assert.Equal("on", refusing.Machine().GetProperty("platform").GetProperty("settings").GetProperty("awake").GetString());
    }

    [Fact]
    public async Task MachineStatusSaysTheIdentityTheClaimsAndTheDriftInWords()
    {
        await using var rig = new MachineRig();
        rig.Platform.Refuse.Add("process.priority");
        _ = rig.Recorded.ClaimProcess(MachineSettings.Default);
        var status = await rig.Ok("MACHINE");
        var words = status.GetProperty("words").GetString()!;
        Assert.StartsWith("PlayMap 0.2.0 (build 17, abc1234) · Windows 11 IoT Enterprise LTSC 24H2 · build 26100.4061 · tier ltsc; 3 claims, 1 refused (process.priority: Windows refused it here); no driver set is locked",
            words, StringComparison.Ordinal);
        Assert.Equal("windows", status.GetProperty("platform").GetString());
        Assert.Equal("ltsc", status.GetProperty("identity").GetProperty("os").GetProperty("tier").GetString());
        Assert.Equal("MACHINE [STATUS]", await rig.Refused("MACHINE NOW"));
    }
}

/// <summary>The context's machine item on a Windows node: the OS line, the tier, PlayMap's build and the drift count, amber with drift
/// and linked to the fleet's machine item; a node whose platform claims nothing adds none.</summary>
public sealed class MachineContextTests
{
    private static System.Text.Json.JsonElement Item(EngineCore engine)
    {
        using var context = System.Text.Json.JsonDocument.Parse(engine.Context());
        foreach (var i in context.RootElement.GetProperty("items").EnumerateArray())
            if (i.GetProperty("id").GetString() == "machine:platform")
                return i.Clone();
        return default;
    }

    [Fact]
    public async Task AWindowsNodeSaysItsOsTierBuildAndDrift()
    {
        await using var rig = new MachineRig();
        var item = Item(rig.Engine);
        Assert.Equal(("machine", "Windows 11 IoT Enterprise LTSC 24H2", "green", "0.2.0+17.abc1234 · build 26100.4061 · tier ltsc · drift 0"),
            (item.GetProperty("kind").GetString(), item.GetProperty("label").GetString(), item.GetProperty("light").GetString(), item.GetProperty("state").GetString()));
        await rig.Ok("MACHINE LOCK");
        rig.Platform.Ident = MachineRig.Identity("591.44");
        rig.Engine.FactsChanged();
        item = Item(rig.Engine);
        Assert.Equal(("amber", "0.2.0+17.abc1234 · build 26100.4061 · tier ltsc · drift 1"), (item.GetProperty("light").GetString(), item.GetProperty("state").GetString()));

        await using var bare = new EngineCore(EngineServices.ForTests(), PlayMap.Core.Model.Project.CreateDefault("Bare"));
        Assert.Equal(System.Text.Json.JsonValueKind.Undefined, Item(bare).ValueKind);
    }
}
