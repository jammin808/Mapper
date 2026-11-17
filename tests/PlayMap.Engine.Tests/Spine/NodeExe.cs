using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Machine;
using PlayMap.Core.Model;
using PlayMap.Tests;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>The node host (tools/PlayMap.Node) built beside these tests, and the repository it lives in.</summary>
internal static class NodeExe
{
    /// <summary>The repository's root: the folder that holds PlayMap.sln, above the tests' output.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root holds PlayMap.sln");
    }

    /// <summary>PlayMap.Node.dll in the same configuration and runtime as these tests (built with them).</summary>
    public static string Locate()
    {
        var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = tfm.Parent?.Name ?? "Debug";
        var dll = Path.Combine(RepoRoot(), "tools", "PlayMap.Node", "bin", configuration, tfm.Name, "PlayMap.Node.dll");
        return File.Exists(dll) ? dll : throw new FileNotFoundException("the node host is built with the engine's tests", dll);
    }

    /// <summary>Starts the node host with these words, its output and errors kept.</summary>
    public static Process Start(params string[] args)
    {
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(Locate());
        foreach (var a in args)
            info.ArgumentList.Add(a);
        return Process.Start(info) ?? throw new InvalidOperationException("the node host did not start");
    }
}

/// <summary>A node's output and errors read as they come, the last lines of both kept for a failing test's words. A redirected pipe
/// nobody reads fills (on Windows its buffer is a few KiB), and a node thread that writes to it then waits for good.</summary>
internal sealed class NodeOutput
{
    private const int Kept = 40;
    private readonly ConcurrentQueue<string> _lines = new();

    public NodeOutput(Process node)
    {
        ArgumentNullException.ThrowIfNull(node);
        node.OutputDataReceived += (_, e) => Keep("out", e.Data);
        node.ErrorDataReceived += (_, e) => Keep("err", e.Data);
        node.BeginOutputReadLine();
        node.BeginErrorReadLine();
    }

    /// <summary>The last lines read, each marked with its stream.</summary>
    public string Tail => string.Join("\n", _lines);

    private void Keep(string stream, string? line)
    {
        if (line is null)
            return;
        _lines.Enqueue(stream + ": " + line);
        while (_lines.Count > Kept)
            _lines.TryDequeue(out _);
    }
}

/// <summary>When a node the test started came up, each time on the clock that took it: when the test started it, the process's own
/// start, the stamp of the first line of its log (its heading, stamped by the log's own thread as it writes it) and of its "is up" line
/// (stamped by the node as it made the line, before its log's thread wrote it: <c>NodeLogProvider.Line</c>), the log file's last write
/// when the test first saw that line, and when the test saw it; beside them, how often
/// the test's wait read the log, the longest it went between two reads and the longest one read took. So a late node is told from a
/// node seen late: a child up seconds after its start and seen a minute later was waited on by the test; one up a minute after its
/// start was itself late. Said in the test's output on every run, pass or fail, and kept as one row of
/// <c>&lt;assembly&gt;-&lt;start&gt;-&lt;pid&gt;.nodehost.csv</c> beside the scheduling probe's rows when the probe runs.</summary>
internal sealed class NodeUpTimes
{
    /// <summary>The columns of the kept file, its first line.</summary>
    public const string Header = "test_started_child_utc,child_process_start_utc,child_log_first_utc,child_up_utc,child_log_written_utc,"
        + "test_saw_up_utc,polls,poll_max_gap_ms,tail_max_ms";

    private long _lastPoll;

    public NodeUpTimes(DateTime startedChild, Process node)
    {
        ArgumentNullException.ThrowIfNull(node);
        StartedChild = startedChild;
        ProcessStart = StartOf(node);
    }

    /// <summary>When the test started the node (UTC, read just before the start).</summary>
    public DateTime StartedChild { get; }

    /// <summary>The node process's own start (UTC), null when the system would not say.</summary>
    public DateTime? ProcessStart { get; }

    /// <summary>The node's stamp on the first line of its log (its heading), once the test has read one.</summary>
    public DateTime? LogFirst { get; private set; }

