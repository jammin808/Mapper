using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using PlayMap.Core.Machine;
using PlayMap.Supervisor.Install;

namespace PlayMap.Supervisor.Windows;

/// <summary>Stops the engine a pid record names, as an elevated command does it (<c>stop</c> with no service, <c>uninstall</c>, an
/// upgrade): only when the record names the installed engine (<see cref="EngineIdentity"/>), its stop event first — the one its command
/// line names under <c>stopEventPrefix</c>, set only when SYSTEM or administrators made it — the grace, then termination, each through
/// the handle opened when it was checked, so no other process is ever ended on the record's word and no event a command line chose is
/// ever set.</summary>
[SupportedOSPlatform("windows")]
public static class EngineStopper
{
    /// <summary>How long a normal stop has before termination.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    public static EngineStop Stop(string folder, string installDir, string stopEventPrefix, Action<string> say)
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
        AskToStop(engine, stopEventPrefix, say);
        return Finish(engine, say);
    }

    /// <summary>Stops every supervised engine no record names (one the supervisor started that has not written its record yet):
    /// the installed exe run as the engine with <c>--supervised</c>, checked again through the handle that ends it, each asked on its
    /// own stop event first. The outcome of the worst stop; <see cref="EngineStop.NoneRan"/> when there is none.</summary>
    public static EngineStop StopUnrecorded(string installDir, string stopEventPrefix, Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(say);
        if (installDir.Length == 0)
            return EngineStop.NoneRan;
        var exe = Path.Combine(installDir, "PlayMap.exe");
        var outcome = EngineStop.NoneRan;
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(EngineIdentity.ImageName))
        {
            using (process)
            {
                using var engine = EngineProcess.Open(process.Id, out _);
                if (engine is null || !EngineIdentity.IsSupervisedEngine(engine.Facts(), exe))
                    continue;
                AskToStop(engine, stopEventPrefix, say);
                var stop = Finish(engine, say);
                if (stop > outcome)
                    outcome = stop;
            }
        }
        return outcome;
    }

    /// <summary>The grace, then termination, through the engine's handle.</summary>
    private static EngineStop Finish(EngineProcess engine, Action<string> say)
    {
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

    /// <summary>Sets the stop event <paramref name="engine"/>'s command line names (<see cref="SupervisorNames.StopEventIn"/>) where it
    /// exists, SYSTEM or administrators made it and this caller may set it; otherwise says why it is not set. Either way the grace and
    /// the termination follow.</summary>
    private static void AskToStop(EngineProcess engine, string prefix, Action<string> say)
    {
        var name = SupervisorNames.StopEventIn(engine.Facts().CommandLine, prefix);
        if (name is null)
        {
            say(Words($"the engine (pid {engine.Pid}) names no stop event of a supervisor's: the grace and the termination follow"));
            return;
        }
        try
        {
            if (!EventWaitHandleAcl.TryOpenExisting(name, EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize | EventWaitHandleRights.ReadPermissions,
                    out var stop))
            {
                say("the engine's stop event " + name + " does not exist: the grace and the termination follow");
                return;
            }
            using (stop)
            {
                var owner = (stop.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value ?? "";
                if (!SupervisorNames.StopEventOwnerTrusted(owner))
                {
                    say("the engine's stop event " + name + " was made by " + (owner.Length > 0 ? owner : "an owner Windows does not name")
                        + ", not by SYSTEM or administrators: it is not set, and the grace and the termination follow");
                    return;
                }
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
