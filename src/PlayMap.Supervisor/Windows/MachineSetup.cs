using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;
using PlayMap.Supervisor.Install;

namespace PlayMap.Supervisor.Windows;

/// <summary>The machine-level actions of the install, done once by an elevated installer through the supervisor's own commands, so
/// the engine never needs to run as administrator: the machine's folders (<see cref="MachineFolders"/>) with the show user's write
/// access to the dumps and the engine's folder alone, WER's dump settings, the Event Log source, the firewall rules, the show user's
/// account right and the service with its recovery actions; and each taken back on uninstall, the folders themselves and what they
/// hold left. Every step says what it did in <paramref name="say"/>; a step Windows refuses throws with Windows' words.</summary>
[SupportedOSPlatform("windows")]
public static class MachineSetup
{
    /// <summary>The machine's PlayMap folder: <c>%ProgramData%\PlayMap</c>.</summary>
    public static string MachineFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PlayMap");

    /// <summary>The value that says a supervisor keeps this machine's engine (the desk then attaches to it and starts none).</summary>
    public const string SupervisedValue = "Supervised";

    /// <summary>Sets every machine-level action: the folders, the show user (or, with none named, any signed-in user) writing the
    /// dumps and the engine's folder alone; WER, the event source, the firewall rules, the show user's right (taken back from
    /// <paramref name="previousShowUser"/> when an upgrade names another account) and the service, started. The machine is marked
    /// supervised only as the last step, once its service runs: an install that stops on the way leaves it unsupervised, so its desk
    /// starts its own engine.</summary>
    public static void Install(string installDir, string showUser, string previousShowUser, bool publicProfile, Action<string> say)
    {
        ArgumentException.ThrowIfNullOrEmpty(installDir);
        ArgumentNullException.ThrowIfNull(say);
        var exe = Path.Combine(installDir, "PlayMap.exe");
        var supervisor = Path.Combine(installDir, "Supervisor", "PlayMap.Supervisor.exe");
        MarkSupervised(false);
        say("this machine is not supervised until its service runs");

        MachineFolders.Make(MachineFolder, showUser, say);

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
        if (previousShowUser.Length > 0 && !string.Equals(previousShowUser, showUser, StringComparison.OrdinalIgnoreCase))
            TakeBackFrom(previousShowUser, say);

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
        MarkSupervised(true);
        say("this machine is supervised: its desk attaches to the engine the service keeps");
    }

    /// <summary>Marks the machine supervised, or not (HKLM\SOFTWARE\PlayMap, Supervised).</summary>
    private static void MarkSupervised(bool supervised)
    {
        if (supervised)
        {
            using var key = Registry.LocalMachine.CreateSubKey(MachinePlan.InstallKey, true);
            key.SetValue(SupervisedValue, 1, RegistryValueKind.DWord);
        }
        else
        {
            using var key = Registry.LocalMachine.OpenSubKey(MachinePlan.InstallKey, true);
            key?.DeleteValue(SupervisedValue, false);
        }
    }

    /// <summary>An upgrade that names another show user takes the right back from the earlier one (the folders' write access is the
    /// new show user's alone already: <see cref="MachineFolders.Make"/> replaces every explicit rule on them).</summary>
    private static void TakeBackFrom(string previous, Action<string> say)
    {
        try
        {
            Revoke(previous);
            say(MachinePlan.ShowUserRight + " taken back from the earlier show user " + previous);
        }
        catch (IdentityNotMappedException)
        {
            say("the earlier show user " + previous + " no longer exists: there is nothing to take back by its name");
        }
    }

    /// <summary>How long a stopping service has to reach Stopped.</summary>
    public static readonly TimeSpan ServiceStopTime = TimeSpan.FromSeconds(30);

    /// <summary>Stops the service and waits until it has stopped (a service that is not there, not running or already stopping is no
    /// failure; one that has not stopped within <see cref="ServiceStopTime"/> is): the engine is then stopped by the caller, and nothing
    /// starts it again.</summary>
    public static void StopService(Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(say);
        if (Run("sc.exe", ["stop", MachinePlan.ServiceName], okExits: [0, 1060, 1061, 1062]) == 1060)
        {
            say("service " + MachinePlan.ServiceName + " is not installed");
            return;
        }
        WaitStopped();
        say("service " + MachinePlan.ServiceName + " stopped");
    }

    /// <summary>Starts the service (one already running is no failure).</summary>
    public static void StartService(Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(say);
        Run("sc.exe", ["start", MachinePlan.ServiceName], okExits: [0, 1056]);
        say("service " + MachinePlan.ServiceName + " started again");
    }

