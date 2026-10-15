using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using PlayMap.Core.Dmx;
using PlayMap.Core.Led;
using PlayMap.Core.Model;
using PlayMap.Core.Time;

namespace PlayMap.Net.Led;

/// <summary>How the sender is made: the shared Art-Net socket (null = no Art-Net on this node), the address the sACN and DDP socket
/// binds (null = any), the multicast TTL, and the clock it schedules on.</summary>
public sealed record LedSenderOptions
{
    public IArtNetEndpoint? ArtNet { get; init; }

    public IPAddress? Bind { get; init; }

    public int MulticastTtl { get; init; } = 1;

    public IMonoClock Clock { get; init; } = MonoClock.Instance;

    /// <summary>A universe not sent for this long is sent again with its last payload (Art-Net recommends 800–1000 ms).</summary>
    public long KeepAliveNs { get; init; } = 900_000_000;

    /// <summary>How often an Art-Net node is polled (directed) while output runs.</summary>
    public long PollNs { get; init; } = 3_000_000_000;

    /// <summary>A node that answered polls and then stayed silent this long is unreachable.</summary>
    public long SilentNs { get; init; } = 10_000_000_000;
}

/// <summary>The only writer of LED sockets. Frames from the render thread are decimated per node on the leader-time grid
/// (floor(leader seconds × rate) changes), held until their emission instant (the light's monotonic instant less the node's latency),
/// then sent as one burst of every universe followed by the latch (ArtSync, the sACN synchronization packet, or the DDP push flag on
/// the device's last packet). A universe not sent for 900 ms is sent again with its last payload. The work is done by
/// <see cref="Pump"/>, which a test drives on a hand-moved clock; <see cref="Start"/> runs it on its own thread, waiting on an event
/// until a millisecond before the next instant and spinning the rest. Once warm the send path allocates nothing: packets are written
/// into per-node buffers and sent to cached socket addresses.</summary>
public sealed class LedSender : ILedOutput
{
    private const int Ring = 256;

    private sealed class NodeRun
    {
        public required LedNodeProgram Program;
        public byte[] Pending = [];
        public byte[] DdpScratch = [];
        /// <summary>A copy of what was last sent, for LED MONITOR (read and written under the sender's gate).</summary>
        public byte[] Shown = [];
        public long ShownLeader;
        public byte[] Last = [];
        public bool HasPending;
        public long Due;
        public long PendingLeader;
        public long LastStep = long.MinValue;
        public long LastLeader;
        public long[] RowSent = [];
        public byte[] RowSeq = [];
        public byte SyncSeq;
        public byte DdpSeq;
        public bool Stopped;
        public bool EverSent;
        public SocketAddress? Address;
        public SocketAddress?[] RowAddress = [];
        public SocketAddress? SyncAddress;
        public readonly byte[] Packet = new byte[Ddp.HeaderLength + Ddp.MaxData + 16];
        public readonly long[] SentAt = new long[Ring];
        public readonly double[] LateMs = new double[Ring];
        public int SentHead;
        public long Frames;
        public long Packets;
        public long FailedAt = long.MinValue;
        public string FailWords = "";
        public long NextPoll;
        public LedNodeSeen? Seen;
    }

    private readonly LedSenderOptions _options;
    private readonly LedFramePool _pool = new();
    private readonly Lock _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Socket _socket;
    private readonly byte[] _poll = new byte[LedArtNet.PollLength];
    private Dictionary<string, NodeRun> _nodes = new(StringComparer.Ordinal);
    private LedSendProgram _program = LedSendProgram.Empty;
    private LedSendProgram? _incoming;
    private readonly List<(string Node, bool Black)> _stops = [];
    private bool _master;
    private bool? _masterChange;
    private bool _masterBlack;
    private long _stale;
    private Thread? _thread;
    private volatile bool _disposed;
    private readonly Dictionary<string, LedNodeSeen> _seen = new(StringComparer.Ordinal);

