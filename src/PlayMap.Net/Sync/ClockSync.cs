using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;

namespace PlayMap.Net.Sync;

/// <summary>One packet of PlayMap's fine clock sync (UDP, between PlayMap nodes). Fixed 40 bytes, little-endian:
/// magic "PMCK", version 1, kind (1 request, 2 reply), two zero bytes, the sequence, four zero bytes, then t1 (the
/// client's monotonic ns when it sent), t2 and t3 (the leader's ns when it read the request and when it answered).
/// Anything else is not a packet: refused, never guessed at (fail closed).</summary>
public readonly record struct SyncPacket(byte Kind, uint Seq, long T1, long T2, long T3)
{
    public const int Size = 40;
    public const byte Request = 1;
    public const byte Reply = 2;
    private const uint Magic = 0x4B434D50;   // "PMCK" read little-endian
    private const byte Version = 1;

    public void Write(Span<byte> b)
    {
        if (b.Length < Size)
            throw new ArgumentException("a sync packet is 40 bytes", nameof(b));
        b[..Size].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(b, Magic);
        b[4] = Version;
        b[5] = Kind;
        BinaryPrimitives.WriteUInt32LittleEndian(b[8..], Seq);
        BinaryPrimitives.WriteInt64LittleEndian(b[16..], T1);
        BinaryPrimitives.WriteInt64LittleEndian(b[24..], T2);
        BinaryPrimitives.WriteInt64LittleEndian(b[32..], T3);
    }

    public byte[] ToBytes()
    {
        var b = new byte[Size];
        Write(b);
        return b;
    }

    public static bool TryRead(ReadOnlySpan<byte> b, out SyncPacket packet)
    {
        packet = default;
        if (b.Length != Size || BinaryPrimitives.ReadUInt32LittleEndian(b) != Magic || b[4] != Version)
            return false;
        if (b[6] != 0 || b[7] != 0 || BinaryPrimitives.ReadUInt32LittleEndian(b[12..]) != 0)
            return false;
        var kind = b[5];
        var t1 = BinaryPrimitives.ReadInt64LittleEndian(b[16..]);
        var t2 = BinaryPrimitives.ReadInt64LittleEndian(b[24..]);
        var t3 = BinaryPrimitives.ReadInt64LittleEndian(b[32..]);
        var valid = kind switch
        {
            Request => t2 == 0 && t3 == 0,
            Reply => t3 >= t2,
            _ => false,
        };
        if (!valid)
            return false;
        packet = new SyncPacket(kind, BinaryPrimitives.ReadUInt32LittleEndian(b[8..]), t1, t2, t3);
        return true;
    }
}

/// <summary>The leader's side: answers each request with t2 (read) and t3 (answer) on its <see cref="NetworkClock"/> —
/// a leader's own clock, or a follower's estimate of its leader's (so a chain still answers in leader time).</summary>
public sealed class ClockSyncResponder : IAsyncDisposable
{
    private readonly NodeSettings _settings;
    private readonly NetworkClock _clock;
    private readonly int? _port;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private Socket? _socket;
    private Task? _loop;
    private long _answered;
    private long _refused;

    /// <param name="port">The port; null = <see cref="NodeSettings.SyncPort"/>, 0 = any free port.</param>
    public ClockSyncResponder(NodeSettings settings, NetworkClock clock, int? port = null, ILogger? log = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _port = port;
        _log = log ?? NullLogger.Instance;
    }

    public int Port { get; private set; }

    public long Answered => Interlocked.Read(ref _answered);

    /// <summary>Datagrams that were not a request: refused unanswered.</summary>
    public long Refused => Interlocked.Read(ref _refused);

    public void Start()
    {
        if (_socket is not null)
            return;
        var bind = NetAddress.ParseBind(_settings.Bind) ?? IPAddress.Any;
        var socket = new Socket(bind.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(bind, _port ?? _settings.SyncPort));
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new BindRefusedException($"the clock sync could not listen on {bind} ({ex.SocketErrorCode}) — it stays closed", ex);
        }
        _socket = socket;
        Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        _loop = Task.Run(() => AnswerAsync(socket, _stop.Token), CancellationToken.None);
    }

    private async Task AnswerAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[256];
        var reply = new byte[SyncPacket.Size];
        EndPoint any = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                // A reset from a client that left (Windows reports it on UDP): not the responder's end.
                _log.LogDebug(ex, "A clock sync receive failed ({Error}).", ex.SocketErrorCode);
                continue;
            }
            var t2 = _clock.NowNs;
            if (!SyncPacket.TryRead(buffer.AsSpan(0, r.ReceivedBytes), out var request) || request.Kind != SyncPacket.Request)
            {
                Interlocked.Increment(ref _refused);
                continue;
            }
            new SyncPacket(SyncPacket.Reply, request.Seq, request.T1, t2, _clock.NowNs).Write(reply);
            try
            {
                await socket.SendToAsync(reply, SocketFlags.None, r.RemoteEndPoint, ct);
                Interlocked.Increment(ref _answered);
            }
            catch (SocketException ex)
            {
                // The asker's route went away; it asks again in a quarter second.
                _log.LogDebug(ex, "A clock sync reply did not leave.");
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // Stopped while a reply was leaving: a follower still asking is the normal case, not a fault of the stop.
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _socket?.Dispose();
        if (_loop is not null)
            await _loop;
        _stop.Dispose();
    }
}