    /// <summary>Waits until the service is Stopped (.NET's services stop on their own thread after <c>sc stop</c> returns, and a stop
    /// pending answers no further control); a service that has gone meanwhile is stopped.</summary>
    private static void WaitStopped()
    {
        using var controller = new ServiceController(MachinePlan.ServiceName);
        try
        {
            controller.WaitForStatus(ServiceControllerStatus.Stopped, ServiceStopTime);
        }
        catch (System.ServiceProcess.TimeoutException ex)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"service {MachinePlan.ServiceName} did not stop within {ServiceStopTime.TotalSeconds:0} s"), ex);
        }
        catch (InvalidOperationException) when (Run("sc.exe", ["query", MachinePlan.ServiceName], okExits: [0, 1060]) == 1060)
        {
            // Removed while it stopped: stopped.
        }
    }

    /// <summary>Takes back what <see cref="Install"/> did, the show user's write access to the machine's folders among it, leaving the
    /// folders, the dumps and the node's own data. Every step is tried whatever the others did, and each that Windows refused is named
    /// in the answer (empty when all were taken back).</summary>
    public static IReadOnlyList<string> Uninstall(string showUser, Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(say);
        var remains = new List<string>();
        // First: a machine whose uninstall stops on the way is no longer supervised, so its desk starts its own engine.
        MarkSupervised(false);
        say("this machine is no longer supervised");
        void Step(string what, Action act)
        {
            try
            {
                act();
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception
                                           or System.Security.SecurityException)
            {
                say(what + " could not be taken back: " + ex.Message);
                remains.Add(what);
            }
        }
        Step("the service " + MachinePlan.ServiceName, () =>
        {
            Run("sc.exe", ["stop", MachinePlan.ServiceName], okExits: [0, 1060, 1061, 1062]);
            if (Run("sc.exe", ["query", MachinePlan.ServiceName], okExits: [0, 1060]) == 0)
                WaitStopped();
            Run("sc.exe", ["delete", MachinePlan.ServiceName], okExits: [0, 1060, 1072]);
            say("service " + MachinePlan.ServiceName + " removed");
        });
        foreach (var rule in MachinePlan.FirewallRules)
        {
            Step("the firewall rule '" + rule.Name + "'", () =>
            {
                Run("netsh.exe", MachinePlan.DeleteRuleArguments(rule), okExits: [0, 1]);
                say("firewall rule '" + rule.Name + "' removed");
            });
        }
        Step("WER LocalDumps for PlayMap.exe", () =>
        {
            Registry.LocalMachine.DeleteSubKeyTree(MachinePlan.WerKey, false);
            say("WER LocalDumps for PlayMap.exe removed");
        });
        Step("the Event Log source " + MachinePlan.EventSource, () =>
        {
            Registry.LocalMachine.DeleteSubKeyTree(EventSourceKey, false);
            say("Event Log source removed");
        });
        Step("the show user's write access to the machine's folders", () => MachineFolders.TakeBack(MachineFolder, say));
        if (showUser.Length > 0)
        {
            Step(MachinePlan.ShowUserRight + " of " + showUser, () =>
            {
                try
                {
                    Revoke(showUser);
                    say(MachinePlan.ShowUserRight + " taken back from " + showUser);
                }
                catch (IdentityNotMappedException)
                {
                    // An account Windows no longer finds holds no right by its name: there is nothing it can take back.
                    say("the show user " + showUser + " no longer exists: there is no right to take back by its name");
                }
            });
        }
        return remains;
    }

    private static string EventSourceKey => @"SYSTEM\CurrentControlSet\Services\EventLog\Application\" + MachinePlan.EventSource;

    /// <summary>Gives <paramref name="account"/> the show user's right (<c>LsaAddAccountRights</c>).</summary>
    public static void Grant(string account) => Rights(account, add: true);

    /// <summary>Takes the show user's right back from <paramref name="account"/>.</summary>
    public static void Revoke(string account) => Rights(account, add: false);

    /// <summary>The account rights <paramref name="account"/> holds, as the local security policy says
    /// (<c>LsaEnumerateAccountRights</c>); none when it holds none.</summary>
    public static IReadOnlyList<string> RightsOf(string account)
    {
        var sid = (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
        var sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);
        var attributes = new Native.LsaObjectAttributes { Length = Marshal.SizeOf<Native.LsaObjectAttributes>() };
        var status = Native.LsaOpenPolicy(0, ref attributes, Native.PolicyLookupNames, out var policy);
        if (status != 0)
            throw new Win32Exception(Native.LsaNtStatusToWinError(status), "LsaOpenPolicy refused");
        try
        {
            status = Native.LsaEnumerateAccountRights(policy, sidBytes, out var rights, out var count);
            // An account that holds no right is not in the policy's list at all (STATUS_OBJECT_NAME_NOT_FOUND).
            if (status == 0xC0000034)
                return [];
            if (status != 0)
                throw new Win32Exception(Native.LsaNtStatusToWinError(status), "LsaEnumerateAccountRights refused " + account);
            try
            {
                var held = new List<string>((int)count);
                var size = Marshal.SizeOf<Native.LsaUnicodeString>();
                for (var i = 0; i < count; i++)
                {
                    var right = Marshal.PtrToStructure<Native.LsaUnicodeString>(rights + (i * size));
                    held.Add(Marshal.PtrToStringUni(right.Buffer, right.Length / 2));
                }
                return held;
            }
            finally
            {
                _ = Native.LsaFreeMemory(rights);
            }
        }
        finally
        {
            _ = Native.LsaClose(policy);
        }
    }

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
