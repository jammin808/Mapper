using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Machine;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;
using PlayMap.Engine.Machine;

namespace PlayMap.Engine.Tests.Machine;

/// <summary>Preflight's machine section on the engine: the platform's facts become rows of section machine with their labels, lights and
/// fix lines; a changed GPU driver against the lock is an amber row naming both versions fixed by MACHINE LOCK, gone once locked again;
/// an enabled display output on the primary display is amber, gone once it moves; every fix line parses as a catalogue line; on a node
/// that is not Windows, STATE and preflight say "the platform seam is Windows only".</summary>
public sealed class PreflightMachineTests
{
    private static async Task<IReadOnlyList<PreflightRow>> Rows(MachineRig rig) =>
        await new MachinePreflight().RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None);

    [Fact]
    public async Task ThePlatformsFactsAreRowsOfTheMachineSection()
    {
        await using var rig = new MachineRig();
        rig.Platform.OwnFacts =
        [
            new MachineFact("os.build", Light.Red, "22631", "Windows 11 24H2 or later is required (this is build 22631)"),
            new MachineFact("power.display", Light.Amber, "10 min", "the display sleeps after 10 min on mains and keep-awake is off", "MACHINE SET AWAKE ON"),
        ];
        var rows = await Rows(rig);
        Assert.All(rows, r => Assert.Equal(("machine", "0badc0de"), (r.Section, r.Node)));
        var build = Assert.Single(rows, r => r.Id == "machine.os.build");
        Assert.Equal(("Windows build", Light.Red, "22631"), (build.Item, build.Light, build.Value));
        var display = Assert.Single(rows, r => r.Id == "machine.power.display");
        Assert.Equal(("Display timeout", "MACHINE SET AWAKE ON"), (display.Item, display.Fix));
        var lockRow = Assert.Single(rows, r => r.Id == "machine.lock");
        Assert.Equal((Light.Grey, "not locked"), (lockRow.Light, lockRow.Value));

        // Fixed: the platform's facts pass, and the rows say so.
        rig.Platform.OwnFacts = [new MachineFact("os.build", Light.Green, "26100", "build 26100.4061")];
        Assert.DoesNotContain(await Rows(rig), r => r.Light is Light.Amber or Light.Red);
    }

    [Fact]
    public async Task AChangedDriverAgainstTheLockIsAmberUntilLockedAgain()
    {
        await using var rig = new MachineRig();
        await rig.Ok("MACHINE LOCK");
        Assert.Equal(Light.Green, Assert.Single(await Rows(rig), r => r.Id == "machine.lock").Light);
        rig.Platform.Ident = MachineRig.Identity("591.44");
        var drift = Assert.Single(await Rows(rig), r => r.Id == "machine.lock.drift:gpu:10de:2bb1");
        Assert.Equal((Light.Amber, "591.44", "MACHINE LOCK"), (drift.Light, drift.Value, drift.Fix));
        Assert.Equal("NVIDIA RTX PRO 4000 Blackwell driver 582.16 → 591.44 since the driver set was locked", drift.Note);
        Assert.Equal("Driver drift gpu:10de:2bb1", drift.Item);
        await rig.Ok(drift.Fix!);
        Assert.DoesNotContain(await Rows(rig), r => r.Id.StartsWith("machine.lock.drift", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnHomeTheDriftSaysTheNodeObservesTheChange()
    {
        await using var rig = new MachineRig();
        rig.Platform.Ident = MachineRig.Identity("582.16") with { Os = EditionTable.Classify("Core", 0x65, "25H2", 26200, 6899) };
        await rig.Ok("MACHINE LOCK");
        rig.Platform.Ident = rig.Platform.Ident with { Adapters = [rig.Platform.Ident.Adapters[0] with { Driver = "591.44" }] };
        var drift = Assert.Single(await Rows(rig), r => r.Id.StartsWith("machine.lock.drift", StringComparison.Ordinal));
        Assert.EndsWith("(drivers cannot be held back on this edition: this node observes the change)", drift.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEnabledDisplayOutputOnThePrimaryDisplayIsAmberUntilItMoves()
    {
        var displays = new FakeDisplays(new DisplayInfo(@"\\.\DISPLAY1", "Desk", 0, 0, 1920, 1080, 60, true),
            new DisplayInfo(@"\\.\DISPLAY2", "Projector", 1920, 0, 1920, 1080, 60, false));
        var project = Project.CreateDefault("Primary");
        var output = project.Outputs[0] with { Name = "Stage left", Kind = OutputKind.Display, DisplayKey = @"\\.\DISPLAY1", Enabled = true };
        await using (var rig = new MachineRig(project with { Outputs = [output] }, outputs: displays))
        {
            var row = Assert.Single(await Rows(rig), r => r.Id == "machine.display.primary:Stage left");
            Assert.Equal((Light.Amber, @"\\.\DISPLAY1"), (row.Light, row.Value));
            Assert.Equal("notifications, UAC and setup screens open on Stage left: make the desk's monitor primary", row.Note);
        }
        await using var moved = new MachineRig(project with { Outputs = [output with { DisplayKey = @"\\.\DISPLAY2" }] }, outputs: displays);
        Assert.DoesNotContain(await Rows(moved), r => r.Id.StartsWith("machine.display.primary", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryFixLineTheSectionOffersIsACatalogueLine()
    {
        await using var rig = new MachineRig();
        await rig.Ok("MACHINE LOCK");
        rig.Platform.Ident = MachineRig.Identity("591.44");
        rig.Platform.OwnFacts = [new MachineFact("power.sleep", Light.Amber, "30 min", "the system sleeps after 30 min on mains and keep-awake is off", "MACHINE SET AWAKE ON")];
        var fixes = (await Rows(rig)).Select(r => r.Fix).OfType<string>().ToList();
        Assert.Equal(["MACHINE SET AWAKE ON", "MACHINE LOCK"], fixes);
        foreach (var fix in fixes)
        {
            Assert.True(WireGrammar.TryParse(fix, out var line, out var error), error?.Reason);
            Assert.NotNull(VerbCatalog.Match(line));
        }
    }

    [Fact]
    public async Task OffWindowsStateAndPreflightSayThePlatformSeamIsWindowsOnly()
    {
        var parts = new MachineParts(null);
        var services = parts.Configure(EngineServices.ForTests());
        await using var engine = new EngineCore(services, Project.CreateDefault("Null platform"));
        // The render host starts before the extensions, so a thread claims its role before the machine part starts.
        using var render = services.MachinePlatform.ClaimThread(ThreadRole.Render);
        await parts.StartAsync(engine, CancellationToken.None);
        using var decode = services.MachinePlatform.ClaimThread(ThreadRole.Decode);
        var rows = await services.PreflightChecks.OfType<MachinePreflight>().Single()
            .RunAsync(new PreflightContext(engine, PreflightDepth.Quick, false), CancellationToken.None);
        engine.FactsChanged();
        using var state = JsonDocument.Parse(engine.State().Json);
        var platform = state.RootElement.GetProperty("machine").GetProperty("platform");
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("windows", platform.GetProperty("name").GetString());
            Assert.Contains(rows, r => r.Id == "machine.os.edition");
        }
        else
        {
            Assert.Equal(("none", MachineParts.NotWindowsWords, "grey"), (platform.GetProperty("name").GetString(), platform.GetProperty("words").GetString(),
                platform.GetProperty("light").GetString()));
            // The section is the platform's grey row and the driver lock's, nothing amber.
            var row = Assert.Single(rows, r => r.Id == "machine.platform");
            Assert.Equal((Light.Grey, "none", "the platform seam is Windows only"), (row.Light, row.Value, row.Note));
            Assert.Equal(["machine.platform", "machine.lock"], rows.Select(r => r.Id));
            Assert.All(rows, r => Assert.Equal(Light.Grey, r.Light));
            // Nothing was claimed, threads' claims included: the one process claim asked nothing and is grey.
            Assert.Equal("", Assert.Single(render.Claims).Asked);
            var claim = Assert.Single(platform.GetProperty("claims").EnumerateArray());
            Assert.Equal(("process", "", false, "grey"), (claim.GetProperty("what").GetString(), claim.GetProperty("asked").GetString(),
                claim.GetProperty("applied").GetBoolean(), claim.GetProperty("light").GetString()));
            // Nothing is held either: keep-awake is asked of a platform that holds nothing, and STATE says it is not held.
            Assert.False(platform.GetProperty("awake").GetBoolean());
            Assert.Equal(("", ""), (platform.GetProperty("powerPlan").GetProperty("name").GetString(), platform.GetProperty("powerPlan").GetProperty("kind").GetString()));
            using var index = JsonDocument.Parse(engine.Companion.Index().Json);
            var section = index.RootElement.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("section").GetString() == "machine");
            Assert.DoesNotContain(section.GetProperty("rows").EnumerateArray(), r => r.GetProperty("light").GetString() == "amber");
            Assert.DoesNotContain(engine.Journal.Recent(50), e => e.Text.Contains("was refused", StringComparison.Ordinal));
        }
        await parts.DisposeAsync();
    }
}
