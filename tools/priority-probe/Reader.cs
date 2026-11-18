using System.Globalization;
using System.Text;

namespace PlayMap.PriorityProbe;

/// <summary>The reader: from each leg's files, never from memory, the table the owner reads. A leg's window is the victim's own
/// window cut to the time its burner was burning (from the burner's ready to its stop, both on the machine's monotonic clock), so a
/// burner that came up late shortens the window and never counts the quiet part; a frame or tick is in it by its due time, a pool item
/// by its queue time. Percentiles are nearest-rank. Every number is a reading, never a pass mark; a ratio is the leg's figure over the
/// baseline leg's.</summary>
internal static class Reader
{
    public static int Run(string root)
    {
        var read = Legs.All.Select(l => Read(root, l)).ToList();
        var baseline = read.FirstOrDefault(r => r.Leg.Name == "baseline" && r.Readable);
        var md = new List<string>
        {
            "### What a saturating process costs a Normal-class process doing real work (a measurement, never a pass mark)",
            "",
            "| Leg | Burner class asked / read back | Burner threads / processors used | Victim class at start / read | Window | Frame lateness p50 / p99 / max | Missed periods | Work wall / processor (frames, wall / processor summed) | Tick overshoot p99 / max | Pool dispatch p99 / max | Ticks with the item still pending |",
            "|---|---|---|---|---|---|---|---|---|---|---|",
        };
        foreach (var r in read)
            md.Add(r.Readable ? r.Row() : $"| {r.Leg.Name} | {r.Missing} | | | | | | | | | |");
        md.Add("");
        md.Add("As a ratio to the baseline leg (the leg's figure over the baseline's; missed periods per thousand expected periods, beside the baseline's):");
        md.Add("");
        md.Add("| Leg | Lateness p50 | Lateness p99 | Lateness max | Missed per 1000 | Overshoot p99 | Overshoot max | Dispatch p99 | Dispatch max |");
        md.Add("|---|---|---|---|---|---|---|---|---|");
        foreach (var r in read.Where(r => r.Readable))
            md.Add(r.RatioRow(baseline));
        md.Add("");
        var calibration = Files.ReadCsv(Path.Combine(root, Calibration.File)).FirstOrDefault();
        md.Add(calibration is null ? "No calibration row: the frame's work is unknown." :
            $"The machine: {calibration["os"]}, {calibration["processors"]} processors; the frame's work {calibration["iterations"]} iterations, " +
            $"{calibration["median_ms"]} ms median wall time (p10 {calibration["p10_ms"]}, p90 {calibration["p90_ms"]}) when calibrated before the legs.");
        md.Add("");
        md.Add("Read beside one another: high-n against normal-n is what the burner's class costs a desk at Normal with every processor busy; " +
            "high-n1 against normal-n1 the same with one processor left free (the width the product's Compute pool and HAP workers take); " +
            "high-n-runner is the victim at the class the runner started it at (its row says which), not a desk's. On Windows a frame's " +
            "processor time is counted in clock ticks, so the work's wall over processor time is read from the window's sums, never a frame's. " +
            "The machine sampler's rows thin out or stop while a burner holds the processors, as the runner's own processes are held: for those " +
            "windows the burner's processors used and the victim's rows are the readings. Synthetic busy threads on the machine named above, " +
            "not a show machine (docs/round2/CARRYOVER.md, fix-ci-priority-measure).");
        md.Add("");
        File.WriteAllLines(Path.Combine(root, "summary.md"), md, new UTF8Encoding(false));
        Files.WriteCsv(Path.Combine(root, "summary.csv"), LegRead.CsvHeader, read.Select(r => r.Csv(baseline)));
        foreach (var line in md)
            Program.Say(line);
        return read.Any(r => r.Readable) ? 0 : 1;
    }

