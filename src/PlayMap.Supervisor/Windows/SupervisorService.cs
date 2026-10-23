using System.Runtime.Versioning;
using System.ServiceProcess;
using PlayMap.Supervisor.Install;

namespace PlayMap.Supervisor.Windows;

/// <summary>The <c>PlayMapSupervisor</c> service (LocalSystem): Windows' start and stop around <see cref="SupervisorHost"/> on this
/// machine's places. Stopping the service leaves the engine running: the next supervisor adopts it by its pid record.</summary>
[SupportedOSPlatform("windows")]
public sealed class SupervisorService : ServiceBase
{
    private readonly SupervisorHost _host = new(SupervisorPlaces.ThisMachine());

    public SupervisorService()
    {
        ServiceName = MachinePlan.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args) => _host.Start();

    protected override void OnStop() => _host.Halt("the service was stopped: the engine keeps running and the next supervisor adopts it");

    protected override void OnShutdown() => _host.Halt("Windows is shutting down");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _host.Dispose();
        base.Dispose(disposing);
    }
}
