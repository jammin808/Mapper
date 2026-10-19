using System.Diagnostics;
using System.Globalization;
using PlayMap.Core.Machine;

namespace PlayMap.Supervisor.Install;

/// <summary>What this OS says of a process: whether it runs, its image's name and full path, when it started (Unix ms), its command
/// line, the SID of the user it runs as, its session and its parent's pid. An empty string or −1 is a fact the OS would not give.
/// </summary>
public readonly record struct ProcessFacts(bool Exists, string Name, string ImagePath, long StartedUnixMs, string CommandLine,
    string UserSid, int Session, int ParentPid)
{
    /// <summary>No process has that id.</summary>
    public static readonly ProcessFacts Gone = new(false, "", "", -1, "", "", -1, 0);
}

/// <summary>What a caller asks of the process a pid record names, beyond the engine's own marks: the installed exe's full path, the
/// show user's SID and the show user's session (empty or −1: not asked).</summary>
public sealed record EngineExpectation(string InstalledExe, string ShowUserSid, int Session)
{
    /// <summary>Any PlayMap engine: what the armed check asks, which only asks the engine and ends nothing.</summary>
    public static readonly EngineExpectation AnyEngine = new("", "", -1);
}

/// <summary>The rule every reader of <c>engine.pid</c> holds a process to before it acts on the record's word. The record's folder is
/// one the show user may write, so the record says only which process to look at: that process must be PlayMap.exe run as the engine,
/// started no later than its record was written and not long before it (the host stamps the record as the engine comes up; a pid
/// that another process took after an unclean stop fails here), and, for a caller that would end it or adopt it with high rights, the
/// installed exe, run as the show user in the show user's session.</summary>
public static class EngineIdentity
{
    /// <summary>The engine's image name.</summary>
    public const string ImageName = "PlayMap";

    /// <summary>How long before its record an engine may have started: the host stamps the record once its doors are open.</summary>
    public static readonly TimeSpan StartedBefore = TimeSpan.FromSeconds(120);

    /// <summary>How far the OS's start time and the record's stamp may differ the wrong way: two clocks' rounding.</summary>
    public static readonly TimeSpan StampSlack = TimeSpan.FromSeconds(2);

    /// <summary>Pure: why the process <paramref name="facts"/> describes is not the engine <paramref name="record"/> names, as
    /// <paramref name="expect"/> asks, or null when it is.</summary>
    public static string? Refusal(EnginePidRecord record, in ProcessFacts facts, EngineExpectation expect)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(expect);
        var pid = record.Pid;
        if (!facts.Exists)
            return Say($"pid {pid} is not running");
        if (!string.Equals(facts.Name, ImageName, StringComparison.OrdinalIgnoreCase))
            return Say($"pid {pid} is {(facts.Name.Length > 0 ? facts.Name : "a process whose name cannot be read")}, not PlayMap.exe");
        if (facts.CommandLine.Length == 0)
            return Say($"pid {pid}'s command line cannot be read");
        if (!IsEngineLine(facts.CommandLine))
            return Say($"pid {pid} is PlayMap.exe run as the desk or a worker, not as the engine");
        if (facts.StartedUnixMs < 0)
            return Say($"pid {pid}'s start cannot be read");
        if (facts.StartedUnixMs > record.StartedUnixMs + (long)StampSlack.TotalMilliseconds)
            return Say($"pid {pid} started after its record was written: another process has taken the pid");
        var before = record.StartedUnixMs - facts.StartedUnixMs;
        if (before > (long)StartedBefore.TotalMilliseconds)
            return Say($"pid {pid} started {before / 1000} s before its record was written: it is not the engine the record names");
        if (expect.InstalledExe.Length > 0 && !SamePath(facts.ImagePath, expect.InstalledExe))
            return Say($"pid {pid} runs {(facts.ImagePath.Length > 0 ? facts.ImagePath : "an image that cannot be read")}, not the installed {expect.InstalledExe}");
        if (expect.ShowUserSid.Length > 0 && !string.Equals(facts.UserSid, expect.ShowUserSid, StringComparison.OrdinalIgnoreCase))
            return Say($"pid {pid} does not run as the show user");
        if (expect.Session >= 0 && facts.Session != expect.Session)
            return Say($"pid {pid} runs in session {facts.Session}, not the show user's session {expect.Session}");
        return null;
    }

    /// <summary>Whether the record names a running PlayMap engine (<see cref="EngineExpectation.AnyEngine"/>), by what this OS says
    /// of its process now: the armed check's rule.</summary>
    public static bool NamesRunningEngine(EnginePidRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Refusal(record, ProcessReader.Read(record.Pid), EngineExpectation.AnyEngine) is null;
    }

    /// <summary>Whether a command line runs its exe as the engine: the first word after the exe (quoted or not) is <c>engine</c>,
    /// as the exe's own entry reads it.</summary>
    public static bool IsEngineLine(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        var line = commandLine.TrimStart();
        if (line.Length == 0)
            return false;
        int rest;
        if (line[0] == '"')
        {
            var close = line.IndexOf('"', 1);
            if (close < 0)
                return false;
            rest = close + 1;
        }
        else
        {
            rest = line.IndexOf(' ', StringComparison.Ordinal);
            if (rest < 0)
                return false;
        }
        var words = line[rest..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 && string.Equals(words[0], "engine", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
            return false;
        static string Full(string p) => Path.IsPathFullyQualified(p) ? Path.GetFullPath(p) : p;
        return string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string Say(FormattableString words) => words.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The facts of a process by its id on this OS: Windows' own through the process's handle, elsewhere what
/// System.Diagnostics and <c>/proc</c> give.</summary>
public static class ProcessReader
{
    public static ProcessFacts Read(int pid) => OperatingSystem.IsWindows() ? Windows.EngineProcess.ReadFacts(pid) : Portable(pid);

    private static ProcessFacts Portable(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
                return ProcessFacts.Gone;
            var started = new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();
            var cmdline = Path.Combine("/proc", pid.ToString(CultureInfo.InvariantCulture), "cmdline");
            var line = File.Exists(cmdline) ? File.ReadAllText(cmdline).Replace('\0', ' ').Trim() : "";
            return new ProcessFacts(true, process.ProcessName, "", started, line, "", -1, 0);
        }
        catch (ArgumentException)
        {
            return ProcessFacts.Gone;
        }
        catch (InvalidOperationException)
        {
            return ProcessFacts.Gone;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            // A process this one may not look at: it exists, and nothing more is known of it.
            return new ProcessFacts(true, "", "", -1, "", "", -1, 0);
        }
    }
}
