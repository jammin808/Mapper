using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PlayMap.Net.Sync;

/// <summary>A group member's sender of vblank reports to its leader's clock-sync port. It moves bytes: the engine says what to
/// send and when (each output once a second, and at once when its lock state changes). A send that does not leave is counted,
/// never thrown: the leader reads the member as not heard after 5 s.</summary>
public sealed class VblankReporter : IDisposable
{
    private readonly Socket _socket;
    private readonly IPEndPoint _leader;
    private readonly ILogger _log;
    private readonly byte[] _buffer = new byte[VblankReport.Size];
    private readonly Lock _gate = new();
    private long _sent;
    private long _failed;

    public VblankReporter(IPEndPoint leader, ILogger? log = null)
    {
        _leader = leader ?? throw new ArgumentNullException(nameof(leader));
        _log = log ?? NullLogger.Instance;
        _socket = new Socket(leader.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(leader.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
    }

    public IPEndPoint Leader => _leader;

    public long Sent => Interlocked.Read(ref _sent);

    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>Sends one report (any thread); false when it did not leave.</summary>
    public bool Send(in VblankReport report)
    {
        if (!report.IsValid)
            return false;
        lock (_gate)
        {
            report.Write(_buffer);
            try
            {
                _socket.SendTo(_buffer, SocketFlags.None, _leader);
                Interlocked.Increment(ref _sent);
                return true;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                Interlocked.Increment(ref _failed);
                _log.LogDebug(ex, "A vblank report to {Leader} did not leave.", _leader);
                return false;
            }
        }
    }

    public void Dispose() => _socket.Dispose();
}
