using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace PlayMap.Supervisor.Windows;

/// <summary>M9's calls asked on this machine, as the supervisor asks them (a probe, not a gate): the active console session, its user,
/// and a child started there with <c>CreateProcessAsUser</c> that reports the session it runs in. The child is started suspended, as the
/// supervisor starts the engine, and is let run before the wait on it (so it runs during the wait, not only once the wait is done). Each
/// answer, yes or no, is said in words; the run fails only when it could say none.</summary>
[SupportedOSPlatform("windows")]
public static class SessionProbe
{
    public static int Run(Action<string> say, Func<uint, string, string, EngineProcess>? launch = null)
    {
        ArgumentNullException.ThrowIfNull(say);
        launch ??= SessionLauncher.Start;
        say("#### M9: the supervisor's session calls");
        say("");
        say(string.Create(CultureInfo.InvariantCulture, $"- asked as: {Environment.UserName} (session {Process.GetCurrentProcess().SessionId})"));
        var session = SessionLauncher.ConsoleSession();
        if (session is not { } s)
        {
            say("- WTSGetActiveConsoleSessionId: no console session (0xFFFFFFFF): no one sits at this machine's console");
            return Program.Done;
        }
        say(string.Create(CultureInfo.InvariantCulture, $"- WTSGetActiveConsoleSessionId: session {s}, user '{SessionLauncher.UserOf(s)}'"));
        var report = Path.Combine(Path.GetTempPath(), "playmap-session-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".txt");
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("this process has no path");
            using (var child = launch(s, exe, "report-session --out \"" + report + "\""))
            {
                say(string.Create(CultureInfo.InvariantCulture, $"- WTSQueryUserToken and CreateProcessAsUser: a child started (pid {child.Pid})"));
                // The child is started suspended; let it run before the wait, or the wait runs out before the child has had any chance.
                child.Resume();
                child.WaitForExit(TimeSpan.FromSeconds(15));
            }
            say(File.Exists(report)
                ? "- the child reports: " + File.ReadAllText(report).Trim()
                : "- the child wrote no report (it may not write where this process reads)");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            say(string.Create(CultureInfo.InvariantCulture, $"- refused: {ex.Message} (error {ex.NativeErrorCode}): the scheduled task at the show user's logon is the fallback"));
        }
        finally
        {
            if (File.Exists(report))
                File.Delete(report);
        }
        say("");
        return Program.Done;
    }
}