    /// <summary>The node's stamp on its "is up" line, once the test has seen it.</summary>
    public DateTime? Up { get; private set; }

    /// <summary>The log file's last write time (UTC) when the test first saw the "is up" line.</summary>
    public DateTime? LogWritten { get; private set; }

    /// <summary>When the test first saw the "is up" line (UTC).</summary>
    public DateTime? Seen { get; private set; }

    public int Polls { get; private set; }

    public double PollMaxGapMs { get; private set; }

    public double TailMaxMs { get; private set; }

    /// <summary>One read of the test's wait: counted, with the time since the read before.</summary>
    public void Poll()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastPoll != 0)
            PollMaxGapMs = Math.Max(PollMaxGapMs, Stopwatch.GetElapsedTime(_lastPoll, now).TotalMilliseconds);
        _lastPoll = now;
        Polls++;
    }

    /// <summary>The log as one read found it (<paramref name="took"/> to read): its first line's stamp, once.</summary>
    public void Read(string text, TimeSpan took)
    {
        ArgumentNullException.ThrowIfNull(text);
        TailMaxMs = Math.Max(TailMaxMs, took.TotalMilliseconds);
        if (LogFirst is null && text.Length > 0)
            LogFirst = StampOf(text, 0);
    }

    /// <summary>The "is up" line found at <paramref name="at"/> in <paramref name="text"/>, read from <paramref name="log"/>: its
    /// stamp, the file's last write and the time now, the first time only.</summary>
    public void SawUp(string text, int at, string log)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Seen is not null)
            return;
        Seen = DateTime.UtcNow;
        Up = StampOf(text, at == 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1);
        try
        {
            LogWritten = File.GetLastWriteTimeUtc(log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The write time stays unknown: the row says so with an empty cell.
            LogWritten = null;
        }
    }

    /// <summary>The kept row, culture-invariant; an unknown time is an empty cell.</summary>
    public string Row() => string.Join(',', Time(StartedChild), Time(ProcessStart), Time(LogFirst), Time(Up), Time(LogWritten), Time(Seen),
        Polls.ToString(CultureInfo.InvariantCulture), PollMaxGapMs.ToString("F1", CultureInfo.InvariantCulture),
        TailMaxMs.ToString("F1", CultureInfo.InvariantCulture));

    /// <summary>The times in words, each after the one before it in seconds.</summary>
    public string Words()
    {
        var words = $"the test started the node at {Time(StartedChild)}; its process started at {Time(ProcessStart) ?? "(not said)"}"
            + $"{After(ProcessStart, StartedChild)}; its log's first line is stamped {Time(LogFirst) ?? "(not read)"}{After(LogFirst, ProcessStart)}; ";
        words += Seen is null
            ? "its \"is up\" line was not seen"
            : $"its \"is up\" line is stamped {Time(Up) ?? "(no stamp read)"}{After(Up, ProcessStart)}, its log was last written at "
              + $"{Time(LogWritten) ?? "(not read)"}, and the test saw the line at {Time(Seen)}{After(Seen, Up)}";
        return words + string.Create(CultureInfo.InvariantCulture,
            $"; the test read the log {Polls} times, at most {PollMaxGapMs:0.0} ms apart, the longest read {TailMaxMs:0.0} ms");
    }

    /// <summary>The times and what the child was (<paramref name="child"/>) in the test's output, and beside the probe's rows when the
    /// probe runs as one row whose first columns are <see cref="Header"/>'s and whose last are <see cref="NodeChildReadings.Header"/>'s
    /// (a file that cannot be written is said, never failed on).</summary>
    public void Keep(ITestOutputHelper output, NodeChildReadings child)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(child);
        output.WriteLine(Words());
        output.WriteLine(child.Words());
        var probe = SchedulingProbe.Running;
        if (probe is null)
            return;
        var name = string.Create(CultureInfo.InvariantCulture,
            $"{typeof(NodeUpTimes).Assembly.GetName().Name}-{DateTime.UtcNow:yyyyMMdd'T'HHmmss}-{Environment.ProcessId}.nodehost.csv");
        var path = Path.Combine(Path.GetDirectoryName(probe.Path)!, name);
        var (header, row) = Lines(child);
        try
        {
            File.WriteAllText(path, header + "\n" + row + "\n");
            output.WriteLine("kept in " + path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"not kept in {path}: {ex.Message}");
        }
    }

    /// <summary>The kept file's two lines: <see cref="Header"/>'s columns then <see cref="NodeChildReadings.Header"/>'s, and the row of
    /// a cell for each.</summary>
    public (string Header, string Row) Lines(NodeChildReadings child)
    {
        ArgumentNullException.ThrowIfNull(child);
        return (Header + "," + NodeChildReadings.Header, Row() + "," + child.Row());
    }

    private static DateTime? StartOf(Process node)
    {
        try
        {
            return node.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The stamp that begins the log line at <paramref name="start"/> (the log's own form, <c>yyyy-MM-ddTHH:mm:ss.fffZ</c>),
    /// null when the line does not begin with one.</summary>
    private static DateTime? StampOf(string text, int start)
    {
        var space = text.IndexOf(' ', start);
        return space > start && DateTime.TryParseExact(text.AsSpan(start, space - start), "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var stamp)
            ? stamp
            : null;
    }

    private static string? Time(DateTime? at) => at?.ToString("O", CultureInfo.InvariantCulture);

    private static string After(DateTime? at, DateTime? from) => at is { } a && from is { } f
        ? string.Create(CultureInfo.InvariantCulture, $" ({(a - f).TotalSeconds:+0.000;-0.000} s)")
        : "";
}

/// <summary>Which leg of the dispatched node host measurement (the CI workflow's <c>nodehostab</c> job) the node host test runs, read
/// from three variables only that job sets. All three unset or empty is today's test exactly: no hold, the home's machine settings as
/// <see cref="NodeSettings.CreateDefault"/> makes them, no project named. A value outside a variable's words is refused with words,
/// never defaulted.</summary>
internal sealed record NodeHostLeg(int HoldMs, string? ProcessClass, string? Output)
{
    /// <summary>How long, in whole milliseconds, the test holds the child after it is seen up and before its PING.</summary>
    public const string HoldVariable = "PLAYMAP_NODEHOST_HOLD_MS";

    /// <summary>The machine settings' process class the child's home is saved with: a word of MACHINE SET PROCESS.</summary>
    public const string ProcessVariable = "PLAYMAP_NODEHOST_PROCESS";

    /// <summary><c>off</c>: the child is started on a project whose one output is not enabled.</summary>
    public const string OutputVariable = "PLAYMAP_NODEHOST_OUTPUT";

    /// <summary>The longest hold a leg may ask, so a mistyped hold cannot keep the test past its job's deadline.</summary>
    public const int MaxHoldMs = 60_000;

    /// <summary>The one word <see cref="OutputVariable"/> takes.</summary>
    public const string OutputOff = "off";

    /// <summary>Today's test: no hold, the home's defaults, no project.</summary>
    public static NodeHostLeg Today { get; } = new(0, null, null);

    /// <summary>The leg <paramref name="variable"/> names; an unknown value throws with the variable, the value and the words it takes.</summary>
    public static NodeHostLeg Read(Func<string, string?> variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        var hold = 0;
        if (variable(HoldVariable) is { Length: > 0 } holdWord
            && !(int.TryParse(holdWord, NumberStyles.None, CultureInfo.InvariantCulture, out hold) && hold <= MaxHoldMs))
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"{HoldVariable} is '{holdWord}': whole milliseconds from 0 to {MaxHoldMs}, or unset"));
        }
        var words = PlatformPolicy.Find("PROCESS")?.Values ?? [];
        string? process = null;
        if (variable(ProcessVariable) is { Length: > 0 } processWord)
        {
            if (!words.Contains(processWord, StringComparer.Ordinal))
                throw new ArgumentException($"{ProcessVariable} is '{processWord}': one of {string.Join(", ", words)} (MACHINE SET PROCESS's words), or unset");
            process = processWord;
        }
        string? output = null;
        if (variable(OutputVariable) is { Length: > 0 } outputWord)
        {
            if (outputWord != OutputOff)
                throw new ArgumentException($"{OutputVariable} is '{outputWord}': {OutputOff} (the child started on a project whose output is not enabled), or unset");
            output = outputWord;
        }
        return new NodeHostLeg(hold, process, output);
    }

    /// <summary>The child's home settings: <paramref name="settings"/> itself unless the leg names a process class.</summary>
    public NodeSettings Home(NodeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return ProcessClass is null ? settings : settings with { Machine = settings.Machine with { Process = ProcessClass } };
    }

    /// <summary>The project file the child is started on, written into <paramref name="folder"/> through the product's own serializer,
    /// or null when the leg names none (today's child takes the default project).</summary>
    public string? WriteProject(string folder, string name)
    {
        if (Output is null)
            return null;
        var dir = Directory.CreateDirectory(Path.Combine(folder, "project"));
        var path = Path.Combine(dir.FullName, "show.playmap");
        File.WriteAllText(path, ProjectJson.Serialize(WithNoOutput(name)));
        return path;
    }

    /// <summary>The default project with every output it has (one) not enabled, nothing else changed.</summary>
    public static Project WithNoOutput(string name)
    {
        var project = Project.CreateDefault(name);
        return project with { Outputs = [.. project.Outputs.Select(o => o with { Enabled = false })] };
    }
}

