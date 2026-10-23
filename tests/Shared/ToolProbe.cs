using System.Diagnostics;

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
    public static string? Run(string path, IEnumerable<string> args, TimeSpan deadline)
    {
        var start = new ProcessStartInfo(path) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args)
            start.ArgumentList.Add(a);
        Process? p;
        try
        {
            p = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        if (p is null)
            return null;
        using (p)
        {
            var text = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(deadline))
            {
                Stop(p);
                return null;
            }
            return text.Wait(TimeSpan.FromSeconds(5)) ? text.Result.Trim() : null;
        }
    }

    private static void Stop(Process p)
    {
        try
        {
            p.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It exited between the wait and the kill.
        }
    }
}
