using PlayMap.Core.Machine;
using PlayMap.Supervisor.Policy;

namespace PlayMap.Supervisor.Status;

/// <summary>The supervisor's policy as its status file says it (<see cref="SupervisorStatus"/>, in Core beside the pid record, so the
/// engine reads the file without the supervisor).</summary>
public static class SupervisorStatusWords
{
    /// <summary>A state's word: "waiting-for-session", "starting", "running", "stopping", "backoff", "stopped-too-many-restarts",
    /// "held".</summary>
    public static string Word(SupervisorState state) => state switch
    {
        SupervisorState.WaitingForSession => "waiting-for-session",
        SupervisorState.Starting => "starting",
        SupervisorState.Running => "running",
        SupervisorState.Stopping => "stopping",
        SupervisorState.Backoff => "backoff",
        SupervisorState.StoppedTooManyRestarts => "stopped-too-many-restarts",
        _ => "held",
    };

    /// <summary>The status of <paramref name="core"/> now.</summary>
    public static SupervisorStatus Of(SupervisorCore core, int pid, long atUnixMs)
    {
        ArgumentNullException.ThrowIfNull(core);
        return new SupervisorStatus(Word(core.State), pid, core.Restarts, core.LastCause, core.FreezeRule, atUnixMs);
    }
}