/// <summary>What the child engine was while the node host test held it, each read from the test host and never set: its priority class
/// as the OS reports it once the test has seen it up (on Linux, what the runtime maps the process's nice value to), the renderer its
/// "is up" line names (letters, digits, dots, spaces and brackets only), its processor time and thread count when up was seen, at the
/// end of a leg's hold and just before the kill, with the times of the connect, the PING's and STATE's answers, the kill and the
/// child's exit, and the first output's figures from the STATE the test reads. A read of a process that is gone or will not say is an
/// empty cell, never a throw into the test.</summary>
internal sealed class NodeChildReadings(NodeHostLeg leg)
{
    /// <summary>The columns this adds at the end of the node host row.</summary>
    public const string Header = "child_priority_class,child_renderer,child_up_read_utc,child_cpu_up_ms,child_threads_up,hold_ms,"
        + "hold_end_utc,child_cpu_hold_end_ms,child_threads_hold_end,hold_processors,connect_utc,ping_utc,state_utc,state_output_id,"
        + "state_output_enabled,state_output_fps,state_output_frame_ms_p95,kill_asked_utc,child_cpu_kill_ms,child_threads_kill,"
        + "child_exited_utc,leg_process,leg_output";

    /// <summary>The longest renderer name kept.</summary>
    public const int RendererMax = 120;

