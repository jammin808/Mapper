using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Core.Bus;

namespace PlayMap.Core.Machine;

/// <summary>The power settings as read: the active plan's name and kind ("high", "ultimate", "balanced", "saver", "other"; "" unread),
/// whether the machine offers Balanced only (Modern Standby) with its power mode, and the sleep and display timeouts on mains in
/// minutes (0: never; −1: unread).</summary>
public sealed record PowerReading(string Plan, string Kind, bool OnlyBalanced, string Mode, int SleepAcMin, int DisplayAcMin)
{
    public static PowerReading Unread { get; } = new("", "", false, "", -1, -1);
}

/// <summary>A connected network as read: its name, its firewall profile ("public", "private", "domain") and whether an inbound rule for
/// PlayMap covers the Public profile.</summary>
public sealed record NetworkProfile(string Name, string Category, bool PublicRule);

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

    /// <summary>The claims made so far, process and threads.</summary>
    public ImmutableArray<Claim> Claims { get; init; } = [];

    /// <summary>The deadline timers' waits counted and their p99 lateness (µs).</summary>
    public int TimerWaits { get; init; }

    public double TimerLateP99Us { get; init; }

    /// <summary>Keep-awake's setting (MACHINE SET AWAKE).</summary>
    public bool AwakeOn { get; init; } = true;
}

/// <summary>The machine's preflight rows, pure: each a fact with its light and words, never a silent change, a fix line only where a
/// verb fixes it. The edition's rows follow D4: a layer the edition lacks is a grey information row ("i"), and the hard rows are
/// amber. Rows that fail name what is wrong; the must rows that pass are green, so a person sees what was checked.</summary>
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
        foreach (var n in r.Networks.Where(n => n.Category == "public" && !n.PublicRule))
            rows.Add(new MachineFact("network.public:" + n.Name, Light.Amber, "public",
                $"the network {n.Name} is on the Public firewall profile and no rule lets PlayMap in there: make it Private in Settings ▸ Network & internet ▸ {n.Name} ▸ Network profile type"));
        Claims(r.Claims, rows);
        rows.Add(r.TimerWaits == 0 ? new MachineFact("timer", Light.Grey, "no waits", "no deadline wait has been timed yet")
            : r.TimerLateP99Us > LateAmberUs
                ? new MachineFact("timer", Light.Amber, Us(r.TimerLateP99Us), $"deadline waits wake {Us(r.TimerLateP99Us)} late (p99 of the last {r.TimerWaits}): over 1 ms")
                : new MachineFact("timer", Light.Green, Us(r.TimerLateP99Us), $"deadline waits wake {Us(r.TimerLateP99Us)} late (p99 of the last {r.TimerWaits})"));
        return rows.ToImmutable();
    }

    private static string Us(double us) => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(us)} µs");

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
                ? new MachineFact(item, Light.Amber, Number(minutes) + " min", after + ": keep-awake holds it off only while an output is open; set it to never in Settings ▸ System ▸ Power & battery")
                : new MachineFact(item, Light.Amber, Number(minutes) + " min", after + " and keep-awake is off", "MACHINE SET AWAKE ON"));
        }
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

    private static string Date(DateOnly d) => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}
