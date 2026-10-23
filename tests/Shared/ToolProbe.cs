namespace PlayMap.Tests;

/// <summary>A tool's answer for a test attribute that asks a tool while a suite is discovered (npm's global root, for the Net suite's
/// node facts and the Engine suite's browser facts). A process the tool starts (npm's, or cmd.exe's for a .cmd on Windows) inherits its
/// output pipe and may hold it open long after, so the probe ends with the tool, never with the end of its pipe: in discovery a wait
/// that does not end stalls the whole test host before any test starts, where blame has no test to name.</summary>
internal static class ToolProbe
{
    /// <summary>The longest a probe of a tool runs before it is stopped with every process it started.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    /// <summary>What a tool wrote, read once the tool itself has exited (or null when it did not within
    /// <paramref name="deadline"/>, when its words were not in within 5 s of its exit, or when it could not be started).</summary>
    public static string? Run(string path, IEnumerable<string> args, TimeSpan deadline) =>
        ChildProcess.Run(path, args, deadline) is { Finished: true, WordsIn: true } ran ? ran.Text.Trim() : null;

    /// <summary>A tool on the PATH by its absolute path, or null: on Windows <c>name.exe</c>, then <c>name.cmd</c> (npm is a batch file
    /// there), folder by folder; elsewhere <c>name</c>. A PATH entry that is not an absolute folder (".", or one relative to wherever the
    /// test host runs) is never looked in.</summary>
    public static string? Find(string name) => Find(name, Environment.GetEnvironmentVariable("PATH") ?? "", OperatingSystem.IsWindows());

    /// <summary><see cref="Find(string)"/> in the folders of <paramref name="path"/> (separated as the PATH is), Windows' way or not.</summary>
    internal static string? Find(string name, string path, bool windows)
    {
        string[] names = windows ? [name + ".exe", name + ".cmd"] : [name];
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Path.IsPathFullyQualified(dir))
                continue;
            foreach (var file in names)
            {
                var candidate = Path.Combine(dir, file);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }
}