    private readonly NodeHostLeg _leg = leg ?? throw new ArgumentNullException(nameof(leg));

    public string? PriorityClass { get; private set; }

    public string? Renderer { get; private set; }

    public DateTime? UpRead { get; private set; }

    public double? CpuUpMs { get; private set; }

    public int? ThreadsUp { get; private set; }

    public DateTime? HoldEnd { get; private set; }

    public double? CpuHoldEndMs { get; private set; }

    public int? ThreadsHoldEnd { get; private set; }

    public DateTime? Connected { get; private set; }

    public DateTime? Pinged { get; private set; }

    public DateTime? Stated { get; private set; }

    public string? OutputId { get; private set; }

    public bool? OutputEnabled { get; private set; }

    public double? OutputFps { get; private set; }

    public double? OutputFrameMsP95 { get; private set; }

    public DateTime? KillAsked { get; private set; }

    public double? CpuKillMs { get; private set; }

    public int? ThreadsKill { get; private set; }

    public DateTime? Exited { get; private set; }

    /// <summary>The child's processor time over the hold per second of it (about 4 is four processors), null without both reads.</summary>
    public double? HoldProcessors => UpRead is { } from && HoldEnd is { } to && CpuUpMs is { } a && CpuHoldEndMs is { } b
        && (to - from).TotalMilliseconds > 0 ? (b - a) / (to - from).TotalMilliseconds : null;

