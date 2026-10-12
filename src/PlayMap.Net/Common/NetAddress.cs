using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PlayMap.Core.Bus;

namespace PlayMap.Net;

/// <summary>The bind address could not be honoured: the listener stays closed (fail closed) rather than open on
/// every interface. The message is the words the operator reads.</summary>
public sealed class BindRefusedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Addresses as the gates compare them: IPv4 written plainly even when a dual-mode socket saw it mapped
/// into IPv6, loopback by the address (never by a name), the trusted list by value.</summary>
public static class NetAddress
{
    public static IPAddress Normal(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    public static string Text(IPAddress? address) => address is null ? "?" : Normal(address).ToString();

    public static bool IsLoopback(IPAddress? address) => address is not null && IPAddress.IsLoopback(Normal(address));

    /// <summary>The node asks for a token: one is set (after normalising, so "  " is no token).</summary>
    public static bool TokenNeeded(NodeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Pairing.Normalise(settings.Token).Length > 0;
    }

    /// <summary>Lines from this address count as paired without a proof: this machine (when loopback is trusted)
    /// or an address on the trusted list (a Patterns desk driving the node as a "Lines" device cannot answer a
    /// NONCE).</summary>
    public static bool IsTrusted(IPAddress? address, NodeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (address is null)
            return false;
        var a = Normal(address);
        if (settings.TrustLoopback && IPAddress.IsLoopback(a))
            return true;
        return IsListed(a, settings);
    }

    /// <summary>The address is on the trusted list (by value, whatever loopback trust says).</summary>
    public static bool IsListed(IPAddress? address, NodeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (address is null || settings.TrustedAddresses.IsDefaultOrEmpty)
            return false;
        var a = Normal(address);
        foreach (var t in settings.TrustedAddresses)
            if (IPAddress.TryParse(t.Trim(), out var trusted) && Normal(trusted).Equals(a))
                return true;
        return false;
    }

    /// <summary>The address a listener binds: null for "" (every interface), the address when the text is one.
    /// Anything else throws <see cref="BindRefusedException"/> — a typo never opens every door.</summary>
    public static IPAddress? ParseBind(string? bind)
    {
        var b = (bind ?? "").Trim();
        if (b.Length == 0)
            return null;
        if (IPAddress.TryParse(b, out var address))
            return Normal(address);
        throw new BindRefusedException($"the bind address '{b}' is not an IP address — the listener stays closed");
    }

    /// <summary>A bind to loopback only: the node talks to this machine alone and says no hello.</summary>
    public static bool IsLoopbackOnly(string? bind) =>
        IPAddress.TryParse((bind ?? "").Trim(), out var a) && IPAddress.IsLoopback(a);

    /// <summary>A listening socket on the bind address (or every interface, dual-stack where the machine has IPv6).
    /// A bind the machine refuses (not one of its addresses, a port in use) throws <see cref="BindRefusedException"/>.</summary>
    public static Socket Listen(string? bind, int port, int backlog = 128)
    {
        var address = ParseBind(bind);
        Socket? socket = null;
        try
        {
            var a = address ?? (Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any);
            socket = new Socket(a.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            if (address is null && a.AddressFamily == AddressFamily.InterNetworkV6)
                socket.DualMode = true;
            // A restarted node takes its port back at once (past its old connections' TIME_WAIT) with the raw
            // SO_REUSEADDR — .NET's ReuseAddress also sets SO_REUSEPORT on Unix, which would let a second process
            // listen on the same port. On Windows the option would let another process share the port, so there
            // the socket keeps it to itself instead.
            if (OperatingSystem.IsWindows())
                socket.ExclusiveAddressUse = true;
            else if (OperatingSystem.IsLinux())
                socket.SetRawSocketOption(1, 2, BitConverter.GetBytes(1));
            else if (OperatingSystem.IsMacOS())
                socket.SetRawSocketOption(0xffff, 0x4, BitConverter.GetBytes(1));
            socket.Bind(new IPEndPoint(a, port));
            socket.Listen(backlog);
            return socket;
        }
        catch (SocketException ex)
        {
            socket?.Dispose();
            throw new BindRefusedException($"could not listen on {(address is null ? "every interface" : address.ToString())} port {port} ({ex.SocketErrorCode}) — the listener stays closed", ex);
        }
    }

    /// <summary>This machine's own unicast addresses on interfaces that are up (loopback excluded) — read from the
    /// interfaces, never the resolver.</summary>
    public static IReadOnlyList<IPAddress> LanAddresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                {
                    var a = u.Address;
                    if (IPAddress.IsLoopback(a) || a.IsIPv6Multicast)
                        continue;
                    if (a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 && !list.Contains(a))
                        list.Add(a);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // A machine that cannot list its interfaces answers on its names only; nothing else depends on the list.
            return list;
        }
        return list;
    }
}
