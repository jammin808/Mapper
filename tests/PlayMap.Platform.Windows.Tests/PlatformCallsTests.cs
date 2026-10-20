using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using PlayMap.Core.Machine;
using PlayMap.Core.Time;
using PlayMap.Platform.Windows.Interop;

namespace PlayMap.Platform.Windows.Tests;

/// <summary>Every call answers, on Windows (the Windows job): each claim says what it asked and either applied with what reading it back
/// shows or refused with its words — on the runner's Server SKU and Basic Render Driver a refusal with its named words passes; the
/// power throttling state reads back as it was set where Windows answers; an MMCSS handle is taken and reverted on dispose where the
/// service runs, the thread's priority put back as it was; 1 000 waits of 1 ms on today's wait and on the high-resolution timer are
/// timed, both distributions recorded, no ceiling asserted (the runner is shared; the lab holds the pass mark). What Windows answered
/// is written to the job's evidence (<c>platform-evidence.md</c> in the runner's temporary folder), which the workflow's wp3-platform
/// step puts in the log and the summary.</summary>
[SupportedOSPlatform("windows")]
public sealed class PlatformCallsTests
{
    private static readonly Lock EvidenceGate = new();

    private static void Evidence(string section, IEnumerable<string> lines)
    {
        var folder = Environment.GetEnvironmentVariable("RUNNER_TEMP");
        if (string.IsNullOrEmpty(folder))
            return;
        var text = new StringBuilder().Append("### ").AppendLine(section);
        foreach (var line in lines)
            text.AppendLine(line);
        lock (EvidenceGate)
            File.AppendAllText(Path.Combine(folder, "platform-evidence.md"), text.AppendLine().ToString());
    }

    private static string Row(Claim c) => $"| {c.What} | {c.Asked} | {(c.Applied ? "applied" : "refused")} | {c.ReadBack} | {c.Words} |";

    /// <summary>The words a refusal of each claim starts with: Windows' own answer, named.</summary>
    private static readonly (string Suffix, string[] Words)[] Refusals =
    [
        ("process.priority", ["Windows refused the priority class: "]),
        ("process.qos", ["Windows refused the power throttling state: "]),
        ("process.gpuPriority", ["Windows refused the GPU scheduling class (", PlatformPolicy.RealtimeWithoutPrivilegeWords]),
        ("process.awake", ["Windows made no power request: ", "Windows refused the display-required request: ", "Windows refused the system-required request: "]),
        (".priority", ["Windows refused the priority: "]),
        (".mmcss", ["MMCSS did not take the thread ("]),
        (".cpusets", ["Windows refused the CPU sets: "]),
        (".qos", ["Windows refused EcoQoS: "]),
        // A thread whose handle could not be taken claimed nothing, and says so.
        ("", ["the thread's handle could not be taken ("]),
    ];

    private static void Answers(Claim c)
    {
        Assert.False(string.IsNullOrEmpty(c.What));
        Assert.False(string.IsNullOrEmpty(c.Asked));
        // Applied: something was read back (or said unread). Refused: Windows' named words say why.
        if (c.Applied)
        {
            Assert.False(string.IsNullOrEmpty(c.ReadBack), c.What);
            return;
        }
        var (_, words) = Refusals.First(r => c.What.EndsWith(r.Suffix, StringComparison.Ordinal));
        Assert.True(words.Any(w => c.Words.StartsWith(w, StringComparison.Ordinal)), $"{c.What}: {c.Words}");
    }

