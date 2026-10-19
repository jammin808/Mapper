using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using PlayMap.Supervisor.Install;

namespace PlayMap.Supervisor.Windows;

/// <summary>The machine-level actions of the install, done once by an elevated installer through the supervisor's own commands, so
/// the engine never runs as administrator: the machine's folders with the show user's write access, WER's dump settings, the Event
/// Log source, the firewall rules, the show user's account right and the service with its recovery actions; and each taken back on
/// uninstall. Every step says what it did in <paramref name="say"/>; a step Windows refuses throws with Windows' words.</summary>
[SupportedOSPlatform("windows")]
public static class MachineSetup
{
    /// <summary>The machine's PlayMap folder: <c>%ProgramData%\PlayMap</c>.</summary>
    public static string MachineFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PlayMap");

    public static void Install(string installDir, string showUser, bool publicProfile, Action<string> say)
    {
        ArgumentException.ThrowIfNullOrEmpty(installDir);
        ArgumentNullException.ThrowIfNull(say);
        var exe = Path.Combine(installDir, "PlayMap.exe");
        var supervisor = Path.Combine(installDir, "Supervisor", "PlayMap.Supervisor.exe");

        foreach (var folder in new[] { Path.Combine(MachineFolder, "dumps"), Path.Combine(MachineFolder, "supervisor") })
        {
            Directory.CreateDirectory(folder);
            if (showUser.Length > 0)
                GrantFolder(folder, showUser);
            say("folder " + folder + (showUser.Length > 0 ? " (" + showUser + " may write)" : ""));
        }

        using (var wer = Registry.LocalMachine.CreateSubKey(MachinePlan.WerKey, true))
        {
            wer.SetValue("DumpFolder", MachinePlan.DumpFolder, RegistryValueKind.ExpandString);
            wer.SetValue("DumpType", MachinePlan.DumpType, RegistryValueKind.DWord);
            wer.SetValue("DumpCount", MachinePlan.DumpCount, RegistryValueKind.DWord);
        }
        say("WER LocalDumps for PlayMap.exe: full, five kept, in " + MachinePlan.DumpFolder);

        using (var source = Registry.LocalMachine.CreateSubKey(EventSourceKey, true))
            source.SetValue("EventMessageFile", @"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll", RegistryValueKind.ExpandString);
        say("Event Log source " + MachinePlan.EventSource);

        foreach (var rule in MachinePlan.FirewallRules)
        {
            Run("netsh.exe", MachinePlan.DeleteRuleArguments(rule), okExits: [0, 1]);
            Run("netsh.exe", MachinePlan.AddRuleArguments(rule, exe, publicProfile));
            say(string.Create(CultureInfo.InvariantCulture, $"firewall rule '{rule.Name}': {rule.Protocol} {rule.Ports} on {MachinePlan.Profiles(publicProfile)}"));
        }

        if (showUser.Length > 0)
        {
            Grant(showUser);
            say(MachinePlan.ShowUserRight + " granted to " + showUser);
        }

        var binPath = "\"" + supervisor + "\" run";
        var exists = Run("sc.exe", ["query", MachinePlan.ServiceName], okExits: [0, 1060]) == 0;
        if (exists)
            Run("sc.exe", ["config", MachinePlan.ServiceName, "binPath=", binPath, "start=", "auto", "obj=", "LocalSystem"]);
        else
            Run("sc.exe", ["create", MachinePlan.ServiceName, "binPath=", binPath, "start=", "auto", "obj=", "LocalSystem",
                "DisplayName=", MachinePlan.ServiceDisplayName]);
        Run("sc.exe", ["description", MachinePlan.ServiceName, MachinePlan.ServiceDescription]);
        Run("sc.exe", ["failure", MachinePlan.ServiceName, "reset=", MachinePlan.RecoveryResetSeconds.ToString(CultureInfo.InvariantCulture),
            "actions=", MachinePlan.RecoveryActions]);
        Run("sc.exe", ["failureflag", MachinePlan.ServiceName, "1"]);
        Run("sc.exe", ["start", MachinePlan.ServiceName], okExits: [0, 1056]);
        say("service " + MachinePlan.ServiceName + ": LocalSystem, automatic, restarts after 5 s and 30 s, started");
    }

