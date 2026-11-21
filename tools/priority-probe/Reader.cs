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
/// apart as pending at the window's start (its wait a lower bound while it had still not run at the window's end).
/// Percentiles are nearest-rank. Every number is a reading, never a pass mark.</summary>
internal static class Reader
{
    public const string SummaryMd = "summary.md";
    public const string SummaryCsv = "summary.csv";

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
        md.Add("| Leg | Burner class asked / read back | Burner threads | Burner processors in the window (its stamps) | Burner processors, ready to stop | Victim class at start / read | Victim processors in the window (its stamps) | Victim processor time, its life | Timer resolution at the window's start / end |");
        md.Add("|---|---|---|---|---|---|---|---|---|");
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
        md.Add("#### Each leg kind across its repeats (each run's figure in the run's order, then the lowest and the highest; nothing is averaged)");
        md.Add("");
        md.Add("| Leg kind | Runs | Missed slots | Longest with no frame finished (ms, in the window) | Worst tick by wake (ms) | Longest with no tick woke (ms, in the window) | Longest pool wait (ms; >= a lower bound) |");
        md.Add("|---|---|---|---|---|---|---|");
        foreach (var kind in Legs.All.Select(l => l.Name))
        {
            var of = runs.Where(r => r.Readable && r.Kind == kind).ToList();
            if (of.Count > 0)
                md.Add(SpreadRow(kind, of));
        }
        md.Add("");
        var calibration = Files.ReadCsv(Path.Combine(root, Calibration.File)).FirstOrDefault();
        md.Add(calibration is null ? "No calibration row: the frame's work is unknown." :
            $"The machine: {calibration["os"]}, {calibration["processors"]} processors; the frame's work {calibration["iterations"]} iterations, " +
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
            "what the burner's class costs a desk at Normal with every processor busy; high-n1 against normal-n1 the same with one processor " +
            "left unburned, which is each of the product's Compute and HAP pools' own width (Compute.cs 102, HapWorkers.cs 92) and not a " +
            "floor the node holds: nothing caps their sum. high-n-runner is the victim at the class the runner started it at, not a desk's. " +
            "The machine sampler's rows thin out or stop while a burner holds the processors, as the runner's own processes are held. " +
            "Synthetic busy threads on the machine named above, not a show machine (docs/round2/CARRYOVER.md, fix-ci-priority-measure and " +
            "fix-priority-probe-reader).");
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
        };
        if (r.Spec is null)
        {
            r.Missing = $"(not a leg this probe knows: '{kind}')";
            return r;
        }
        var victim = Files.ReadCsv(Path.Combine(dir, "victim.csv")).FirstOrDefault();
        var burner = Files.ReadCsv(Path.Combine(dir, "burner.csv")).FirstOrDefault();
        if (victim is null || (r.Spec.Threads > 0 && burner is null))
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
        }
        var how = victim["class_mode"] == "keep" ? ", kept as started" : victim["class_as_told"] == "not told" ? ", not set (Windows only)" : ", set Normal";
        r.VictimClass = $"{victim["class_at_start"]} (base {victim["base_at_start"]}) / {victim["class_read"]} (base {victim["base_read"]}){how}";
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
        string RanAfter(long started) => burnerStop is { } stop && started >= stop
            ? $"it ran {Rows.Ms(started - stop)} ms after the burner stopped"
            : $"it ran {Rows.Ms(started - to)} ms after the window's end" + (burnerStop is null ? "" : ", the burner still burning");
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

        if (burner is not null && r.LeadFrom < from)
        {
            r.LeadS = (from - r.LeadFrom) / 1e9;
            (r.LeadFrameSlots, r.LeadMissed) = SlotsIn(slots, r.LeadFrom, from);
            r.LeadNoFrame = Longest(r.LeadFrom, from, dones);
            r.LeadTicks = wakes.Count(w => w >= r.LeadFrom && w < from);
            r.LeadNoTick = Longest(r.LeadFrom, from, wakes);
        }
        else if (burner is not null)
        {
            r.LeadWords = $"none: the burner was ready {((r.LeadFrom - windowStart) / 1e9).ToString("0.000", CultureInfo.InvariantCulture)} s into the victim's window, where the window starts";
        }
        r.Readable = true;
        return r;
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
        return name.Length > 3 && char.IsAsciiDigit(name[0]) && char.IsAsciiDigit(name[1]) && name[2] == '-' ? name[3..] : name;
    }

    /// <summary>A cell by its column, or null where the column is not there (a file an older probe wrote) or the cell is empty.</summary>
    public static string? Cell(Dictionary<string, string> row, string column) => row.TryGetValue(column, out var v) && v.Length > 0 ? v : null;

    private static long Long(string s) => long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static double Dbl(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
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
    public string Label => (Place is { } p ? "#" + p.ToString("00", CultureInfo.InvariantCulture) + " " : "") + Kind + (Outcome is "ok" or "" ? "" : $" ({Outcome})");

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
        $"| {Label} | {BurnerClass} | {BurnerThreads} | {BurnerWindow.Words} | {BurnerLife} | {VictimClass} | {VictimWindow.Words} | {VictimLife} ms | {TimerAtStart} / {TimerAtEnd} |";

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
    ];

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static string F(double v, string format = "0.000") => double.IsNaN(v) ? "-" : v.ToString(format, CultureInfo.InvariantCulture);
}
