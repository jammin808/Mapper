using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using PlayMap.Core.Bus;

namespace PlayMap.Core.Machine;

/// <summary>The power settings as read: the active plan's name and kind ("high", "ultimate", "balanced", "saver", "other"; "" unread),
/// whether the machine offers Balanced only (Modern Standby) with its power mode, and the sleep and display timeouts on mains in
/// minutes (0: never; −1: unread).</summary>
public sealed record PowerReading(string Plan, string Kind, bool OnlyBalanced, string Mode, int SleepAcMin, int DisplayAcMin)
{
    public static PowerReading Unread { get; } = new("", "", false, "", -1, -1);
}

/// <summary>Windows Update's policy as read (the Update client policies under <c>Policies\Microsoft\Windows\WindowsUpdate</c> and a pause
/// in <c>WindowsUpdate\UX\Settings</c>): whether updates are held back from the show (deferred, paused, or not installed on their
/// own) and the values read, in words.</summary>
public sealed record UpdatePolicy(bool Read, bool Held, string Values)
{
    public static UpdatePolicy Unread { get; } = new(false, false, "");
}

/// <summary>An adapter a connected network is reached through: its id (the adapter's GUID), its name and its unicast addresses.</summary>
public sealed record NetworkAdapter(Guid Id, string Name, ImmutableArray<IPAddress> Addresses)
{
    /// <summary>The adapters <paramref name="ids"/> name among <paramref name="adapters"/>, once each, in the order named; an id that
    /// names none is left out.</summary>
    public static ImmutableArray<NetworkAdapter> Of(IEnumerable<Guid> ids, IReadOnlyList<NetworkAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(adapters);
        var found = ImmutableArray.CreateBuilder<NetworkAdapter>();
        foreach (var id in ids.Distinct())
            if (adapters.FirstOrDefault(a => a.Id == id) is { } adapter)
                found.Add(adapter);
        return found.ToImmutable();
    }

    /// <summary>Whether the adapter carries <paramref name="address"/> (an IPv4 address mapped into IPv6 read as IPv4).</summary>
    public bool Carries(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var wanted = MachineRows.Normal(address);
        return Addresses.Any(a => MachineRows.Normal(a).Equals(wanted));
    }
}

/// <summary>A connected network as read: its name, its firewall profile ("public", "private", "domain"), whether an inbound rule for
/// PlayMap covers the Public profile, and the adapters it is reached through (empty where the list named none).</summary>
public sealed record NetworkProfile(string Name, string Category, bool PublicRule)
{
    public ImmutableArray<NetworkAdapter> Adapters { get; init; } = [];
}

/// <summary>Everything the platform read for the machine's preflight rows, each "unread" where Windows did not answer (−1, "", or
/// false with the reading's own flag). Pure data: the Windows platform fills it, a test fakes it.</summary>
public sealed record MachineReadings
{
    public OsIdentity Os { get; init; } = OsIdentity.Unknown;

    /// <summary>Whether this build of PlayMap carries an Authenticode signature.</summary>
    public bool Signed { get; init; }

    /// <summary>Smart App Control's state (<c>CI\Policy\VerifiedAndReputablePolicyState</c>): 0 off, 1 enforcing, 2 evaluating, −1 unread.</summary>
    public int SmartAppControl { get; init; } = -1;

    public PowerReading Power { get; init; } = PowerReading.Unread;

    /// <summary>The screen saver: 1 on, 0 off, −1 unread.</summary>
    public int ScreenSaver { get; init; } = -1;

    /// <summary>Why a reboot is pending (each source that says so); empty with <see cref="RebootRead"/> when none is.</summary>
    public ImmutableArray<string> RebootPending { get; init; } = [];

    public bool RebootRead { get; init; }

    public ImmutableArray<NetworkProfile> Networks { get; init; } = [];

    /// <summary>Whether the networks were read (false: their row is unread, with words).</summary>
    public bool NetworksRead { get; init; }

    /// <summary>The node's show doors open now, each with the address it is bound to (the network host's, then the control inputs');
    /// empty where none was handed to the platform.</summary>
    public ImmutableArray<ShowDoor> Doors { get; init; } = [];

    /// <summary>The claims made so far, process and threads.</summary>
    public ImmutableArray<Claim> Claims { get; init; } = [];

    /// <summary>The deadline timers' waits counted and their p99 lateness (µs).</summary>
    public int TimerWaits { get; init; }

