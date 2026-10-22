using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;

namespace PlayMap.Net.Bus;

/// <summary>How the hello runs; the defaults are the contract's.</summary>
public sealed record HelloOptions
{
    /// <summary>The port this node listens for hellos on (0 = any free port, for tests).</summary>
    public int ListenPort { get; init; } = ShowBusConstants.HelloPort;

    /// <summary>Where the hello goes; null = 255.255.255.255:9928 and 127.0.0.1:9928.</summary>
    public IReadOnlyList<IPEndPoint>? Targets { get; init; }

    public TimeSpan Every { get; init; } = TimeSpan.FromMilliseconds(ShowBusConstants.HelloEveryMs);

    /// <summary>How often the peer table is aged.</summary>
    public TimeSpan SweepEvery { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>The Show Bus hello (§2): this node's hello every 2 s to the broadcast address and loopback on UDP 9928
/// from a socket bound to the node's bind address (none at all when the node is bound to loopback only, or does not
/// announce), and every other node's hello taken in on 9928 shared with every other app on this machine
/// (SO_REUSEADDR, and SO_REUSEPORT where the platform needs it). This process's own hellos are known by the
/// instance word and ignored. Never sends on Patterns' own beacon port.</summary>
public sealed class HelloService : IAsyncDisposable
{
    private readonly NodeSettings _settings;
    private readonly Func<ShowBusHello> _hello;
    private readonly HelloOptions _options;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _loops = [];
    private IReadOnlyList<IPEndPoint> _targets;
    private Socket? _listen;
    private Socket? _send;
    private long _sent;
    private long _heard;
    private long _dropped;
    private int _disposed;

    /// <param name="settings">The node's id, bind and announce switch.</param>
    /// <param name="hello">What to say, read before each send (a renamed node says its new name at once);
    /// <see cref="PlayMapHello"/> builds PlayMap's. Its instance word is replaced with this service's.</param>
    public HelloService(NodeSettings settings, Func<ShowBusHello> hello, IMonoClock? clock = null, HelloOptions? options = null, ILogger? log = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _hello = hello ?? throw new ArgumentNullException(nameof(hello));
        _options = options ?? new HelloOptions();
        _log = log ?? NullLogger.Instance;
        Peers = new PeerTable(clock ?? MonoClock.Instance);
        _targets = Checked(_options.Targets ??
        [
            new IPEndPoint(IPAddress.Broadcast, ShowBusConstants.HelloPort),
            new IPEndPoint(IPAddress.Loopback, ShowBusConstants.HelloPort),
        ]);
    }

    /// <summary>Eight hex, new per process: a datagram carrying it is this process's own.</summary>
    public string Instance { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    public PeerTable Peers { get; }

    /// <summary>The port hellos are heard on (after <see cref="Start"/>).</summary>
    public int ListenPort { get; private set; }

    /// <summary>Hellos that left this node's socket.</summary>
    public long Sent => Interlocked.Read(ref _sent);

    /// <summary>Hellos heard from other nodes, each counted once the peer table has taken it in and told its listeners, so a reader
    /// that sees the count sees what the table made of every hello it counts.</summary>
    public long Heard => Interlocked.Read(ref _heard);

    /// <summary>Datagrams on the port that were not a hello (or too big): dropped, never fatal.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Whether this node says hello at all: it announces and is not bound to loopback only.</summary>
    public bool Announcing => _settings.Announce && !NetAddress.IsLoopbackOnly(_settings.Bind);

    public IReadOnlyList<IPEndPoint> Targets
    {
        get => _targets;
        set => _targets = Checked(value);
    }

    /// <summary>The capabilities round 2 adds to the contract's: <c>eye</c> (EYE / EYE STATUS answer the board in Patterns' EyeJson
    /// shape) and <c>show</c> (Show Mode's facts in STATE and the context) — sixteen in all, every one a strict reader keeps.</summary>
    public static readonly IReadOnlyList<string> RoundTwoCaps = ["eye", "show"];

    /// <summary>PlayMap's hello: kind playmap, its ports (http, ws and https on one port when it serves TLS, wire,
    /// sync), its capabilities, its clock, its certificate's fingerprint and the build it runs (<paramref name="build"/>, this
    /// process's own unless told). No show data, no secret.</summary>
    public static ShowBusHello PlayMapHello(NodeSettings settings, long clockUnixMs, string? tlsFingerprint, string? host = null, BuildInfo? build = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var ports = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["http"] = settings.HttpPort,
            ["ws"] = settings.HttpPort,
            ["wire"] = settings.WirePort,
            ["sync"] = settings.SyncPort,
        };
        if (tlsFingerprint is not null)
            ports["https"] = settings.HttpPort;
        return new ShowBusHello(ShowBusConstants.KindPlayMap, 1, 1, settings.NodeId, settings.Name, host ?? Environment.MachineName,
            ports, [.. ShowBusHello.PlayMapCaps, .. RoundTwoCaps.Where(c => !ShowBusHello.PlayMapCaps.Contains(c, StringComparer.Ordinal))],
            clockUnixMs, null, tlsFingerprint) { Build = (build ?? BuildInfo.Current).Informational };
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_listen is not null)
            return;
        var bind = NetAddress.ParseBind(_settings.Bind);
        _listen = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        Share(_listen);
        var listenOn = bind is not null && IPAddress.IsLoopback(bind) ? IPAddress.Loopback : IPAddress.Any;
        _listen.Bind(new IPEndPoint(listenOn, _options.ListenPort));
        ListenPort = ((IPEndPoint)_listen.LocalEndPoint!).Port;
        _loops.Add(Task.Run(() => ListenAsync(_listen, _stop.Token), CancellationToken.None));
        _loops.Add(Task.Run(() => SweepAsync(_stop.Token), CancellationToken.None));
        if (Announcing && (bind is null || bind.AddressFamily == AddressFamily.InterNetwork))
        {
            _send = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
            _send.Bind(new IPEndPoint(bind ?? IPAddress.Any, 0));
            _loops.Add(Task.Run(() => SayAsync(_send, _stop.Token), CancellationToken.None));
        }
        else if (Announcing)
        {
            _log.LogWarning("The hello is IPv4 broadcast: a node bound to an IPv6 address says none.");
        }
    }

    private static IReadOnlyList<IPEndPoint> Checked(IReadOnlyList<IPEndPoint> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Any(t => t.Port == ShowBusConstants.PatternsBeaconPortNeverSend))
            throw new ArgumentException("UDP 9700 is Patterns' own beacon: nothing else sends on it", nameof(targets));
        return targets.ToList();
    }

