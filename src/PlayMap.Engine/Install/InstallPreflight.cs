using System.Globalization;
using PlayMap.Core.Bus;
using PlayMap.Core.Preflight;

namespace PlayMap.Engine.Install;

/// <summary>Preflight's <c>install</c> section (docs/round3/PLAN.md §3.5): the install itself (a folder run is amber on a show node — one
/// whose project drives an enabled output — and grey elsewhere), the supervisor's state (red once it stopped trying, with the line that
/// resumes it in words; the freeze rule waiting for shown frames said as information), auto-logon on a supervised node and for whom,
/// the show machine's policies (an information row: this build writes none, and Home honours none), and the build unsigned (an
/// information row). Information rows are grey with the value "i". Read from published facts only.</summary>
public sealed class InstallPreflight(Func<EngineCore, InstallFacts>? facts = null) : IPreflightCheck
{
    public string Section => "install";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(1);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = context.Engine;
        var f = facts?.Invoke(engine) ?? engine.InstallNow();
        var showNode = engine.Store.Project.Outputs.Any(o => o.Enabled);
        return Task.FromResult(Rows(f, engine.Services.Settings.NodeId, showNode));
    }

    /// <summary>Pure: the section's rows for these facts.</summary>
    public static IReadOnlyList<PreflightRow> Rows(InstallFacts f, string node, bool showNode)
    {
        ArgumentNullException.ThrowIfNull(f);
        const string section = "install";
        var rows = new List<PreflightRow>();
        if (!f.Installed)
        {
            rows.Add(new PreflightRow("install:installed", node, section, "Install", showNode ? Light.Amber : Light.Grey, "folder run",
                showNode
                    ? "not installed: nothing restarts the engine if it stops — install PlayMap on this show node (docs/INSTALL.md)"
                    : "not installed: a folder run (this node drives no output)",
                null));
        }
        else
        {
            rows.Add(new PreflightRow("install:installed", node, section, "Install", Light.Green, f.Version,
                string.Create(CultureInfo.InvariantCulture, $"installed {f.Version} in {f.Dir}"), null));
            rows.Add(Supervisor(f, node, section));
            if (f.Supervised)
            {
                rows.Add(f.AutoLogon
                    ? new PreflightRow("install:autologon", node, section, "Auto-logon", Light.Green, "on", "auto-logon signs in " + f.AutoLogonUser, null)
                    : new PreflightRow("install:autologon", node, section, "Auto-logon", Light.Amber, "off",
                        "auto-logon is off: after a power cut nobody signs in, so the supervisor has no session to start the engine in (docs/INSTALL.md)", null));
            }
        }
        rows.Add(f.EditionHasUpdatePolicies
            ? new PreflightRow("install:policies", node, section, "Show machine policies", Light.Grey, "i",
                "this build writes no Windows Update policy: defer updates and set the maintenance window by hand (docs/INSTALL.md)", null)
            : new PreflightRow("install:policies", node, section, "Show machine policies", Light.Grey, "i",
                "not on this edition: Home honours no Update policy — pause updates past the show and set active hours by hand (docs/INSTALL.md)", null));
        rows.Add(new PreflightRow("install:signed", node, section, "Signature", Light.Grey, "i",
            f.Signed ? "signed" : "this build is unsigned (the owner's decision): check it against its SHA256SUMS (docs/INSTALL.md)", null));
        return rows;
    }

    private static PreflightRow Supervisor(InstallFacts f, string node, string section)
    {
        if (!f.Supervised)
            return new PreflightRow("install:supervisor", node, section, "Supervisor", Light.Grey, "i", "installed without the supervisor", null);
        if (f.Supervisor is not { } s)
            return new PreflightRow("install:supervisor", node, section, "Supervisor", Light.Amber, "no status",
                "the supervisor has written no status: is the PlayMapSupervisor service running?", null);
        return s.State switch
        {
            "stopped-too-many-restarts" => new PreflightRow("install:supervisor", node, section, "Supervisor", Light.Red, "stopped",
                "the supervisor stopped after too many restarts (" + s.LastCause + ") — run PlayMap.Supervisor.exe resume", null),
            "held" => new PreflightRow("install:supervisor", node, section, "Supervisor", Light.Amber, "held",
                "a person stopped the engine — run PlayMap.Supervisor.exe resume", null),
            "running" or "starting" when s.FreezeRule.StartsWith("the engine reports no shown frames", StringComparison.Ordinal) =>
                new PreflightRow("install:supervisor", node, section, "Supervisor", Light.Grey, "i",
                    string.Create(CultureInfo.InvariantCulture, $"supervised, {s.Restarts} restarts in ten minutes; {s.FreezeRule}"), null),
            "running" or "starting" => new PreflightRow("install:supervisor", node, section, "Supervisor", Light.Green, s.State,
                string.Create(CultureInfo.InvariantCulture, $"supervised, {s.Restarts} restarts in ten minutes; freeze rule {s.FreezeRule}"), null),
            _ => new PreflightRow("install:supervisor", node, section, "Supervisor", Light.Amber, s.State,
                "the supervisor is " + s.State.Replace('-', ' ') + (s.LastCause.Length > 0 ? ": " + s.LastCause : ""), null),
        };
    }
}
