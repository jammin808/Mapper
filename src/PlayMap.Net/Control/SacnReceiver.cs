using System.Net;
using System.Net.Sockets;
using PlayMap.Core.Dmx;

namespace PlayMap.Net.Control;

/// <summary>Receives one sACN datagram: the bytes (valid only during the call) and the sender.</summary>
public delegate void SacnReceived(ReadOnlySpan<byte> packet, SocketAddress from);

/// <summary>The node's sACN receiver (UDP 5568): a unicast socket on the configured interface ("" = all) and the multicast groups of
/// the universes it is told to hear (239.255.u/256.u%256), joined on that interface. On Linux each group has a socket bound to the
/// group's own address (so a node hears only the groups it joined, and the unicast socket is kept out of every group); elsewhere one
/// socket bound to all addresses joins every group. Sockets are shared with other programs (address reuse). A port or group that
/// cannot be opened is a <see cref="Problem"/> with words, never a throw; one consumer's fault never closes a socket.</summary>
public sealed class SacnReceiver : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly int _port;
    private readonly Dictionary<int, Listener> _groups = [];
    private Listener? _unicast;
    private SacnReceived? _received;
    private volatile bool _disposed;
    private long _packets;
    private long _faults;

    /// <param name="interfaceAddress">"" = all interfaces; an IPv4 address = that interface only.</param>
    /// <param name="port">5568 on a node.</param>
    public SacnReceiver(string interfaceAddress, int port = Sacn.Port)
    {
        Interface = string.IsNullOrWhiteSpace(interfaceAddress) ? null
            : IPAddress.TryParse(interfaceAddress, out var a) ? a : throw new ArgumentException($"{interfaceAddress} is no address", nameof(interfaceAddress));
        _port = port;
    }

    public event SacnReceived? Received
    {
        add
        {
            lock (_gate)
                _received += value;
        }
        remove
        {
            lock (_gate)
                _received -= value;
        }
    }

    public IPAddress? Interface { get; }

    public string? Problem { get; private set; }

    public bool IsOpen
    {
        get
        {
            lock (_gate)
                return _unicast is not null;
        }
    }

    /// <summary>The universes whose groups are joined.</summary>
    public IReadOnlyList<int> Joined
    {
        get
        {
            lock (_gate)
                return [.. _groups.Keys.Order()];
        }
    }

    public long Packets => Interlocked.Read(ref _packets);

    public long Faults => Interlocked.Read(ref _faults);

    private static bool GroupSockets => OperatingSystem.IsLinux();

    /// <summary>Opens the unicast socket and joins exactly the groups of these universes (leaving the others).</summary>
    public void Listen(IReadOnlyCollection<int> universes)
    {
        ArgumentNullException.ThrowIfNull(universes);
        var stopped = new List<Listener>();
        lock (_gate)
        {
            if (_disposed)
                return;
            var problems = new List<string>();
            if (_unicast is null)
            {
                var bind = Interface ?? IPAddress.Any;
                _unicast = Open(new IPEndPoint(bind, _port), "sACN", problems, socket =>
                {
                    if (GroupSockets && Interface is null)
                        HearOwnGroupsOnly(socket);
                });
            }
            var wanted = universes.Where(u => u is >= 1 and <= Sacn.MaxUniverse).ToHashSet();
            foreach (var u in _groups.Keys.Where(u => !wanted.Contains(u)).ToList())
            {
                if (Leave(u, _groups[u]) is { } stopping)
                    stopped.Add(stopping);
                _groups.Remove(u);
            }
            foreach (var u in wanted.Where(u => !_groups.ContainsKey(u)).Order())
            {
                if (Join(u, problems) is { } listener)
                    _groups[u] = listener;
            }
            Problem = problems.Count == 0 ? null : string.Join("; ", problems);
        }
        foreach (var l in stopped)
            l.Stop();
    }

    /// <summary>Linux: a socket bound to all addresses hears every group joined on the machine unless IP_MULTICAST_ALL (49) is off.</summary>
    private static void HearOwnGroupsOnly(Socket socket)
    {
        try
        {
            socket.SetRawSocketOption(0, 49, BitConverter.GetBytes(0));
        }
        catch (SocketException)
        {
            // An older kernel without the option: duplicates of group packets are dropped by the sequence check.
        }
    }

    /// <summary>Closes every socket (sACN input turned off).</summary>
    public void Close() => Close(join: false);

    private void Close(bool join)
    {
        List<Listener> closing;
        lock (_gate)
        {
            closing = [.. _groups.Values.Where(l => !ReferenceEquals(l, _unicast))];
            if (_unicast is not null)
                closing.Add(_unicast);
            _groups.Clear();
            _unicast = null;
            Problem = null;
        }
        foreach (var l in closing)
            l.Stop(join);
    }

    private Listener? Join(int universe, List<string> problems)
    {
        var group = Sacn.GroupOf(universe);
        var on = Interface ?? IPAddress.Any;
        if (!GroupSockets)
        {
            if (_unicast is null)
                return null;
            try
            {
                _unicast.Socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group, on));
                return _unicast;
            }
            catch (SocketException ex)
            {
                problems.Add($"universe {universe}: could not join {group}: {ex.Message}");
                return null;
            }
        }
        return Open(new IPEndPoint(group, _port), $"sACN universe {universe}", problems,
            socket => socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group, on)));
    }

    /// <summary>Leaves a group: its own socket is answered to be stopped (outside the gate); a membership on the shared socket is
    /// dropped.</summary>
    private Listener? Leave(int universe, Listener listener)
    {
        if (!ReferenceEquals(listener, _unicast))
            return listener;
        try
        {
            listener.Socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, new MulticastOption(Sacn.GroupOf(universe), Interface ?? IPAddress.Any));
        }
        catch (SocketException)
        {
            // The membership is gone with the group already.
        }
        return null;
    }

    private Listener? Open(IPEndPoint at, string what, List<string> problems, Action<Socket> configure)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(at);
            configure(socket);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            problems.Add($"{what} could not open UDP {_port} on {at.Address}: {(ex.SocketErrorCode == SocketError.AddressAlreadyInUse ? "another program holds it" : ex.Message)}");
            return null;
        }
        var listener = new Listener(socket);
        listener.Start(this);
        return listener;
    }

    private void Deliver(ReadOnlySpan<byte> packet, SocketAddress from)
    {
        Interlocked.Increment(ref _packets);
        var handlers = Volatile.Read(ref _received);
        try
        {
            handlers?.Invoke(packet, from);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Interlocked.Increment(ref _faults);
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        Close(join: true);
        return ValueTask.CompletedTask;
    }

    /// <summary>One socket and the thread that reads it into one reused buffer and address.</summary>
    private sealed class Listener(Socket socket)
    {
        private volatile bool _stopped;
        private Thread? _thread;

        public Socket Socket { get; } = socket;

        public void Start(SacnReceiver owner)
        {
            _thread = new Thread(() => Loop(owner)) { IsBackground = true, Name = "PlayMap sacn" };
            _thread.Start();
        }

        private void Loop(SacnReceiver owner)
        {
            var buffer = new byte[1024];
            var from = new SocketAddress(AddressFamily.InterNetwork);
            while (!_stopped)
            {
                int n;
                try
                {
                    if (!Socket.Poll(250_000, SelectMode.SelectRead))
                        continue;
                    n = Socket.ReceiveFrom(buffer, SocketFlags.None, from);
                }
                catch (SocketException) when (!_stopped)
                {
                    continue;
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    return;
                }
                owner.Deliver(buffer.AsSpan(0, n), from);
            }
        }

        /// <summary>Closes the socket; the thread ends within its 250 ms poll (joined only when asked: the control loop never waits).</summary>
        public void Stop(bool join = false)
        {
            _stopped = true;
            Socket.Dispose();
            if (join)
                _thread?.Join(TimeSpan.FromSeconds(2));
        }
    }
}
