using System.Globalization;
using PlayMap.Core.Machine;

namespace PlayMap.Supervisor.Install;

/// <summary>Who may answer on the supervisor's pipe, as its client checks the process serving it before it reads the answer: the
/// service runs as LocalSystem from the installed <c>Supervisor\PlayMap.Supervisor.exe</c>. A server this account may read is held to
/// that (SYSTEM's SID, or the installed exe's image); a server it may not read at all — as a SYSTEM service is to a standard account —
/// is taken only in session 0, where services run and a signed-in user's programs do not.</summary>
public static class PipeServerRule
{
    /// <summary>LocalSystem's SID.</summary>
    public const string LocalSystemSid = "S-1-5-18";

    /// <summary>Pure: why the pipe's server (pid <paramref name="pid"/>, <paramref name="server"/> as far as this account may read it,
    /// in <paramref name="session"/>) is not the supervisor, or null when it is.</summary>
    public static string? Refusal(int pid, in ProcessFacts server, int session, string installedSupervisor)
    {
        ArgumentNullException.ThrowIfNull(installedSupervisor);
        if (server.UserSid.Length > 0)
            return server.UserSid == LocalSystemSid ? null : Say($"pid {pid}, which runs as another account than SYSTEM");
        if (server.ImagePath.Length > 0)
            return string.Equals(server.ImagePath, installedSupervisor, StringComparison.OrdinalIgnoreCase)
                ? null
                : Say($"pid {pid} ({server.ImagePath}), which is not the installed {installedSupervisor}");
        return session == 0 ? null : Say($"pid {pid}, a process this account may not read, in session {session} rather than the services' session 0");
    }

    private static string Say(FormattableString words) => words.ToString(CultureInfo.InvariantCulture);
}