    /// <summary>Stops the service (a service that is not there or not running is no failure): the engine is then stopped by the
    /// caller, and nothing starts it again.</summary>
    public static void StopService(Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(say);
        Run("sc.exe", ["stop", MachinePlan.ServiceName], okExits: [0, 1060, 1062]);
        say("service " + MachinePlan.ServiceName + " stopped");
    }

    /// <summary>Takes back what <see cref="Install"/> did, leaving the dumps and the node's own data.</summary>
    public static void Uninstall(string showUser, Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(say);
        Run("sc.exe", ["stop", MachinePlan.ServiceName], okExits: [0, 1060, 1062]);
        Run("sc.exe", ["delete", MachinePlan.ServiceName], okExits: [0, 1060, 1072]);
        say("service " + MachinePlan.ServiceName + " removed");
        foreach (var rule in MachinePlan.FirewallRules)
            Run("netsh.exe", MachinePlan.DeleteRuleArguments(rule), okExits: [0, 1]);
        say("firewall rules removed");
        Registry.LocalMachine.DeleteSubKeyTree(MachinePlan.WerKey, false);
        say("WER LocalDumps for PlayMap.exe removed");
        Registry.LocalMachine.DeleteSubKeyTree(EventSourceKey, false);
        say("Event Log source removed");
        if (showUser.Length > 0)
        {
            Revoke(showUser);
            say(MachinePlan.ShowUserRight + " taken back from " + showUser);
        }
    }

    private static string EventSourceKey => @"SYSTEM\CurrentControlSet\Services\EventLog\Application\" + MachinePlan.EventSource;

    /// <summary>Gives <paramref name="account"/> the show user's right (<c>LsaAddAccountRights</c>).</summary>
    public static void Grant(string account) => Rights(account, add: true);

    /// <summary>Takes the show user's right back from <paramref name="account"/>.</summary>
    public static void Revoke(string account) => Rights(account, add: false);

    private static void Rights(string account, bool add)
    {
        var sid = (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
        var sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);
        var attributes = new Native.LsaObjectAttributes { Length = Marshal.SizeOf<Native.LsaObjectAttributes>() };
        var status = Native.LsaOpenPolicy(0, ref attributes, Native.PolicyCreateAccount | Native.PolicyLookupNames, out var policy);
        if (status != 0)
            throw new Win32Exception(Native.LsaNtStatusToWinError(status), "LsaOpenPolicy refused");
        var name = Marshal.StringToHGlobalUni(MachinePlan.ShowUserRight);
        try
        {
            var bytes = (ushort)(MachinePlan.ShowUserRight.Length * 2);
            Native.LsaUnicodeString[] rights = [new() { Buffer = name, Length = bytes, MaximumLength = (ushort)(bytes + 2) }];
            status = add
                ? Native.LsaAddAccountRights(policy, sidBytes, rights, 1)
                : Native.LsaRemoveAccountRights(policy, sidBytes, false, rights, 1);
            // An account that holds no right has nothing to take back (STATUS_OBJECT_NAME_NOT_FOUND).
            if (status != 0 && !(status == 0xC0000034 && !add))
                throw new Win32Exception(Native.LsaNtStatusToWinError(status), (add ? "LsaAddAccountRights" : "LsaRemoveAccountRights") + " refused " + account);
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            _ = Native.LsaClose(policy);
        }
    }

    private static void GrantFolder(string folder, string account)
    {
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new NTAccount(account), FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    /// <summary>Runs a System32 tool; its exit code, or throws with its words when the code is not one of <paramref name="okExits"/>.</summary>
    private static int Run(string tool, IReadOnlyList<string> arguments, int[]? okExits = null)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), tool);
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in arguments)
            start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new InvalidOperationException(tool + " did not start");
        var output = process.StandardOutput.ReadToEnd();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        var code = process.ExitCode;
        if (Array.IndexOf(okExits ?? [0], code) < 0)
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"{tool} {string.Join(' ', arguments)} answered {code}: {output.Trim()} {errors.Trim()}"));
        return code;
    }
}
