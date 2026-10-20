using System.Collections.Immutable;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using PlayMap.Core.Machine;
using PlayMap.Platform.Windows.Interop;

namespace PlayMap.Platform.Windows;

/// <summary>What Windows says about the machine, read and never written: the operating system and its edition, the CPU and its sets,
/// the power plan and its timeouts, the screen saver, a pending reboot, Smart App Control, the connected networks' firewall profiles
/// with PlayMap's rules, and hardware scheduling. Each read says where it read; a value Windows does not give is unread, never a
/// guess. Any thread.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class WindowsReadings
{
    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    // The power plans Windows ships (their scheme GUIDs) and the settings read (subgroup, setting).
    private static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid SleepSubgroup = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid StandbyTimeout = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    private static readonly Guid VideoSubgroup = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid VideoTimeout = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

    private static readonly Guid UsbSubgroup = new("2a737441-1930-4402-8d77-b2bebba308a3");
    private static readonly Guid UsbSelectiveSuspend = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
    private static readonly Guid PcieSubgroup = new("501a4d13-42af-4429-9fd1-a8218c268e20");
    private static readonly Guid PcieLinkState = new("ee12f906-d277-404b-b6da-e5fa1a576df5");

    // The power mode's overlays (Settings ▸ System ▸ Power & battery).
    private static readonly Guid BestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    private static readonly Guid NetworkListClsid = new("dcb00c01-570f-4a9b-8d69-199fdba5723b");
    private static readonly Guid NetworkListIid = new("dcb00000-570f-4a9b-8d69-199fdba5723b");

    /// <summary>The operating system: <c>RtlGetVersion</c>'s build, <c>GetProductInfo</c>'s product type and the registry's
    /// <c>EditionID</c>, <c>DisplayVersion</c> and <c>UBR</c>.</summary>
    public static OsIdentity Os()
    {
        var info = new OsVersionInfoEx { Size = (uint)sizeof(OsVersionInfoEx) };
        var status = Native.RtlGetVersion(ref info);
        int build = status == 0 ? (int)info.Build : 0;
        uint product = 0;
        if (status == 0 && !Native.GetProductInfo(info.Major, info.Minor, 0, 0, out product))
            product = 0;
        using var key = Registry.LocalMachine.OpenSubKey(CurrentVersion);
        var edition = key?.GetValue("EditionID") as string ?? "";
        var display = key?.GetValue("DisplayVersion") as string ?? "";
        var revision = key?.GetValue("UBR") is int ubr ? ubr : 0;
        return EditionTable.Classify(edition, product, display, build, revision);
    }

    /// <summary>The CPU sets Windows lists (<c>GetSystemCpuSetInformation</c>), or none when it does not answer.</summary>
    public static ImmutableArray<CpuSetInfo> CpuSets()
    {
        _ = Native.GetSystemCpuSetInformation(null, 0, out var length, Native.GetCurrentProcess(), 0);
        if (length == 0)
            return [];
        var buffer = new byte[length];
        var list = ImmutableArray.CreateBuilder<CpuSetInfo>();
        fixed (byte* p = buffer)
        {
            if (!Native.GetSystemCpuSetInformation(p, length, out var returned, Native.GetCurrentProcess(), 0))
                return [];
            uint at = 0;
            while (at + 8 <= returned)
            {
                var entry = (SystemCpuSetInformation*)(p + at);
                if (entry->Size == 0)
                    break;
                // Type 0: CpuSetInformation.
                if (entry->Type == 0 && at + 32 <= returned)
                    list.Add(new CpuSetInfo(entry->Id, entry->LogicalProcessorIndex + (64 * entry->Group), entry->EfficiencyClass, entry->LastLevelCacheIndex));
                at += entry->Size;
            }
        }
        return list.ToImmutable();
    }

    /// <summary>The processor's name (<c>HARDWARE\DESCRIPTION\System\CentralProcessor\0</c>), "" when unread.</summary>
    public static string CpuModel()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
    }

    /// <summary>Hardware-accelerated GPU scheduling as the person set it (<c>GraphicsDrivers\HwSchMode</c>: 2 on, 1 off): true, false,
    /// or null when unset (Windows' default for the driver).</summary>
    public static bool? Hags()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
        return key?.GetValue("HwSchMode") is int mode ? mode == 2 : null;
    }

    /// <summary>Smart App Control (<c>CI\Policy\VerifiedAndReputablePolicyState</c>): 0 off, 1 enforcing, 2 evaluating, −1 unread.</summary>
    public static int SmartAppControl()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Policy");
        return key?.GetValue("VerifiedAndReputablePolicyState") is int state ? state : -1;
    }

    /// <summary>Why a reboot is pending: Windows Update's <c>RebootRequired</c>, servicing's <c>RebootPending</c>, the session manager's
    /// <c>PendingFileRenameOperations</c>.</summary>
    public static ImmutableArray<string> RebootPending()
    {
        var why = ImmutableArray.CreateBuilder<string>();
        using (var update = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            if (update is not null)
                why.Add("Windows Update");
        using (var servicing = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
            if (servicing is not null)
                why.Add("component servicing");
        using (var session = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
            if (session?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 })
                why.Add("files waiting to be replaced");
        return why.ToImmutable();
    }

    /// <summary>The screen saver, the user's: on (1) only when <c>SPI_GETSCREENSAVEACTIVE</c> answers TRUE and a screen saver is chosen
    /// (a non-empty <c>SCRNSAVE.EXE</c> under <c>Control Panel\Desktop</c> or its policy key), since the flag reads TRUE with "(None)"
    /// chosen; off (0) when either says no; −1 when the two readings do not decide.</summary>
    public static int ScreenSaver()
    {
        int active = 0;
        bool? flag = Native.SystemParametersInfoW(Native.SpiGetScreenSaveActive, 0, &active, 0) ? active != 0 : null;
        return ScreenSaverReading.State(flag, ScreenSaverChosen());
    }

    /// <summary>Whether a screen saver is chosen: a non-empty <c>SCRNSAVE.EXE</c> in the policy key or the user's own; null when neither
    /// key opens.</summary>
    private static bool? ScreenSaverChosen()
    {
        bool? chosen = null;
        foreach (var path in new[] { @"Software\Policies\Microsoft\Windows\Control Panel\Desktop", @"Control Panel\Desktop" })
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            if (key is null)
                continue;
            if (key.GetValue("SCRNSAVE.EXE") is string exe && exe.Trim().Length > 0)
                return true;
            chosen = false;
        }
        return chosen;
    }

    /// <summary>The active power plan, the power mode where the machine offers Balanced only, and the sleep and display timeouts on mains.</summary>
    public static PowerReading Power()
    {
        if (Native.PowerGetActiveScheme(0, out var active) != 0 || active is null)
            return PowerReading.Unread;
        var scheme = *active;
        _ = Native.LocalFree((nint)active);
        var plan = FriendlyName(scheme) ?? scheme.ToString();
        var kind = scheme == HighPerformance ? "high" : scheme == UltimatePerformance ? "ultimate" : scheme == Balanced ? "balanced"
            : scheme == PowerSaver ? "saver" : "other";
        // A machine on Modern Standby offers no High performance plan: Balanced and its power mode are what there is.
        var onlyBalanced = kind == "balanced" && FriendlyName(HighPerformance) is null;
        return new PowerReading(plan, kind, onlyBalanced, onlyBalanced ? PowerMode() : "", Minutes(scheme, SleepSubgroup, StandbyTimeout),
            Minutes(scheme, VideoSubgroup, VideoTimeout));
    }

    private static string? FriendlyName(Guid scheme)
    {
        uint size = 0;
        if (Native.PowerReadFriendlyName(0, scheme, null, null, null, ref size) != 0 || size == 0)
            return null;
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            if (Native.PowerReadFriendlyName(0, scheme, null, null, p, ref size) != 0)
                return null;
            return new string((char*)p).Trim();
        }
    }

    private static int Minutes(Guid scheme, Guid subgroup, Guid setting) =>
        Native.PowerReadACValueIndex(0, scheme, subgroup, setting, out var seconds) == 0 ? (int)Math.Ceiling(seconds / 60.0) : -1;

    /// <summary>A setting of the active plan on mains (<c>PowerReadACValueIndex</c>), or −1 unread.</summary>
    private static int ActiveAc(Guid subgroup, Guid setting)
    {
        if (Native.PowerGetActiveScheme(0, out var active) != 0 || active is null)
            return -1;
        var scheme = *active;
        _ = Native.LocalFree((nint)active);
        return Native.PowerReadACValueIndex(0, scheme, subgroup, setting, out var value) == 0 ? (int)value : -1;
    }

    /// <summary>USB selective suspend on mains: 1 on, 0 off, −1 unread.</summary>
    public static int UsbSuspend() => ActiveAc(UsbSubgroup, UsbSelectiveSuspend);

    /// <summary>PCIe link-state power management on mains: 0 off, 1 moderate, 2 maximum savings, −1 unread.</summary>
    public static int PcieLinkStatePower() => ActiveAc(PcieSubgroup, PcieLinkState);

    /// <summary>The user's notifications (<c>PushNotifications\ToastEnabled</c>, on when unset, Windows' default): 1 on, 0 off.</summary>
    public static int Notifications()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications");
        return key?.GetValue("ToastEnabled") is int on ? (on != 0 ? 1 : 0) : 1;
    }

    /// <summary>Game Mode (<c>GameBar\AutoGameModeEnabled</c>, on when unset, Windows 11's default): 1 on, 0 off.</summary>
    public static int GameMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
        return key?.GetValue("AutoGameModeEnabled") is int on ? (on != 0 ? 1 : 0) : 1;
    }

    /// <summary>Memory Integrity as it runs: <c>NtQuerySystemInformation(SystemCodeIntegrityInformation)</c>'s
    /// <c>CODEINTEGRITY_OPTION_HVCI_KMCI_ENABLED</c> (1 on, 0 off); where Windows does not answer, the setting
    /// (<c>DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity\Enabled</c>), and −1 when that value is absent (set by policy, or
    /// never set): unknown, never a guess.</summary>
    public static int MemoryIntegrity()
    {
        var info = new CodeIntegrityInformation((uint)sizeof(CodeIntegrityInformation), 0);
        if (Native.NtQuerySystemInformation(Native.SystemCodeIntegrityInformation, &info, (uint)sizeof(CodeIntegrityInformation), out _) >= 0)
            return (info.CodeIntegrityOptions & Native.CodeIntegrityHvciKmciEnabled) != 0 ? 1 : 0;
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
        return key?.GetValue("Enabled") is int on ? (on != 0 ? 1 : 0) : -1;
    }

    /// <summary>Windows Update's policy: held when quality updates are deferred, automatic updates are off or only notify, or updates are
    /// paused past now (<paramref name="nowUnixMs"/>); the values read, in words.</summary>
    public static UpdatePolicy Updates(long nowUnixMs)
    {
        var values = new List<string>();
        var held = false;
        using (var policy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate"))
        {
            if (policy?.GetValue("DeferQualityUpdates") is int defer && defer == 1 && policy.GetValue("DeferQualityUpdatesPeriodInDays") is int days && days > 0)
            {
                values.Add($"quality updates deferred {Number(days)} days");
                held = true;
            }
            if (policy?.GetValue("DeferFeatureUpdates") is int feature && feature == 1 && policy.GetValue("DeferFeatureUpdatesPeriodInDays") is int fdays)
                values.Add($"feature updates deferred {Number(fdays)} days");
            using var au = policy?.OpenSubKey("AU");
            if (au?.GetValue("NoAutoUpdate") is int none && none == 1)
            {
                values.Add("automatic updates off");
                held = true;
            }
            if (au?.GetValue("AUOptions") is int options)
            {
                values.Add($"AUOptions {Number(options)}");
                held |= options is 2 or 3;
            }
        }
        using (var ux = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings"))
        {
            if (ux?.GetValue("PauseUpdatesExpiryTime") is string until
                && DateTimeOffset.TryParse(until, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var expiry))
            {
                var paused = expiry.ToUnixTimeMilliseconds() > nowUnixMs;
                values.Add(paused ? $"paused until {expiry.UtcDateTime.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}" : "a pause that has ended");
                held |= paused;
            }
        }
        return new UpdatePolicy(true, held, values.Count == 0 ? "no Update policy and no pause is set" : string.Join("; ", values));
    }

    /// <summary>The power mode's overlay (<c>PowerGetEffectiveOverlayScheme</c>, where powrprof has it): "best power efficiency",
    /// "balanced", "best performance", or "" unread.</summary>
    private static string PowerMode()
    {
        // By its absolute path in the system directory, never a search by name.
        if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "powrprof.dll"), out var library))
            return "";
        try
        {
            if (!NativeLibrary.TryGetExport(library, "PowerGetEffectiveOverlayScheme", out var export))
                return "";
            var overlay = Guid.Empty;
            if (((delegate* unmanaged[Stdcall]<Guid*, uint>)export)(&overlay) != 0)
                return "";
            return overlay == BestPerformance ? "best performance" : overlay == BestEfficiency ? "best power efficiency" : "balanced";
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    /// <summary>Windows Firewall's rules as the registry keeps them: the local store (<c>FirewallPolicy\FirewallRules</c>) and the
    /// policy store a GPO writes (<c>Policies\Microsoft\WindowsFirewall\FirewallRules</c>); none where neither opens.</summary>
    public static ImmutableArray<string> FirewallRuleText()
    {
        var rules = ImmutableArray.CreateBuilder<string>();
        foreach (var path in new[]
        {
            @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules",
            @"SOFTWARE\Policies\Microsoft\WindowsFirewall\FirewallRules",
        })
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key is not null)
                rules.AddRange(key.GetValueNames().Select(n => key.GetValue(n) as string).OfType<string>());
        }
        return rules.ToImmutable();
    }

    /// <summary>The connected networks with their firewall profiles (the Network List Manager), each with whether a rule lets
    /// <paramref name="exePath"/> in on Public and the adapters its connections go through, with their addresses; false, with the
    /// words, when the list does not open.</summary>
    public static bool TryNetworks(string exePath, out ImmutableArray<NetworkProfile> networks, out string words)
    {
        networks = [];
        // COINIT_MULTITHREADED; a thread already in another apartment keeps it.
        var init = Native.CoInitializeEx(0, 0x0);
        try
        {
            var status = Native.CoCreateInstance(NetworkListClsid, 0, 0x1 | 0x4, NetworkListIid, out var manager);
            if (status < 0 || manager == 0)
            {
                words = "the Network List Manager did not open (" + Native.StatusWords(status) + ")";
                return false;
            }
            var publicRule = FirewallRules.CoverPublic(FirewallRuleText(), exePath, Environment.ExpandEnvironmentVariables);
            var adapters = LocalAdapters();
            var list = ImmutableArray.CreateBuilder<NetworkProfile>();
            try
            {
                nint enumerator = 0;
                status = ((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(manager, Slots.NetworkListGetNetworks))(manager, 1, &enumerator);
                if (status < 0 || enumerator == 0)
                {
                    words = "the Network List Manager listed no networks (" + Native.StatusWords(status) + ")";
                    return false;
                }
                try
                {
                    var next = (delegate* unmanaged[Stdcall]<nint, uint, nint*, uint*, int>)Slot(enumerator, Slots.EnumNetworksNext);
                    for (var i = 0; i < 64; i++)
                    {
                        nint network = 0;
                        uint fetched = 0;
                        if (next(enumerator, 1, &network, &fetched) != 0 || fetched == 0 || network == 0)
                            break;
                        try
                        {
                            list.Add(new NetworkProfile(NetworkName(network), Category(network), publicRule)
                            {
                                Adapters = NetworkAdapter.Of(AdapterIds(network), adapters),
                            });
                        }
                        finally
                        {
                            Release(network);
                        }
                    }
                }
                finally
                {
                    Release(enumerator);
                }
            }
            finally
            {
                Release(manager);
            }
            networks = list.ToImmutable();
            words = "";
            return true;
        }
        finally
        {
            // S_OK and S_FALSE initialised this thread's COM, and are matched; RPC_E_CHANGED_MODE left it as it was.
            if (init >= 0)
                Native.CoUninitialize();
        }
    }

    private static string NetworkName(nint network)
    {
        nint name = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(network, Slots.NetworkGetName))(network, &name) < 0 || name == 0)
            return "unnamed";
        try
        {
            return Marshal.PtrToStringBSTR(name);
        }
        finally
        {
            Native.SysFreeString(name);
        }
    }

    /// <summary>The ids of the adapters a network's connections go through (each connection's adapter GUID); none where the list
    /// names none.</summary>
    private static List<Guid> AdapterIds(nint network)
    {
        var ids = new List<Guid>();
        nint connections = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(network, Slots.NetworkGetNetworkConnections))(network, &connections) < 0 || connections == 0)
            return ids;
        try
        {
            var next = (delegate* unmanaged[Stdcall]<nint, uint, nint*, uint*, int>)Slot(connections, Slots.EnumNetworkConnectionsNext);
            for (var i = 0; i < 64; i++)
            {
                nint connection = 0;
                uint fetched = 0;
                if (next(connections, 1, &connection, &fetched) != 0 || fetched == 0 || connection == 0)
                    break;
                try
                {
                    Guid id;
                    if (((delegate* unmanaged[Stdcall]<nint, Guid*, int>)Slot(connection, Slots.NetworkConnectionGetAdapterId))(connection, &id) >= 0)
                        ids.Add(id);
                }
                finally
                {
                    Release(connection);
                }
            }
        }
        finally
        {
            Release(connections);
        }
        return ids;
    }

    /// <summary>This machine's network adapters (an interface's id is its adapter's GUID on Windows) with their unicast addresses; none
    /// where Windows does not list them.</summary>
    private static List<NetworkAdapter> LocalAdapters()
    {
        var adapters = new List<NetworkAdapter>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                if (Guid.TryParse(nic.Id, out var id))
                    adapters.Add(new NetworkAdapter(id, nic.Name, [.. nic.GetIPProperties().UnicastAddresses.Select(u => u.Address)]));
        }
        catch (NetworkInformationException)
        {
            // No adapter list: each network names none, and a node bound to an address reads none of them.
            adapters.Clear();
        }
        return adapters;
    }

    private static string Category(nint network)
    {
        int category = -1;
        _ = ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(network, Slots.NetworkGetCategory))(network, &category);
        return category switch
        {
            0 => "public",
            1 => "private",
            2 => "domain",
            _ => "unknown",
        };
    }

    /// <summary>Whether the file at <paramref name="path"/> carries an Authenticode certificate table (its first 4 KB read).</summary>
    public static bool Signed(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return false;
        try
        {
            using var file = File.OpenRead(path);
            var head = new byte[Math.Min(4096, file.Length)];
            file.ReadExactly(head);
            return PeImage.HasCertificateTable(head);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static nint Slot(nint com, int slot) => (*(nint**)com)[slot];

    private static void Release(nint com) => _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(com, Slots.Release))(com);

    /// <summary>Whether this process's token holds <c>SeIncreaseBasePriorityPrivilege</c> (enabled or not).</summary>
    public static bool HoldsIncreaseBasePriority() => HoldsPrivilege("SeIncreaseBasePriorityPrivilege", enable: false);

    /// <summary>Enables a privilege the token holds; false when it holds none or the token refuses.</summary>
    public static bool EnableIncreaseBasePriority() => HoldsPrivilege("SeIncreaseBasePriorityPrivilege", enable: true);

    private static bool HoldsPrivilege(string name, bool enable)
    {
        if (!Native.LookupPrivilegeValueW(null, name, out var luid))
            return false;
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TokenQuery | Native.TokenAdjustPrivileges, out var token))
            return false;
        try
        {
            _ = Native.GetTokenInformation(token, Native.TokenPrivileges, null, 0, out var length);
            if (length < 4)
                return false;
            var buffer = new byte[length];
            var held = false;
            fixed (byte* p = buffer)
            {
                if (!Native.GetTokenInformation(token, Native.TokenPrivileges, p, length, out _))
                    return false;
                var count = *(uint*)p;
                var entries = (LuidAndAttributes*)(p + 4);
                for (var i = 0; i < count && 4 + ((i + 1) * sizeof(LuidAndAttributes)) <= length; i++)
                    held |= entries[i].Luid == luid;
            }
            if (!held || !enable)
                return held;
            var state = new TokenPrivilege(1, new LuidAndAttributes(luid, Native.SePrivilegeEnabled));
            return Native.AdjustTokenPrivileges(token, false, state, 0, 0, 0) && Marshal.GetLastPInvokeError() == 0;
        }
        finally
        {
            _ = Native.CloseHandle(token);
        }
    }

    /// <summary>Text for a number in claim words.</summary>
    public static string Number(long n) => n.ToString(CultureInfo.InvariantCulture);
}
