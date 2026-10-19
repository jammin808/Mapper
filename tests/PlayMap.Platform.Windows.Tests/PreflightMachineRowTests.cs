using PlayMap.Core.Bus;
using PlayMap.Core.Machine;
using PlayMap.Core.Wire;

namespace PlayMap.Platform.Windows.Tests;

/// <summary>The machine's preflight rows, pure: each must row fails on an injected fake fact and passes once the fact is put right; a
/// refused claim keeps its words; a reading Windows did not give is grey "unknown", never a guess; every fix line is a line of the
/// catalogue.</summary>
public sealed class PreflightMachineRowTests
{
    private static readonly DateOnly Today = new(2026, 10, 19);

    /// <summary>A machine with every must row passing: Pro 25H2, Ultimate Performance, no timeouts, no screen saver, no reboot pending,
    /// a Private network, every claim applied, the timer within 1 ms.</summary>
    private static readonly MachineReadings Good = new()
    {
        Os = EditionTable.Classify("Professional", 0x30, "25H2", 26200, 6899),
        SmartAppControl = 0,
        Power = new PowerReading("Ultimate Performance", "ultimate", false, "", 0, 0),
        ScreenSaver = 0,
        RebootRead = true,
        NetworksRead = true,
        Networks = [new NetworkProfile("Show LAN", "private", false)],
        Claims = [new Claim("process.qos", "high", true, "high", "")],
        TimerWaits = 1000,
        TimerLateP99Us = 180,
    };

    public static readonly TheoryData<string, string> Faults = new()
    {
        { "os.build", "build 22631" },
        { "power.plan", "Balanced" },
        { "power.sleep", "30 min" },
        { "power.display", "10 min" },
        { "screensaver", "on" },
        { "reboot", "pending" },
        { "network.public:Show LAN", "public" },
        { "claim:thread.render.mmcss", "refused" },
        { "timer", "late" },
        { "sac", "evaluation" },
    };

    private static MachineReadings Break(string item) => item switch
    {
        "os.build" => Good with { Os = EditionTable.Classify("Professional", 0x30, "23H2", 22631, 4460) },
        "power.plan" => Good with { Power = Good.Power with { Plan = "Balanced", Kind = "balanced" } },
        "power.sleep" => Good with { Power = Good.Power with { SleepAcMin = 30 } },
        "power.display" => Good with { Power = Good.Power with { DisplayAcMin = 10 } },
        "screensaver" => Good with { ScreenSaver = 1 },
        "reboot" => Good with { RebootPending = ["Windows Update"] },
        "network.public:Show LAN" => Good with { Networks = [new NetworkProfile("Show LAN", "public", false)] },
        "claim:thread.render.mmcss" => Good with { Claims = [.. Good.Claims, new Claim("thread.render.mmcss", "Games", false, "", "MMCSS did not take the thread (error 1060: The specified service does not exist as an installed service.)")] },
        "timer" => Good with { TimerLateP99Us = 15_600 },
        "sac" => Good with { SmartAppControl = 2 },
        _ => throw new ArgumentException(item, nameof(item)),
    };

    [Theory]
    [MemberData(nameof(Faults))]
    public void EachRowFailsOnAnInjectedFactAndPassesOnceFixed(string item, string fault)
    {
        var failing = MachineRows.Build(Break(item), Today);
        var row = Assert.Single(failing, r => r.Item == item);
        Assert.True(row.Light is Light.Amber or Light.Red, $"{item} ({fault}) lit {row.Light}");
        Assert.False(string.IsNullOrWhiteSpace(row.Words));

        var fixedRows = MachineRows.Build(Good, Today);
        Assert.DoesNotContain(fixedRows, r => r.Light is Light.Amber or Light.Red);
        Assert.True(fixedRows.FirstOrDefault(r => r.Item == item) is null or { Light: Light.Green }, item);
    }