public enum SyncQuality
{
    /// <summary>Not following anyone, or nothing heard yet.</summary>
    None,
    /// <summary>Fewer than four exchanges: the estimate is not to be trusted for frames yet.</summary>
    Acquiring,
    /// <summary>The best round trip is a millisecond or less (a switch, not a radio): frame-accurate.</summary>
    Fine,
    /// <summary>Up to 5 ms round trip: within a refresh.</summary>
    Fair,
    /// <summary>A longer round trip: the uncertainty is half of it.</summary>
    Poor,
    /// <summary>No answer for two seconds: the last estimate stands (slewed, never stepped) until one comes.</summary>
    Lost,
}

/// <summary>Where the follower's clock stands against its leader: the offset of the exchange with the smallest round
/// trip (what was measured — the clock itself also applies the drift between exchanges), its round trip (the offset
/// is exact only for a symmetric path: the error is at most half of it), the drift, the quality.</summary>
public sealed record SyncSnapshot(string Leader, double OffsetNs, long RoundTripNs, double DriftPpm, int Samples, SyncQuality Quality)
{
    public static readonly SyncSnapshot None = new("", 0, 0, 0, 0, SyncQuality.None);

    public double OffsetMs => OffsetNs / Durations.NsPerMs;

    public double RoundTripMs => RoundTripNs / (double)Durations.NsPerMs;
}

/// <summary>The follower's side: an exchange with the chosen leader every 250 ms, each reply matched to the request
/// it answers (sequence and t1 echoed — a stray or replayed reply is dropped), fed to a
/// <see cref="ClockOffsetEstimator"/> (min-RTT of the last eight, drift by regression). Readers on other threads
/// (the render loop) read <see cref="Clock"/>, which follows an estimator that is never written after it is
/// published: each exchange publishes a fresh one built from the recent samples.</summary>
public sealed class ClockSyncClient : IAsyncDisposable
{
    private const int Outstanding = 64;
    private const int Kept = 32;
    private static readonly TimeSpan LostAfter = TimeSpan.FromSeconds(2);

    /// <summary>The span of samples a drift needs before it is applied.</summary>
    public static readonly TimeSpan DriftSpan = TimeSpan.FromSeconds(2);

    /// <summary>The largest drift taken as real (500 ppm; a PC's crystal is within ±100).</summary>
    public const double MaxDrift = 500e-6;

    private readonly IMonoClock _local;
    private readonly TimeSpan _every;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly (uint Seq, long T1)[] _sent = new (uint, long)[Outstanding];
    private readonly Queue<ClockSample> _samples = new();
    private CancellationTokenSource? _run;
    private Task? _loops;
    private Socket? _socket;
    private IPEndPoint? _leader;
    private volatile SyncSnapshot _snapshot = SyncSnapshot.None;
    private uint _seq;
    private long _lastHeardNs;
    private long _strays;

    public ClockSyncClient(IMonoClock local, TimeSpan? every = null, ILogger? log = null)
    {
        _local = local ?? throw new ArgumentNullException(nameof(local));
        _every = every ?? TimeSpan.FromMilliseconds(250);
        _log = log ?? NullLogger.Instance;
        Clock = new NetworkClock(local);
    }

    /// <summary>The leader's time as this node reads it (its own until the first exchange closes).</summary>
    public NetworkClock Clock { get; }

    /// <summary>Where the estimate stands now; the quality turns Lost after two seconds without an answer.</summary>
    public SyncSnapshot Snapshot
    {
        get
        {
            var s = _snapshot;
            if (s.Quality is SyncQuality.None)
                return s;
            var silent = _local.NowNs - Interlocked.Read(ref _lastHeardNs);
            return silent > (long)LostAfter.TotalMilliseconds * Durations.NsPerMs ? s with { Quality = SyncQuality.Lost } : s;
        }
    }

    /// <summary>Replies that matched no request of ours (or failed the format): dropped.</summary>
    public long Strays => Interlocked.Read(ref _strays);

