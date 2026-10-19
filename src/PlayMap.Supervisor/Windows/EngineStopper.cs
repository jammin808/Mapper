using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using PlayMap.Core.Machine;
using PlayMap.Supervisor.Install;

namespace PlayMap.Supervisor.Windows;

/// <summary>Stops the engine a pid record names, as an elevated command does it (<c>stop</c> with no service, <c>uninstall</c>, an
/// upgrade): only when the record names the installed engine (<see cref="EngineIdentity"/>), its stop event first, the grace, then
/// termination, each through the handle opened when it was checked, so no other process is ever ended on the record's word.</summary>
[SupportedOSPlatform("windows")]
public static class EngineStopper
{
    /// <summary>How long a normal stop has before termination.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    public static EngineStop Stop(string folder, string installDir, string stopEventName, Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(say);
        var record = Directory.Exists(folder) ? EnginePidRecord.Named(folder) : null;
        if (record is null || !EngineProcess.ReadFacts(record.Pid).Exists)
        {
            say("no supervised engine runs");
            return EngineStop.NoneRan;
        }
        if (installDir.Length == 0)
        {
            say(Words($"no install is recorded on this machine: pid {record.Pid}, which the pid record names, is not stopped"));
            return EngineStop.NotTheEngine;
        }
        using var engine = EngineProcess.Adopt(record, new EngineExpectation(Path.Combine(installDir, "PlayMap.exe"), "", -1), out var why);
        if (engine is null)
        {
            say(Words($"the pid record names pid {record.Pid}, which is not the installed engine ({why}): not stopped"));
            return EngineStop.NotTheEngine;
        }
        AskToStop(stopEventName, say);
        if (engine.WaitForExit(Grace))
        {
            say(Words($"the engine (pid {engine.Pid}) stopped"));
            return EngineStop.Stopped;
        }
        if (engine.End(out var refused) && engine.WaitForExit(Grace))
        {
            say(Words($"the engine (pid {engine.Pid}) did not stop within {Grace.TotalSeconds:0} s and was terminated"));
            return EngineStop.Terminated;
        }
        say(Words($"the engine (pid {engine.Pid}) did not stop within {Grace.TotalSeconds:0} s and could not be terminated: {(refused.Length > 0 ? refused : "it still runs")}"));
        return EngineStop.Failed;
    }

    /// <summary>Sets the engine's stop event where it exists and this caller may set it; either way the grace and the termination
    /// follow, with words for a refusal.</summary>
    private static void AskToStop(string name, Action<string> say)
    {
        try
        {
            if (EventWaitHandleAcl.TryOpenExisting(name, EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize, out var stop))
            {
                using (stop)
                    stop.Set();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            say("the engine's stop event could not be set (" + ex.Message + "): the grace and the termination follow");
        }
    }

    private static string Words(FormattableString words) => words.ToString(CultureInfo.InvariantCulture);
}