    /// <summary>The renderer the "is up" line found at <paramref name="at"/> in <paramref name="text"/> names, the first time.</summary>
    public void SawUp(string text, int at) => Renderer ??= RendererOf(text, at);

    /// <summary>The child as the test saw it up: its priority class, processor time and threads.</summary>
    public void Up(Process node)
    {
        ArgumentNullException.ThrowIfNull(node);
        PriorityClass = Read(node, p => p.PriorityClass.ToString());
        (CpuUpMs, UpRead, ThreadsUp) = Sample(node);
    }

    /// <summary>The child at the end of the leg's hold.</summary>
    public void Held(Process node)
    {
        ArgumentNullException.ThrowIfNull(node);
        (CpuHoldEndMs, HoldEnd, ThreadsHoldEnd) = Sample(node);
    }

    public void Connect() => Connected = DateTime.UtcNow;

    public void Ping() => Pinged = DateTime.UtcNow;

    /// <summary>STATE answered: its time, and the first output's id, enabled, fps and frame p95 where STATE carries them.</summary>
    public void State(JsonElement root)
    {
        Stated = DateTime.UtcNow;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("outputs", out var outputs)
            || outputs.ValueKind != JsonValueKind.Array || outputs.GetArrayLength() == 0 || outputs[0].ValueKind != JsonValueKind.Object)
        {
            return;
        }
        var first = outputs[0];
        OutputId = first.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? Keep(id.GetString() ?? "", IdChar) : null;
        OutputEnabled = first.TryGetProperty("enabled", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? on.GetBoolean() : null;
        OutputFps = Number(first, "fps");
        OutputFrameMsP95 = Number(first, "frameMsP95");
    }

    /// <summary>The child just before the kill is asked.</summary>
    public void BeforeKill(Process node)
    {
        ArgumentNullException.ThrowIfNull(node);
        (CpuKillMs, KillAsked, ThreadsKill) = Sample(node);
    }

    /// <summary>The child's exit as the system stamped it.</summary>
    public void Ended(Process node)
    {
        ArgumentNullException.ThrowIfNull(node);
        Exited = Read(node, p => (DateTime?)p.ExitTime.ToUniversalTime());
    }

    /// <summary>The kept cells, culture-invariant; an unknown reading is an empty cell.</summary>
    public string Row() => string.Join(',', PriorityClass ?? "", Renderer ?? "", Time(UpRead), Ms(CpuUpMs), Count(ThreadsUp),
        _leg.HoldMs > 0 ? Count(_leg.HoldMs) : "", Time(HoldEnd), Ms(CpuHoldEndMs), Count(ThreadsHoldEnd), Cell(HoldProcessors, "F2"),
        Time(Connected), Time(Pinged), Time(Stated), OutputId ?? "", OutputEnabled is null ? "" : Flag(OutputEnabled),
        Cell(OutputFps, "F1"), Cell(OutputFrameMsP95, "F2"), Time(KillAsked), Ms(CpuKillMs), Count(ThreadsKill), Time(Exited),
        _leg.ProcessClass ?? "", _leg.Output ?? "");

    /// <summary>The readings in words, for the test's output.</summary>
    public string Words()
    {
        var words = new StringBuilder();
        words.Append(Invariant($"the child read as priority class {PriorityClass ?? "(not read)"} on renderer {Renderer ?? "(not named)"}"));
        words.Append(Invariant($"; its processor time {Ms(CpuUpMs)} ms with {Count(ThreadsUp)} threads when seen up at {Time(UpRead)}"));
        if (_leg.HoldMs > 0)
        {
            words.Append(Invariant($", {Ms(CpuHoldEndMs)} ms with {Count(ThreadsHoldEnd)} threads after a {_leg.HoldMs} ms hold ending "));
            words.Append(Invariant($"{Time(HoldEnd)} ({Fixed(HoldProcessors, "F2")} processors over it)"));
        }
        words.Append(Invariant($", {Ms(CpuKillMs)} ms with {Count(ThreadsKill)} threads before the kill asked at {Time(KillAsked)}"));
        words.Append(Invariant($"; connected {Time(Connected)}, PING answered {Time(Pinged)}, STATE answered {Time(Stated)}"));
        words.Append(Invariant($"; the first output {OutputId ?? "(none)"} enabled {Flag(OutputEnabled)} at {Fixed(OutputFps, "F1")} fps, "));
        words.Append(Invariant($"frame p95 {Fixed(OutputFrameMsP95, "F2")} ms; exited {Time(Exited)}"));
        if (_leg != NodeHostLeg.Today)
            words.Append(Invariant($"; the leg: process {_leg.ProcessClass ?? "(home default)"}, output {_leg.Output ?? "(default project)"}"));
        return words.ToString();
    }