    private static LegRead Read(string root, LegSpec leg)
    {
        var dir = Path.Combine(root, "leg-" + leg.Name);
        var r = new LegRead(leg);
        if (!Directory.Exists(dir))
        {
            r.Missing = "(not run)";
            return r;
        }
        var victim = Files.ReadCsv(Path.Combine(dir, "victim.csv")).FirstOrDefault();
        var burner = Files.ReadCsv(Path.Combine(dir, "burner.csv")).FirstOrDefault();
        var legRow = Files.ReadCsv(Path.Combine(dir, "leg.csv")).FirstOrDefault();
        r.Outcome = legRow is null ? "(no leg row)" : legRow["outcome"] + (legRow["words"].Length > 0 ? ": " + legRow["words"] : "");
        if (victim is null || (leg.Threads > 0 && burner is null))
        {
            r.Missing = "(no files: " + r.Outcome + ")";
            return r;
        }
        var from = Long(victim["window_start_mono_ns"]);
        var to = Long(victim["window_end_mono_ns"]);
        if (burner is not null)
        {
            from = Math.Max(from, Long(burner["ready_mono_ns"]));
            to = Math.Min(to, Long(burner["stop_mono_ns"]));
            // Where the claim is not made (off Windows), the class asked is said as not claimed, never paired with a read as if it were.
            r.BurnerClass = burner["class_as_told"] == "not told"
                ? $"{burner["class_asked"]}, not claimed (Windows only) / {burner["class_read"]} (base {burner["base_read"]})"
                : $"{burner["class_asked"]} / claim read {burner["claim_read_back"]}, {burner["class_read"]} (base {burner["base_read"]})";
            r.BurnerUse = $"{burner["threads"]} / {burner["processors_used"]}";
        }
        else
        {
            r.BurnerClass = "-";
            r.BurnerUse = "-";
        }
        var how = victim["class_mode"] == "keep" ? ", kept as started" : victim["class_as_told"] == "not told" ? ", not set (Windows only)" : ", set Normal";
        r.VictimClass = $"{victim["class_at_start"]} (base {victim["base_at_start"]}) / {victim["class_read"]} (base {victim["base_read"]}){how}";
        if (to <= from)
        {
            r.Missing = "(no window: the burner burned outside the victim's window; " + r.Outcome + ")";
            return r;
        }
        r.WindowS = (to - from) / 1e9;

        var lateness = new List<double>();
        var workWall = 0.0;
        var workCpu = 0.0;
        var cpuRead = true;
        foreach (var f in Files.ReadCsv(Path.Combine(dir, "frames.csv")))
        {
            var due = Long(f["due_mono_ns"]);
            if (due < from || due >= to)
                continue;
            lateness.Add(Dbl(f["lateness_ms"]));
            r.Missed += (int)Dbl(f["missed_after"]);
            workWall += Dbl(f["work_wall_ms"]);
            if (f["work_cpu_ms"].Length == 0)
                cpuRead = false;
            else
                workCpu += Dbl(f["work_cpu_ms"]);
        }
        r.Frames = lateness.Count;
        r.Lateness = Stats.Of(lateness);
        r.WorkWallMs = workWall;
        r.WorkCpuMs = cpuRead ? workCpu : double.NaN;
        r.WorkRatio = cpuRead && workCpu > 0 ? workWall / workCpu : double.NaN;

        var overshoot = new List<double>();
        foreach (var t in Files.ReadCsv(Path.Combine(dir, "ticks.csv")))
        {
            var due = Long(t["due_mono_ns"]);
            if (due < from || due >= to)
                continue;
            overshoot.Add(Dbl(t["overshoot_ms"]));
            if (t["pool_still_pending"] == "1")
                r.PendingTicks++;
        }
        r.Ticks = overshoot.Count;
        r.Overshoot = Stats.Of(overshoot);

        var dispatch = new List<double>();
        foreach (var p in Files.ReadCsv(Path.Combine(dir, "pool.csv")))
        {
            var queued = Long(p["queued_mono_ns"]);
            if (queued < from || queued >= to)
                continue;
            dispatch.Add(Dbl(p["dispatch_ms"]));
        }
        r.Dispatch = Stats.Of(dispatch);
        r.Readable = true;
        return r;
    }

    private static long Long(string s) => long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static double Dbl(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
}

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

internal sealed class LegRead(LegSpec leg)
{
    public static readonly string[] CsvHeader =
    [
        "leg", "outcome", "burner_class", "burner_threads_processors", "victim_class", "window_s", "frames", "lateness_p50_ms",
        "lateness_p99_ms", "lateness_max_ms", "missed_periods", "missed_per_1000", "work_wall_over_cpu", "ticks", "overshoot_p99_ms",
        "overshoot_max_ms", "pool_items", "dispatch_p99_ms", "dispatch_max_ms", "ticks_item_pending", "ratio_lateness_p50",
        "ratio_lateness_p99", "ratio_lateness_max", "ratio_overshoot_p99", "ratio_overshoot_max", "ratio_dispatch_p99", "ratio_dispatch_max",
        "work_wall_ms_sum", "work_cpu_ms_sum",
    ];

