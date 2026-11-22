using System.Globalization;
using System.Text;

namespace PlayMap.PriorityProbe;

/// <summary>The reader: from each leg's files, never from memory, the tables the owner reads. A leg's window is the victim's own window
/// cut to the time its burner was burning (from the burner's ready to its stop, both on the machine's monotonic clock), so a burner that
/// came up late shortens the window and never counts the quiet part. The burner starts at the victim's ready, so it has burned for a
/// lead before the window opens: the lead is a table of its own and never in the window's figures. In the window every frame slot is
/// rebuilt (each frame's due, then due + k periods for each slot its work ran past), so a stall that began before the window is counted
/// for the slots it held inside it; frames are counted by their start and ticks by their wake, with the due rule's figures beside,
/// labelled; a pool item is in it by its queue time, and one still waiting at the window's end is pending, its wait a lower bound; an
/// item queued before the window and still waiting at its start held the window from its start, so it is the window's too, said
/// apart as pending at the window's start (its wait a lower bound while it had still not run at the window's end). The warm-up is read
/// as every leg is and never counted with its kind; in the buy legs the victim's files are the busy side's own deadline and are read
/// exactly as a victim's; the whole-core-free legs say the logical processors their burner was pinned to and left free, and count as
/// pinned only where every thread was; a node leg's window is cut to the node's time at work as well (from its at-work stamp to its stop)
/// and its node read from its own rows (its class, its processor time and its threads' at the window's bounds, STATE's figures); the
/// machine sampler's every-process rows (the prioritycost job's) are joined per leg by pid, never by the counter's instance name: a
/// sample whose processes' shares sum past the machine's logical processors by more than <see cref="ImpossibleMargin"/> is refused,
/// counted and said, and only the samples kept name who else ran (the processes outside the probe's own and the node, with the most
/// processor time in the window and in its longest stretch with no frame finished), the sampler's own share and the node's.
/// Percentiles are nearest-rank. Every number is a reading, never a pass mark.</summary>
internal static class Reader
{
    public const string SummaryMd = "summary.md";
    public const string SummaryCsv = "summary.csv";

    /// <summary>How far past the machine's logical processors a sample's processes' shares (Idle among them) may sum before the sample
    /// is refused: half a logical processor. The sums of run 502's samples that kept their names read 3.71 to 4.04 on four logical
    /// processors; the ones whose instances were renumbered between the counter's two collections read 4.85 to 57.90.</summary>
    public const double ImpossibleMargin = 0.5;

