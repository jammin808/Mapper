using System.Net;

namespace PlayMap.Core.Machine;

/// <summary>A door show traffic reaches this node on — the line wire, the web port, the clock sync, OSC, Art-Net, sACN, an NDI or OMT
/// send's listener — by its word ("wire", "web", "sync", "osc", "artnet", "sacn", "ndi", "omt"), and the local address its socket is
/// bound to: <see cref="IPAddress.Any"/> or <see cref="IPAddress.IPv6Any"/> when it listens on every interface.</summary>
public readonly record struct ShowDoor(string Name, IPAddress Address)
{
    /// <summary>Whether the door listens on every interface.</summary>
    public bool Everywhere
    {
        get
        {
            var a = Address.IsIPv4MappedToIPv6 ? Address.MapToIPv4() : Address;
            return a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any);
        }
    }
}
