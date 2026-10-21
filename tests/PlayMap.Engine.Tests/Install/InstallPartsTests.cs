using PlayMap.Core.Bus;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Host;
using PlayMap.Engine.Install;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Install;

/// <summary>The exe's install part, taken from its composition by reference and composed on a node as the host composes it: preflight's
/// install section is one of the node's checks, and PREFLIGHT QUICK's report carries its rows. No test machine holds the install's key
/// (Linux has no registry, and the Windows test job installs nothing), so the rows are a folder run's on both CI jobs.</summary>
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
}