    public double TimerLateP99Us { get; init; }

    /// <summary>Keep-awake's setting (MACHINE SET AWAKE).</summary>
    public bool AwakeOn { get; init; } = true;

    /// <summary>USB selective suspend on mains: 1 on, 0 off, −1 unread.</summary>
    public int UsbSuspend { get; init; } = -1;

    /// <summary>PCIe link-state power management on mains: 0 off, 1 moderate, 2 maximum savings, −1 unread.</summary>
    public int PcieLinkState { get; init; } = -1;

    /// <summary>Notifications (the user's toasts): 1 on, 0 off, −1 unread.</summary>
    public int Notifications { get; init; } = -1;

    public UpdatePolicy Updates { get; init; } = UpdatePolicy.Unread;

    /// <summary>Hardware-accelerated GPU scheduling as set: 1 on, 0 off, −1 unset (the driver's default).</summary>
    public int Hags { get; init; } = -1;

    /// <summary>Memory Integrity (hypervisor-protected code integrity): 1 on, 0 off, −1 unread.</summary>
    public int MemoryIntegrity { get; init; } = -1;

    /// <summary>Game Mode: 1 on, 0 off, −1 unread.</summary>
    public int GameMode { get; init; } = -1;
}

/// <summary>The machine's preflight rows, pure: each a fact with its light and words, never a silent change, a fix line only where a
/// verb fixes it. The edition's rows follow D4: a layer the edition lacks is a grey information row ("i") — Home's four, and on Pro and
/// Pro for Workstations the kiosk's (no Shell Launcher) — and the hard rows are amber. A network show traffic can cross on Public with
/// no rule letting PlayMap in is amber: one whose adapters carry a show door's address, or any connected one while a show door listens
/// on every interface. Rows that fail name what is wrong; the must rows that pass are green, so a person sees what was checked.</summary>
public static class MachineRows
{
    /// <summary>The value an information row carries (grey, with the i).</summary>
    public const string Information = "i";

    /// <summary>A timer lateness over this (µs, p99) is amber.</summary>
    public const double LateAmberUs = 1000;

    public const string SmartAppControlWords =
        "turn Smart App Control off before the show: once it enforces, Windows blocks this unsigned build and this node will not start";

    /// <summary>The words of a row whose reading Windows did not answer.</summary>
    public static string Unread(string what) => $"{what} could not be read: unknown";

    /// <summary>The platform's own rows from what it read, on <paramref name="today"/> (the servicing row's date).</summary>
    public static ImmutableArray<MachineFact> Build(MachineReadings r, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(r);
        var rows = ImmutableArray.CreateBuilder<MachineFact>();
        Os(r.Os, today, rows);
        if (r.SmartAppControl == 2 && !r.Signed)
            rows.Add(new MachineFact("sac", Light.Amber, "evaluation", SmartAppControlWords));
        Power(r.Power, r.AwakeOn, rows);
        rows.Add(r.ScreenSaver switch
        {
            1 => new MachineFact("screensaver", Light.Amber, "on", "the screen saver is on: keeping the display awake does not stop the screen saver"),
            0 => new MachineFact("screensaver", Light.Green, "off", "the screen saver is off"),
            _ => new MachineFact("screensaver", Light.Grey, "unknown", Unread("the screen saver")),
        });
        rows.Add(!r.RebootRead ? new MachineFact("reboot", Light.Grey, "unknown", Unread("a pending reboot"))
            : r.RebootPending.IsEmpty ? new MachineFact("reboot", Light.Green, "none", "no reboot is pending")
            : new MachineFact("reboot", Light.Amber, "pending", "a reboot is pending (" + string.Join(", ", r.RebootPending) + "): restart before the show, not during it"));
        if (!r.NetworksRead)
            rows.Add(new MachineFact("network", Light.Grey, "unknown", Unread("the networks' firewall profiles")));
        else
            Networks(r.Networks, r.Doors, rows);
        Settings(r, rows);
        Claims(r.Claims, rows);
        rows.Add(r.TimerWaits == 0 ? new MachineFact("timer", Light.Grey, "no waits", "no deadline wait has been timed yet")
            : r.TimerLateP99Us > LateAmberUs
                ? new MachineFact("timer", Light.Amber, Us(r.TimerLateP99Us), $"deadline waits wake {Us(r.TimerLateP99Us)} late (p99 of the last {r.TimerWaits}): over 1 ms")
                : new MachineFact("timer", Light.Green, Us(r.TimerLateP99Us), $"deadline waits wake {Us(r.TimerLateP99Us)} late (p99 of the last {r.TimerWaits})"));
        return rows.ToImmutable();
    }

