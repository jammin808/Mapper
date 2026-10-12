using System.Net;
using PlayMap.Devices.Drivers;
using PlayMap.Devices.Drivers.PjLink;
using PlayMap.Devices.Model;
using PlayMap.Devices.Transport;

namespace PlayMap.Devices.Manager;

/// <summary>What to look for and how. The sweep is off by default: it knocks on every address of a subnet.</summary>
public sealed record DiscoveryOptions
{
    /// <summary>Where the search goes: the broadcast address and PJLink's port by default.</summary>
    public IPEndPoint SearchTarget { get; init; } = new(IPAddress.Broadcast, PjLinkProtocol.Port);

    /// <summary>How long answers are collected. Projectors answer within moments; a slow one may need longer.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Also try TCP 4352 on every address of the /24 around <see cref="SweepAround"/>.</summary>
    public bool SweepSubnet { get; init; }

    public IPAddress? SweepAround { get; init; }

    /// <summary>The port the sweep and the identification use (4352; tests use their own).</summary>
    public int PjLinkPort { get; init; } = PjLinkProtocol.Port;

    /// <summary>A credential to try on protected projectors (a venue often sets one PJLink password for all).</summary>
    public string CredentialRef { get; init; } = "";

    /// <summary>Also try a TCP connection to the suggested native protocol's port, to say whether it answers.</summary>
    public bool ProbeNativePort { get; init; } = true;

    public DriverTimings Timings { get; init; } = DriverTimings.Default;
}

/// <summary>A projector found on the network, what it says it is, and the driver PlayMap suggests for it.</summary>
public sealed record DiscoveredProjector(
    string Host,
    string Mac,
    int PjLinkClass,
    string? Manufacturer,
    string? Model,
    string? Info,
    string? Name,
    string? Serial,
    ProjectorProtocol Suggested,
    string Why,
    bool? NativePortOpen,
    string Problem);

/// <summary>Which driver suits a projector, from what PJLink's INF1/INF2 say it is.</summary>
public static class DriverSuggestion
{
    /// <summary>The suggested protocol and the reason in words. PJLink stays the suggestion where the native driver
    /// is unverified — the operator can switch after testing.</summary>
    public static (ProjectorProtocol Protocol, string Why) For(string? manufacturer, string? model)
    {
        var m = (manufacturer ?? "").ToUpperInvariant();
        var p = (model ?? "").ToUpperInvariant();
        if (m.Contains("CHRISTIE", StringComparison.Ordinal))
            return (ProjectorProtocol.ChristieAscii, "Christie's serial protocol (TCP 3002) adds lens positions, test patterns and freeze");
        if (m.Contains("PANASONIC", StringComparison.Ordinal))
            return (ProjectorProtocol.PanasonicNtControl, "NTCONTROL (TCP 1024) adds lens positions and memory, test patterns, geometry off, edge blend and temperatures");
        if (m.Contains("BARCO", StringComparison.Ordinal))
            return (ProjectorProtocol.BarcoPulse, "Barco's Pulse API (TCP 9090) adds shutter position, lens steps and identity — on Pulse-platform models; others stay on PJLink");
        if (m.Contains("EPSON", StringComparison.Ordinal) || p.StartsWith("EB-", StringComparison.Ordinal))
            return (ProjectorProtocol.EpsonEscVpNet, "ESC/VP.net (TCP 3629) adds freeze, lens memory and the error code");
        if (m.Contains("SONY", StringComparison.Ordinal))
            return (ProjectorProtocol.PjLink, "Sony's ADCP driver is unverified — PJLink until ADCP is tested on this model");
        if (m.Contains("NEC", StringComparison.Ordinal) || m.Contains("SHARP", StringComparison.Ordinal))
            return (ProjectorProtocol.PjLink, "the NEC binary driver is unverified — PJLink until it is tested on this model");
        return (ProjectorProtocol.PjLink, "PJLink covers power, input, shutter and health on every projector");
    }