    /// <summary>Starts (or moves) the exchange to a leader. The estimate starts over: two leaders' clocks differ.</summary>
    public async Task FollowAsync(IPEndPoint leader)
    {
        ArgumentNullException.ThrowIfNull(leader);
        await StopAsync();
        var socket = new Socket(leader.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(leader.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
        lock (_gate)
        {
            _samples.Clear();
            Array.Clear(_sent);
            _leader = leader;
        }
        _snapshot = SyncSnapshot.None with { Leader = leader.ToString() };
        Clock.Lead();
        _socket = socket;
        _run = new CancellationTokenSource();
        var ct = _run.Token;
        _loops = Task.WhenAll(Task.Run(() => AskAsync(socket, leader, ct), CancellationToken.None), Task.Run(() => HearAsync(socket, ct), CancellationToken.None));
    }

    /// <summary>Stops following: the node keeps its own clock from here (the last estimate is dropped).</summary>
    public async Task StopAsync()
    {
        if (_run is null)
            return;
        await _run.CancelAsync();
        _socket?.Dispose();
        try
        {
            if (_loops is not null)
                await _loops;
        }
        catch (OperationCanceledException)
        {
            // The loops end on the stop they were given.
        }
        _run.Dispose();
        _run = null;
        _socket = null;
        _leader = null;
        Clock.Lead();
        _snapshot = SyncSnapshot.None;
    }

    private async Task AskAsync(Socket socket, IPEndPoint leader, CancellationToken ct)
    {
        var bytes = new byte[SyncPacket.Size];
        do
        {
            uint seq;
            long t1;
            lock (_gate)
            {
                seq = ++_seq;
                t1 = _local.NowNs;
                _sent[seq % Outstanding] = (seq, t1);
            }
            new SyncPacket(SyncPacket.Request, seq, t1, 0, 0).Write(bytes);
            try
            {
                await socket.SendToAsync(bytes, SocketFlags.None, leader, ct);
            }
            catch (SocketException ex)
            {
                // The leader is unreachable this time: the quality says Lost if it stays so.
                _log.LogDebug(ex, "A clock sync request did not leave ({Error}).", ex.SocketErrorCode);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
        }
        while (await Waits.DelayAsync(_every, ct));
    }

    private async Task HearAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[256];
        EndPoint any = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                // An ICMP unreachable reported on the socket (the leader is not up yet): keep listening.
                _log.LogDebug(ex, "A clock sync receive failed ({Error}).", ex.SocketErrorCode);
                continue;
            }
            var t4 = _local.NowNs;
            if (!SyncPacket.TryRead(buffer.AsSpan(0, r.ReceivedBytes), out var reply) || reply.Kind != SyncPacket.Reply || !Take(reply, r.RemoteEndPoint, t4))
                Interlocked.Increment(ref _strays);
        }
    }

    /// <summary>A reply that answers one of ours from the leader becomes a sample; the published estimator is rebuilt.</summary>
    private bool Take(SyncPacket reply, EndPoint from, long t4)
    {
        lock (_gate)
        {
            if (_leader is null || from is not IPEndPoint ep || !NetAddress.Normal(ep.Address).Equals(NetAddress.Normal(_leader.Address)) || ep.Port != _leader.Port)
                return false;
            ref var slot = ref _sent[reply.Seq % Outstanding];
            if (slot.Seq != reply.Seq || slot.T1 != reply.T1 || slot.Seq == 0)
                return false;
            slot = default;   // a reply answers its request once
            var sample = new ClockSample(reply.T1, reply.T2, reply.T3, t4);
            if (sample.RoundTrip < 0)
                return false;
            _samples.Enqueue(sample);
            while (_samples.Count > Kept)
                _samples.Dequeue();
            var (published, estimator) = Publishable();
            Clock.Follow(published);
            Interlocked.Exchange(ref _lastHeardNs, t4);
            var rtt = estimator.RoundTrip;
            var quality = estimator.Samples < 4 ? SyncQuality.Acquiring
                : rtt <= Durations.NsPerMs ? SyncQuality.Fine
                : rtt <= 5 * Durations.NsPerMs ? SyncQuality.Fair
                : SyncQuality.Poor;
            _snapshot = new SyncSnapshot(_leader.ToString(), estimator.Offset, rtt, published.Drift * 1e6, estimator.Samples, quality);
            return true;
        }
    }

    /// <summary>A fresh estimator from the recent samples, never written after it is published. Drift is applied
    /// only when it is a measurement, not noise: the samples span at least <see cref="DriftSpan"/> and the slope is one
    /// a crystal can have (within <see cref="MaxDrift"/>); otherwise the clock follows the best exchange's offset
    /// alone — a slope regressed from a fraction of a second would move the clock by milliseconds between exchanges.</summary>
    private (ClockOffsetEstimator Published, ClockOffsetEstimator Full) Publishable()
    {
        var full = new ClockOffsetEstimator(ShowBusConstants.OffsetWindow, Kept);
        foreach (var s in _samples)
            full.Add(s);
        var span = _samples.Count > 1 ? _samples.Last().T4 - _samples.Peek().T1 : 0;
        if (span >= (long)DriftSpan.TotalMilliseconds * Durations.NsPerMs && Math.Abs(full.Drift) <= MaxDrift)
            return (full, full);
        var offsetOnly = new ClockOffsetEstimator(ShowBusConstants.OffsetWindow, Kept);
        offsetOnly.Add(full.Best);
        return (offsetOnly, full);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