    [Fact]
    public void ABuildBelow24H2IsRedWithTheBuildNamed()
    {
        var row = Assert.Single(MachineRows.Build(Break("os.build"), Today), r => r.Item == "os.build");
        Assert.Equal((Light.Red, "Windows 11 24H2 or later is required (this is build 22631)"), (row.Light, row.Words));
    }

    [Fact]
    public void ARefusedClaimKeepsItsWords()
    {
        var row = Assert.Single(MachineRows.Build(Break("claim:thread.render.mmcss"), Today), r => r.Item == "claim:thread.render.mmcss");
        Assert.Equal("thread.render.mmcss Games was refused: MMCSS did not take the thread (error 1060: The specified service does not exist as an installed service.)", row.Words);
        Assert.Equal("Claim thread.render.mmcss", MachineRows.Label(row.Item));
        // A claim that asked nothing (a platform that claims nothing) is not a refusal.
        Assert.DoesNotContain(MachineRows.Build(Good with { Claims = [new Claim("process", "", false, "", "the platform seam is Windows only")] }, Today),
            r => r.Item.StartsWith("claim:", StringComparison.Ordinal));
    }

    [Fact]
    public void ABalancedOnlyMachineSaysItsPowerModeNotThePlansName()
    {
        var balancedOnly = Good with { Power = new PowerReading("Balanced", "balanced", true, "balanced", 0, 0) };
        var row = Assert.Single(MachineRows.Build(balancedOnly, Today), r => r.Item == "power.plan");
        Assert.Equal((Light.Amber, "balanced"), (row.Light, row.Value));
        Assert.StartsWith("this machine offers Balanced only; its power mode is balanced", row.Words, StringComparison.Ordinal);
        Assert.DoesNotContain("not High Performance", row.Words, StringComparison.Ordinal);
        var best = Good with { Power = new PowerReading("Balanced", "balanced", true, "best performance", 0, 0) };
        Assert.Equal(Light.Green, Assert.Single(MachineRows.Build(best, Today), r => r.Item == "power.plan").Light);
    }

    [Fact]
    public void AReadingWindowsDidNotGiveIsGreyUnknown()
    {
        var rows = MachineRows.Build(new MachineReadings(), Today);
        foreach (var item in new[] { "os.edition", "power.plan", "power.sleep", "power.display", "screensaver", "reboot", "network" })
        {
            var row = Assert.Single(rows, r => r.Item == item);
            Assert.Equal((Light.Grey, "unknown"), (row.Light, row.Value));
            Assert.EndsWith("could not be read: unknown", row.Words, StringComparison.Ordinal);
        }
        Assert.Equal((Light.Grey, "no waits"), rows.Single(r => r.Item == "timer") is var t ? (t.Light, t.Value) : default);
    }

    [Fact]
    public void EveryFixLineIsALineOfTheCatalogue()
    {
        var awakeOff = Good with { AwakeOn = false, Power = Good.Power with { DisplayAcMin = 10 } };
        var display = Assert.Single(MachineRows.Build(awakeOff, Today), r => r.Item == "power.display");
        Assert.Equal("MACHINE SET AWAKE ON", display.Fix);
        // With keep-awake on the timeout stays amber, and no verb fixes it: the words say where a person sets it.
        Assert.Null(Assert.Single(MachineRows.Build(Break("power.display"), Today), r => r.Item == "power.display").Fix);

        var locked = LockedDriverSet.Of(NodeIdentity.Unread, 1);
        var drifted = MachineRows.Engine(Good.Os, locked with { Ffmpeg = "n9.0.1" }, locked, []);
        var fixes = MachineRows.Build(awakeOff, Today).Concat(drifted).Select(r => r.Fix).OfType<string>().ToList();
        Assert.Equal(["MACHINE SET AWAKE ON", "MACHINE LOCK"], fixes);
        foreach (var fix in fixes)
        {
            Assert.True(WireGrammar.TryParse(fix, out var line, out var error), error?.Reason);
            Assert.NotNull(VerbCatalog.Match(line));
        }
    }
}
