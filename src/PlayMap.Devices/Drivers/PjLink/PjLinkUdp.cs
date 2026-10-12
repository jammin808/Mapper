using System.Net;
using System.Net.Sockets;
using System.Text;
using PlayMap.Devices.Transport;

namespace PlayMap.Devices.Drivers.PjLink;

/// <summary>A projector that answered a search: its address and the MAC address it gave.</summary>
public sealed record PjLinkSearchHit(IPAddress Address, string Mac);

public sealed class PjLinkNoticeEventArgs(PjLinkNotice notice, IPEndPoint from) : EventArgs
{
    public PjLinkNotice Notice { get; } = notice;

    public IPEndPoint From { get; } = from;
}

/// <summary>
/// PJLink class 2 on UDP 4352: the search ("%2SRCH" broadcast, each projector answers "%2ACKN=mac" to the
/// controller's port 4352) and the status notifications projectors send there ("%2LKUP=mac" when the network
/// comes up, "%2ERST=…", "%2POWR=…", "%2INPT=…" when those change). One socket does both, bound with address
/// reuse so the search's answers and the notifications arrive on the same port the search left from.
/// </summary>
public sealed class PjLinkUdp : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private List<PjLinkSearchHit>? _collecting;
    private Task? _loop;

    /// <param name="port">4352 in a venue; a free port in tests (0 = any).</param>
    /// <param name="bind">The local address to listen on; any by default.</param>
    public PjLinkUdp(int port = PjLinkProtocol.Port, IPAddress? bind = null)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.EnableBroadcast = true;
            _socket.Bind(new IPEndPoint(bind ?? IPAddress.Any, port));
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    public int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;

    /// <summary>The last receive failure in words, "" while receiving works.</summary>
    public string Problem { get; private set; } = "";

    /// <summary>Raised on the receive loop for every class 2 datagram but our own search echo.</summary>
    public event EventHandler<PjLinkNoticeEventArgs>? Noticed;

    public void Start()
    {
        lock (_gate)
            _loop ??= Task.Run(() => ReceiveLoopAsync(_stop.Token), _stop.Token);
    }

    /// <summary>Broadcasts a search to <paramref name="target"/> (the subnet's broadcast address, port 4352) and
    /// collects the answers for <paramref name="window"/>. A projector answers once per search; the same MAC from
    /// two addresses is two hits (two network ports of one box).</summary>
    public async Task<IReadOnlyList<PjLinkSearchHit>> SearchAsync(IPEndPoint target, TimeSpan window, CancellationToken ct)
    {
        Start();
        var hits = new List<PjLinkSearchHit>();
        lock (_gate)
            _collecting = hits;
        try
        {
            await _socket.SendToAsync(Encoding.ASCII.GetBytes(PjLinkProtocol.SearchDatagram), SocketFlags.None, target, ct).ConfigureAwait(false);
            await Task.Delay(window, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
                _collecting = null;
        }
        lock (_gate)
            return hits.DistinctBy(h => (h.Address, h.Mac)).ToList();
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[2048];
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
            {
                // An ICMP "port unreachable" for an earlier send surfaces here on some systems; the socket is fine.
                continue;
            }
            catch (SocketException e)
            {
                Problem = "PJLink UDP receive stopped — " + e.SocketErrorCode;
                break;
            }
            var from = (IPEndPoint)r.RemoteEndPoint;
            var text = Encoding.ASCII.GetString(buffer, 0, r.ReceivedBytes);
            foreach (var line in text.Split('\r', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                Handle(line, from);
        }
    }

    private void Handle(string line, IPEndPoint from)
    {
        var notice = PjLinkProtocol.ParseNotice(line);
        if (notice is null || notice.Kind == PjLinkNoticeKind.Search)
            return;
        if (notice.Kind == PjLinkNoticeKind.SearchAck)
        {
            lock (_gate)
                _collecting?.Add(new PjLinkSearchHit(from.Address, notice.Value));
        }
        try
        {
            Noticed?.Invoke(this, new PjLinkNoticeEventArgs(notice, from));
        }
        catch (Exception e)
        {
            // A subscriber's fault must not stop the receive loop; it is kept as the problem for the desk.
            Problem = "a PJLink notice handler failed — " + e.Message;
        }
    }

    /// <summary>
    /// The optional sweep (off by default — it knocks on every address of a subnet): a TCP connection to port 4352
    /// on each address of <paramref name="addresses"/>, kept when the box greets as PJLink. For networks where
    /// broadcast does not reach (routed VLANs) or class 1 projectors that cannot answer a search.
    /// </summary>
    public static async Task<IReadOnlyList<IPAddress>> SweepAsync(IEnumerable<IPAddress> addresses, int port, TimeSpan timeout, int parallel, CancellationToken ct)
    {
        var found = new List<IPAddress>();
        var gate = new Lock();
        await Parallel.ForEachAsync(addresses, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(parallel, 1, 64), CancellationToken = ct }, async (address, c) =>
        {
            try
            {
                await using var ch = await TcpChannel.ConnectAsync(address.ToString(), port, timeout, c).ConfigureAwait(false);
                var greeting = PjLinkProtocol.ParseGreeting(await ch.ReadUntilAsync((byte)'\r', timeout, c).ConfigureAwait(false));
                if (greeting.Kind is PjLinkGreetingKind.Open or PjLinkGreetingKind.Protected)
                {
                    lock (gate)
                        found.Add(address);
                }
            }
            catch (DeviceIoException)
            {
                // Nothing there, or not PJLink: a sweep expects most addresses to be silent.
            }
        }).ConfigureAwait(false);
        return found.OrderBy(a => a.GetAddressBytes(), ByteOrder.Instance).ToList();
    }

    /// <summary>The 254 host addresses of the /24 around <paramref name="any"/>.</summary>
    public static IEnumerable<IPAddress> Subnet24(IPAddress any)
    {
        var b = any.GetAddressBytes();
        for (var i = 1; i < 255; i++)
            yield return new IPAddress([b[0], b[1], b[2], (byte)i]);
    }

    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static readonly ByteOrder Instance = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (x is null || y is null)
                return (x is null ? 0 : 1) - (y is null ? 0 : 1);
            for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
                if (x[i] != y[i])
                    return x[i].CompareTo(y[i]);
            return x.Length.CompareTo(y.Length);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _socket.Dispose();
        Task? loop;
        lock (_gate)
            loop = _loop;
        if (loop is not null)
            await loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