    /// <summary>The renderer named by the "is up" line that holds <paramref name="at"/>: the words after <c>renderer </c> up to the
    /// <c>;</c> that ends them, kept to letters, digits, dots, spaces and brackets, at most <see cref="RendererMax"/> characters; ""
    /// when the line names none.</summary>
    public static string RendererOf(string text, int at)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (at < 0 || at > text.Length)
            return "";
        var end = text.IndexOf('\n', at);
        var line = end < 0 ? text[at..] : text[at..end];
        const string Mark = "renderer ";
        var start = line.IndexOf(Mark, StringComparison.Ordinal);
        if (start < 0)
            return "";
        var name = line[(start + Mark.Length)..];
        var stop = name.IndexOf(';', StringComparison.Ordinal);
        return Keep(stop < 0 ? name : name[..stop], RendererChar);
    }

    private static bool RendererChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or ' ' or '(' or ')' or '[' or ']';

    /// <summary>An entity id's characters: never a comma or a line of the row's.</summary>
    private static bool IdChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.';

    /// <summary>The characters of <paramref name="words"/> that <paramref name="kept"/> allows, at most <see cref="RendererMax"/>, trimmed.</summary>
    private static string Keep(string words, Func<char, bool> kept)
    {
        var text = new StringBuilder(Math.Min(words.Length, RendererMax));
        foreach (var c in words)
        {
            if (text.Length == RendererMax)
                break;
            if (kept(c))
                text.Append(c);
        }
        return text.ToString().Trim();
    }

    private static double? Number(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number : null;

    /// <summary>The child's processor time, the time it was read and its thread count, read afresh (a <see cref="Process"/> keeps
    /// what it read before). The time is taken just after the processor time is read, so a test host held before the read (the starved
    /// case) holds the stamp too, and a rise in processor time between two samples is over the span between their stamps.</summary>
    private static (double? CpuMs, DateTime At, int? Threads) Sample(Process node)
    {
        _ = Read(node, Refresh);
        var cpu = Read(node, p => (double?)p.TotalProcessorTime.TotalMilliseconds);
        var at = DateTime.UtcNow;
        return (cpu, at, Read(node, p => (int?)p.Threads.Count));
    }

    private static bool Refresh(Process node)
    {
        node.Refresh();
        return true;
    }

    /// <summary>One read of <paramref name="node"/>, its default when the process is gone, will not say or cannot be read here.</summary>
    private static T? Read<T>(Process node, Func<Process, T> read)
    {
        try
        {
            return read(node);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or IOException
                                       or UnauthorizedAccessException)
        {
            // The reading stays unknown: its cell is empty.
            return default;
        }
    }

    private static string Invariant(FormattableString words) => FormattableString.Invariant(words);

    private static string Time(DateTime? at) => at?.ToString("O", CultureInfo.InvariantCulture) ?? "";

    private static string Cell(double? value, string format) => value?.ToString(format, CultureInfo.InvariantCulture) ?? "";

    private static string Fixed(double? value, string format) => value?.ToString(format, CultureInfo.InvariantCulture) ?? "?";

    private static string Flag(bool? value) => value is null ? "?" : value.Value ? "true" : "false";

    private static string Ms(double? ms) => ms?.ToString("F0", CultureInfo.InvariantCulture) ?? "";

    private static string Count(int? n) => n?.ToString(CultureInfo.InvariantCulture) ?? "";
}
