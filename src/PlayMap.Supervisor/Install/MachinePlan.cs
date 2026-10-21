namespace PlayMap.Supervisor.Install;

/// <summary>One inbound firewall rule for the installed <c>PlayMap.exe</c>.</summary>
public sealed record FirewallRule(string Name, string Protocol, string Ports);

/// <summary>One of the machine's folders: its path below <c>%ProgramData%\PlayMap</c> (<see cref="Under"/>, empty for that folder itself),
/// whether the show user may change what it holds, and what it holds.</summary>
public sealed record MachineFolderPlan(IReadOnlyList<string> Under, bool ShowUserWrites, string Holds)
{
    /// <summary>The folder's path under <paramref name="root"/> (the machine's PlayMap folder).</summary>
    public string In(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        return Under.Count == 0 ? root : Path.Combine([root, .. Under]);
    }
}

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

    /// <summary>The engine's stop event (<see cref="Core.Machine.SupervisorNames.StopEvent"/>): the supervisor sets it before the grace,
    /// which ends in termination of an engine still running. The engine it started opens it with Synchronize, the one right its ACL
    /// gives the show user, and stops as any asked stop does once it is set.</summary>
    public const string StopEvent = Core.Machine.SupervisorNames.StopEvent;

    /// <summary>Whether a stop event that exists already when the supervisor starts was made by SYSTEM or administrators, the only
    /// owners it is kept from: one another account made could be set or cleared by that account.</summary>
    public static bool StopEventOwnerTrusted(string ownerSid) => OwnerTrusted(ownerSid);

    /// <summary>Whether an object's owner is SYSTEM or administrators: an object any other account owns, that account may change.</summary>
    public static bool OwnerTrusted(string ownerSid) => ownerSid is "S-1-5-18" or "S-1-5-32-544";

    /// <summary>The machine's folders under <c>%ProgramData%\PlayMap</c>, each after the folder it lies in, all owned by administrators:
    /// the PlayMap folder itself, whose access is its own (SYSTEM and administrators full control, every account read) and which every
    /// folder below inherits; WER's dumps; the supervisor's folder, whose status file and log SYSTEM writes by path, so only SYSTEM and
    /// administrators may change what it holds; and the engine's folder inside it, which holds the pid record alone and which the
    /// supervisor only reads. The show user may change what the dumps and the engine's folder hold, and nothing SYSTEM acts on by
    /// path.</summary>
    public static readonly IReadOnlyList<MachineFolderPlan> Folders =
    [
        new([], false, "the machine's PlayMap folder"),
        new(["dumps"], true, "WER's dumps of the engine"),
        new(["supervisor"], false, "the supervisor's status file and log"),
        new(["supervisor", "engine"], true, "the engine's pid record"),
    ];

    /// <summary>INTERACTIVE: who writes the show user's folders and may wait on the engine's stop event when an install names no show
    /// user — every account signed in to a session, the console's user the supervisor then starts the engine as among them.</summary>
    public const string AnyInteractiveUserSid = "S-1-5-4";

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
