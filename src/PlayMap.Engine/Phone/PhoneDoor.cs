using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using PlayMap.Core.Wire;
using PlayMap.Net.Tls;

namespace PlayMap.Engine.Phone;

/// <summary>Where an invite's link points (the node's https address a phone can reach) and the short fingerprint of the certificate
/// the phone will be shown; or, with no address, why there is none.</summary>
public sealed record PhoneDoor(Uri? Base, string Fingerprint, string Why);

/// <summary>
/// The phone's way in, found when an invite is asked for. The certificate is the one the web port really serves — read by a TLS
/// handshake to the port itself, so a node that serves plain HTTP only is seen to (a phone's camera needs https) rather than
/// assumed not to. The address is this machine's on the interface that reaches the asking session (the same subnet), else its first
/// private address, else the bind address.
/// </summary>
public static class PhoneDoors
{
    /// <summary>The door for a session asking, with the web port's number and bind address.</summary>
    public static async Task<PhoneDoor> FindAsync(int port, string bind, SessionInfo session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (port <= 0)
            return new PhoneDoor(null, "", "the web port is closed, so a phone has nothing to open");
        var local = IPAddress.TryParse(bind.Trim(), out var b) && !b.Equals(IPAddress.Any) && !b.Equals(IPAddress.IPv6Any) ? b : IPAddress.Loopback;
        var fingerprint = await ServedFingerprintAsync(local, port, ct).ConfigureAwait(false);
        if (fingerprint is null)
            return new PhoneDoor(null, "", "this node serves no https, and a phone gives its camera only to a secure page — turn on encrypted connections first");
        var address = AddressFor(session, local);
        if (address is null)
            return new PhoneDoor(null, fingerprint, "this machine has no network address a phone could reach");
        var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        return new PhoneDoor(new Uri($"https://{host}:{port}"), fingerprint, "");
    }

    /// <summary>The short fingerprint of the certificate the port serves, or null when it answers no TLS handshake.</summary>
    public static async Task<string?> ServedFingerprintAsync(IPAddress address, int port, CancellationToken ct)
    {
        using var tcp = new TcpClient(address.AddressFamily);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await tcp.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            string? seen = null;
            // Read here, never trusted: the certificate is shown to a person to compare with the phone's, and nothing goes over this
            // connection but the handshake.
#pragma warning disable S4830 // This handshake only reads the certificate the node's own port serves.
            await using var tls = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
            {
                if (cert is not null)
                    seen = TlsFingerprint.Of(cert as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(cert.GetRawCertData()));
                return true;
            });
#pragma warning restore S4830
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "playmap" }, timeout.Token).ConfigureAwait(false);
            return seen is null ? null : TlsFingerprint.Short(seen);
        }
        catch (Exception ex) when (ex is IOException or SocketException or System.Security.Authentication.AuthenticationException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>This machine's address that reaches the session: on its subnet when it came over the network, else the first private
    /// one, else <paramref name="fallback"/> when it is not loopback.</summary>
    public static IPAddress? AddressFor(SessionInfo session, IPAddress fallback)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(fallback);
        var unicast = new List<(IPAddress Address, int Prefix)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork)
                        unicast.Add((u.Address, u.PrefixLength));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No interface list: the fallback below.
        }
        if (IPAddress.TryParse(session.RemoteAddress, out var remote) && !IPAddress.IsLoopback(remote))
        {
            var r = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;
            foreach (var (a, prefix) in unicast)
            {
                if (SameSubnet(a, r, prefix))
                    return a;
            }
        }
        foreach (var (a, _) in unicast)
        {
            if (IsPrivate(a))
                return a;
        }
        if (unicast.Count > 0)
            return unicast[0].Address;
        return IPAddress.IsLoopback(fallback) ? null : fallback;
    }

    private static bool SameSubnet(IPAddress a, IPAddress b, int prefix)
    {
        if (a.AddressFamily != b.AddressFamily || a.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var x = a.GetAddressBytes();
        var y = b.GetAddressBytes();
        for (var bit = 0; bit < Math.Min(prefix, 32); bit++)
        {
            var mask = 0x80 >> (bit % 8);
            if ((x[bit / 8] & mask) != (y[bit / 8] & mask))
                return false;
        }
        return true;
    }

    /// <summary>RFC 1918, carrier-grade NAT and link-local.</summary>
    public static bool IsPrivate(IPAddress a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var b = a.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
            || (b[0] == 100 && b[1] is >= 64 and <= 127) || (b[0] == 169 && b[1] == 254));
    }
}
