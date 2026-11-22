using System.Globalization;
using System.Text;

namespace PlayMap.PriorityProbe;

/// <summary>The files' columns, one place: every writer and the reader name them from here, and a new column goes at a row's end.</summary>
internal static class Rows
{
    public static readonly string[] VictimHeader =
    [
        "os", "pid", "processors", "class_mode", "class_at_start", "base_at_start", "class_read", "base_read", "class_as_told", "class_words",
        "frame_timer", "tick_timer", "work_iterations", "ready_mono_ns", "window_start_mono_ns", "window_end_mono_ns", "anchor_mono_ns", "anchor_utc",
        "frames", "frames_dropped", "ticks", "ticks_dropped", "pool_items", "pool_pending_at_end", "gen0_collections", "process_cpu_ms",
        "cpu_ms_at_window_start", "cpu_stamp_start_mono_ns", "cpu_ms_at_window_end", "cpu_stamp_end_mono_ns",
        "timer_current_100ns_at_window_start", "timer_current_100ns_at_window_end", "pool_wait_end_mono_ns", "claim_applied", "claim_read_back",
    ];

    public static readonly string[] FramesHeader = ["utc", "due_mono_ns", "start_mono_ns", "lateness_ms", "work_wall_ms", "work_cpu_ms", "missed_after"];

    public static readonly string[] TicksHeader = ["utc", "due_mono_ns", "woke_mono_ns", "overshoot_ms", "pool_still_pending"];

    public static readonly string[] PoolHeader = ["utc", "queued_mono_ns", "started_mono_ns", "dispatch_ms"];

    public static readonly string[] BurnerHeader =
    [
        "os", "pid", "processors", "threads", "class_asked", "class_at_start", "base_at_start", "claim_applied", "claim_read_back",
        "class_read", "base_read", "class_as_told", "claim_words", "ready_mono_ns", "ready_utc", "stop_mono_ns", "wall_ms", "cpu_ms",
        "processors_used", "cpu_ms_at_window_start", "cpu_stamp_start_mono_ns", "cpu_ms_at_window_end", "cpu_stamp_end_mono_ns",
        "pin", "pinned_processors", "free_processors", "pin_words",
    ];

    /// <summary>The node a node leg ran (<see cref="NodeRun"/>): its class asked and read back at work and at the window's end, STATE's
    /// claim, its clips and outputs, its renderer, its instants, its processor time stamped at the window's bounds, and STATE's decode,
    /// output and pacing figures at both bounds (each a count since its decoder or output opened; an empty cell is a figure STATE did
    /// not carry, never a zero), and STATE's MMCSS claims for its render and present threads (a thread MMCSS raises runs above the
    /// controller's stamps and the sampler for its share, so a reader needs to know whether one was).</summary>
    public static readonly string[] NodeHeader =
    [
        "os", "pid", "processors", "class_asked", "class_at_work", "base_at_work", "class_at_end", "base_at_end", "state_claim_read_back",
        "state_claim_applied", "at_work", "clips", "clip_files", "output_size", "renderer", "up_mono_ns", "at_work_mono_ns", "stop_mono_ns",
        "cpu_ms_at_window_start", "cpu_stamp_start_mono_ns", "cpu_ms_at_window_end", "cpu_stamp_end_mono_ns",
        "fps_at_start", "fps_at_end", "output_dropped_at_start", "output_dropped_at_end", "frame_ms_p95_at_end",
        "missed_vblanks_at_start", "missed_vblanks_at_end", "decode_dropped_at_start", "decode_dropped_at_end",
        "decode_late_at_start", "decode_late_at_end", "decode_ms_p95_at_end", "decoders_at_end",
        "state_read_start_mono_ns", "state_read_end_mono_ns", "controller", "words", "state_render_mmcss", "state_present_mmcss",
    ];

    /// <summary>The node's threads at the window's bounds: each one's id, name, group (<see cref="NodeRun.Group"/>) and processor time
    /// at the window's start and end (empty where it was not there).</summary>
    public static readonly string[] NodeThreadsHeader = ["tid", "name", "group", "cpu_ms_at_window_start", "cpu_ms_at_window_end"];