    /// <summary>The native protocol the manufacturer has, even where PlayMap keeps PJLink as the suggestion.</summary>
    public static ProjectorProtocol? Native(string? manufacturer)
    {
        var m = (manufacturer ?? "").ToUpperInvariant();
        if (m.Contains("CHRISTIE", StringComparison.Ordinal))
            return ProjectorProtocol.ChristieAscii;
        if (m.Contains("PANASONIC", StringComparison.Ordinal))
            return ProjectorProtocol.PanasonicNtControl;
        if (m.Contains("BARCO", StringComparison.Ordinal))
            return ProjectorProtocol.BarcoPulse;
        if (m.Contains("EPSON", StringComparison.Ordinal))
            return ProjectorProtocol.EpsonEscVpNet;
        if (m.Contains("SONY", StringComparison.Ordinal))
            return ProjectorProtocol.SonyAdcp;
        if (m.Contains("NEC", StringComparison.Ordinal) || m.Contains("SHARP", StringComparison.Ordinal))
            return ProjectorProtocol.NecBinary;
        return null;
    }
}

/// <summary>
/// Finds projectors: the PJLink class 2 search (and, when asked, a TCP sweep for class 1 projectors and routed
/// networks), then asks each one who it is over PJLink (INF1, INF2, NAME, CLSS, SNUM) and suggests a driver.
/// </summary>
public sealed class ProjectorDiscovery(ICredentialStore credentials, IDeviceLog log)
{
    /// <summary>Searches with <paramref name="udp"/> (bound to UDP 4352 in a venue) and identifies what answered.</summary>
    public async Task<IReadOnlyList<DiscoveredProjector>> DiscoverAsync(PjLinkUdp udp, DiscoveryOptions options, CancellationToken ct)
    {
        var hits = await udp.SearchAsync(options.SearchTarget, options.Window, ct).ConfigureAwait(false);
        var hosts = hits.GroupBy(h => h.Address).ToDictionary(g => g.Key, g => g.First().Mac);
        if (options.SweepSubnet && options.SweepAround is { } around)
        {
            var swept = await PjLinkUdp.SweepAsync(PjLinkUdp.Subnet24(around), options.PjLinkPort, options.Timings.Connect, 32, ct).ConfigureAwait(false);
            foreach (var a in swept)
                hosts.TryAdd(a, "");
        }
        var found = new List<DiscoveredProjector>();
        foreach (var (address, mac) in hosts.OrderBy(h => h.Key.ToString(), StringComparer.Ordinal))
            found.Add(await IdentifyAsync(address.ToString(), mac, options, ct).ConfigureAwait(false));
        return found;
    }

    /// <summary>Asks one projector who it is over PJLink and suggests a driver.</summary>
    public async Task<DiscoveredProjector> IdentifyAsync(string host, string mac, DiscoveryOptions options, CancellationToken ct)
    {
        var link = new ProjectorLink("dvdiscover", host, ProjectorProtocol.PjLink, host, options.PjLinkPort, options.CredentialRef);
        await using var driver = new PjLinkDriver(link, credentials, log, options.Timings);
        var reading = await driver.ReadStatusAsync(ct).ConfigureAwait(false);
        var s = reading.State;
        var (suggested, why) = DriverSuggestion.For(s?.Manufacturer, s?.Model);
        bool? open = null;
        if (options.ProbeNativePort && DriverSuggestion.Native(s?.Manufacturer) is { } native)
            open = await PortOpenAsync(host, ProjectorProtocols.DefaultPort(native), options.Timings.Connect, ct).ConfigureAwait(false);
        return new DiscoveredProjector(host, mac, driver.ProjectorClass, s?.Manufacturer, s?.Model, s?.Info, s?.ProjectorName, s?.Serial,
            suggested, why, open, reading.Reached ? "" : reading.Result.Words);
    }

    private static async Task<bool> PortOpenAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await using var ch = await TcpChannel.ConnectAsync(host, port, timeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (DeviceIoException)
        {
            return false;
        }
    }
}