    /// <summary>The port is shared with every other show app on this machine.</summary>
    private void Share(Socket socket)
    {
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            // SO_REUSEPORT: Linux (15) and macOS (0x200) need it beside SO_REUSEADDR for two sockets on one UDP port.
            if (OperatingSystem.IsLinux())
                socket.SetRawSocketOption(1, 15, BitConverter.GetBytes(1));
            else if (OperatingSystem.IsMacOS())
                socket.SetRawSocketOption(0xffff, 0x0200, BitConverter.GetBytes(1));
        }
        catch (SocketException ex)
        {
            // Without it the port is still ours; another app on this machine may not share it.
            _log.LogDebug(ex, "SO_REUSEPORT is not available here.");
        }
    }

    private async Task SayAsync(Socket socket, CancellationToken ct)
    {
        do
        {
            try
            {
                var bytes = (_hello() with { Instance = Instance }).ToJson();
                if (bytes.Length > ShowBusConstants.HelloMaxBytes)
                {
                    _log.LogWarning("The hello is {Bytes} bytes, past the contract's {Max}: not sent.", bytes.Length, ShowBusConstants.HelloMaxBytes);
                }
                else
                {
                    foreach (var target in _targets)
                        await SendOneAsync(socket, bytes, target, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A hello that cannot be built this time is said again in two seconds.
                _log.LogWarning(ex, "The hello could not be made.");
            }
        }
        while (await Waits.DelayAsync(_options.Every, ct));
    }

    private async Task SendOneAsync(Socket socket, byte[] bytes, IPEndPoint target, CancellationToken ct)
    {
        try
        {
            await socket.SendToAsync(bytes, SocketFlags.None, target, ct);
            Interlocked.Increment(ref _sent);
        }
        catch (SocketException ex)
        {
            // A network with no broadcast route (a laptop off the LAN) still hears this machine on loopback.
            _log.LogDebug(ex, "The hello to {Target} did not leave ({Error}).", target, ex.SocketErrorCode);
        }
    }

    private async Task ListenAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                // A reset from an unreachable port (Windows reports it on UDP) is not this listener's end.
                _log.LogDebug(ex, "A hello receive failed ({Error}).", ex.SocketErrorCode);
                continue;
            }
            if (r.ReceivedBytes > 4096 || ShowBusHello.Parse(buffer.AsSpan(0, r.ReceivedBytes)) is not { } hello)
            {
                Interlocked.Increment(ref _dropped);
                continue;
            }
            if (hello.Instance == Instance)
                continue;
            var from = r.RemoteEndPoint is IPEndPoint ep ? NetAddress.Text(ep.Address) : "";
            try
            {
                Peers.Hear(hello, from);
            }
            catch (Exception ex)
            {
                // A listener to the peer table that faults must not stop the hello.
                _log.LogWarning(ex, "A peer-table listener faulted.");
            }
            Interlocked.Increment(ref _heard);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        while (await Waits.DelayAsync(_options.SweepEvery, ct))
        {
            try
            {
                Peers.Sweep();
            }
            catch (Exception ex)
            {
                // A listener that faults on one sweep hears the next.
                _log.LogWarning(ex, "A peer-table listener faulted.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stop.CancelAsync();
        _listen?.Dispose();
        _send?.Dispose();
        try
        {
            await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Stopping: a loop that ends badly as its socket closes is not news.
            _log.LogDebug(ex, "The hello stopped with a loop still closing.");
        }
        _stop.Dispose();
    }
}
