using PlayMap.Devices.Drivers.Barco;
using PlayMap.Devices.Drivers.Christie;
using PlayMap.Devices.Drivers.Epson;
using PlayMap.Devices.Drivers.Nec;
using PlayMap.Devices.Drivers.Panasonic;
using PlayMap.Devices.Drivers.PjLink;
using PlayMap.Devices.Drivers.Sony;
using PlayMap.Devices.Model;

namespace PlayMap.Devices.Drivers;

/// <summary>Makes drivers for links — the seam the manager uses, so tests can hand it drivers of their own.</summary>
public interface IProjectorDriverFactory
{
    IProjectorDriver Create(ProjectorLink link);
}

/// <summary>The drivers PlayMap ships, one per protocol.</summary>
public sealed class ProjectorDrivers(ICredentialStore credentials, IDeviceLog log, DriverTimings? timings = null) : IProjectorDriverFactory
{
    public IProjectorDriver Create(ProjectorLink link) => link.Protocol switch
    {
        ProjectorProtocol.ChristieAscii => new ChristieDriver(link, credentials, log, timings),
        ProjectorProtocol.PanasonicNtControl => new PanasonicDriver(link, credentials, log, timings),
        ProjectorProtocol.BarcoPulse => new BarcoPulseDriver(link, credentials, log, timings),
        ProjectorProtocol.SonyAdcp => new SonyAdcpDriver(link, credentials, log, timings),
        ProjectorProtocol.EpsonEscVpNet => new EpsonDriver(link, credentials, log, timings),
        ProjectorProtocol.NecBinary => new NecDriver(link, credentials, log, timings),
        _ => new PjLinkDriver(link, credentials, log, timings),
    };
}
