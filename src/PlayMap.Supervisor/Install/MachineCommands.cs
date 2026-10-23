using System.Globalization;
using PlayMap.Core.Machine;

namespace PlayMap.Supervisor.Install;

/// <summary>How a stop of the record's engine ended.</summary>
/// <remarks>In order of weight: of several stops, the heaviest is the outcome.</remarks>
public enum EngineStop
{
    /// <summary>No engine runs: the record names none, or a process that has ended.</summary>
    NoneRan,
    /// <summary>The record names a process that is not the installed engine: nothing was stopped.</summary>
    NotTheEngine,
    /// <summary>The engine stopped within the grace.</summary>
    Stopped,
    /// <summary>The engine did not stop within the grace and was ended.</summary>
    Terminated,
    /// <summary>The engine runs and could not be ended.</summary>
    Failed,
}

/// <summary>A program running from the install's folder: its pid and its image.</summary>
public readonly record struct RunningProgram(int Pid, string Image);

/// <summary>What the machine-level commands act through: this machine's Windows, or a test's fake.</summary>
public interface IMachineActions
{
    /// <summary>The install's folder ("" when none is recorded).</summary>
    string InstallDir { get; }

    /// <summary>Stops the service and waits until it has stopped (one that is not there is stopped); throws with Windows' words.</summary>
    void StopService(Action<string> say);

    /// <summary>Starts the service again; throws with Windows' words.</summary>
    void StartService(Action<string> say);

    /// <summary>Stops the engine the pid record names, when it is the installed engine.</summary>
    EngineStop StopEngine(Action<string> say);

    /// <summary>Takes back every machine-level action, each step tried whatever the others did; what remains, in words (none when
    /// all were taken back).</summary>
    IReadOnlyList<string> Remove(string showUser, Action<string> say);

    /// <summary>The programs running from the install's folder but this command and the supervisor's own exe; with
    /// <paramref name="exceptTheEngine"/>, also but the supervisor's engine (<see cref="EngineIdentity.IsSupervisedEngine"/>) and what
    /// it or the service started.</summary>
    IReadOnlyList<RunningProgram> RunningFromInstall(bool exceptTheEngine);
}

/// <summary>Where the armed check looks: the engine's folder that holds its pid record, how it finds an engine without a record, which
/// record it trusts, and how long an engine has to answer.</summary>
public sealed record ArmedLook(string Folder, Func<(int Pid, int Port)?> FindEngine, Func<EnginePidRecord, bool> IsEngine, TimeSpan AnswerTime);

/// <summary>The installer's machine-level commands, in their order and with their refusals; what each step does is the machine's
/// (<see cref="IMachineActions"/>). Every one runs the armed check first and fails closed, so no caller can stop an engine that is
/// armed, or one that does not answer, through them.</summary>
public static class MachineCommands
{
    /// <summary>How long the processes an engine started have to end with it before an upgrade is refused.</summary>
    public static readonly TimeSpan ChildrenEnd = TimeSpan.FromSeconds(10);

    /// <summary><c>uninstall</c>: the armed check; nothing else running from the install's folder (refused with words before anything
    /// is stopped, as its files could not be removed); the service stopped; the engine stopped (one that cannot be is refused, the
    /// service started again and nothing removed); then every machine-level action taken back, each tried whatever the others did, and
    /// what remains named.</summary>
    public static async Task<int> UninstallAsync(IMachineActions machine, ArmedLook look, string showUser, TimeZoneInfo zone, Action<string> say,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(say);
        if (await ArmedAsync(look, zone, say, ct).ConfigureAwait(false) is { } refused)
            return refused;
        if (machine.RunningFromInstall(exceptTheEngine: true) is [var other, ..])
        {
            say(Running(machine, other) + ": close it (the desk, or an engine run by hand), then uninstall again");
            return Program.Refused;
        }
        if (!TryStopService(machine, say, "nothing was removed"))
            return Program.Refused;
        if (machine.StopEngine(say) == EngineStop.Failed)
        {
            StartAgain(machine, say);
            say("the engine still runs: the service is started again and nothing is removed");
            return Program.Refused;
        }
        var remains = machine.Remove(showUser, say);
        if (remains.Count == 0)
            return Program.Done;
        say("not taken back: " + string.Join("; ", remains));
        return Program.Refused;
    }

    /// <summary><c>upgrade-stop</c>, before an upgrade replaces the earlier install's files: the armed check; nothing else running from
    /// the install's folder (a desk, an engine run by hand: refused with words before anything is stopped); the service stopped and
    /// the engine stopped; and once the processes the engine started have ended with it, the files may be replaced. A refusal after
    /// the service stopped starts it again. The machine-level actions stay: the new install's <c>install</c> sets each again.</summary>
    public static async Task<int> UpgradeStopAsync(IMachineActions machine, ArmedLook look, TimeZoneInfo zone, Action<string> say,
        TimeSpan? childrenEnd = null, CancellationToken ct = default)
    {
        var within = childrenEnd ?? ChildrenEnd;
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(say);
        if (await ArmedAsync(look, zone, say, ct).ConfigureAwait(false) is { } refused)
            return refused;
        if (machine.RunningFromInstall(exceptTheEngine: true) is [var other, ..])
        {
            say(Running(machine, other) + ": close it (the desk, or an engine run by hand), then install again");
            return Program.Refused;
        }
        if (!TryStopService(machine, say, "nothing was replaced"))
            return Program.Refused;
        if (machine.StopEngine(say) == EngineStop.Failed)
        {
            StartAgain(machine, say);
            say("the engine still runs: the service is started again and nothing is replaced");
            return Program.Refused;
        }
        var left = machine.RunningFromInstall(exceptTheEngine: false);
        var waited = TimeSpan.Zero;
        while (left.Count > 0 && waited < within)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            waited += TimeSpan.FromMilliseconds(500);
            left = machine.RunningFromInstall(exceptTheEngine: false);
        }
        if (left is [var still, ..])
        {
            StartAgain(machine, say);
            say(Running(machine, still) + " after the engine stopped: the service is started again and nothing is replaced");
            return Program.Refused;
        }
        say("the earlier install's service and engine are stopped: its files may be replaced");
        return Program.Done;
    }

    /// <summary>The armed check's refusal (its exit code, with its words said), or null when it goes on.</summary>
    private static async Task<int?> ArmedAsync(ArmedLook look, TimeZoneInfo zone, Action<string> say, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(look);
        var facts = await PreinstallCheck.LookAsync(look.Folder, look.FindEngine, look.IsEngine, look.AnswerTime, ct).ConfigureAwait(false);
        var verdict = PreinstallCheck.Decide(facts, zone);
        say(verdict.Words);
        return verdict.GoOn ? null : PreinstallVerdict.RefusedExit;
    }

    private static bool TryStopService(IMachineActions machine, Action<string> say, string unchanged)
    {
        try
        {
            machine.StopService(say);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            say("the service could not be stopped (" + ex.Message + "): " + unchanged);
            return false;
        }
    }

    private static void StartAgain(IMachineActions machine, Action<string> say)
    {
        try
        {
            machine.StartService(say);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            say("the service could not be started again (" + ex.Message + "): start PlayMapSupervisor by hand");
        }
    }

    private static string Running(IMachineActions machine, RunningProgram program) => string.Create(CultureInfo.InvariantCulture,
        $"a program runs from {machine.InstallDir} (pid {program.Pid}, {program.Image[(program.Image.LastIndexOfAny(['\\', '/']) + 1)..]})");
}