    public static int Run(string root, bool say = true)
    {
        var runs = Directory.GetDirectories(root, "leg-*").Select(d => Read(root, d)).OrderBy(r => r.SortKey, StringComparer.Ordinal).ToList();
        var md = new List<string>
        {
            "### What a saturating process costs a Normal-class process doing real work (a measurement, never a pass mark)",
            "",
        };
        md.AddRange(SchedulerLines(root, runs));
        md.Add("");
        md.AddRange(BeforeTheLegs(root));
        md.Add("");
        md.Add("#### The places (each leg's place in this run's order, from its own row)");
        md.Add("");
        md.Add("| Place | Leg | Why it is there |");
        md.Add("|---|---|---|");
        foreach (var r in runs)
            md.Add($"| {(r.Place is { } at ? "#" + at.ToString("00", CultureInfo.InvariantCulture) : "-")} | {r.Kind}{(r.Warmup ? " (the warm-up)" : "")} | {(r.PlaceNote.Length > 0 ? r.PlaceNote : "-")} |");
        md.Add("");
        md.Add("#### The window: frames");
        md.Add("");
        md.Add("| Leg | Window | Frames / missed of the slots (every slot rebuilt) | Missed per 1000 | By the due rule: frames / missed | Longest with no frame finished (in the window; whole) | Frames started | Lateness by start p50 / p99 / max | Lateness by due p50 / p99 / max | Work wall / processor (sums) |");
        md.Add("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in runs)
            md.Add(r.Readable ? r.FramesRow() : $"| {r.Label} | {r.Missing} | | | | | | | | |");
        md.Add("");
        md.Add("#### The window: the tick and the pool item");
        md.Add("");
        md.Add("| Leg | Ticks woke (by wake) | Overshoot by wake p50 / p99 / max | By the due rule: ticks, overshoot p99 / max | Longest with no tick woke | Ticks with the item still pending | Pool items run / queued | Dispatch of those run p99 / max | Still pending at the window's end | Pending at the window's start (queued before it) |");
        md.Add("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in runs.Where(r => r.Readable))
            md.Add(r.TicksRow());
        md.Add("");
        md.Add("#### The processes");
        md.Add("");
        md.Add("| Leg | Burner class asked / read back | Burner threads | Burner processors in the window (its stamps) | Burner processors, ready to stop | Victim class at start / read | Victim processors in the window (its stamps) | Victim processor time, its life | Timer resolution at the window's start / end | Burner's logical processors (pinned / free) |");
        md.Add("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in runs.Where(r => r.Readable))
            md.Add(r.ProcessRow());
        md.Add("");
        md.Add("#### The burning lead (from the burner's ready to the window's start; never in the window's figures)");
        md.Add("");
        md.Add("| Leg | Lead | Frames / missed of its slots | Longest with no frame finished | Ticks woke | Longest with no tick woke |");
        md.Add("|---|---|---|---|---|---|");
        foreach (var r in runs.Where(r => r.Readable))
            md.Add(r.LeadRow());
        md.Add("");
        md.Add($"#### Who else ran (the machine sampler's every-process rows, outside the probe's own processes, the node and Idle, joined by pid; processor-seconds are each sample's share times its overlap; a sample whose shares sum past the logical processors by more than {ImpossibleMargin.ToString("0.0#", CultureInfo.InvariantCulture)} is refused and names no one)");
        md.Add("");
        md.Add("| Leg | Samples over the window | Most processor time in the window (base priority) | In the longest stretch with no frame finished (base priority) | Samples refused (over the window / in the leg) | The sampler itself in the window |");
        md.Add("|---|---|---|---|---|---|");
        foreach (var r in runs.Where(r => r.Readable))
            md.Add($"| {r.Label} | {r.SamplesInWindow} | {r.OthersInWindow} | {r.OthersInNoFrame} | {r.SamplesRefused} | {r.SamplerShare} |");
        md.Add("");
        if (runs.Any(r => r.Readable && r.Node is not null))
        {
            md.Add("#### The node (PlayMap's own node as the busy process; its class from its home's settings, read back from the OS; its figures STATE's at each bound, as STATE counts them; its processors include the draw of its enabled outputs, which on a machine with no GPU is Mesa's llvmpipe on the processors: its threads' groups say how much)");
            md.Add("");
            md.Add("| Leg | Class asked / read at work / read at the window's end (STATE's claim) | Clips, outputs, renderer | Node processors in the window (its stamps) | By the sampler (pid, samples kept) | Its threads by group (processors in the window) | Outputs at the window's start / end: fps; dropped; missed vblanks; frame p95 at the end | Decoders at the end: dropped start / end; late start / end; decode p95 at the end |");
            md.Add("|---|---|---|---|---|---|---|---|");
            foreach (var r in runs.Where(r => r.Readable && r.Node is not null))
                md.Add(r.NodeRow());
            md.Add("");
        }
        md.Add("#### Each leg kind across its repeats (each run's figure in the run's order, then the lowest and the highest; nothing is averaged; the warm-up and a whole-core-free leg not pinned each in a row of their own, never with their kind)");
        md.Add("");
        md.Add("| Leg kind | Runs | Missed slots | Longest with no frame finished (ms, in the window) | Worst tick by wake (ms) | Longest with no tick woke (ms, in the window) | Longest pool wait (ms; >= a lower bound) |");
        md.Add("|---|---|---|---|---|---|---|");
        foreach (var kind in Legs.All.Select(l => l.Name))
        {
            foreach (var group in runs.Where(r => r.Readable && r.Kind == kind).GroupBy(r => r.SpreadKey).OrderBy(g => g.Key.Length).ThenBy(g => g.Key, StringComparer.Ordinal))
                md.Add(SpreadRow(group.Key, group.ToList()));
        }
        md.Add("");
        var calibration = Files.ReadCsv(Path.Combine(root, Calibration.File)).FirstOrDefault();
        md.Add(calibration is null ? "No calibration row: the frame's work is unknown." :
            $"The machine: {calibration["os"]}, {calibration["processors"]} logical processors; the frame's work {calibration["iterations"]} iterations, " +
            $"{calibration["median_ms"]} ms median wall time (p10 {calibration["p10_ms"]}, p90 {calibration["p90_ms"]}) when calibrated before the legs.");
        md.Add("");
        md.Add("How to read it. A frame slot is missed when the frame before it is still at work at the slot's end (Victim.cs, the frame " +
            "loop), so frames and missed slots tile the slots one for one; the due rule charges a frame's missed slots to that frame's own " +
            "due and so drops a stall that began before the window. Frames by start and ticks by wake count what happened inside the window " +
            "(a tick due before it that woke in it is counted, with its overshoot). The tick waits on the runtime's own wait, so its overshoot " +
            "hides a delay under the default timer granularity. A pool item still waiting at the window's end is pending: its wait is a lower " +
            "bound (>=), ended only when it ran, and never a completed wait; one queued before the window and not yet run at its start is " +
            "pending at the window's start, the window's too, its whole wait said with the part before the window (a lower bound while it " +
            "had still not run at the window's end); the longest pool wait is the larger of the longest completed wait and the largest " +
            "lower bound, >= only when the bound is the larger. Processors in the window are each process's own processor time " +
            "stamped at the window's bounds, over the stamps' own span; on Windows a thread's processor time is counted in clock ticks " +
            "(15.625 ms at the default resolution), so a 20 s window's figure is good to about a tick per thread. high-n against normal-n is " +
            "what the burner's class costs a desk at Normal with every logical processor busy; high-n1 against normal-n1 the same with one " +
            "logical processor left unburned (on a machine with SMT the sibling of a busy one, never a whole core), which is each of the " +
            "product's Compute and HAP pools' own width (Compute.cs 102, HapWorkers.cs 92) and not a floor the node holds: nothing caps their " +
            "sum. high-core1 against normal-core1 the same with a whole core left free, the burner's threads pinned one to each logical " +
            "processor of every other core (Windows only; a leg not pinned is said so and never counted as pinned). buy-high against " +
            "buy-normal is what HIGH buys: the victim's own loops, in a process that claims the class through the product's platform, are the " +
            "busy side's own deadline beside a burner NORMAL on every logical processor; a synthetic deadline loop beside a synthetic " +
            "competing load, not the node's render loop and not a real desk. high-n-runner is the victim at the class the runner started it " +
            "at, not a desk's. high-n1-pinned is high-n1 with its threads pinned one to each of the logical processors less one (beside " +
            "high-n1 the pinning alone differs, beside high-core1 the free core and its one thread fewer). The node legs put PlayMap's own " +
            "node in the burner's place, its class from its home's settings (node-high, node-normal beside the victim alone; node-high-load, " +
            "node-normal-load beside a NORMAL burner on every logical processor): it decodes only what an enabled output draws, so each clip " +
            "has a 64 x 36 output, and on a machine with no GPU each draw is Mesa's on the processors, so its threads by group say how much " +
            "of it is Mesa's and how much the node's own decode, render and pools'. The warm-up is the job's first burned leg, read as every leg is and never counted with its kind. The machine " +
            "sampler's rows thin out or stop while a burner holds the processors, as the runner's own processes are held; who else ran is " +
            "read from its every-process rows, each sample's span the call's own (its row's time less its sample_ms, to its time), so a " +
            "held sample spans long and its share is spread over it. Synthetic busy threads on the machine named above, not a show machine " +
            "and not a Windows 11 client with a desk in the foreground (docs/round2/CARRYOVER.md, fix-ci-priority-measure, " +
            "fix-priority-probe-reader, fix-priority-probe-third and fix-priority-probe-node).");
        md.Add("");
        File.WriteAllLines(Path.Combine(root, SummaryMd), md, new UTF8Encoding(false));
        Files.WriteCsv(Path.Combine(root, SummaryCsv), LegRead.CsvHeader, runs.Select(r => r.Csv()));
        if (say)
        {
            foreach (var line in md)
                Program.Say(line);
        }
        return runs.Any(r => r.Readable) ? 0 : 1;
    }

    /// <summary>The runner's scheduler as the job read it before any leg, then each leg whose own reading differs from it.</summary>
    private static IEnumerable<string> SchedulerLines(string root, List<LegRead> runs)
    {
        var job = Files.ReadCsv(Path.Combine(root, Scheduler.File)).FirstOrDefault();
        if (job is null)
        {
            yield return "The runner's scheduler: not recorded (this run's probe read no scheduler.csv).";
            yield break;
        }
        yield return "The runner's scheduler (read, never set; " + job["when"] + "): " + SchedulerWords(job) + ".";
        var differ = new List<string>();
        foreach (var r in runs)
        {
            var own = Files.ReadCsv(Path.Combine(r.Dir, Scheduler.File)).FirstOrDefault();
            if (own is not null && SchedulerWords(own) != SchedulerWords(job))
                differ.Add($"{r.Label}: {SchedulerWords(own)}");
        }
        if (differ.Count > 0)
            yield return "Read again at a leg's end and not the same: " + string.Join("; ", differ) + ".";
        else if (runs.Any(r => File.Exists(Path.Combine(r.Dir, Scheduler.File))))
            yield return "Read again at each leg's end: the same.";
    }

    private static string SchedulerWords(Dictionary<string, string> s)
    {
        string Res(string column) => Cell(s, column) is { } v ? (Long(v) / 1e4).ToString("0.000", CultureInfo.InvariantCulture) + " ms" : "not read";
        var separation = Cell(s, "win32_priority_separation") is { } p
            ? $"{p} (0x{Long(p).ToString("X", CultureInfo.InvariantCulture)})" : "not read";
        var topology = Cell(s, "cores") is { } cores
            ? $"{Cell(s, "logical_processors") ?? "?"} logical processors on {cores} cores, SMT {Cell(s, "smt") ?? "not read"}" : "topology not read";
        var words = Cell(s, "words") is { } w ? $" ({w})" : "";
        return $"Win32PrioritySeparation {separation}; timer resolution (NtQueryTimerResolution) minimum {Res("timer_minimum_100ns")}, " +
            $"maximum {Res("timer_maximum_100ns")}, current {Res("timer_current_100ns")}; {topology}{words}";
    }

    public const string ServersFile = "servers.csv";
    public const string QuietFile = "quiet.csv";
    public const string ReadingsFile = "readings.csv";

    /// <summary>What the job read before the legs (tools/ci-tests.ps1 -PriorityCost writes both files at the root): the build's servers
    /// still alive after the build and after their shutdown, and the quiet start's wait; each "not recorded" where the run wrote none.</summary>
    private static IEnumerable<string> BeforeTheLegs(string root)
    {
        yield return "#### Before the legs (the job's own readings; none is a pass mark)";
        yield return "";
        yield return "| Reading | What it read |";
        yield return "|---|---|";
        var servers = Path.Combine(root, ServersFile);
        if (!File.Exists(servers))
        {
            yield return $"| build servers | not recorded (this run wrote no {ServersFile}) |";
        }
        else
        {
            var rows = Files.ReadCsv(servers);
            if (rows.Count == 0)
                yield return $"| build servers | {ServersFile} holds no reading |";
            foreach (var row in rows)
                yield return $"| build servers {Cell(row, "when") ?? "(no when)"} | {Cell(row, "alive") ?? "none"}{(Cell(row, "words") is { } w ? " (" + w + ")" : "")} |";
        }
        // Whatever else the job read before the legs (the kernel trace tools on the runner, each read and never used): one row each.
        foreach (var row in Files.ReadCsv(Path.Combine(root, ReadingsFile)))
            yield return $"| {Cell(row, "what") ?? "(no what)"} | {Cell(row, "read") ?? "(not read)"} |";
        var quiet = Path.Combine(root, QuietFile);
        if (!File.Exists(quiet))
        {
            yield return $"| quiet start | not recorded (this run wrote no {QuietFile}) |";
            yield break;
        }
        var samples = Files.ReadCsv(quiet);
        if (samples.Count == 0)
        {
            yield return $"| quiet start | {QuietFile} holds no reading |";
            yield break;
        }
        var last = samples[^1];
        var read = samples.Where(r => Cell(r, "processor_pct") is not null).ToList();
        string Pct(Dictionary<string, string> r) => Dbl(r["processor_pct"]).ToString("0.0", CultureInfo.InvariantCulture);
        var busiest = read.OrderByDescending(r => Dbl(r["processor_pct"])).FirstOrDefault();
        var tail = read.Skip(Math.Max(0, read.Count - 6)).Select(Pct);
        yield return $"| quiet start | {Cell(last, "outcome") ?? "(no outcome)"} after {Cell(last, "elapsed_s") ?? "?"} s; " +
            $"{read.Count.ToString(CultureInfo.InvariantCulture)} readings of the machine's processor time (the sampler's counter), the threshold " +
            $"{Cell(last, "threshold_pct") ?? "?"} % for {Cell(last, "needed") ?? "?"} in a row, bounded at {Cell(last, "bound_s") ?? "?"} s; " +
            $"{(busiest is null ? "none read" : $"the highest {Pct(busiest)} % at {busiest["utc"]} (the busiest then: {Cell(busiest, "top_processes") ?? "not read"})")}; " +
            $"the last: {(read.Count == 0 ? "none" : string.Join(", ", tail) + " %")} |";
    }

    private static string SpreadRow(string kind, List<LegRead> of)
    {
        string Each(IEnumerable<double> values, Func<double, string> f)
        {
            var list = values.ToList();
            var real = list.Where(v => !double.IsNaN(v)).ToList();
            return string.Join(", ", list.Select(f)) + (real.Count > 1 ? $" ({f(real.Min())} to {f(real.Max())})" : "");
        }
        static string Ms(double v) => double.IsNaN(v) ? "-" : v.ToString("0.0", CultureInfo.InvariantCulture);
        // A tick frozen across the window has no wake in it, so the worst tick reads '-' there: the longest with no tick woke stands
        // beside it and says the freeze.
        var pool = string.Join(", ", of.Select(r => r.LongestPoolWaitWords));
        return $"| {kind} | {of.Count} | {Each(of.Select(r => (double)r.MissedSlots), v => v.ToString("0", CultureInfo.InvariantCulture))} | " +
            $"{Each(of.Select(r => r.NoFrame.Ms), Ms)} | {Each(of.Select(r => r.TicksByWake.Max), Ms)} | {Each(of.Select(r => r.NoTick.Ms), Ms)} | {pool} |";
    }

    private static LegRead Read(string root, string dir)
    {
        var folder = Path.GetFileName(dir);
        var legRow = Files.ReadCsv(Path.Combine(dir, "leg.csv")).FirstOrDefault();
        var kind = legRow?["leg"] ?? KindOf(folder);
        var r = new LegRead(dir, folder, kind, Legs.All.FirstOrDefault(l => l.Name == kind))
        {
            Place = legRow is not null && Cell(legRow, "run") is { } run ? (int)Long(run) : null,
            SortKey = (legRow?["started_utc"] ?? "~") + "|" + folder,
            Outcome = legRow is null ? "(no leg row)" : legRow["outcome"] + (legRow["words"].Length > 0 ? ": " + legRow["words"] : ""),
            // The warm-up by its row's label; a folder whose row is gone, by the folder's own label.
            Warmup = legRow is not null ? Cell(legRow, "place_label") == "warmup" : folder.EndsWith("-warmup", StringComparison.Ordinal),
            PlaceNote = legRow is not null ? Cell(legRow, "place_note") ?? "" : "",
        };
        if (r.Spec is null)
        {
            r.Missing = $"(not a leg this probe knows: '{kind}')";
            return r;
        }
        var victim = Files.ReadCsv(Path.Combine(dir, "victim.csv")).FirstOrDefault();
        var burner = Files.ReadCsv(Path.Combine(dir, "burner.csv")).FirstOrDefault();
        var node = Files.ReadCsv(Path.Combine(dir, NodeRun.File)).FirstOrDefault();
        if (victim is null || (r.Spec.Threads > 0 && burner is null) || (r.Spec.IsNode && node is null))
        {
            r.Missing = "(no files: " + r.Outcome + ")";
            return r;
        }
        var windowStart = Long(victim["window_start_mono_ns"]);
        var windowEnd = Long(victim["window_end_mono_ns"]);
        var from = windowStart;
        var to = windowEnd;
        long? burnerStop = null;
        if (burner is not null)
        {
            var ready = Long(burner["ready_mono_ns"]);
            burnerStop = Long(burner["stop_mono_ns"]);
            from = Math.Max(from, ready);
            to = Math.Min(to, burnerStop.Value);
            r.LeadFrom = ready;
            // Where the claim is not made (off Windows), the class asked is said as not claimed, never paired with a read as if it were.
            r.BurnerClass = burner["class_as_told"] == "not told"
                ? $"{burner["class_asked"]}, not claimed (Windows only) / {burner["class_read"]} (base {burner["base_read"]})"
                : $"{burner["class_asked"]} / claim read {burner["claim_read_back"]}, {burner["class_read"]} (base {burner["base_read"]})";
            r.BurnerThreads = burner["threads"];
            r.BurnerLife = burner["processors_used"];
            r.BurnerWindow = WindowUse(burner, windowStart, windowEnd);
            (r.Pin, r.PinWords) = PinOf(burner);
        }
        long? nodeStop = null;
        if (node is not null)
        {
            // The node was at work before the victim's window was made: the window is cut to its time at work, and its lead (burning
            // beside the victim's frames before the window) is from the victim's ready, or from the burner's where one burns too.
            var atWork = Long(node["at_work_mono_ns"]);
            nodeStop = Long(node["stop_mono_ns"]);
            if (atWork > 0)
                from = Math.Max(from, atWork);
            if (nodeStop > 0)
                to = Math.Min(to, nodeStop.Value);
            if (burner is null)
                r.LeadFrom = Long(victim["ready_mono_ns"]);
            r.Node = NodeRead.Of(dir, node, windowStart, windowEnd);
        }
        var mode = victim["class_mode"];
        var how = mode == "keep" ? ", kept as started"
            : victim["class_as_told"] == "not told" ? (mode.StartsWith("claim-", StringComparison.Ordinal) ? $", {mode[6..]} not claimed (Windows only)" : ", not set (Windows only)")
            : mode.StartsWith("claim-", StringComparison.Ordinal) ? $", claimed {mode[6..]} through the product's platform (claim read {Cell(victim, "claim_read_back") ?? "not recorded"})"
            : ", set Normal";
        r.VictimClass = $"{victim["class_at_start"]} (base {victim["base_at_start"]}) / {victim["class_read"]} (base {victim["base_read"]}){how}";
        r.Claimed = victim["class_as_told"] == "yes";
        r.VictimWindow = WindowUse(victim, windowStart, windowEnd);
        r.VictimLife = victim["process_cpu_ms"];
        r.TimerAtStart = TimerMs(victim, "timer_current_100ns_at_window_start");
        r.TimerAtEnd = TimerMs(victim, "timer_current_100ns_at_window_end");
        if (to <= from)
        {
            r.Missing = "(no window: the burner burned outside the victim's window; " + r.Outcome + ")";
            return r;
        }
        r.From = from;
        r.To = to;
        r.WindowS = (to - from) / 1e9;

        var frames = Files.ReadCsv(Path.Combine(dir, "frames.csv")).Select(f => new Frame(
            Long(f["due_mono_ns"]), Long(f["start_mono_ns"]), Dbl(f["lateness_ms"]), Dbl(f["work_wall_ms"]),
            f["work_cpu_ms"].Length == 0 ? double.NaN : Dbl(f["work_cpu_ms"]), (int)Long(f["missed_after"]))).ToList();
        var slots = new List<(long Due, bool Frame)>();
        foreach (var f in frames)
        {
            slots.Add((f.Due, true));
            for (var k = 1; k <= f.MissedAfter; k++)
                slots.Add((f.Due + k * Victim.FramePeriodNs, false));
        }
        for (var i = 0; i + 1 < slots.Count; i++)
        {
            if (Math.Abs(slots[i + 1].Due - slots[i].Due - Victim.FramePeriodNs) > 2)
                r.SlotBreaks++;
        }
        (r.FrameSlots, r.MissedSlots) = SlotsIn(slots, from, to);
        var dones = frames.Select(f => f.Done).OrderBy(d => d).ToList();
        r.NoFrame = Longest(from, to, dones);

        var lateByDue = new List<double>();
        var workWall = 0.0;
        var workCpu = 0.0;
        var cpuRead = true;
        foreach (var f in frames.Where(f => f.Due >= from && f.Due < to))
        {
            lateByDue.Add(f.LatenessMs);
            r.DueMissed += f.MissedAfter;
            workWall += f.WorkWallMs;
            if (double.IsNaN(f.WorkCpuMs))
                cpuRead = false;
            else
                workCpu += f.WorkCpuMs;
        }
        r.DueFrames = lateByDue.Count;
        r.LatenessByDue = Stats.Of(lateByDue);
        r.WorkWallMs = workWall;
        r.WorkCpuMs = cpuRead ? workCpu : double.NaN;
        r.WorkRatio = cpuRead && workCpu > 0 ? workWall / workCpu : double.NaN;
        r.LatenessByStart = Stats.Of(frames.Where(f => f.Start >= from && f.Start < to).Select(f => f.LatenessMs).ToList());

        var ticks = Files.ReadCsv(Path.Combine(dir, "ticks.csv")).Select(t => (Due: Long(t["due_mono_ns"]), Woke: Long(t["woke_mono_ns"]),
            Over: Dbl(t["overshoot_ms"]), Pending: t["pool_still_pending"] == "1")).ToList();
        var byWake = ticks.Where(t => t.Woke >= from && t.Woke < to).ToList();
        r.TicksByWake = Stats.Of(byWake.Select(t => t.Over).ToList());
        r.PendingByWake = byWake.Count(t => t.Pending);
        r.TicksByDue = Stats.Of(ticks.Where(t => t.Due >= from && t.Due < to).Select(t => t.Over).ToList());
        var wakes = ticks.Select(t => t.Woke).OrderBy(w => w).ToList();
        r.NoTick = Longest(from, to, wakes);

        var pool = Files.ReadCsv(Path.Combine(dir, "pool.csv")).Select(p => (Queued: Long(p["queued_mono_ns"]), Started: Long(p["started_mono_ns"]),
            Wait: Dbl(p["dispatch_ms"]))).ToList();
        var queuedIn = pool.Where(p => p.Queued >= from && p.Queued < to).ToList();
        var ran = queuedIn.Where(p => p.Started < to).ToList();
        r.Dispatch = Stats.Of(ran.Select(p => p.Wait).ToList());
        r.PoolRun = ran.Count;
        r.PoolQueued = queuedIn.Count;
        var busyStop = burnerStop ?? nodeStop;
        var busy = burnerStop is not null ? "the burner" : "the node";
        string RanAfter(long started) => busyStop is { } stop && started >= stop
            ? $"it ran {Rows.Ms(started - stop)} ms after {busy} stopped"
            : $"it ran {Rows.Ms(started - to)} ms after the window's end" + (busyStop is null ? "" : $", {busy} still burning");
        foreach (var p in queuedIn.Where(p => p.Started >= to))
            r.Pending.Add((p.Wait, RanAfter(p.Started)));
        // A tick that queued an item (the item was not pending at it) whose run no pool row records: the item never ran while the victim
        // waited for it, so its wait is at least the time from its queue to the end of that wait (the window's end where it is not stamped).
        var queuedRuns = pool.Select(p => p.Queued).ToHashSet();
        var waitEnd = Cell(victim, "pool_wait_end_mono_ns") is { } e ? Long(e) : (long?)null;
        string NeverRan() => waitEnd is null
            ? "it never ran while the victim recorded (to the window's end; the victim's wait for it is not stamped in this run)"
            : "it never ran while the victim waited for it";
        foreach (var t in byWake.Where(t => !t.Pending && !queuedRuns.Contains(t.Woke)))
        {
            r.PoolQueued++;
            r.Pending.Add((((waitEnd ?? to) - t.Woke) / 1e6, NeverRan()));
        }
        // An item queued before the window (in the lead, or before the burner was ready) and not yet run at the window's start held the
        // window from its start: it is the window's, apart from the items queued in it, with its whole wait and the part of it before the
        // window said. While it had still not run at the window's end its wait is a lower bound.
        foreach (var p in pool.Where(p => p.Queued < from && p.Started >= from))
        {
            var before = $"queued {Rows.Ms(from - p.Queued)} ms before the window's start";
            r.PendingAtStart.Add(p.Started < to
                ? new PoolWait(p.Wait, false, $"{before}; it ran {Rows.Ms(p.Started - from)} ms into the window")
                : new PoolWait(p.Wait, true, $"{before}; still waiting at the window's end; {RanAfter(p.Started)}"));
        }
        foreach (var t in ticks.Where(t => t.Woke < from && !t.Pending && !queuedRuns.Contains(t.Woke)))
            r.PendingAtStart.Add(new PoolWait(((waitEnd ?? to) - t.Woke) / 1e6, true, $"queued {Rows.Ms(from - t.Woke)} ms before the window's start; {NeverRan()}"));

        r.Others(Files.ReadCsv(Path.Combine(dir, ProcessesFile)), File.Exists(Path.Combine(dir, ProcessesFile)), victim, from, to,
            node is not null && Cell(node, "pid") is { } nodePid ? nodePid : null);

        if ((burner is not null || node is not null) && r.LeadFrom < from)
        {
            r.LeadS = (from - r.LeadFrom) / 1e9;
            (r.LeadFrameSlots, r.LeadMissed) = SlotsIn(slots, r.LeadFrom, from);
            r.LeadNoFrame = Longest(r.LeadFrom, from, dones);
            r.LeadTicks = wakes.Count(w => w >= r.LeadFrom && w < from);
            r.LeadNoTick = Longest(r.LeadFrom, from, wakes);
        }
        else if (burner is not null || node is not null)
        {
            r.LeadWords = $"none: {(burner is not null ? "the burner" : "the node's victim")} was ready {((r.LeadFrom - windowStart) / 1e9).ToString("0.000", CultureInfo.InvariantCulture)} s into the victim's window, where the window starts";
        }
        r.Readable = true;
        return r;
    }

    public const string ProcessesFile = "machine.processes.csv";
    public const string NodeThreads = NodeRun.ThreadsFile;

    /// <summary>The burner's pin as its row gives it: "unpinned" (not asked), "pinned" with the logical processors pinned to and left
    /// free, "not pinned" with the words why (and the plan's processors, never said as taken), or "not recorded" for an older probe's row.</summary>
    private static (string Pin, string Words) PinOf(Dictionary<string, string> burner)
    {
        if (!burner.ContainsKey("pin"))
            return ("not recorded", "not recorded (this run's burner wrote no pin)");
        var pin = Cell(burner, "pin") ?? "none";
        var pinned = Cell(burner, "pinned_processors") ?? "";
        var free = Cell(burner, "free_processors") ?? "";
        var words = Cell(burner, "pin_words") ?? "";
        return pin switch
        {
            "none" => ("none", "unpinned"),
            "pinned" => ("pinned", $"pinned to {pinned}, free {free}{(words.Length > 0 ? " (" + words + ")" : "")}"),
            _ => ("not pinned", $"not pinned{(pinned.Length > 0 ? $" (the plan: {pinned}, free {free})" : "")}: {words}"),
        };
    }

    /// <summary>The frame slots and missed slots whose due is in [from, to).</summary>
    private static (int Frames, int Missed) SlotsIn(List<(long Due, bool Frame)> slots, long from, long to)
    {
        var frames = 0;
        var missed = 0;
        foreach (var s in slots.Where(s => s.Due >= from && s.Due < to))
        {
            if (s.Frame)
                frames++;
            else
                missed++;
        }
        return (frames, missed);
    }

    /// <summary>The longest stretch of [from, to) with none of <paramref name="sorted"/> in it, and the whole stretch it belongs to: from
    /// the last event at or before its start to the first at or after its end, either of which may lie outside the range (null where
    /// there is none: before the first event, or not ended while the victim recorded).</summary>
    private static Stretch Longest(long from, long to, List<long> sorted)
    {
        var prev = from;
        var best = new Stretch(0, from, from, null, null);
        foreach (var e in sorted.Where(e => e >= from && e < to))
        {
            if (e - prev > best.Ms * 1e6)
                best = best with { Ms = (e - prev) / 1e6, Start = prev, End = e };
            prev = e;
        }
        if (to - prev > best.Ms * 1e6)
            best = best with { Ms = (to - prev) / 1e6, Start = prev, End = to };
        var before = sorted.LastOrDefault(e => e <= best.Start, long.MinValue);
        var after = sorted.FirstOrDefault(e => e >= best.End, long.MinValue);
        return best with { WholeStart = before == long.MinValue ? null : before, WholeEnd = after == long.MinValue ? null : after };
    }

    /// <summary>The processors a process used between its own stamps at the window's bounds, with how late each stamp was when more than a
    /// millisecond; "not recorded" for a run whose probe stamped no bounds.</summary>
    private static WindowUse WindowUse(Dictionary<string, string> row, long windowStart, long windowEnd)
    {
        if (Cell(row, "cpu_ms_at_window_start") is not { } c0 || Cell(row, "cpu_ms_at_window_end") is not { } c1 ||
            Cell(row, "cpu_stamp_start_mono_ns") is not { } t0 || Cell(row, "cpu_stamp_end_mono_ns") is not { } t1)
            return new WindowUse(double.NaN, "not recorded (this run's probe stamped no window bounds)");
        var span = (Long(t1) - Long(t0)) / 1e6;
        if (span <= 0)
            return new WindowUse(double.NaN, "its stamps span no time");
        var used = (Dbl(c1) - Dbl(c0)) / span;
        var lateStart = (Long(t0) - windowStart) / 1e6;
        var lateEnd = (Long(t1) - windowEnd) / 1e6;
        var words = lateStart > 1 || lateEnd > 1
            ? $" (stamped {lateStart.ToString("0.0", CultureInfo.InvariantCulture)} ms and {lateEnd.ToString("0.0", CultureInfo.InvariantCulture)} ms after the window's bounds)"
            : "";
        return new WindowUse(used, used.ToString("0.00", CultureInfo.InvariantCulture) + words);
    }

    /// <summary>A timer reading in ms; "not recorded" where the run's probe had no such column, "not read" where its cell is empty (off
    /// Windows, or the call failed: the leg's scheduler.csv says which).</summary>
    private static string TimerMs(Dictionary<string, string> row, string column) =>
        !row.ContainsKey(column) ? "not recorded"
        : Cell(row, column) is { } v ? (Long(v) / 1e4).ToString("0.000", CultureInfo.InvariantCulture) + " ms"
        : "not read";

    private static string KindOf(string folder)
    {
        var name = folder.StartsWith("leg-", StringComparison.Ordinal) ? folder[4..] : folder;
        name = name.EndsWith("-warmup", StringComparison.Ordinal) ? name[..^"-warmup".Length] : name;
        return name.Length > 3 && char.IsAsciiDigit(name[0]) && char.IsAsciiDigit(name[1]) && name[2] == '-' ? name[3..] : name;
    }

    /// <summary>A cell by its column, or null where the column is not there (a file an older probe wrote) or the cell is empty.</summary>
    public static string? Cell(Dictionary<string, string> row, string column) => row.TryGetValue(column, out var v) && v.Length > 0 ? v : null;

    public static long Long(string s) => long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);

    public static double Dbl(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
}

internal readonly record struct Frame(long Due, long Start, double LatenessMs, double WorkWallMs, double WorkCpuMs, int MissedAfter)
{
    public long Done => Start + (long)Math.Round(WorkWallMs * 1e6);
}

/// <summary>A stretch with no event: its length inside the range read, its bounds, and the whole stretch's bounds where they are known.</summary>
internal readonly record struct Stretch(double Ms, long Start, long End, long? WholeStart, long? WholeEnd)
{
    public double WholeMs => WholeStart is { } a && WholeEnd is { } b ? (b - a) / 1e6 : double.NaN;
}

internal readonly record struct WindowUse(double Processors, string Words);

/// <summary>A pool item's wait: completed, or a lower bound (<see cref="AtLeast"/>) where the item had still not run when the window ended.</summary>
internal readonly record struct PoolWait(double Ms, bool AtLeast, string Words);

/// <summary>p50, p99 and max of a list, nearest-rank, NaN each when it is empty.</summary>
internal readonly record struct Stats(double P50, double P99, double Max, int Count)
{
    public static Stats Of(List<double> values)
    {
        if (values.Count == 0)
            return new Stats(double.NaN, double.NaN, double.NaN, 0);
        values.Sort();
        double At(double q) => values[Math.Clamp((int)Math.Ceiling(q * values.Count) - 1, 0, values.Count - 1)];
        return new Stats(At(0.50), At(0.99), values[^1], values.Count);
    }
}

internal sealed class LegRead(string dir, string folder, string kind, LegSpec? spec)
{
    public static readonly string[] CsvHeader =
    [
        "folder", "leg", "run", "outcome", "burner_class", "burner_threads", "victim_class", "window_s",
        "slots", "frame_slots", "missed_slots", "missed_per_1000", "slot_breaks", "due_rule_frames", "due_rule_missed",
        "longest_no_frame_done_ms", "longest_no_frame_done_whole_ms", "frames_started",
        "lateness_by_start_p50_ms", "lateness_by_start_p99_ms", "lateness_by_start_max_ms",
        "lateness_by_due_p50_ms", "lateness_by_due_p99_ms", "lateness_by_due_max_ms", "work_wall_ms_sum", "work_cpu_ms_sum", "work_wall_over_cpu",
        "ticks_by_wake", "overshoot_by_wake_p50_ms", "overshoot_by_wake_p99_ms", "overshoot_by_wake_max_ms", "ticks_by_wake_item_pending",
        "longest_no_tick_ms", "ticks_by_due", "overshoot_by_due_p99_ms", "overshoot_by_due_max_ms",
        "pool_queued", "pool_run", "dispatch_p99_ms", "dispatch_max_ms", "pool_pending_at_end", "pool_pending_at_least_ms",
        "lead_s", "lead_frame_slots", "lead_missed_slots", "lead_longest_no_frame_done_ms", "lead_ticks_by_wake", "lead_longest_no_tick_ms",
        "burner_processors_ready_to_stop", "burner_processors_in_window", "victim_processors_in_window", "victim_cpu_ms_life",
        "timer_at_window_start", "timer_at_window_end", "pool_pending_at_start", "pool_longest_wait",
        "place_label", "deadline_side", "burner_pin", "burner_pin_words", "samples_in_window", "others_in_window", "others_in_no_frame",
        "samples_refused_in_window", "samples_refused_in_leg", "sampler_processor_s_in_window", "node_class_asked", "node_class_at_work",
        "node_class_at_end", "node_processors_in_window", "node_processors_by_sampler", "node_groups", "node_fps", "node_decode",
    ];

    public string Dir { get; } = dir;
    public string Folder { get; } = folder;
    public string Kind { get; } = kind;
    public LegSpec? Spec { get; } = spec;
    public int? Place { get; init; }
    public string SortKey { get; init; } = "";
    public string Outcome { get; init; } = "";
    public bool Readable { get; set; }
    public string Missing { get; set; } = "";
    public bool Warmup { get; init; }
    public string PlaceNote { get; init; } = "";
    public string Pin { get; set; } = "none";
    public string PinWords { get; set; } = "-";
    public string SamplesInWindow { get; set; } = "-";
    public string OthersInWindow { get; set; } = "-";
    public string OthersInNoFrame { get; set; } = "-";
    public int RefusedInWindow { get; set; }
    public int RefusedInLeg { get; set; }
    public string SamplesRefused { get; set; } = "-";
    public string SamplerShare { get; set; } = "-";
    public string SamplerSeconds { get; set; } = "";
    public string NodeBySampler { get; set; } = "-";

    /// <summary>The node of a node leg as its rows read, or null for every other leg.</summary>
    public NodeRead? Node { get; set; }

    /// <summary>The victim's loops are the busy side's own deadline (a buy leg).</summary>
    public bool Deadline => Spec?.Deadline == true;

    /// <summary>The deadline process read back the class it was told to claim, with the claim applied (class_as_told "yes").</summary>
    public bool Claimed { get; set; }

    /// <summary>The leg's row in the spread across repeats: its kind, the warm-up apart from it, a whole-core-free leg not pinned apart
    /// from the pinned ones, a buy leg whose class was not claimed apart from the claimed ones, and the claimed buy legs said as the busy
    /// side's own deadline.</summary>
    public string SpreadKey =>
        Warmup ? "warmup " + Kind
        : Spec is { Pin: not "none" } && Pin != "pinned" ? Kind + ", not pinned"
        : Deadline && !Claimed ? Kind + ", not claimed (the busy side's own deadline)"
        : Deadline ? Kind + " (the busy side's own deadline)"
        : Kind;

    public string BurnerClass { get; set; } = "-";
    public string BurnerThreads { get; set; } = "-";
    public string BurnerLife { get; set; } = "-";
    public WindowUse BurnerWindow { get; set; } = new(double.NaN, "-");
    public string VictimClass { get; set; } = "";
    public WindowUse VictimWindow { get; set; } = new(double.NaN, "-");
    public string VictimLife { get; set; } = "";
    public string TimerAtStart { get; set; } = "";
    public string TimerAtEnd { get; set; } = "";

    public long From { get; set; }
    public long To { get; set; }
    public double WindowS { get; set; }
    public int FrameSlots { get; set; }
    public int MissedSlots { get; set; }
    public int SlotBreaks { get; set; }
    public int DueFrames { get; set; }
    public int DueMissed { get; set; }
    public Stretch NoFrame { get; set; } = new(double.NaN, 0, 0, null, null);
    public Stats LatenessByStart { get; set; }
    public Stats LatenessByDue { get; set; }
    public double WorkWallMs { get; set; } = double.NaN;
    public double WorkCpuMs { get; set; } = double.NaN;
    public double WorkRatio { get; set; } = double.NaN;

    public Stats TicksByWake { get; set; }
    public int PendingByWake { get; set; }
    public Stats TicksByDue { get; set; }
    public Stretch NoTick { get; set; } = new(double.NaN, 0, 0, null, null);

    public int PoolQueued { get; set; }
    public int PoolRun { get; set; }
    public Stats Dispatch { get; set; }
    public List<(double AtLeastMs, string Words)> Pending { get; } = [];
    public List<PoolWait> PendingAtStart { get; } = [];

    public long LeadFrom { get; set; }
    public double LeadS { get; set; } = double.NaN;
    public string LeadWords { get; set; } = "none: no burner";
    public int LeadFrameSlots { get; set; }
    public int LeadMissed { get; set; }
    public Stretch LeadNoFrame { get; set; } = new(double.NaN, 0, 0, null, null);
    public int LeadTicks { get; set; }
    public Stretch LeadNoTick { get; set; } = new(double.NaN, 0, 0, null, null);

    public int Slots => FrameSlots + MissedSlots;

    /// <summary>The leg's cell: its place in the order where the run has one, its name, and its outcome beside it whenever the leg is not
    /// ok, so a broken leg's numbers are never read as a whole leg's.</summary>
    public string Label => (Place is { } p ? "#" + p.ToString("00", CultureInfo.InvariantCulture) + " " : "") + Kind +
        (Warmup ? " (the warm-up)" : "") + (Deadline ? " (the busy side's own deadline)" : "") + (Outcome is "ok" or "" ? "" : $" ({Outcome})");

    public double MissedPer1000 => Slots > 0 ? MissedSlots * 1000.0 / Slots : double.NaN;

    /// <summary>The longest pool wait the window holds, over the items run, those still pending at its end and those pending at its
    /// start: the larger of the longest completed wait and the largest lower bound, marked >= only when the lower bound is the larger
    /// (or as large), so a pending item never hides a longer completed wait and a completed one never hides a pending one; "-" for none.</summary>
    public string LongestPoolWaitWords
    {
        get
        {
            var done = PendingAtStart.Where(p => !p.AtLeast).Select(p => p.Ms).Append(Dispatch.Max).Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Max();
            var bound = PendingAtStart.Where(p => p.AtLeast).Select(p => p.Ms).Concat(Pending.Select(p => p.AtLeastMs)).DefaultIfEmpty(double.NaN).Max();
            if (double.IsNaN(bound))
                return F(done, "0.0");
            return double.IsNaN(done) || bound >= done ? ">= " + F(bound, "0.0") : F(done, "0.0");
        }
    }

    public string FramesRow() =>
        $"| {Label} | {F(WindowS, "0.0")} s | {FrameSlots} / {MissedSlots} of {Slots}{(SlotBreaks > 0 ? $" (the slots break {SlotBreaks} times: counted as the rows give them)" : "")} | " +
        $"{F(MissedPer1000, "0.0")} | {DueFrames} / {DueMissed} | {StretchWords(NoFrame, From)} | {LatenessByStart.Count} | " +
        $"{F(LatenessByStart.P50)} / {F(LatenessByStart.P99)} / {F(LatenessByStart.Max)} ms | {F(LatenessByDue.P50)} / {F(LatenessByDue.P99)} / {F(LatenessByDue.Max)} ms | " +
        $"{F(WorkRatio, "0.00")} ({F(WorkWallMs, "0")} / {F(WorkCpuMs, "0")} ms) |";

    public string TicksRow() =>
        $"| {Label} | {TicksByWake.Count} | {F(TicksByWake.P50)} / {F(TicksByWake.P99)} / {F(TicksByWake.Max)} ms | " +
        $"{TicksByDue.Count}, {F(TicksByDue.P99)} / {F(TicksByDue.Max)} ms | {StretchWords(NoTick, From)} | {PendingByWake} of {TicksByWake.Count} | " +
        $"{PoolRun} / {PoolQueued} | {F(Dispatch.P99)} / {F(Dispatch.Max)} ms | " +
        $"{(Pending.Count == 0 ? "none" : string.Join("; ", Pending.Select(p => $">= {F(p.AtLeastMs, "0.0")} ms ({p.Words})")))} | " +
        $"{(PendingAtStart.Count == 0 ? "none" : string.Join("; ", PendingAtStart.Select(p => $"{(p.AtLeast ? ">= " : "")}{F(p.Ms, "0.0")} ms ({p.Words})")))} |";

    public string ProcessRow() =>
        $"| {Label} | {BurnerClass} | {BurnerThreads} | {BurnerWindow.Words} | {BurnerLife} | {VictimClass} | {VictimWindow.Words} | {VictimLife} ms | {TimerAtStart} / {TimerAtEnd} | {PinWords} |";

    public string LeadRow() =>
        double.IsNaN(LeadS)
            ? $"| {Label} | {LeadWords} | | | | |"
            : $"| {Label} | {F(LeadS, "0.000")} s | {LeadFrameSlots} / {LeadMissed} of {LeadFrameSlots + LeadMissed} | {StretchWords(LeadNoFrame, LeadFrom)} | " +
              $"{LeadTicks} | {StretchWords(LeadNoTick, LeadFrom)} |";

    /// <summary>"16600.8 ms (+3.399 s to +20.000 s; whole 17603.6 ms)": the stretch inside the range from its start, then the whole one.</summary>
    private static string StretchWords(Stretch s, long origin)
    {
        if (double.IsNaN(s.Ms))
            return "-";
        string At(long t) => "+" + ((t - origin) / 1e9).ToString("0.000", CultureInfo.InvariantCulture) + " s";
        var whole = s.WholeStart is null ? "whole: nothing before it"
            : s.WholeEnd is null ? "whole: not ended while the victim recorded"
            : Math.Abs(s.WholeMs - s.Ms) < 0.05 ? "" : $"whole {F(s.WholeMs, "0.0")} ms";
        return $"{F(s.Ms, "0.0")} ms ({At(s.Start)} to {At(s.End)}{(whole.Length > 0 ? "; " + whole : "")})";
    }

    public string[] Csv() =>
    [
        Folder, Kind, Place?.ToString(CultureInfo.InvariantCulture) ?? "", Readable ? Outcome : Missing, BurnerClass, BurnerThreads, VictimClass,
        F(WindowS, "0.000"),
        I(Slots), I(FrameSlots), I(MissedSlots), F(MissedPer1000, "0.00"), I(SlotBreaks), I(DueFrames), I(DueMissed),
        F(NoFrame.Ms, "0.0"), F(NoFrame.WholeMs, "0.0"), I(LatenessByStart.Count),
        F(LatenessByStart.P50), F(LatenessByStart.P99), F(LatenessByStart.Max),
        F(LatenessByDue.P50), F(LatenessByDue.P99), F(LatenessByDue.Max), F(WorkWallMs, "0.0"), F(WorkCpuMs, "0.0"), F(WorkRatio, "0.000"),
        I(TicksByWake.Count), F(TicksByWake.P50), F(TicksByWake.P99), F(TicksByWake.Max), I(PendingByWake),
        F(NoTick.Ms, "0.0"), I(TicksByDue.Count), F(TicksByDue.P99), F(TicksByDue.Max),
        I(PoolQueued), I(PoolRun), F(Dispatch.P99), F(Dispatch.Max), I(Pending.Count), Pending.Count > 0 ? F(Pending.Max(p => p.AtLeastMs), "0.0") : "",
        F(LeadS, "0.000"), double.IsNaN(LeadS) ? "" : I(LeadFrameSlots), double.IsNaN(LeadS) ? "" : I(LeadMissed), F(LeadNoFrame.Ms, "0.0"),
        double.IsNaN(LeadS) ? "" : I(LeadTicks), F(LeadNoTick.Ms, "0.0"),
        BurnerLife, F(BurnerWindow.Processors, "0.00"), F(VictimWindow.Processors, "0.00"), VictimLife, TimerAtStart, TimerAtEnd,
        I(PendingAtStart.Count), LongestPoolWaitWords,
        Warmup ? "warmup" : "", Deadline ? "yes" : "no", Pin, PinWords, SamplesInWindow, OthersInWindow, OthersInNoFrame,
        I(RefusedInWindow), I(RefusedInLeg), SamplerSeconds, Node?.Asked ?? "", Node?.AtWork ?? "", Node?.AtEnd ?? "",
        Node is null ? "" : F(Node.Window.Processors, "0.00"), NodeBySampler is "-" ? "" : NodeBySampler, Node?.Groups ?? "", Node?.Fps ?? "", Node?.Decode ?? "",
    ];

    public string NodeRow() =>
        Node is not { } n ? $"| {Label} | | | | | | | |"
        : $"| {Label} | {n.ClassWords} | {n.Work} | {n.Window.Words} | {NodeBySampler} | {n.Groups} | {n.Outputs} | {n.Decoders} |";

    /// <summary>Joins the machine sampler's every-process rows to the window and to its longest stretch with no frame finished, by pid
    /// (a counter's instance name is renumbered as same-named processes start and end, so one pid can be read under more than one name
    /// in a leg and one name can stand for two pids; its time stays one entry, said under every name read). A sample spans its call (its
    /// row's time less its sample_ms, to its time) and counts its share times its overlap: the share the sampler joined by pid across
    /// its own samples where its rows carry one (pid_processors), else the counter's cooked share. A sample whose rows' cooked shares sum
    /// past the machine's logical processors by more than <see cref="Reader.ImpossibleMargin"/> is refused (the counter paired two
    /// processes under one instance name between its collections), counted over the window and over the leg, and never read again. The
    /// refusal is by the cooked shares even where the pid-joined ones are there and sound, so a sample a renumbered instance spoiled is
    /// dropped whole, its pid-joined rows with it: the rule is the same for a run whose sampler wrote no pid-joined share.
    /// From the samples kept: the processes outside the probe's own, the node and Idle with the most processor time in each, at most
    /// three, each with its pid, the highest base priority read and the sampler's note on it; the sampler's own processor-seconds and
    /// share of the window; the node's processors over the window by its pid. A stretch no kept sample overlaps says so. "not
    /// recorded" where the leg holds no such file.</summary>
    public void Others(List<Dictionary<string, string>> rows, bool recorded, Dictionary<string, string> victim, long from, long to, string? nodePid = null)
    {
        if (!recorded)
        {
            SamplesInWindow = "not recorded";
            OthersInWindow = OthersInNoFrame = "not recorded (this run's sampler listed no process rows)";
            SamplesRefused = SamplerShare = "not recorded";
            NodeBySampler = nodePid is null ? "-" : "not recorded";
            return;
        }
        var anchorMono = Reader.Long(victim["anchor_mono_ns"]);
        var anchorUtc = DateTime.Parse(victim["anchor_utc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        var logical = Reader.Cell(victim, "processors") is { } lp ? Reader.Dbl(lp) : Environment.ProcessorCount;
        double Mono(DateTime utc) => anchorMono + (utc - anchorUtc).Ticks * 100.0;
        var joined = rows.Count > 0 && rows[0].ContainsKey("pid_processors");
        var all = rows.Where(r => Reader.Cell(r, "utc") is not null && Reader.Cell(r, "sample_ms") is not null)
            .GroupBy(r => r["utc"] + "|" + r["sample_ms"], StringComparer.Ordinal)
            .Select(g =>
            {
                var end = Mono(DateTime.Parse(g.First()["utc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime());
                var sum = g.Sum(r => Reader.Cell(r, "processors") is { } p ? Reader.Dbl(p) : 0);
                return (Start: end - Reader.Dbl(g.First()["sample_ms"]) * 1e6, End: end, Rows: g.ToList(), Impossible: sum > logical + Reader.ImpossibleMargin);
            })
            .ToList();
        var samples = all.Where(x => !x.Impossible).ToList();
        RefusedInLeg = all.Count(x => x.Impossible);
        RefusedInWindow = all.Count(x => x.Impossible && x.End > from && x.Start < to);
        SamplesRefused = $"{I(RefusedInWindow)} / {I(RefusedInLeg)}";
        static bool Probe(string name) => name.StartsWith("playmap.priorityprobe", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("idle", StringComparison.OrdinalIgnoreCase) || name.Equals("_total", StringComparison.OrdinalIgnoreCase);
        string? Share(Dictionary<string, string> r) => joined ? Reader.Cell(r, "pid_processors") : Reader.Cell(r, "processors");
        double Overlap(double a, double b, (double Start, double End, List<Dictionary<string, string>> Rows, bool Impossible) x) =>
            (Math.Min(b, x.End) - Math.Max(a, x.Start)) / 1e9;
        string Top(double a, double b)
        {
            var over = samples.Where(x => x.End > a && x.Start < b).ToList();
            if (over.Count == 0)
                return "no sample covers it";
            var by = new Dictionary<string, (double Seconds, string Name, int Base, string Note)>(StringComparer.Ordinal);
            foreach (var x in over)
            {
                var overlap = Overlap(a, b, x);
                foreach (var r in x.Rows)
                {
                    if (Reader.Cell(r, "name") is not { } name || Probe(name) || Share(r) is not { } p)
                        continue;
                    var key = Reader.Cell(r, "pid") ?? "?";
                    if (key == nodePid)
                        continue;
                    var seconds = Reader.Dbl(p) * overlap;
                    var bas = Reader.Cell(r, "base_priority") is { } v ? (int)Reader.Dbl(v) : -1;
                    by[key] = by.TryGetValue(key, out var had)
                        ? (had.Seconds + seconds, had.Name.Split(" / ").Contains(name, StringComparer.Ordinal) ? had.Name : had.Name + " / " + name,
                            Math.Max(had.Base, bas), had.Note.Length > 0 ? had.Note : Reader.Cell(r, "note") ?? "")
                        : (seconds, name, bas, Reader.Cell(r, "note") ?? "");
                }
            }
            var top = by.Where(e => e.Value.Seconds > 0).OrderByDescending(e => e.Value.Seconds).ThenBy(e => e.Key, StringComparer.Ordinal).Take(3).ToList();
            var span = over.Count.ToString(CultureInfo.InvariantCulture) + (over.Count == 1 ? " sample" : " samples");
            return top.Count == 0
                ? $"none outside the probe ({span})"
                : string.Join("; ", top.Select(e => $"{e.Value.Name} (pid {e.Key}, base " +
                    $"{(e.Value.Base < 0 ? "not read" : e.Value.Base.ToString(CultureInfo.InvariantCulture))}{(e.Value.Note.Length > 0 ? ", " + e.Value.Note : "")}) " +
                    $"{e.Value.Seconds.ToString("0.00", CultureInfo.InvariantCulture)} processor-s")) + $" ({span})";
        }
        var window = samples.Where(x => x.End > from && x.Start < to).ToList();
        SamplesInWindow = window.Count.ToString(CultureInfo.InvariantCulture);
        OthersInWindow = Top(from, to);
        OthersInNoFrame = double.IsNaN(NoFrame.Ms) ? "-" : Top(NoFrame.Start, NoFrame.End);
        // The sampler's own processor time over the window, from its own rows (its note), with the highest base it read for itself.
        var seconds = 0.0;
        var covered = 0.0;
        var samplerBase = -1;
        var sampled = false;
        foreach (var x in window)
        {
            var overlap = Overlap(from, to, x);
            covered += overlap;
            foreach (var r in x.Rows.Where(r => Reader.Cell(r, "note") == "the sampler"))
            {
                sampled = true;
                if (Share(r) is { } p)
                    seconds += Reader.Dbl(p) * overlap;
                if (Reader.Cell(r, "base_priority") is { } b)
                    samplerBase = Math.Max(samplerBase, (int)Reader.Dbl(b));
            }
        }
        var windowS = (to - from) / 1e9;
        SamplerSeconds = sampled ? seconds.ToString("0.00", CultureInfo.InvariantCulture) : "";
        SamplerShare = window.Count == 0 ? "no sample covers it"
            : !sampled ? "not in its own rows"
            : $"{seconds.ToString("0.00", CultureInfo.InvariantCulture)} processor-s ({(windowS > 0 ? seconds / windowS : double.NaN).ToString("0.000", CultureInfo.InvariantCulture)} of a processor over the window), base {(samplerBase < 0 ? "not read" : samplerBase.ToString(CultureInfo.InvariantCulture))}";
        if (nodePid is not null)
        {
            // The node's share over the samples that carry one for it alone: a pid's row with an empty share (its first row in the
            // sampler's calls, or a time that went back) is seen but read for nothing, and its overlap is not in the divisor.
            var nodeSeconds = 0.0;
            var nodeCovered = 0.0;
            var seen = 0;
            var shared = 0;
            foreach (var x in window)
            {
                var overlap = Overlap(from, to, x);
                foreach (var r in x.Rows.Where(r => Reader.Cell(r, "pid") == nodePid))
                {
                    seen++;
                    if (Share(r) is { } p)
                    {
                        shared++;
                        nodeSeconds += Reader.Dbl(p) * overlap;
                        nodeCovered += overlap;
                    }
                }
            }
            NodeBySampler = window.Count == 0 ? "no sample covers it"
                : seen == 0 ? $"pid {nodePid} in no sample kept ({I(window.Count)})"
                : shared == 0 ? $"pid {nodePid} in {I(seen)} of {I(window.Count)} samples kept, none with a share"
                : $"{(nodeCovered > 0 ? nodeSeconds / nodeCovered : double.NaN).ToString("0.00", CultureInfo.InvariantCulture)} (pid {nodePid}, {I(shared)} of {I(window.Count)} samples kept, over {nodeCovered.ToString("0.0", CultureInfo.InvariantCulture)} s of the window)";
        }
    }

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static string F(double v, string format = "0.000") => double.IsNaN(v) ? "-" : v.ToString(format, CultureInfo.InvariantCulture);
}

/// <summary>A node leg's node as its rows read it (node.csv, node.threads.csv): its class asked and read back, its work, its processors
/// over the window from its stamps, its threads by group over the same stamps (a thread there only at the end was started in the
/// window and counts from nothing; one gone by the end is left out and counted), and STATE's output and decode figures at both bounds,
/// as STATE counts them.</summary>
internal sealed class NodeRead
{
    public string Asked { get; private init; } = "";
    public string AtWork { get; private init; } = "";
    public string AtEnd { get; private init; } = "";
    public string ClassWords { get; private init; } = "";
    public string Work { get; private init; } = "";
    public WindowUse Window { get; private init; } = new(double.NaN, "-");
    public string Groups { get; private init; } = "";
    public string Fps { get; private init; } = "";
    public string Outputs { get; private init; } = "";
    public string Decode { get; private init; } = "";
    public string Decoders { get; private init; } = "";

    public static NodeRead Of(string dir, Dictionary<string, string> node, long windowStart, long windowEnd)
    {
        string C(string column) => Reader.Cell(node, column) ?? "-";
        var window = ReaderWindow(node, windowStart, windowEnd);
        var groups = "not recorded (no node.threads.csv)";
        var threads = Files.ReadCsv(Path.Combine(dir, NodeRun.ThreadsFile));
        if (threads.Count > 0 && Reader.Cell(node, "cpu_stamp_start_mono_ns") is { } t0 && Reader.Cell(node, "cpu_stamp_end_mono_ns") is { } t1)
        {
            var span = (Reader.Long(t1) - Reader.Long(t0)) / 1e6;
            var by = new SortedDictionary<string, double>(StringComparer.Ordinal);
            var gone = 0;
            foreach (var t in threads)
            {
                if (Reader.Cell(t, "cpu_ms_at_window_end") is not { } end)
                {
                    gone++;
                    continue;
                }
                var start = Reader.Cell(t, "cpu_ms_at_window_start") is { } s ? Reader.Dbl(s) : 0;
                var g = Reader.Cell(t, "group") ?? "other";
                by[g] = by.GetValueOrDefault(g) + (Reader.Dbl(end) - start);
            }
            groups = span <= 0 ? "its stamps span no time"
                : string.Join("; ", by.Where(e => e.Value > 0).OrderByDescending(e => e.Value).ThenBy(e => e.Key, StringComparer.Ordinal)
                      .Select(e => $"{e.Key} {(e.Value / span).ToString("0.00", CultureInfo.InvariantCulture)}"))
                  + (gone > 0 ? $" ({gone.ToString(CultureInfo.InvariantCulture)} thread{(gone == 1 ? "" : "s")} gone by the end, not counted)" : "");
        }
        var claim = Reader.Cell(node, "state_claim_read_back") is { } rb ? $"STATE's claim {rb}{(Reader.Cell(node, "state_claim_applied") is { } a ? ", applied " + a : "")}" : "STATE's claim not read";
        // MMCSS raises a thread into the realtime band for its share, above the controller's stamps and the sampler: said where recorded.
        if (Reader.Cell(node, "state_render_mmcss") is { } rm)
            claim += $"; MMCSS render {rm}, present {Reader.Cell(node, "state_present_mmcss") ?? "-"}";
        var atWork = $"{C("class_at_work")} (base {C("base_at_work")})";
        var atEnd = $"{C("class_at_end")} (base {C("base_at_end")})";
        return new NodeRead
        {
            Asked = C("class_asked"),
            AtWork = atWork,
            AtEnd = atEnd,
            ClassWords = $"{C("class_asked")} / {atWork} / {atEnd} ({claim})",
            Work = $"{C("clips")} clips ({C("clip_files")}), one {C("output_size")} output each; {C("renderer")}" +
                (Reader.Cell(node, "words") is { } w ? $"; {w}" : "") + (Reader.Cell(node, "controller") is { } c ? $"; {c}" : ""),
            Window = window,
            Groups = groups,
            Fps = $"{C("fps_at_start")} / {C("fps_at_end")}",
            Outputs = $"{C("fps_at_start")} / {C("fps_at_end")} fps; dropped {C("output_dropped_at_start")} / {C("output_dropped_at_end")}; " +
                $"missed vblanks {C("missed_vblanks_at_start")} / {C("missed_vblanks_at_end")}; frame p95 {C("frame_ms_p95_at_end")} ms",
            Decode = $"dropped {C("decode_dropped_at_start")} / {C("decode_dropped_at_end")}; late {C("decode_late_at_start")} / {C("decode_late_at_end")}; p95 {C("decode_ms_p95_at_end")} ms",
            Decoders = $"{C("decoders_at_end")} open: dropped {C("decode_dropped_at_start")} / {C("decode_dropped_at_end")}; late {C("decode_late_at_start")} / {C("decode_late_at_end")}; decode p95 {C("decode_ms_p95_at_end")} ms",
        };
    }

    /// <summary>The node's processors between its stamps at the window's bounds (the controller's, at the highest thread priority), with
    /// how late each stamp was when more than a millisecond.</summary>
    private static WindowUse ReaderWindow(Dictionary<string, string> node, long windowStart, long windowEnd)
    {
        if (Reader.Cell(node, "cpu_ms_at_window_start") is not { } c0 || Reader.Cell(node, "cpu_ms_at_window_end") is not { } c1 ||
            Reader.Cell(node, "cpu_stamp_start_mono_ns") is not { } t0 || Reader.Cell(node, "cpu_stamp_end_mono_ns") is not { } t1)
            return new WindowUse(double.NaN, "not stamped");
        var span = (Reader.Long(t1) - Reader.Long(t0)) / 1e6;
        if (span <= 0)
            return new WindowUse(double.NaN, "its stamps span no time");
        var used = (Reader.Dbl(c1) - Reader.Dbl(c0)) / span;
        var lateStart = (Reader.Long(t0) - windowStart) / 1e6;
        var lateEnd = (Reader.Long(t1) - windowEnd) / 1e6;
        var words = lateStart > 1 || lateEnd > 1
            ? $" (stamped {lateStart.ToString("0.0", CultureInfo.InvariantCulture)} ms and {lateEnd.ToString("0.0", CultureInfo.InvariantCulture)} ms after the window's bounds)"
            : "";
        return new WindowUse(used, used.ToString("0.00", CultureInfo.InvariantCulture) + words);
    }
}