    public LegSpec Leg { get; } = leg;
    public bool Readable { get; set; }
    public string Missing { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string BurnerClass { get; set; } = "";
    public string BurnerUse { get; set; } = "";
    public string VictimClass { get; set; } = "";
    public double WindowS { get; set; }
    public int Frames { get; set; }
    public int Missed { get; set; }
    public int Ticks { get; set; }
    public int PendingTicks { get; set; }
    public double WorkRatio { get; set; } = double.NaN;
    public double WorkWallMs { get; set; } = double.NaN;
    public double WorkCpuMs { get; set; } = double.NaN;
    public Stats Lateness { get; set; }
    public Stats Overshoot { get; set; }
    public Stats Dispatch { get; set; }

    /// <summary>Missed periods per thousand periods the window holds.</summary>
    public double MissedPer1000 => WindowS > 0 ? Missed * 1000.0 / (WindowS * 1e9 / Victim.FramePeriodNs) : double.NaN;

    /// <summary>The leg's cell: its name, and its outcome beside it whenever the leg is not ok, so a broken leg's numbers are never
    /// read as a whole leg's.</summary>
    public string LegCell => Outcome is "ok" or "" ? Leg.Name : $"{Leg.Name} ({Outcome})";

    /// <summary>The work's wall over processor time, with the frames and both sums it is made of: on Windows each frame's processor
    /// time is counted in clock ticks, so only a window's sum is meaningful, and a window of few frames says so by its count.</summary>
    public string WorkCell => $"{F(WorkRatio, "0.00")} ({Frames} frames, {F(WorkWallMs, "0")} / {F(WorkCpuMs, "0")} ms)";

    public string Row() =>
        $"| {LegCell} | {BurnerClass} | {BurnerUse} | {VictimClass} | {F(WindowS, "0.0")} s | {F(Lateness.P50)} / {F(Lateness.P99)} / {F(Lateness.Max)} ms | " +
        $"{Missed} of {F(WindowS * 1e9 / Victim.FramePeriodNs, "0")} | {WorkCell} | {F(Overshoot.P99)} / {F(Overshoot.Max)} ms | " +
        $"{F(Dispatch.P99)} / {F(Dispatch.Max)} ms ({Dispatch.Count}) | {PendingTicks} of {Ticks} |";

    public string RatioRow(LegRead? b) =>
        $"| {LegCell} | {R(Lateness.P50, b?.Lateness.P50)} | {R(Lateness.P99, b?.Lateness.P99)} | {R(Lateness.Max, b?.Lateness.Max)} | " +
        $"{F(MissedPer1000, "0.0")} (baseline {F(b?.MissedPer1000 ?? double.NaN, "0.0")}) | {R(Overshoot.P99, b?.Overshoot.P99)} | " +
        $"{R(Overshoot.Max, b?.Overshoot.Max)} | {R(Dispatch.P99, b?.Dispatch.P99)} | {R(Dispatch.Max, b?.Dispatch.Max)} |";

    public string[] Csv(LegRead? b) =>
    [
        Leg.Name, Outcome, BurnerClass, BurnerUse, VictimClass, F(WindowS, "0.000"), Frames.ToString(CultureInfo.InvariantCulture),
        F(Lateness.P50), F(Lateness.P99), F(Lateness.Max), Missed.ToString(CultureInfo.InvariantCulture), F(MissedPer1000, "0.00"),
        F(WorkRatio, "0.000"), Ticks.ToString(CultureInfo.InvariantCulture), F(Overshoot.P99), F(Overshoot.Max),
        Dispatch.Count.ToString(CultureInfo.InvariantCulture), F(Dispatch.P99), F(Dispatch.Max), PendingTicks.ToString(CultureInfo.InvariantCulture),
        Ratio(Lateness.P50, b?.Lateness.P50), Ratio(Lateness.P99, b?.Lateness.P99), Ratio(Lateness.Max, b?.Lateness.Max),
        Ratio(Overshoot.P99, b?.Overshoot.P99), Ratio(Overshoot.Max, b?.Overshoot.Max), Ratio(Dispatch.P99, b?.Dispatch.P99),
        Ratio(Dispatch.Max, b?.Dispatch.Max), F(WorkWallMs, "0.0"), F(WorkCpuMs, "0.0"),
    ];

    private static string F(double v, string format = "0.000") => double.IsNaN(v) ? "-" : v.ToString(format, CultureInfo.InvariantCulture);

    private static string Ratio(double v, double? baseline) =>
        baseline is not { } b || double.IsNaN(b) || double.IsNaN(v) ? "" : b <= 0 ? "" : (v / b).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>"×12.40", or why there is no ratio: no baseline read, or a baseline of zero.</summary>
    private static string R(double v, double? baseline) =>
        baseline is not { } b || double.IsNaN(b) ? "- (no baseline)" : double.IsNaN(v) ? "-" : b <= 0 ? "- (baseline 0)" : "×" + (v / b).ToString("0.00", CultureInfo.InvariantCulture);
}