    private static string Us(double us) => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(us)} µs");

    private const string NonePublic = "no connected network is on the Public firewall profile without a rule letting PlayMap in";

    /// <summary>The networks' firewall rows, for the networks show traffic can cross: every connected one when a show door listens on
    /// every interface (the row saying which doors do) or no door was handed over, else only those whose adapters carry a door's
    /// address (the row naming the doors, the address and the adapter). Each such Public network with no rule letting PlayMap in is
    /// amber; with none, one green row says what was read.</summary>
    private static void Networks(ImmutableArray<NetworkProfile> networks, ImmutableArray<ShowDoor> doors, ImmutableArray<MachineFact>.Builder rows)
    {
        var everywhere = doors.Where(d => d.Everywhere).Select(d => DoorWord(d.Name)).Distinct(StringComparer.Ordinal).ToList();
        if (doors.IsEmpty || everywhere.Count > 0)
        {
            var listen = $"this node's {List(everywhere)} {(everywhere.Count == 1 ? "listens" : "listen")} on every interface";
            var amber = false;
            foreach (var n in networks.Where(n => n.Category == "public" && !n.PublicRule))
            {
                amber = true;
                rows.Add(new MachineFact("network.public:" + n.Name, Light.Amber, "public", doors.IsEmpty
                    ? $"the network {n.Name} is on the Public firewall profile and no rule lets PlayMap in there: if show traffic crosses it, make it Private in {ProfileSetting(n.Name)}"
                    : $"the network {n.Name} is on the Public firewall profile and no rule lets PlayMap in there, and {listen}, so show traffic can cross it: make it Private in {ProfileSetting(n.Name)}, or bind the node to its show adapter's address"));
            }
            if (!amber)
                rows.Add(doors.IsEmpty
                    ? new MachineFact("network", Light.Green, "none public", NonePublic)
                    : new MachineFact("network", Light.Green, "every interface", $"{listen}, and {NonePublic}"));
            return;
        }
        // Every door is bound to an address: the networks whose adapters carry none are not the show's, and are not read.
        var addresses = string.Join(", ", doors.Select(d => Normal(d.Address)).Distinct().Select(a => a.ToString()));
        var carried = networks.Select(n => (Network: n, Through: Through(n, doors))).Where(c => c.Through.Count > 0).ToList();
        var refused = false;
        foreach (var (n, through) in carried.Where(c => c.Network.Category == "public" && !c.Network.PublicRule))
        {
            refused = true;
            rows.Add(new MachineFact("network.public:" + n.Name, Light.Amber, "public",
                $"the network {n.Name} carries this node's {string.Join("; ", through)} and is on the Public firewall profile with no rule letting PlayMap in: make it Private in {ProfileSetting(n.Name)}"));
        }
        if (refused)
            return;
        rows.Add(new MachineFact("network", Light.Green, addresses, carried.Count == 0
            ? $"the show doors are bound to {addresses}, which no connected network carries"
            : "the show doors are " + string.Join("; ", carried.Select(c => $"on {c.Network.Name} ({c.Network.Category}): {string.Join("; ", c.Through)}"))));
    }

    /// <summary>The doors a network's adapters carry, by adapter and address ("line wire and web port on 192.168.10.20 through Ethernet
    /// 2"), in the adapters' order.</summary>
    private static List<string> Through(NetworkProfile network, ImmutableArray<ShowDoor> doors)
    {
        var through = new List<string>();
        foreach (var adapter in network.Adapters)
            foreach (var at in doors.Where(d => adapter.Carries(d.Address)).GroupBy(d => Normal(d.Address)))
                through.Add($"{List(at.Select(d => DoorWord(d.Name)).Distinct(StringComparer.Ordinal))} on {at.Key} through {adapter.Name}");
        return through;
    }

    private static string ProfileSetting(string network) => $"Settings ▸ Network & internet ▸ {network} ▸ Network profile type";

    /// <summary>A show door as a row names it.</summary>
    private static string DoorWord(string door) => door switch
    {
        "wire" => "line wire",
        "web" => "web port",
        "sync" => "clock sync",
        "osc" => "OSC",
        "artnet" => "Art-Net",
        "sacn" => "sACN",
        _ => door,
    };

    /// <summary>"a", "a and b", "a, b and c".</summary>
    private static string List(IEnumerable<string> words)
    {
        var list = words.ToList();
        return list.Count < 2 ? string.Concat(list) : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1];
    }

    private static void Os(OsIdentity os, DateOnly today, ImmutableArray<MachineFact>.Builder rows)
    {
        if (os.Build == 0 && os.Family == EditionFamily.Unknown)
        {
            rows.Add(new MachineFact("os.edition", Light.Grey, "unknown", Unread("the operating system")));
            return;
        }
        rows.Add(os.Build is > 0 and < EditionTable.MinBuild
            ? new MachineFact("os.build", Light.Red, Number(os.Build),
                $"Windows 11 24H2 or later is required (this is build {Number(os.Build)})")
            : new MachineFact("os.build", Light.Green, Number(os.Build), $"build {Number(os.Build)}.{Number(os.Revision)}"));
        var tier = EditionTable.Tier(os);
        var line = $"{EditionTable.Name(os)}{(os.Version.Length > 0 ? " " + os.Version : "")} · build {Number(os.Build)}.{Number(os.Revision)}";
        var said = tier switch
        {
            SupportTier.Home => " · supported, unmanaged",
            SupportTier.Ltsc => " · the permanent show node's tier",
            SupportTier.Managed => " · supported, managed with update policies",
            SupportTier.Unsupported => " · unsupported",
            _ => "",
        };
        rows.Add(new MachineFact("os.edition", Light.Grey, EditionTable.Word(tier), line + said));
        if (os.Family == EditionFamily.Home)
        {
            rows.Add(new MachineFact("edition.updates", Light.Grey, Information,
                "updates cannot be deferred on this edition: pause them past the show in Settings ▸ Windows Update"));
            rows.Add(new MachineFact("edition.kiosk", Light.Grey, Information, "no kiosk on this edition: the supervisor starts the engine"));
            rows.Add(new MachineFact("edition.displays", Light.Grey, Information,
                "outputs are desktop monitors on this edition: keep the desk's monitor primary"));
            rows.Add(new MachineFact("edition.remote", Light.Grey, Information, "Remote Desktop cannot reach this node"));
        }
        // Pro and Pro for Workstations have no Shell Launcher: the supervisor's engine runs with Explorer underneath.
        if (os.Family is EditionFamily.Pro or EditionFamily.Workstation)
            rows.Add(new MachineFact("edition.kiosk", Light.Grey, Information, "no Shell Launcher on this edition: the supervisor starts the engine with Explorer underneath"));
        if (os.Family is EditionFamily.Server or EditionFamily.Unknown)
            return;
        if (ServicingTable.EndOf(os) is not { } end)
        {
            rows.Add(new MachineFact("os.servicing", Light.Grey, "unknown",
                $"servicing unknown: {EditionTable.Name(os)} {(os.Version.Length > 0 ? os.Version : "build " + Number(os.Build))} is not in this build's servicing table (dated {Date(ServicingTable.TableDate)})"));
            return;
        }
        if (today <= end)
            return;
        var move = ServicingTable.OldestServiced(os, today) is { Length: > 0 } next ? $" — move to {next} or later" : "";
        rows.Add(new MachineFact("os.servicing", Light.Amber, "ended",
            $"{EditionTable.Name(os)} {os.Version} left servicing on {Date(end)}: this node gets no security updates{move}"));
    }

    private static void Power(PowerReading p, bool awakeOn, ImmutableArray<MachineFact>.Builder rows)
    {
        if (p.Kind.Length == 0)
            rows.Add(new MachineFact("power.plan", Light.Grey, "unknown", Unread("the power plan")));
        else if (p.Kind is "high" or "ultimate")
            rows.Add(new MachineFact("power.plan", Light.Green, p.Plan, $"the power plan is {p.Plan}"));
        else if (p.OnlyBalanced)
            rows.Add(new MachineFact("power.plan", p.Mode is "best performance" ? Light.Green : Light.Amber, p.Mode.Length > 0 ? p.Mode : p.Plan,
                $"this machine offers Balanced only; its power mode is {(p.Mode.Length > 0 ? p.Mode : "unknown")}: set Best performance in Settings ▸ System ▸ Power & battery, and keep sleep, display, USB suspend and PCIe link-state off on mains"));
        else
            rows.Add(new MachineFact("power.plan", Light.Amber, p.Plan,
                $"the power plan is {p.Plan}: choose High performance or Ultimate Performance in Control Panel ▸ Power Options"));
        Timeout("power.sleep", "the system sleeps", p.SleepAcMin);
        Timeout("power.display", "the display sleeps", p.DisplayAcMin);

        void Timeout(string item, string what, int minutes)
        {
            if (minutes < 0)
            {
                rows.Add(new MachineFact(item, Light.Grey, "unknown", Unread(what.Replace("sleeps", "timeout", StringComparison.Ordinal))));
                return;
            }
            if (minutes == 0)
            {
                rows.Add(new MachineFact(item, Light.Green, "never", what.Replace("sleeps", "never sleeps", StringComparison.Ordinal) + " on mains"));
                return;
            }
            var after = $"{what} after {Number(minutes)} min on mains";
            rows.Add(awakeOn
                ? new MachineFact(item, Light.Amber, Number(minutes) + " min", after + ": keep-awake holds it off only while the project has an enabled display output; set it to never in Settings ▸ System ▸ Power & battery")
                : new MachineFact(item, Light.Amber, Number(minutes) + " min", after + " and keep-awake is off", "MACHINE SET AWAKE ON"));
        }
    }

    /// <summary>The settings that matter beyond the must rows: USB selective suspend, PCIe link-state power management, notifications
    /// and the Update policy (amber when they can take a device, the GPU's link or the screen during a show), and hardware scheduling,
    /// Memory Integrity and Game Mode as grey facts, never changed.</summary>
    private static void Settings(MachineReadings r, ImmutableArray<MachineFact>.Builder rows)
    {
        const string Advanced = "Control Panel ▸ Power Options ▸ the plan's advanced settings";
        rows.Add(r.UsbSuspend switch
        {
            1 => new MachineFact("power.usb", Light.Amber, "on", $"USB selective suspend is on: a USB device (a DMX or timecode interface, a capture device) may be powered down — turn it off in {Advanced} ▸ USB settings"),
            0 => new MachineFact("power.usb", Light.Green, "off", "USB selective suspend is off"),
            _ => new MachineFact("power.usb", Light.Grey, "unknown", Unread("USB selective suspend")),
        });
        rows.Add(r.PcieLinkState switch
        {
            0 => new MachineFact("power.pcie", Light.Green, "off", "PCIe link-state power management is off"),
            1 or 2 => new MachineFact("power.pcie", Light.Amber, r.PcieLinkState == 1 ? "moderate" : "maximum",
                $"PCIe link-state power management saves power ({(r.PcieLinkState == 1 ? "moderate" : "maximum")}): the GPU's link may sleep — turn it off in {Advanced} ▸ PCI Express"),
            _ => new MachineFact("power.pcie", Light.Grey, "unknown", Unread("PCIe link-state power management")),
        });
        rows.Add(r.Notifications switch
        {
            1 => new MachineFact("notifications", Light.Amber, "on", "notifications are on: a toast can open over an output — turn them off in Settings ▸ System ▸ Notifications"),
            0 => new MachineFact("notifications", Light.Green, "off", "notifications are off"),
            _ => new MachineFact("notifications", Light.Grey, "unknown", Unread("notifications")),
        });
        // Home has no Update client policies: its information row says what a person does instead.
        if (r.Os.Family is not (EditionFamily.Home or EditionFamily.Unknown))
        {
            rows.Add(!r.Updates.Read ? new MachineFact("updates", Light.Grey, "unknown", Unread("the Windows Update policy"))
                : r.Updates.Held ? new MachineFact("updates", Light.Green, "held", "updates are held back: " + r.Updates.Values)
                : new MachineFact("updates", Light.Amber, "not deferred",
                    "updates are not deferred, paused or held: Windows may install one and restart during a show (" + r.Updates.Values + ") — defer them by policy, or pause them past the show in Settings ▸ Windows Update"));
        }
        rows.Add(new MachineFact("hags", Light.Grey, Word(r.Hags, "unset"),
            r.Hags < 0 ? "hardware-accelerated GPU scheduling is unset: the driver's default" : $"hardware-accelerated GPU scheduling is {Word(r.Hags, "")}"));
        rows.Add(new MachineFact("memoryIntegrity", Light.Grey, Word(r.MemoryIntegrity, "unknown"),
            r.MemoryIntegrity < 0 ? Unread("Memory Integrity") : $"Memory Integrity is {Word(r.MemoryIntegrity, "")}"));
        rows.Add(new MachineFact("gameMode", Light.Grey, Word(r.GameMode, "unknown"),
            r.GameMode < 0 ? Unread("Game Mode") : $"Game Mode is {Word(r.GameMode, "")}"));

        static string Word(int value, string unread) => value switch { 1 => "on", 0 => "off", _ => unread };
    }

    private static void Claims(ImmutableArray<Claim> claims, ImmutableArray<MachineFact>.Builder rows)
    {
        var refused = 0;
        foreach (var c in claims)
        {
            if (c.Applied || c.Asked.Length == 0)
                continue;
            refused++;
            rows.Add(new MachineFact("claim:" + c.What, Light.Amber, c.Asked, $"{c.What} {c.Asked} was refused: {c.Words}"));
        }
        if (refused == 0 && !claims.IsEmpty)
            rows.Add(new MachineFact("claims", Light.Green, Number(claims.Length), "every claim asked of Windows applied"));
    }

    /// <summary>The rows the engine adds from what only it knows: each move from the locked driver set (amber, fixed by MACHINE LOCK; on
    /// Home, where drivers cannot be held back, the words say the node observes the change), and each enabled display output on the
    /// primary display.</summary>
    public static ImmutableArray<MachineFact> Engine(OsIdentity os, LockedDriverSet? locked, LockedDriverSet now,
        IReadOnlyList<(string Output, string Display)> outputsOnPrimary)
    {
        ArgumentNullException.ThrowIfNull(os);
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(outputsOnPrimary);
        var rows = ImmutableArray.CreateBuilder<MachineFact>();
        if (locked is null)
            rows.Add(new MachineFact("lock", Light.Grey, "not locked", "no driver set is locked: MACHINE LOCK keeps this one as the show's"));
        else
        {
            var drift = locked.DriftTo(now);
            var home = os.Family == EditionFamily.Home ? " (drivers cannot be held back on this edition: this node observes the change)" : "";
            foreach (var d in drift)
                rows.Add(new MachineFact("lock.drift:" + d.Item, Light.Amber, d.Now.Length == 0 ? "gone" : d.Now, d.Words + home, "MACHINE LOCK"));
            if (drift.IsEmpty)
                rows.Add(new MachineFact("lock", Light.Green, "locked", "the drivers are the locked set"));
        }
        foreach (var (output, display) in outputsOnPrimary)
            rows.Add(new MachineFact("display.primary:" + output, Light.Amber, display,
                $"notifications, UAC and setup screens open on {output}: make the desk's monitor primary"));
        return rows.ToImmutable();
    }

    /// <summary>A row's label as preflight shows it.</summary>
    public static string Label(string item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var colon = item.IndexOf(':', StringComparison.Ordinal);
        var head = colon < 0 ? item : item[..colon];
        var tail = colon < 0 ? "" : " " + item[(colon + 1)..];
        return head switch
        {
            "platform" => "Machine platform",
            "os.build" => "Windows build",
            "os.edition" => "Windows edition",
            "os.servicing" => "Windows servicing",
            "edition.updates" => "Updates on this edition",
            "edition.kiosk" => "Kiosk on this edition",
            "edition.displays" => "Displays on this edition",
            "edition.remote" => "Remote Desktop on this edition",
            "sac" => "Smart App Control",
            "power.plan" => "Power plan",
            "power.sleep" => "Sleep timeout",
            "power.display" => "Display timeout",
            "screensaver" => "Screen saver",
            "power.usb" => "USB selective suspend",
            "power.pcie" => "PCIe link-state power",
            "notifications" => "Notifications",
            "updates" => "Windows Update policy",
            "hags" => "Hardware GPU scheduling",
            "memoryIntegrity" => "Memory Integrity",
            "gameMode" => "Game Mode",
            "reboot" => "Pending reboot",
            "network" => "Firewall profiles",
            "network.public" => "Network" + tail,
            "claims" => "Claims",
            "claim" => "Claim" + tail,
            "timer" => "Timer lateness",
            "lock" => "Driver lock",
            "lock.drift" => "Driver drift" + tail,
            "display.primary" => "Primary display" + tail,
            _ => item,
        };
    }

    private static string Number(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>An address as rows compare it: an IPv4 address mapped into IPv6 read as IPv4.</summary>
    internal static IPAddress Normal(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static string Date(DateOnly d) => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}
