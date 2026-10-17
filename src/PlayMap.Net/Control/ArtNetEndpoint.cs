using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using PlayMap.Core.Dmx;

namespace PlayMap.Net.Control;

/// <summary>The node's one Art-Net socket (UDP 6454): opened the first time a consumer subscribes or sends, bound to the configured
/// interface ("" = all), with broadcast on and the address reusable so other Art-Net programs on this machine still hear
/// broadcasts. When the port is held by a program that does not share it, the socket sends from an ephemeral port and says so
/// in <see cref="Problem"/>. Receive runs on its own thread into one reused buffer and address; send uses the span overload with a
/// cached <see cref="SocketAddress"/> per destination, so a warm send allocates nothing.</summary>
public sealed class ArtNetEndpoint : IArtNetEndpoint
{
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<IPEndPoint, SocketAddress> _addresses = new();
    private readonly int _port;
    private Socket? _socket;
    private Thread? _thread;
    private ArtNetReceived? _received;
    private volatile bool _disposed;

    /// <param name="interfaceAddress">"" = all interfaces; an address = that interface only.</param>
    /// <param name="port">6454 on a node; a test may give 0 for an ephemeral port.</param>
    public ArtNetEndpoint(string interfaceAddress, int port = ArtNet.Port)
    {
        Interface = string.IsNullOrWhiteSpace(interfaceAddress) ? null
            : IPAddress.TryParse(interfaceAddress, out var a) ? a : throw new ArgumentException($"{interfaceAddress} is no address", nameof(interfaceAddress));
        _port = port;
    }

    public event ArtNetReceived? Received
    {
        add
        {
            lock (_gate)
                _received += value;
            Open();
        }
        remove
        {
            lock (_gate)
                _received -= value;
        }
    }

    public IPAddress? Interface { get; }

    public string? Problem { get; private set; }

    public bool IsOpen => _socket is not null;

    /// <summary>The port the socket is bound to (6454, or the ephemeral one it fell back to), or 0 while closed.</summary>
    public int LocalPort => (_socket?.LocalEndPoint as IPEndPoint)?.Port ?? 0;

    private long _packets;
    private long _sent;
    private long _faults;

    /// <summary>Packets received.</summary>
    public long Packets => Interlocked.Read(ref _packets);

    /// <summary>Packets sent.</summary>
    public long Sent => Interlocked.Read(ref _sent);

    /// <summary>Consumer faults the receive loop survived.</summary>
    public long Faults => Interlocked.Read(ref _faults);

    public SocketAddress AddressOf(IPEndPoint to)
    {
        ArgumentNullException.ThrowIfNull(to);
        return _addresses.GetOrAdd(to, t => t.Serialize());
    }

    public bool Send(ReadOnlySpan<byte> packet, SocketAddress to)
    {
        ArgumentNullException.ThrowIfNull(to);
        var socket = _socket ?? Open();
        if (socket is null)
            return false;
        try
        {
            socket.SendTo(packet, SocketFlags.None, to);
            Interlocked.Increment(ref _sent);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Opens the socket now (a consumer needs it); answers it, or null with <see cref="Problem"/> set.</summary>
    public Socket? Open()
    {
        lock (_gate)
        {
            if (_disposed)
                return null;
            if (_socket is not null)
                return _socket;
            var bind = Interface ?? IPAddress.Any;
            var socket = new Socket(bind.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.EnableBroadcast = true;
                // Send never blocks: a full send buffer drops the packet (Send answers false).
                socket.Blocking = false;
                try
                {
                    socket.Bind(new IPEndPoint(bind, _port));
                    Problem = null;
                }
                catch (SocketException)
                {
                    socket.Bind(new IPEndPoint(bind, 0));
                    Problem = $"UDP {_port} is in use by another program — sending from an ephemeral port, hearing nothing";
                }
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                Problem = $"Art-Net could not open on {bind}: {ex.Message}";
                return null;
            }
            _socket = socket;
            _thread = StartReceiving(socket);
            return socket;
        }
    }

    /// <summary>The receive thread for a socket just opened. Its own method: the thread's closure (it captures the socket) is made
    /// only when a socket opens. Written in <see cref="Open"/>, an optimised build would allocate it at the method's entry, on every
    /// send of every frame while the socket cannot open.</summary>
    private Thread StartReceiving(Socket socket)
    {
        var thread = new Thread(() => ReceiveLoop(socket)) { IsBackground = true, Name = "PlayMap artnet" };
        thread.Start();
        return thread;
    }

    private void ReceiveLoop(Socket socket)
    {
        var buffer = new byte[2048];
        var from = new SocketAddress(socket.AddressFamily);
        while (!_disposed)
        {
            int n;
            try
            {
                if (!socket.Poll(250_000, SelectMode.SelectRead))
                    continue;
                n = socket.ReceiveFrom(buffer, SocketFlags.None, from);
            }
            catch (SocketException) when (!_disposed)
            {
                // Would-block, or a refused send to a closed port surfacing here on some stacks: the socket stays open.
                continue;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return;
            }
            Interlocked.Increment(ref _packets);
            ArtNetReceived? handlers;
            lock (_gate)
                handlers = _received;
            try
            {
                handlers?.Invoke(buffer.AsSpan(0, n), from);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // One consumer's fault never closes the socket for the others; it is counted.
                Interlocked.Increment(ref _faults);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Socket? socket;
        Thread? thread;
        lock (_gate)
        {
            _disposed = true;
            socket = _socket;
            thread = _thread;
            _socket = null;
        }
        socket?.Dispose();
        thread?.Join(TimeSpan.FromSeconds(2));
        return ValueTask.CompletedTask;
    }
}