    public LedSender(LedSenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        var bind = options.Bind ?? IPAddress.Any;
        _socket = new Socket(bind.AddressFamily, SocketType.Dgram, ProtocolType.Udp) { Blocking = false, EnableBroadcast = true };
        _socket.Bind(new IPEndPoint(bind, 0));
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, Math.Clamp(options.MulticastTtl, 1, 255));
        _pool.Submitted = () => _wake.Set();
        LedArtNet.WritePoll(_poll);
    }

    private bool _listening;

    /// <summary>Hears ArtPollReplies from the moment Art-Net is first sent: subscribing opens the shared socket, which only a
    /// consumer that needs it may do.</summary>
    private void Listen()
    {
        if (_listening || _options.ArtNet is not { } art)
            return;
        _listening = true;
        art.Received += OnArtNet;
    }

    public ILedFrameSink Sink => _pool;

    /// <summary>Frames the render thread handed that a newer one replaced before they were sent, or that came for an older plan.</summary>
    public long Stale => Interlocked.Read(ref _stale);

    public bool Master
    {
        get
        {
            lock (_gate)
                return _masterChange ?? _master;
        }
    }

    public void Apply(LedSendProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        lock (_gate)
            _incoming = program;
        _wake.Set();
    }

    public void SetMaster(bool on, bool blackFirst)
    {
        lock (_gate)
        {
            _masterChange = on;
            _masterBlack = blackFirst;
        }
        _wake.Set();
    }

    public void StopNode(string nodeId, bool blackFirst)
    {
        lock (_gate)
            _stops.Add((nodeId, blackFirst));
        _wake.Set();
    }

    /// <summary>Starts the send thread (a node; tests call <see cref="Pump"/> themselves instead).</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null || _disposed)
                return;
            _thread = new Thread(Run) { IsBackground = true, Name = "PlayMap led-send", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }
    }

    private void Run()
    {
        var clock = _options.Clock;
        while (!_disposed)
        {
            var next = Pump(clock.NowNs);
            var wait = next - clock.NowNs;
            if (wait > 1_000_000)
            {
                _wake.WaitOne(TimeSpan.FromTicks(Math.Min((wait - 1_000_000) / 100, TimeSpan.TicksPerSecond)));
                continue;
            }
            var spin = new SpinWait();
            while (!_disposed && clock.NowNs < next)
                spin.SpinOnce(-1);
        }
    }

    /// <summary>Does every send due at <paramref name="now"/> (monotonic ns): applies a new program or master change, takes the
    /// render thread's frames, sends what is due, keeps idle universes alive and polls Art-Net nodes. Answers the next instant
    /// work is due.</summary>
    public long Pump(long now)
    {
        TakeControl(now);
        while (_pool.Take() is { } frame)
        {
            Accept(frame);
            _pool.Return(frame);
        }
        var next = now + 100_000_000;
        var on = _master && _program.Refused.Length == 0;
        foreach (var run in _nodes.Values)
        {
            if (!on || run.Stopped || run.Program.Universes.IsEmpty)
            {
                run.HasPending = false;
                continue;
            }
            if (run.HasPending)
            {
                if (run.Due <= now)
                {
                    (run.Pending, run.Last) = (run.Last, run.Pending);
                    run.HasPending = false;
                    run.LastLeader = run.PendingLeader;
                    SendAll(run, now, run.Last);
                    run.LateMs[run.SentHead] = Math.Max(0, now - run.Due) / 1e6;
                    run.SentAt[run.SentHead] = now;
                    run.SentHead = (run.SentHead + 1) % Ring;
                    run.Frames++;
                }
                else
                {
                    next = Math.Min(next, run.Due);
                }
            }
            if (run.EverSent)
            {
                var oldest = long.MaxValue;
                foreach (var t in run.RowSent)
                    oldest = Math.Min(oldest, t);
                if (oldest + _options.KeepAliveNs <= now)
                    KeepAlive(run, now);
                foreach (var t in run.RowSent)
                    next = Math.Min(next, t + _options.KeepAliveNs);
            }
            if (run.Program.Protocol == LedProtocol.ArtNet && _options.ArtNet is { } art && run.Address is { } to)
            {
                Listen();
                if (now >= run.NextPoll)
                {
                    art.Send(_poll, to);
                    run.NextPoll = now + _options.PollNs;
                }
                next = Math.Min(next, run.NextPoll);
            }
        }
        return Math.Max(next, now);
    }

    /// <summary>Applies a waiting program, master change and node stops (black frames and terminations go out now).</summary>
    private void TakeControl(long now)
    {
        LedSendProgram? incoming;
        bool? master;
        bool masterBlack;
        (string Node, bool Black)[] stops;
        lock (_gate)
        {
            incoming = _incoming;
            _incoming = null;
            master = _masterChange;
            _masterChange = null;
            masterBlack = _masterBlack;
            stops = _stops.Count == 0 ? [] : [.. _stops];
            _stops.Clear();
        }
        // Stops first: a node stopped and then dropped by the program that follows (LED OUTPUT OFF <node>) keeps its HOLD.
        foreach (var (node, black) in stops)
            if (_nodes.TryGetValue(node, out var run) && !run.Stopped)
                Stop(run, black, now);
        if (incoming is not null)
            Swap(incoming, now);
        if (master is { } m && m != _master)
        {
            if (!m)
                foreach (var run in _nodes.Values)
                    if (!run.Stopped)
                        Stop(run, masterBlack, now);
            _master = m;
            foreach (var run in _nodes.Values)
            {
                run.Stopped = !m;
                run.LastStep = long.MinValue;
            }
        }
    }

    private void Swap(LedSendProgram program, long now)
    {
        var next = new Dictionary<string, NodeRun>(StringComparer.Ordinal);
        foreach (var p in program.Nodes)
        {
            _nodes.TryGetValue(p.Id, out var old);
            var rows = p.Universes.Length;
            var run = new NodeRun
            {
                Program = p,
                Pending = new byte[rows * LedPlan.SlotsPerRow],
                Last = new byte[rows * LedPlan.SlotsPerRow],
                RowSent = new long[rows],
                RowSeq = new byte[rows],
                RowAddress = new SocketAddress?[rows],
                Shown = new byte[rows * LedPlan.SlotsPerRow],
                Stopped = !_master || (old?.Stopped == true && SameNode(old.Program, p)),
                Seen = old?.Seen,
                NextPoll = now,
                LastStep = old?.LastStep ?? long.MinValue,
            };
            if (old is not null && SameNode(old.Program, p))
            {
                // The same node and rows: its sequence numbers and last payload carry over.
                for (var r = 0; r < rows; r++)
                {
                    var o = old.Program.Universes.IndexOf(p.Universes[r]);
                    if (o < 0)
                    {
                        // A universe new to the node waits for its first frame, not for a keep-alive of nothing.
                        run.RowSent[r] = now;
                        continue;
                    }
                    old.Last.AsSpan(o * LedPlan.SlotsPerRow, LedPlan.SlotsPerRow).CopyTo(run.Last.AsSpan(r * LedPlan.SlotsPerRow));
                    run.RowSeq[r] = old.RowSeq[o];
                    run.RowSent[r] = old.RowSent[o];
                }
                run.EverSent = old.EverSent;
                run.SyncSeq = old.SyncSeq;
                run.DdpSeq = old.DdpSeq;
                run.LastLeader = old.LastLeader;
                run.Frames = old.Frames;
                run.Packets = old.Packets;
                Array.Copy(old.SentAt, run.SentAt, Ring);
                Array.Copy(old.LateMs, run.LateMs, Ring);
                run.SentHead = old.SentHead;
            }
            Address(run);
            next[p.Id] = run;
        }
        // What leaves the air is blacked, never left holding its last look: a node dropped or moved to another target, a node
        // whose universes the new program no longer has, every node when the bandwidth guard starts refusing.
        var refusing = program.Refused.Length > 0 && _program.Refused.Length == 0;
        foreach (var (id, old) in _nodes)
        {
            if (old.Stopped || !old.EverSent || !_master || _program.Refused.Length > 0)
                continue;
            if (!next.TryGetValue(id, out var kept) || !SameNode(old.Program, kept.Program))
                Stop(old, true, now);
            else if (refusing || old.Program.Protocol == LedProtocol.Ddp && !old.Program.Universes.SequenceEqual(kept.Program.Universes))
                Blank(old, now);
            else
                for (var r = 0; r < old.Program.Universes.Length; r++)
                    if (!kept.Program.Universes.Contains(old.Program.Universes[r]))
                        BlankRow(old, r);
        }
        _nodes = next;
        _program = program;
    }

    private static bool SameNode(LedNodeProgram a, LedNodeProgram b) =>
        a.Protocol == b.Protocol && Equals(a.Target, b.Target) && a.Multicast == b.Multicast;

    /// <summary>The cached socket addresses of a node: its own, each universe's multicast group (sACN), and the sync universe's.</summary>
    private void Address(NodeRun run)
    {
        var p = run.Program;
        if (p.Target is { } t)
            run.Address = p.Protocol == LedProtocol.ArtNet && _options.ArtNet is { } art ? art.AddressOf(t) : t.Serialize();
        if (p.Protocol != LedProtocol.Sacn)
            return;
        for (var r = 0; r < p.Universes.Length; r++)
            run.RowAddress[r] = p.Multicast ? new IPEndPoint(Sacn.GroupOf(p.Universes[r]), Sacn.Port).Serialize() : run.Address;
        if (p.SacnSyncUniverse > 0)
            run.SyncAddress = p.Multicast ? new IPEndPoint(Sacn.GroupOf(p.SacnSyncUniverse), Sacn.Port).Serialize() : run.Address;
    }

    /// <summary>Copies a frame's rows into each node that sends it (its leader step moved), due at the light less the node's
    /// latency. A frame of another plan is dropped.</summary>
    private void Accept(LedFrame frame)
    {
        if (frame.PlanSerial != _program.Serial)
        {
            Interlocked.Increment(ref _stale);
            return;
        }
        foreach (var run in _nodes.Values)
        {
            var p = run.Program;
            if (run.Stopped || p.Universes.IsEmpty || frame.Rows < p.FirstRow + p.Universes.Length)
                continue;
            var step = LedPrograms.StepOf(frame.LeaderNs, p.RateMilliHz);
            if (step == run.LastStep)
                continue;
            run.LastStep = step;
            if (run.HasPending)
                Interlocked.Increment(ref _stale);
            frame.Slots.AsSpan(p.FirstRow * LedPlan.SlotsPerRow, p.Universes.Length * LedPlan.SlotsPerRow).CopyTo(run.Pending);
            run.HasPending = true;
            run.Due = frame.LightMonoNs - p.LatencyNs;
            run.PendingLeader = frame.LeaderNs;
        }
    }

    private void Stop(NodeRun run, bool black, long now)
    {
        if (black)
            Blank(run, now);
        run.Stopped = true;
        run.HasPending = false;
    }

    /// <summary>The node's black frame (the current program's: colour and dimmer slots 0, constants kept), latched; sACN then
    /// terminates each universe three times.</summary>
    private void Blank(NodeRun run, long now)
    {
        if (!run.EverSent)
            return;
        _program.Black.AsSpan(run.Program.FirstRow * LedPlan.SlotsPerRow, run.Program.Universes.Length * LedPlan.SlotsPerRow).CopyTo(run.Last);
        SendAll(run, now, run.Last);
        if (run.Program.Protocol == LedProtocol.Sacn)
            for (var k = 0; k < 3; k++)
                for (var r = 0; r < run.Program.Universes.Length; r++)
                    SendSacn(run, r, run.Last, terminated: true);
    }

    /// <summary>One universe a node leaves: its black row, latched (sACN: terminated three times).</summary>
    private void BlankRow(NodeRun run, int r)
    {
        _program.Black.AsSpan((run.Program.FirstRow + r) * LedPlan.SlotsPerRow, LedPlan.SlotsPerRow).CopyTo(run.Last.AsSpan(r * LedPlan.SlotsPerRow));
        if (run.Program.Protocol == LedProtocol.ArtNet)
        {
            SendArtDmx(run, r, run.Last);
            if (run.Program.Latch)
                SendArtSync(run);
            return;
        }
        SendSacn(run, r, run.Last, terminated: false);
        for (var k = 0; k < 3; k++)
            SendSacn(run, r, run.Last, terminated: true);
    }

    /// <summary>Every universe of the node back to back, then the latch.</summary>
    private void SendAll(NodeRun run, long now, byte[] payload)
    {
        var p = run.Program;
        switch (p.Protocol)
        {
            case LedProtocol.ArtNet:
                for (var r = 0; r < p.Universes.Length; r++)
                    SendArtDmx(run, r, payload);
                if (p.Latch)
                    SendArtSync(run);
                break;
            case LedProtocol.Sacn:
                for (var r = 0; r < p.Universes.Length; r++)
                    SendSacn(run, r, payload, terminated: false);
                if (p.Latch && p.SacnSyncUniverse > 0)
                    SendSacnSync(run);
                break;
            default:
                SendDdp(run, payload);
                break;
        }
        for (var r = 0; r < p.Universes.Length; r++)
            run.RowSent[r] = now;
        run.EverSent = true;
        lock (_gate)
        {
            payload.AsSpan(0, Math.Min(payload.Length, run.Shown.Length)).CopyTo(run.Shown);
            run.ShownLeader = run.LastLeader;
        }
    }

    private void KeepAlive(NodeRun run, long now)
    {
        // The whole node again with its last payload (and its latch, so nodes in sync mode stay in it).
        SendAll(run, now, run.Last);
    }

    private void SendArtDmx(NodeRun run, int r, byte[] payload)
    {
        if (_options.ArtNet is not { } art || run.Address is not { } to)
        {
            Fail(run, _options.ArtNet is null ? "this node has no Art-Net socket" : run.Program.Why);
            return;
        }
        run.RowSeq[r] = (byte)(run.RowSeq[r] == 255 ? 1 : run.RowSeq[r] + 1);
        var seq = run.RowSeq[r];
        var n = ArtNet.WriteDmx(run.Packet, seq, run.Program.Universes[r], payload.AsSpan(r * LedPlan.SlotsPerRow, Math.Max(2, run.Program.Used[r])));
        Count(run, art.Send(run.Packet.AsSpan(0, n), to), art.Problem);
    }

    private void SendArtSync(NodeRun run)
    {
        if (_options.ArtNet is not { } art || run.Address is not { } to)
            return;
        var n = ArtNet.WriteSync(run.Packet);
        Count(run, art.Send(run.Packet.AsSpan(0, n), to), art.Problem);
    }

    private void SendSacn(NodeRun run, int r, byte[] payload, bool terminated)
    {
        var p = run.Program;
        if (run.RowAddress[r] is not { } to)
        {
            Fail(run, p.Why);
            return;
        }
        run.RowSeq[r] = unchecked((byte)(run.RowSeq[r] + 1));
        var seq = run.RowSeq[r];
        var header = new SacnData(_program.Cid, _program.SourceName, p.SacnPriority, p.Latch ? p.SacnSyncUniverse : 0, seq, false, terminated, false,
            p.Universes[r], 0, p.Used[r]);
        var n = Sacn.WriteData(run.Packet, header, payload.AsSpan(r * LedPlan.SlotsPerRow, p.Used[r]));
        Count(run, SendTo(run.Packet.AsSpan(0, n), to), null);
    }

    private void SendSacnSync(NodeRun run)
    {
        if (run.SyncAddress is not { } to)
            return;
        run.SyncSeq = unchecked((byte)(run.SyncSeq + 1));
        var n = Sacn.WriteSync(run.Packet, new SacnSync(_program.Cid, run.SyncSeq, run.Program.SacnSyncUniverse));
        Count(run, SendTo(run.Packet.AsSpan(0, n), to), null);
    }

    /// <summary>The device's buffer (its rows' bytes up to the last used) in packets of at most 1440 bytes; the last carries push.</summary>
    private void SendDdp(NodeRun run, byte[] payload)
    {
        var p = run.Program;
        if (run.Address is not { } to)
        {
            Fail(run, p.Why);
            return;
        }
        var bytes = 0;
        for (var r = 0; r < p.Universes.Length; r++)
            if (p.Used[r] > 0)
                bytes = Math.Max(bytes, p.Universes[r] * LedPlan.SlotsPerRow + p.Used[r]);
        var data = DdpBuffer(run, payload, bytes);
        for (var offset = 0; offset < bytes; offset += Ddp.MaxData)
        {
            var len = Math.Min(Ddp.MaxData, bytes - offset);
            run.DdpSeq = (byte)(run.DdpSeq % 15 + 1);
            // The push flag on the device's last packet latches the frame.
            var last = offset + len >= bytes;
            var n = Ddp.Write(run.Packet, last, run.DdpSeq, p.DdpType, offset, data.Slice(offset, len));
            Count(run, SendTo(run.Packet.AsSpan(0, n), to), null);
        }
    }

    /// <summary>The device's buffer: rows are its consecutive 512-byte blocks (a block not in the plan reads as zeros).</summary>
    private static ReadOnlySpan<byte> DdpBuffer(NodeRun run, byte[] payload, int bytes)
    {
        var p = run.Program;
        // Rows are consecutive blocks from 0 in the common case: the payload is the buffer as it stands.
        var contiguous = true;
        for (var r = 0; r < p.Universes.Length; r++)
            contiguous &= p.Universes[r] == r;
        if (contiguous)
            return payload.AsSpan(0, bytes);
        if (run.DdpScratch.Length < bytes)
            run.DdpScratch = new byte[bytes];
        var buffer = run.DdpScratch;
        Array.Clear(buffer, 0, bytes);
        for (var r = 0; r < p.Universes.Length; r++)
            payload.AsSpan(r * LedPlan.SlotsPerRow, p.Used[r]).CopyTo(buffer.AsSpan(p.Universes[r] * LedPlan.SlotsPerRow));
        return buffer.AsSpan(0, bytes);
    }

    private bool SendTo(ReadOnlySpan<byte> packet, SocketAddress to)
    {
        try
        {
            _socket.SendTo(packet, SocketFlags.None, to);
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

    private void Count(NodeRun run, bool ok, string? problem)
    {
        if (ok)
        {
            run.Packets++;
            return;
        }
        Fail(run, problem ?? "the network refused the packet");
    }

    private void Fail(NodeRun run, string words)
    {
        run.FailedAt = _options.Clock.NowNs;
        run.FailWords = words.Length > 0 ? words : "the network refused the packet";
    }

    private void OnArtNet(ReadOnlySpan<byte> packet, SocketAddress from)
    {
        if (ArtNet.OpOf(packet) != ArtOp.PollReply || !LedArtNet.TryReadReply(packet, out var reply, out _))
            return;
        // Keyed by where the reply came from (what the node is patched at), and by the address it reports for itself.
        var source = ((IPEndPoint)new IPEndPoint(IPAddress.Any, 0).Create(from)).Address.ToString();
        var seen = new LedNodeSeen("artnet", source, reply.ShortName, reply.LongName, reply.Universes, reply.RefreshHz, reply.Mac,
            _options.Clock.NowNs);
        lock (_gate)
        {
            _seen[source] = seen;
            _seen[reply.Address] = seen;
        }
    }

    public LedOutputView View()
    {
        var now = _options.Clock.NowNs;
        var nodes = ImmutableArray.CreateBuilder<LedNodeView>();
        Dictionary<string, LedNodeSeen> seen;
        bool master;
        LedSendProgram program;
        lock (_gate)
        {
            seen = new Dictionary<string, LedNodeSeen>(_seen, StringComparer.Ordinal);
            master = _masterChange ?? _master;
            program = _incoming ?? _program;
        }
        var runs = _nodes;
        foreach (var p in program.Nodes)
        {
            runs.TryGetValue(p.Id, out var run);
            seen.TryGetValue(p.Host, out var heard);
            var (fps, late) = run is null ? (0, 0) : Rates(run, now);
            string state, why;
            if (!master)
                (state, why) = ("off", "this machine does not send LED data — LED OUTPUT ON");
            else if (program.Refused.Length > 0)
                (state, why) = ("refused", program.Refused);
            else if (p.Universes.IsEmpty)
                (state, why) = ("idle", p.Why.Length > 0 ? p.Why : "nothing is patched to it");
            else if (run is { Stopped: true })
                (state, why) = ("off", "stopped — LED OUTPUT ON " + p.Id);
            else if (p.Target is null && !p.Multicast)
                (state, why) = ("unreachable", p.Why);
            else if (run is not null && run.FailedAt > now - 2_000_000_000)
                (state, why) = ("unreachable", run.FailWords);
            else if (heard is not null && now - heard.SeenMonoNs > _options.SilentNs)
                (state, why) = ("unreachable", $"silent to ArtPoll for {(now - heard.SeenMonoNs) / 1_000_000_000} s after it had answered");
            else if (run is null || run.Frames == 0)
                (state, why) = ("idle", "no frame has been sent to it yet");
            else if (late > 1000.0 / 60)
                (state, why) = ("late", $"frames leave {late:0.0} ms after their instant");
            else
                (state, why) = ("sending", "");
            nodes.Add(new LedNodeView(p.Id, state, fps, p.Universes.Length, late, run?.LastLeader ?? 0, heard, why, run?.Frames ?? 0, run?.Packets ?? 0));
        }
        return new LedOutputView(master, nodes.ToImmutable(), _pool.Dropped + _pool.Skipped, Stale);
    }

    /// <summary>Frames a second over the last two seconds, and the 95th percentile of their lateness, ms.</summary>
    private static (double Fps, double LateP95) Rates(NodeRun run, long now)
    {
        var late = new List<double>();
        for (var i = 0; i < Ring; i++)
            if (run.SentAt[i] != 0 && run.SentAt[i] > now - 2_000_000_000)
                late.Add(run.LateMs[i]);
        if (late.Count == 0)
            return (0, 0);
        late.Sort();
        return (late.Count / 2.0, late[(int)Math.Ceiling(late.Count * 0.95) - 1]);
    }

    public bool TryMonitor(string nodeId, int universe, Span<byte> slots, out long leaderNs)
    {
        leaderNs = 0;
        if (!_nodes.TryGetValue(nodeId, out var run) || !run.EverSent)
            return false;
        var r = run.Program.Universes.IndexOf(universe);
        if (r < 0)
            return false;
        lock (_gate)
        {
            run.Shown.AsSpan(r * LedPlan.SlotsPerRow, LedPlan.SlotsPerRow).CopyTo(slots);
            leaderNs = run.ShownLeader;
        }
        return true;
    }

    /// <summary>What the node at an address said in its last ArtPollReply, or null.</summary>
    public LedNodeSeen? SeenAt(string host)
    {
        lock (_gate)
            return _seen.TryGetValue(host, out var s) ? s : null;
    }

    public async ValueTask DisposeAsync()
    {
        Thread? thread;
        lock (_gate)
        {
            _disposed = true;
            thread = _thread;
        }
        _wake.Set();
        if (thread is not null)
            await Task.Run(() => thread.Join(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        if (_listening && _options.ArtNet is { } art)
            art.Received -= OnArtNet;
        _socket.Dispose();
        _wake.Dispose();
    }
}
