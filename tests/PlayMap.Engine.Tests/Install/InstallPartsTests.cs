using PlayMap.Core.Bus;
using PlayMap.Core.Machine;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Host;
using PlayMap.Engine.Install;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Install;

/// <summary>The exe's install part, taken from its composition by reference and composed on a node as the host composes it: preflight's
/// install section is one of the node's checks, and PREFLIGHT QUICK's report carries its rows. No test machine holds the install's key
/// (Linux has no registry, and the Windows test job installs nothing), so the rows are a folder run's on both CI jobs. An engine the
/// supervisor started reads the supervisor's folder and journals, as it starts, the cause of the stop before it when the status file
/// names it; any other engine reads no folder.</summary>
public sealed class InstallPartsTests
{
    private static EngineParts Parts()
    {
        var standard = EngineParts.Standard().Extensions;
        Assert.Contains(InstallParts.Factory, standard);
        return EngineParts.Bare with { Extensions = [Assert.Single(standard, f => f == InstallParts.Factory)] };
    }

    [Fact]
    public async Task TheExesCompositionPutsTheInstallSectionInPreflightQuick()
    {
        await using var rig = await HostRig.StartAsync("Install", parts: Parts());
        Assert.Single(rig.Engine.PreflightChecks, c => c is InstallPreflight);

        var report = PreflightReports.Read(await rig.OkAsync("PREFLIGHT QUICK WAIT"));
        Assert.NotNull(report);
        var rows = report.Rows.Where(r => r.Section == "install").ToList();
        Assert.Equal(["install:installed", "install:policies", "install:signed"], rows.Select(r => r.Id));
        // The node's project drives an enabled output (a new project's own), so a folder run is a show node's warning, with the way out.
        Assert.Contains(rig.Engine.Store.Project.Outputs, o => o.Enabled);
        Assert.Equal((Light.Amber, "folder run"), (rows[0].Light, rows[0].Value));
        Assert.StartsWith("not installed: nothing restarts the engine if it stops", rows[0].Note, StringComparison.Ordinal);
        Assert.Equal((Light.Grey, "i"), (rows[1].Light, rows[1].Value));
        Assert.Equal((Light.Grey, "i"), (rows[2].Light, rows[2].Value));
        Assert.StartsWith("this build is unsigned", rows[2].Note, StringComparison.Ordinal);
    }

    private const string Frozen = "frozen: shown frames stood at 600 for 3 s while the GPU ran";

    [Fact]
    public async Task OnlyAnEngineTheSupervisorStartedReadsTheSupervisorsFolder()
    {
        foreach (var supervised in new[] { false, true })
        {
            InstallParts? made = null;
            var parts = EngineParts.Bare with
            {
                Extensions = [s => made = Assert.IsType<InstallParts>(Assert.Single(Parts().Extensions)(s))],
            };
            string[] words = supervised ? ["engine", "headless", "--no-recover", "--supervised"] : ["engine", "headless", "--no-recover"];
            await using var rig = await HostRig.StartAsync("Install", parts: parts, options: home => HostRig.Parse([.. words, "--home", home]));
            Assert.NotNull(made);
            // The status lies in the supervisor's folder, above the engine's folder that holds the pid record.
            Assert.Equal(supervised ? SupervisorStatus.MachineFolder() : null, made.SupervisorFolder);
        }
    }

    [Fact]
    public async Task ASupervisedEngineJournalsTheCauseOfTheStopBeforeIt()
    {
        var folder = Directory.CreateTempSubdirectory("playmap-supervisor-").FullName;
        try
        {
            new SupervisorStatus("starting", Environment.ProcessId, 2, Frozen, "active", 1_760_000_000_000).Write(folder);
            await using var rig = await HostRig.StartAsync("Install", parts: EngineParts.Bare with
            {
                Extensions = [_ => new InstallParts(folder, Environment.ProcessId)],
            });
            var row = Assert.Single(rig.Engine.Journal.Recent(200), r => r.Kind == "supervisor");
            Assert.Equal("the supervisor started this engine after: " + Frozen + " (2 restarts in ten minutes)", row.Text);
            Assert.False(row.Ok);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task NoCauseNoStatusOrAnotherEnginesStatusJournalsNothing()
    {
        var folder = Directory.CreateTempSubdirectory("playmap-supervisor-").FullName;
        try
        {
            var statuses = new SupervisorStatus?[]
            {
                null,
                new("starting", Environment.ProcessId, 0, "", "the engine reports no shown frames: the freeze rule waits for them", 1),
                new("running", Environment.ProcessId + 1, 1, "the engine exited", "active", 1),
            };
            foreach (var status in statuses)
            {
                File.Delete(Path.Combine(folder, SupervisorStatus.FileName));
                status?.Write(folder);
                await using var rig = await HostRig.StartAsync("Install", parts: EngineParts.Bare with
                {
                    Extensions = [_ => new InstallParts(folder, Environment.ProcessId)],
                });
                Assert.DoesNotContain(rig.Engine.Journal.Recent(200), r => r.Kind == "supervisor");
            }
            // An engine the supervisor did not start reads no folder, whatever the folder holds.
            new SupervisorStatus("starting", Environment.ProcessId, 2, Frozen, "active", 1).Write(folder);
            await using var desk = await HostRig.StartAsync("Install", parts: EngineParts.Bare with
            {
                Extensions = [_ => new InstallParts(null, Environment.ProcessId)],
            });
            Assert.DoesNotContain(desk.Engine.Journal.Recent(200), r => r.Kind == "supervisor");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void TheStartsWordsNameTheCauseOnlyOfAStatusThatNamesThisEngine()
    {
        var status = new SupervisorStatus("starting", 4120, 3, "the engine exited", "active", 1);
        Assert.Equal("the supervisor started this engine after: the engine exited (3 restarts in ten minutes)", InstallParts.StartWords(status, 4120));
        Assert.Null(InstallParts.StartWords(status, 4121));
        Assert.Null(InstallParts.StartWords(status with { LastCause = "" }, 4120));
        Assert.Null(InstallParts.StartWords(null, 4120));
    }
}
