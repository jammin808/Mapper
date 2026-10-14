using System.Net;
using System.Net.Sockets;

namespace PlayMap.Net.Control;

/// <summary>Receives one OSC datagram: the bytes (valid only during the call) and the sender.</summary>
public delegate void OscDatagram(ReadOnlySpan<byte> packet, IPEndPoint from);

/// <summary>The node's OSC socket (UDP, 9943 by default): datagrams on its own thread to one handler, replies from the same
/// socket so a client hears them on the port it sent from. A handler's fault is counted and the loop goes on: one datagram can
/// never close the port. A port that cannot be bound is a <see cref="Problem"/> with words, never a throw.</summary>
public sealed class OscServer : IDisposable
{
    private readonly OscDatagram _handler;
    private Socket? _socket;
    private Thread? _thread;
    private volatile bool _disposed;
    private long _faults;
    private long _packets;

    public OscServer(OscDatagram handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    public string? Problem { get; private set; }

    public bool IsOpen => _socket is not null;

    public IPEndPoint? Local => _socket?.LocalEndPoint as IPEndPoint;

    public long Packets => Interlocked.Read(ref _packets);

    public long Faults => Interlocked.Read(ref _faults);

    /// <summary>Binds the address and port (0 = an ephemeral port); answers false with <see cref="Problem"/> set.</summary>
    public bool Start(IPAddress bind, int port)
    {
        ArgumentNullException.ThrowIfNull(bind);
        if (_socket is not null)
            return true;
        var socket = new Socket(bind.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(bind, port));
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            Problem = $"OSC could not open UDP {port} on {bind}: {ex.SocketErrorCode switch { SocketError.AddressAlreadyInUse => "another program holds it", _ => ex.Message }}";
            return false;
        }
        Problem = null;
        _socket = socket;
        _thread = new Thread(() => Loop(socket)) { IsBackground = true, Name = "PlayMap osc" };
        _thread.Start();
        return true;
    }

    /// <summary>Sends a datagram from the server's port; false when closed or refused.</summary>
    public bool Send(ReadOnlySpan<byte> packet, IPEndPoint to)
    {
        ArgumentNullException.ThrowIfNull(to);
        var socket = _socket;
        if (socket is null)
            return false;
        try
        {
            socket.SendTo(packet, SocketFlags.None, to);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    private void Loop(Socket socket)
    {
        var buffer = new byte[65536];
        while (!_disposed)
        {
            EndPoint from = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
            int n;
            try
            {
                n = socket.ReceiveFrom(buffer, ref from);
            }
            catch (SocketException) when (!_disposed)
            {
                continue;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return;
            }
            Interlocked.Increment(ref _packets);
            try
            {
                _handler(buffer.AsSpan(0, n), (IPEndPoint)from);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Interlocked.Increment(ref _faults);
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        var socket = _socket;
        _socket = null;
        socket?.Dispose();
        _thread?.Join(TimeSpan.FromSeconds(2));
    }
}
