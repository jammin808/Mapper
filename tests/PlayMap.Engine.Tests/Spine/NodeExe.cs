using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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

    /// <summary>The times in the test's output, and beside the probe's rows when the probe runs (a file that cannot be written is
    /// said, never failed on).</summary>
    public void Keep(ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.WriteLine(Words());
        var probe = SchedulingProbe.Running;
        if (probe is null)
            return;
        var name = string.Create(CultureInfo.InvariantCulture,
            $"{typeof(NodeUpTimes).Assembly.GetName().Name}-{DateTime.UtcNow:yyyyMMdd'T'HHmmss}-{Environment.ProcessId}.nodehost.csv");
        var path = Path.Combine(Path.GetDirectoryName(probe.Path)!, name);
        try
        {
            File.WriteAllText(path, Header + "\n" + Row() + "\n");
            output.WriteLine("kept in " + path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"not kept in {path}: {ex.Message}");
        }
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
