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

    public EngineStop StopEngine(Action<string> say) =>
        EngineStopper.Stop(EnginePidRecord.MachineFolder(), InstallDir, MachinePlan.StopEvent, say);

    public IReadOnlyList<string> Remove(string showUser, Action<string> say) => MachineSetup.Uninstall(showUser, say);

    public IReadOnlyList<RunningProgram> RunningFromInstall(bool exceptTheEngine)
    {
        var dir = InstallDir;
        if (dir.Length == 0)
            return [];
        var supervisor = Path.Combine(dir, "Supervisor", "PlayMap.Supervisor.exe");
        var engine = exceptTheEngine ? SupervisedEnginePid(dir) : 0;
        var running = new List<RunningProgram>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId)
                    continue;
                var facts = EngineProcess.ReadFacts(process.Id);
                if (!facts.Exists || !Under(facts.ImagePath, dir) || string.Equals(facts.ImagePath, supervisor, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (engine != 0 && (process.Id == engine || facts.ParentPid == engine))
                    continue;
                running.Add(new RunningProgram(process.Id, facts.ImagePath));
            }
        }
        return running;
    }

    /// <summary>The installed engine the pid record names (0 when it names none that passes <see cref="EngineIdentity"/>).</summary>
    private static int SupervisedEnginePid(string dir)
    {
        var folder = EnginePidRecord.MachineFolder();
        if (!Directory.Exists(folder) || EnginePidRecord.Named(folder) is not { } record)
            return 0;
        using var engine = EngineProcess.Adopt(record, new EngineExpectation(Path.Combine(dir, "PlayMap.exe"), "", -1), out _);
        return engine?.Pid ?? 0;
    }

    private static bool Under(string image, string dir) =>
        image.Length > dir.Length && image.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase);
}