    public static readonly string[] LegHeader =
    [
        "leg", "label", "burner_threads", "burner_class", "victim_class", "seconds", "lead_seconds", "started_utc", "victim_ready_utc",
        "burner_ready_utc", "ended_utc", "victim_exit", "burner_exit", "outcome", "words", "run", "place_label", "place_note",
    ];

    /// <summary>One reading of the runner's scheduler (<see cref="Scheduler"/>): the timer's resolution in NtQueryTimerResolution's own
    /// names and 100 ns units; an empty cell is a value not read, its words in the last cell.</summary>
    public static readonly string[] SchedulerHeader =
    [
        "utc", "when", "os", "processors", "win32_priority_separation", "timer_minimum_100ns", "timer_maximum_100ns", "timer_current_100ns",
        "logical_processors", "cores", "smt", "words",
    ];

    public static readonly string[] CalibrationHeader = ["utc", "os", "processors", "iterations", "target_ms", "median_ms", "p10_ms", "p90_ms", "samples"];

    public static string Ms(long ns) => (ns / 1e6).ToString("0.000", CultureInfo.InvariantCulture);
}

/// <summary>The probe's file writes: whole files, UTF-8 without a mark, invariant text; a cell never carries a comma or a line break.</summary>
internal static class Files
{
    public static void WriteAllLines(string path, IEnumerable<string> lines) =>
        File.WriteAllLines(path, lines, new UTF8Encoding(false));

    public static void WriteCsv(string path, string[] header, IEnumerable<string[]> rows)
    {
        var lines = new List<string> { string.Join(',', header) };
        foreach (var row in rows)
        {
            if (row.Length != header.Length)
                throw new InvalidOperationException($"{Path.GetFileName(path)}: a row of {row.Length} cells under {header.Length} columns");
            lines.Add(string.Join(',', row.Select(Clean)));
        }
        WriteAllLines(path, lines);
    }

    public static void AppendCsv(string path, string[] header, string[] row)
    {
        if (!File.Exists(path))
            WriteAllLines(path, [string.Join(',', header)]);
        File.AppendAllLines(path, [string.Join(',', row.Select(Clean))], new UTF8Encoding(false));
    }

    /// <summary>The rows of a file the probe wrote, by column name; empty when the file is not there or holds no row. A row whose cell
    /// count is not the header's is left out (a torn line).</summary>
    public static List<Dictionary<string, string>> ReadCsv(string path)
    {
        var rows = new List<Dictionary<string, string>>();
        if (!File.Exists(path))
            return rows;
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0)
            return rows;
        var header = lines[0].Split(',');
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(',');
            if (cells.Length != header.Length)
                continue;
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < header.Length; i++)
                row[header[i]] = cells[i];
            rows.Add(row);
        }
        return rows;
    }

    private static string Clean(string cell) => cell.Replace(',', ';').Replace('\r', ' ').Replace('\n', ' ');
}

/// <summary>A command line of --name value pairs after the mode: each read by name; a missing or malformed value is refused with
/// words (exit 2), never defaulted.</summary>
internal sealed class Args
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public Args(IEnumerable<string> args)
    {
        string? name = null;
        foreach (var a in args)
        {
            if (name is null)
            {
                if (!a.StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"'{a}' is not an option (--name value)");
                name = a[2..];
            }
            else
            {
                _values[name] = a;
                name = null;
            }
        }
        if (name is not null)
            throw new ArgumentException($"--{name} has no value");
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string Text(string name) =>
        _values.TryGetValue(name, out var v) && v.Length > 0 ? v : throw new ArgumentException($"--{name} is required");

    public double Number(string name) =>
        double.TryParse(Text(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) && v >= 0
            ? v : throw new ArgumentException($"--{name} takes a number of zero or more, not '{Text(name)}'");

    public long Integer(string name) =>
        long.TryParse(Text(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0
            ? v : throw new ArgumentException($"--{name} takes a whole number of zero or more, not '{Text(name)}'");

    public string Word(string name, params string[] words) =>
        words.Contains(Text(name), StringComparer.Ordinal) ? Text(name) : throw new ArgumentException($"--{name} takes {string.Join(" or ", words)}, not '{Text(name)}'");
}
