using System.Diagnostics;
using System.Runtime.Versioning;
using PlayMap.Core.Machine;
using PlayMap.Supervisor.Install;

namespace PlayMap.Supervisor.Windows;

/// <summary>This machine's Windows, as the installer's commands act on it: the service through the service manager, the engine through
/// <see cref="EngineStopper"/>, the machine-level actions through <see cref="MachineSetup"/>, and the programs running from the install's
/// folder by their images.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMachine : IMachineActions
{
    public string InstallDir => SupervisorHost.InstallValue("InstallDir").TrimEnd('\\');

    public void StopService(Action<string> say) => MachineSetup.StopService(say);

    public void StartService(Action<string> say) => MachineSetup.StartService(say);

    /// <summary>The engine the record names, then any supervised engine no record names yet (one the service started moments
    /// before it was stopped): the heavier outcome.</summary>
    public EngineStop StopEngine(Action<string> say)
    {
        var recorded = EngineStopper.Stop(EnginePidRecord.MachineFolder(), InstallDir, MachinePlan.StopEvent, say);
        var unrecorded = EngineStopper.StopUnrecorded(InstallDir, MachinePlan.StopEvent, say);
        return unrecorded > recorded ? unrecorded : recorded;
    }

    public IReadOnlyList<string> Remove(string showUser, Action<string> say) => MachineSetup.Uninstall(showUser, say);

    public IReadOnlyList<RunningProgram> RunningFromInstall(bool exceptTheEngine)
    {
        var dir = InstallDir;
        if (dir.Length == 0)
            return [];
        var supervisor = Path.Combine(dir, "Supervisor", "PlayMap.Supervisor.exe");
        var exe = Path.Combine(dir, "PlayMap.exe");
        var under = new List<(int Pid, ProcessFacts Facts)>();
        var supervised = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId)
                    continue;
                var facts = EngineProcess.ReadFacts(process.Id);
                if (!facts.Exists || !Under(facts.ImagePath, dir))
                    continue;
                // The service, and the engine it keeps whether or not its record is written yet.
                if (string.Equals(facts.ImagePath, supervisor, StringComparison.OrdinalIgnoreCase) || EngineIdentity.IsSupervisedEngine(facts, exe))
                    supervised.Add(process.Id);
                under.Add((process.Id, facts));
            }
        }
        var running = new List<RunningProgram>();
        foreach (var (pid, facts) in under)
        {
            if (string.Equals(facts.ImagePath, supervisor, StringComparison.OrdinalIgnoreCase))
                continue;
            if (exceptTheEngine && (supervised.Contains(pid) || supervised.Contains(facts.ParentPid)))
                continue;
            running.Add(new RunningProgram(pid, facts.ImagePath));
        }
        return running;
    }

    private static bool Under(string image, string dir) =>
        image.Length > dir.Length && image.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase);
}