    /// <summary>Runs <paramref name="body"/> on a thread of its own, so a claim there is that thread's only one.</summary>
    private static void OnThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure = ex;
            }
        });
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [WindowsFact]
    public void EveryProcessClaimAnswersAndTheThrottlingStateReadsBackAsSet()
    {
        using var platform = new WindowsMachinePlatform();
        var claims = platform.ClaimProcess(MachineSettings.Default);
        Assert.Equal(["process.priority", "process.qos", "process.gpuPriority"], claims.Select(c => c.What));
        Assert.Equal(["high", "high", "normal"], claims.Select(c => c.Asked));
        Assert.All(claims, Answers);
        var qos = claims[1];
        if (qos.Applied && qos.ReadBack != "unread")
            Assert.Equal("high", qos.ReadBack);

        // AUTO hands the throttling back, read back the same way, then HIGH again; the class goes to NORMAL and back.
        var auto = platform.ClaimProcess(MachineSettings.Default with { Qos = "auto", Process = "normal" });
        Assert.All(auto, Answers);
        if (auto[1].Applied && auto[1].ReadBack != "unread")
            Assert.Equal("auto", auto[1].ReadBack);
        if (auto[0].Applied)
            Assert.Equal("normal", auto[0].ReadBack);
        var again = platform.ClaimProcess(MachineSettings.Default);
        Assert.All(again, Answers);
        Assert.Same(MachineSettings.Default, platform.Settings);

        var probes = platform.Capabilities();
        var identity = platform.Identity();
        // The realtime privilege, DXGI's words only where it failed, NVAPI's only where an NVIDIA adapter is there, HAGS for each adapter
        // by its LUID with its driver, the high-resolution timer and whether timer requests are honoured.
        Assert.Equal(MachineCapabilities.RealtimePrivilege, probes.Probes[0].Feature);
        Assert.Equal([MachineCapabilities.HighResolutionTimer, MachineCapabilities.TimerResolutionHonoured], probes.Probes.TakeLast(2).Select(p => p.Feature));
        Assert.Equal(identity.Adapters.Select(a => (a.Luid, a.Driver)),
            probes.Probes.Where(p => p.Feature == MachineCapabilities.Hags).Select(p => (p.AdapterLuid, p.Driver)));
        if (identity.Adapters.IsEmpty)
            Assert.False(Assert.Single(probes.Probes, p => p.Feature == MachineCapabilities.Adapters).Supported);
        if (!identity.Adapters.Any(a => a.VendorId == VendorPolicy.Nvidia))
            Assert.DoesNotContain(probes.Probes, p => p.Feature == MachineCapabilities.NvApi);
        Assert.All(probes.Probes, p => Assert.True(p.Supported || p.Words.Length > 0, p.Feature));
        // Honoured only where the QoS claim reads back high; set and unread is not observed.
        var honoured = Assert.Single(probes.Probes, p => p.Feature == MachineCapabilities.TimerResolutionHonoured);
        Assert.Equal(again[1].Applied && again[1].ReadBack == "high", honoured.Supported);
        // The power plan is read with the capabilities: a name and a kind, or unread.
        Assert.True(platform.Power.Kind is "" or "high" or "ultimate" or "balanced" or "saver" or "other", platform.Power.Kind);

        Evidence("Process claims (default settings, then QOS AUTO with PROCESS NORMAL)",
            ["| claim | asked | answer | read back | words |", "|---|---|---|---|---|", .. claims.Select(Row), .. auto.Select(Row)]);
        Evidence("Capability probes", probes.Probes.Select(p => $"- {p.Feature}: {(p.Supported ? "yes" : "no")} {p.Words}"));
    }

    [WindowsFact]
    public void EachRolesThreadClaimAnswersAndItsDisposeRevertsThePriority()
    {
        using var platform = new WindowsMachinePlatform();
        _ = platform.ClaimProcess(MachineSettings.Default);
        var rows = new List<string> { "| claim | asked | answer | read back | words |", "|---|---|---|---|---|" };
        foreach (var role in Enum.GetValues<ThreadRole>())
        {
            // Each role on a thread of its own, so its claim is the thread's only one.
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var before = Native.GetThreadPriority(Native.GetCurrentThread());
                    WindowsThreadClaim made;
                    bool mmcssTaken;
                    using (var claim = platform.ClaimThread(role))
                    {
                        made = Assert.IsType<WindowsMachinePlatform.ThreadHold>(claim).Shared;
                        mmcssTaken = claim.Claims.Any(c => c.What.EndsWith(".mmcss", StringComparison.Ordinal) && c.Applied);
                        Assert.NotEmpty(claim.Claims);
                        Assert.All(claim.Claims, Answers);
                        var expected = PlatformPolicy.For(role);
                        var priority = Assert.Single(claim.Claims, c => c.What.EndsWith(".priority", StringComparison.Ordinal));
                        Assert.Equal(PlatformPolicy.Word(expected.Priority), priority.Asked);
                        if (expected.MmcssTask is { } task)
                        {
                            var mmcss = Assert.Single(claim.Claims, c => c.What.EndsWith(".mmcss", StringComparison.Ordinal));
                            Assert.Equal(task, mmcss.Asked);
                            if (mmcss.Applied)
                                Assert.StartsWith("task index", mmcss.ReadBack, StringComparison.Ordinal);
                            else
                                Assert.StartsWith("MMCSS did not take the thread", mmcss.Words, StringComparison.Ordinal);
                        }
                        else
                        {
                            Assert.DoesNotContain(claim.Claims, c => c.What.EndsWith(".mmcss", StringComparison.Ordinal));
                        }
                        lock (rows)
                            rows.AddRange(claim.Claims.Select(Row));
                    }
                    // Disposed: the thread left its MMCSS task (Windows' answer kept) and its priority is back as it was.
                    Assert.Equal(mmcssTaken ? (bool?)true : null, made.MmcssReverted);
                    Assert.Equal(before, Native.GetThreadPriority(Native.GetCurrentThread()));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failure = ex;
                }
            });
            thread.Start();
            thread.Join();
            Assert.Null(failure);
        }
        Assert.NotEmpty(platform.Claims);
        Evidence("Thread claims by role", rows.Distinct());
    }

    [WindowsFact]
    public void ASecondClaimOnAClaimedThreadJoinsItAndTheLastHolderReverts()
    {
        using var platform = new WindowsMachinePlatform();
        _ = platform.ClaimProcess(MachineSettings.Default);
        OnThread(() =>
        {
            var before = Native.GetThreadPriority(Native.GetCurrentThread());
            var first = platform.ClaimThread(ThreadRole.Timecode);
            var second = platform.ClaimThread(ThreadRole.Timecode);
            // Two listeners on one capture thread hold one claim: the second never reads the first's priority as the thread's own.
            var shared = Assert.IsType<WindowsMachinePlatform.ThreadHold>(first).Shared;
            Assert.Same(shared, Assert.IsType<WindowsMachinePlatform.ThreadHold>(second).Shared);
            Assert.Equal(first.Claims, second.Claims);
            var mmcss = Assert.Single(first.Claims, c => c.What == "thread.timecode.mmcss");
            first.Dispose();
            first.Dispose();
            // One holder is left: nothing is reverted yet.
            Assert.Null(shared.MmcssReverted);
            Assert.True(shared.Running);
            second.Dispose();
            Assert.Equal(mmcss.Applied ? (bool?)true : null, shared.MmcssReverted);
            Assert.Equal(before, Native.GetThreadPriority(Native.GetCurrentThread()));
            // Claimed again after the last let go: a fresh claim.
            using var third = platform.ClaimThread(ThreadRole.Timecode);
            Assert.NotSame(shared, Assert.IsType<WindowsMachinePlatform.ThreadHold>(third).Shared);
        });
    }

    [WindowsFact]
    public void EveryWordHandedToThePlatformReachesTheNextHoldTimerAndThreadClaimAndTheFacts()
    {
        using var platform = new WindowsMachinePlatform();
        _ = platform.ClaimProcess(MachineSettings.Default with { Awake = "off" });

        // AWAKE OFF holds nothing; handed ON, the next hold is a power request, and the facts follow the setting.
        using (platform.KeepAwake())
        {
            Assert.False(platform.Awake);
            Assert.Null(platform.AwakeClaim);
        }
        var displayOff = Assert.Single(platform.Facts(), f => f.Item == "power.display");
        platform.Use(MachineSettings.Default);
        Assert.Equal("on", platform.Settings.Awake);
        MachineFact displayOn;
        using (platform.KeepAwake())
        {
            var held = Assert.NotNull(platform.AwakeClaim);
            Answers(held);
            Assert.Equal(("process.awake", "on"), (held.What, held.Asked));
            Assert.Equal(held.Applied, platform.Awake);
            Assert.Contains(platform.Claims, c => c.What == "process.awake");
            displayOn = Assert.Single(platform.Facts(), f => f.Item == "power.display");
        }
        Assert.False(platform.Awake);
        Assert.Null(platform.AwakeClaim);
        if (displayOff.Light == PlayMap.Core.Bus.Light.Amber)
        {
            Assert.Equal("MACHINE SET AWAKE ON", displayOff.Fix);
            Assert.Null(displayOn.Fix);
        }

        // TIMER DEFAULT: the next timer is the runtime's wait, and the platform names it so; HIGH again: the high-resolution one.
        platform.Use(MachineSettings.Default with { Timer = "default" });
        using (var timer = platform.CreateTimer())
        {
            Assert.IsNotType<HighResolutionTimer>(timer);
            Assert.Equal("default", platform.TimerMode(timer));
        }
        platform.Use(MachineSettings.Default);
        using (var timer = platform.CreateTimer())
        {
            Assert.IsType<HighResolutionTimer>(timer);
            Assert.Equal("high", platform.TimerMode(timer));
        }

        // MMCSS OFF: the next render thread joins no task; ON again: it does.
        platform.Use(MachineSettings.Default with { Mmcss = "off" });
        OnThread(() =>
        {
            using var claim = platform.ClaimThread(ThreadRole.Render);
            Assert.DoesNotContain(claim.Claims, c => c.What == "thread.render.mmcss");
        });
        platform.Use(MachineSettings.Default);
        OnThread(() =>
        {
            using var claim = platform.ClaimThread(ThreadRole.Render);
            Assert.Contains(claim.Claims, c => c.What == "thread.render.mmcss");
        });

        // CPUSETS OFF places nothing; AUTO again is placed afresh, never with OFF's words.
        platform.Use(MachineSettings.Default with { CpuSets = "off" });
        OnThread(() =>
        {
            using var claim = platform.ClaimThread(ThreadRole.Render);
            Assert.DoesNotContain(claim.Claims, c => c.What == "thread.render.cpusets");
        });
        platform.Use(MachineSettings.Default);
        OnThread(() =>
        {
            using var claim = platform.ClaimThread(ThreadRole.Render);
            var cpuSets = Assert.Single(claim.Claims, c => c.What == "thread.render.cpusets");
            Assert.DoesNotContain(CpuSetPlan.OffWords, cpuSets.Words, StringComparison.Ordinal);
        });
    }

    [WindowsFact]
    public void AThousandWaitsOfOneMillisecondOnEachWaitAreTimedAndRecordedWithNoCeiling()
    {
        using var platform = new WindowsMachinePlatform();
        var clock = MonoClock.Instance;
        double[] Time(IDeadlineTimer timer)
        {
            var late = new double[1000];
            for (var i = 0; i < late.Length; i++)
            {
                var due = clock.NowNs + 1_000_000;
                Assert.True(timer.WaitUntil(due));
                late[i] = (clock.NowNs - due) / 1e3;
                // Never early: a deadline wait answers at or after its instant.
                Assert.True(late[i] >= 0, $"wait {i} answered {late[i]} µs early");
            }
            Array.Sort(late);
            return late;
        }

        using var today = new RuntimeDeadlineTimer(clock);
        var runtime = Time(today);
        using var high = platform.CreateTimer();
        Assert.IsType<HighResolutionTimer>(high);
        var resolution = Time(high);
        Assert.Equal(1000, platform.TimerLateness.Waits);
        Assert.True(platform.TimerLateness.P99Us > 0);

        // A cancel handle already set answers false at once.
        using var cancel = new ManualResetEvent(true);
        Assert.False(high.WaitUntil(clock.NowNs + 1_000_000_000, cancel));

        static string Line(string name, double[] late) => string.Create(CultureInfo.InvariantCulture,
            $"| {name} | {late[499]:F0} | {late[899]:F0} | {late[989]:F0} | {late[998]:F0} | {late[^1]:F0} |");
        Evidence("1 000 waits of 1 ms: lateness (µs) on the runner, no ceiling",
            ["| wait | p50 | p90 | p99 | p99.9 | max |", "|---|---|---|---|---|---|", Line("today's wait (WaitOne)", runtime), Line("high-resolution timer", resolution)]);
    }

    [WindowsFact]
    public void TheIdentityAndTheFactsAnswerOnThisMachine()
    {
        using var platform = new WindowsMachinePlatform();
        _ = platform.ClaimProcess(MachineSettings.Default);
        var identity = platform.Identity();
        Assert.True(identity.Os.Build >= 20348, identity.Os.Build.ToString(CultureInfo.InvariantCulture));
        Assert.NotEqual(EditionFamily.Unknown, identity.Os.Family);
        Assert.Equal(Environment.ProcessorCount, identity.Cpu.Logical);
        Assert.NotEmpty(identity.Cpu.ByEfficiencyClass);
        Assert.Equal(Environment.ProcessorCount, identity.Cpu.ByEfficiencyClass.Sum());
        Assert.All(identity.Adapters, a => Assert.False(string.IsNullOrEmpty(a.Name)));
        var facts = platform.Facts();
        Assert.Contains(facts, f => f.Item == "os.edition");
        Assert.All(facts, f => Assert.False(string.IsNullOrWhiteSpace(f.Words), f.Item));

        using (platform.KeepAwake())
            Assert.True(platform.Awake);
        Assert.False(platform.Awake);

        Evidence("Identity", [
            $"- OS: {EditionTable.Name(identity.Os)} {identity.Os.Version} ({identity.Os.Edition}) build {identity.Os.Build}.{identity.Os.Revision}, tier {EditionTable.Word(EditionTable.Tier(identity.Os))}",
            $"- CPU: {identity.Cpu.Model}, {identity.Cpu.Logical} logical, by efficiency class [{string.Join(", ", identity.Cpu.ByEfficiencyClass)}], {identity.Cpu.VectorBits}-bit vectors",
            .. identity.Adapters.Select(a => $"- GPU {a.LuidText}: {a.Name} ({a.VendorId:x4}:{a.DeviceId:x4}) driver {a.Driver}, {a.TierWord}, {a.SupportWord}, outputs {a.DrivesOutputs}"),
        ]);
        Evidence("Machine facts", facts.Select(f => $"- {f.Item}: {f.Light} {f.Value} — {f.Words}"));
    }

    [WindowsFact]
    public void EachConnectedNetworkNamesTheAdaptersItsConnectionsGoThroughWithTheirAddresses()
    {
        var read = WindowsReadings.TryNetworks(Environment.ProcessPath ?? "", out var networks, out var words);
        var local = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
        foreach (var network in networks)
            foreach (var adapter in network.Adapters)
            {
                var nic = Assert.Single(local, i => Guid.TryParse(i.Id, out var id) && id == adapter.Id);
                Assert.Equal(nic.Name, adapter.Name);
            }
        // A connected network has a connection, and each connection goes through an adapter this machine lists.
        if (read && !networks.IsEmpty)
            Assert.Contains(networks, n => !n.Adapters.IsEmpty);
        Evidence("Networks and their adapters", read
            ? networks.Select(n => $"- {n.Name}: {n.Category}, rule {(n.PublicRule ? "yes" : "no")}, through "
                + string.Join("; ", n.Adapters.Select(a => $"{a.Name} ({string.Join(", ", a.Addresses)})")))
            : [$"- not read: {words}"]);
    }
}
