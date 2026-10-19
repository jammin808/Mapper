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

    private static void Answers(Claim c)
    {
        Assert.False(string.IsNullOrEmpty(c.What));
        Assert.False(string.IsNullOrEmpty(c.Asked));
        // Applied: something was read back (or said unread). Refused: the words say why.
        if (c.Applied)
            Assert.False(string.IsNullOrEmpty(c.ReadBack), c.What);
        else
            Assert.False(string.IsNullOrWhiteSpace(c.Words), c.What);
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
        Assert.Equal([MachineCapabilities.RealtimePrivilege, MachineCapabilities.Hags, MachineCapabilities.HighResolutionTimer, MachineCapabilities.TimerResolutionHonoured],
            probes.Probes.Select(p => p.Feature));
        Assert.All(probes.Probes, p => Assert.True(p.Supported || p.Words.Length > 0, p.Feature));

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
                    using (var claim = platform.ClaimThread(role))
                    {
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
                    // Disposed: the thread's priority is back as it was.
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
}
