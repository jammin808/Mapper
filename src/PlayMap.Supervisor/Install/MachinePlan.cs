namespace PlayMap.Supervisor.Install;

/// <summary>One inbound firewall rule for the installed <c>PlayMap.exe</c>.</summary>
public sealed record FirewallRule(string Name, string Protocol, string Ports);

/// <summary>What the installer does on the machine, once, so the engine never runs as administrator
/// (docs/round3/design/installer.md §3): pure data, which <c>install</c> applies and <c>uninstall</c> takes back.</summary>
public static class MachinePlan
{
    /// <summary>The service's name.</summary>
    public const string ServiceName = "PlayMapSupervisor";

    /// <summary>The service's display name.</summary>
    public const string ServiceDisplayName = "PlayMap Supervisor";

    /// <summary>The service's description, as Windows' Services list shows it.</summary>
    public const string ServiceDescription =
        "Starts PlayMap's engine in the show user's session, restarts it when it stops and stops one whose picture has frozen.";

    /// <summary>The recovery actions: restart after 5 s, after 30 s, then none; the count reset after a day; non-crash failures too.</summary>
    public const string RecoveryActions = "restart/5000/restart/30000//0";

    /// <summary>The recovery count's reset, in seconds.</summary>
    public const int RecoveryResetSeconds = 86400;

    /// <summary>The Event Log source in the Application log.</summary>
    public const string EventSource = "PlayMap";

    /// <summary>The account right the show user is given: the realtime GPU queue's base priority, used only when measured.</summary>
    public const string ShowUserRight = "SeIncreaseBasePriorityPrivilege";

    /// <summary>WER's LocalDumps key for the engine's exe (under HKLM).</summary>
    public const string WerKey = @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\PlayMap.exe";

    /// <summary>The dumps folder WER writes, unexpanded.</summary>
    public const string DumpFolder = @"%ProgramData%\PlayMap\dumps";

    /// <summary>A full dump.</summary>
    public const int DumpType = 2;

    /// <summary>Dumps kept.</summary>
    public const int DumpCount = 5;

    /// <summary>The install's registry key (under HKLM): InstallDir, Version, ShowUser, Supervised.</summary>
    public const string InstallKey = @"SOFTWARE\PlayMap";

    /// <summary>The engine's stop event, which the supervisor sets for a normal stop.</summary>
    public const string StopEvent = @"Global\PlayMap-Engine-Stop";

    /// <summary>The supervisor's pipe.</summary>
    public const string PipeName = "PlayMap.Supervisor";

    /// <summary>The inbound rules: TCP 9940 (web, MCP) and 9941 (line wire); UDP 9928 (hello), 9942 (clock), 9943 (OSC), 6454
    /// (Art-Net), 5568 (sACN), 5353 (mDNS).</summary>
    public static readonly IReadOnlyList<FirewallRule> FirewallRules =
    [
        new("PlayMap (TCP in)", "TCP", "9940,9941"),
        new("PlayMap (UDP in)", "UDP", "9928,9942,9943,6454,5568,5353"),
    ];

    /// <summary>The profiles the rules hold on: private and domain, and Public when the person asks (a new network is Public).</summary>
    public static string Profiles(bool includePublic) => includePublic ? "private,domain,public" : "private,domain";

    /// <summary>netsh's words for one rule, scoped to the local subnet.</summary>
    public static IReadOnlyList<string> AddRuleArguments(FirewallRule rule, string exe, bool includePublic)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return ["advfirewall", "firewall", "add", "rule", "name=" + rule.Name, "dir=in", "action=allow", "program=" + exe,
            "protocol=" + rule.Protocol, "localport=" + rule.Ports, "profile=" + Profiles(includePublic), "remoteip=localsubnet", "enable=yes"];
    }

    /// <summary>netsh's words to remove one rule (every rule of that name).</summary>
    public static IReadOnlyList<string> DeleteRuleArguments(FirewallRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return ["advfirewall", "firewall", "delete", "rule", "name=" + rule.Name];
    }
}
