using System.Collections.Immutable;
using System.Net;
using PlayMap.Core.Model;

namespace PlayMap.Core.Led;

/// <summary>One frame of slots read back from the GPU: every row's 512 bytes, the leader instant of the light it belongs to, the
/// monotonic instant that light is expected, and the plan it was made with. Pooled: the render thread fills it, the sender returns
/// it.</summary>
public sealed class LedFrame
{
    public long LeaderNs;
    public long LightMonoNs;
    public long PlanSerial;
    public int Rows;
    public byte[] Slots = [];
}

/// <summary>Where the render thread hands frames: never blocks, never allocates once warm (a pool of frames whose buffers grow
/// only when the plan's rows do).</summary>
public interface ILedFrameSink
{
    /// <summary>A free frame with room for <paramref name="rows"/> rows, or null when every frame is in flight (the frame is
    /// skipped and counted).</summary>
    LedFrame? Rent(int rows);

    /// <summary>Queues a filled frame for the sender.</summary>
    void Submit(LedFrame frame);
}

/// <summary>A pool of four frames between the render thread and the sender. When every frame is queued, renting one drops the oldest
/// the sender has not taken (counted); the sender takes them in order and returns each.</summary>
public sealed class LedFramePool : ILedFrameSink
{
    public const int Size = 4;

    private readonly Lock _gate = new();
    private readonly LedFrame[] _frames = [new(), new(), new(), new()];
    private readonly bool[] _free = [true, true, true, true];
    private readonly LedFrame?[] _queue = new LedFrame?[Size];
    private int _head;
    private int _count;
    private long _dropped;
    private long _skipped;

    /// <summary>Called after each submit (the sender's wake-up); must not block.</summary>
    public Action? Submitted { get; set; }

    /// <summary>Frames dropped because the sender had not taken them when the render thread needed their buffer.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Frames the render thread skipped because the sender held every frame.</summary>
    public long Skipped => Interlocked.Read(ref _skipped);

    public LedFrame? Rent(int rows)
    {
        LedFrame? f = null;
        lock (_gate)
        {
            if (_closed)
                return null;
            for (var i = 0; i < Size && f is null; i++)
            {
                if (!_free[i])
                    continue;
                _free[i] = false;
                f = _frames[i];
            }
            if (f is null && _count > 0)
            {
                // The sender is behind: the oldest frame it has not taken is dropped and its buffer reused.
                f = _queue[_head]!;
                _queue[_head] = null;
                _head = (_head + 1) % Size;
                _count--;
                Interlocked.Increment(ref _dropped);
            }
        }
        if (f is null)
        {
            Interlocked.Increment(ref _skipped);
            return null;
        }
        var bytes = rows * LedPlan.SlotsPerRow;
        if (f.Slots.Length < bytes)
            f.Slots = new byte[bytes];
        f.Rows = rows;
        return f;
    }

    public void Submit(LedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            if (_closed)
                return;
            if (_count == Size)
            {
                var oldest = _queue[_head]!;
                _queue[_head] = null;
                _head = (_head + 1) % Size;
                _count--;
                Free(oldest);
                Interlocked.Increment(ref _dropped);
            }
            _queue[(_head + _count) % Size] = frame;
            _count++;
            // Under the gate, so nothing is woken once Close has returned.
            Submitted?.Invoke();
        }
    }

    private bool _closed;

    /// <summary>The sender is gone: from now on renting answers null and submitting does nothing, quietly (a render thread still
    /// drawing during shutdown neither throws nor wakes a disposed sender).</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            Submitted = null;
        }
    }

    public bool Closed
    {
        get
        {
            lock (_gate)
                return _closed;
        }
    }

    /// <summary>The oldest queued frame, or null; give it back with <see cref="Return"/>.</summary>
    public LedFrame? Take()
    {
        lock (_gate)
        {
            if (_count == 0)
                return null;
            var f = _queue[_head]!;
            _queue[_head] = null;
            _head = (_head + 1) % Size;
            _count--;
            return f;
        }
    }

    public void Return(LedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
            Free(frame);
    }

    /// <summary>Queued frames waiting for the sender.</summary>
    public int Queued
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    private void Free(LedFrame frame)
    {
        for (var i = 0; i < Size; i++)
            if (ReferenceEquals(_frames[i], frame))
                _free[i] = true;
    }
}

/// <summary>One node as the sender runs it: where its packets go, how often (decimated on the leader-time grid), how early (its
/// latency), how they are latched, and its rows of the plan (universes and the slots used in each).</summary>
/// <param name="Target">The node's address and port (sACN multicast: unused, each universe goes to its group).</param>
/// <param name="RateMilliHz">The rate in thousandths of a hertz (integer, so every machine decimates the same).</param>
/// <param name="Universes">The universe of each of its rows (DDP: the 512-byte block of its buffer).</param>
/// <param name="Used">The slots used in each row (DDP: bytes).</param>
public sealed record LedNodeProgram(string Id, string Name, LedProtocol Protocol, string Host, IPEndPoint? Target, long RateMilliHz, long LatencyNs,
    bool Latch, int SacnPriority, int SacnSyncUniverse, bool Multicast, int FirstRow, ImmutableArray<int> Universes, ImmutableArray<int> Used,
    byte DdpType, string Why);

/// <summary>What the sender runs: the nodes of one plan (its serial matches the frames made with it), their black frame (colour and
/// dimmer slots 0, constants kept), this machine's sACN identity, and the bandwidth guard's refusal ("" = none: when set nothing
/// is sent).</summary>
public sealed record LedSendProgram(long Serial, ImmutableArray<LedNodeProgram> Nodes, byte[] Black, Guid Cid, string SourceName, string Refused)
{
    public static readonly LedSendProgram Empty = new(0, [], [], Guid.Empty, "PlayMap", "");
}

/// <summary>What a node said about itself (an ArtPollReply), when it was heard.</summary>
public sealed record LedNodeSeen(string Protocol, string Host, string Name, string LongName, ImmutableArray<int> Universes, int RefreshHz,
    string Mac, long SeenMonoNs);

/// <summary>A node as the sender observed it: its state word ("off", "idle", "sending", "late", "unreachable", "refused"), frames a
/// second, rows, its 95th-percentile lateness over the last two seconds, the leader instant of the last frame sent, what it said
/// about itself, and why it is in that state.</summary>
public sealed record LedNodeView(string Id, string State, double Fps, int Universes, double LateMs, long LastLeaderNs, LedNodeSeen? Seen,
    string Why, long Frames, long Packets);

/// <summary>The sender as STATE reads it: the machine master, every node it runs, frames dropped between the GPU and the network.</summary>
public sealed record LedOutputView(bool Output, ImmutableArray<LedNodeView> Nodes, long Dropped, long Stale)
{
    public static readonly LedOutputView Off = new(false, [], 0, 0);
}

/// <summary>The LED network edge as the engine uses it (PlayMap.Net implements it; tests use it on loopback with a hand-moved clock).
/// The sender is the only writer of LED sockets.</summary>
public interface ILedOutput : IAsyncDisposable
{
    /// <summary>Where the render thread hands frames.</summary>
    ILedFrameSink Sink { get; }

    /// <summary>Swaps in a program (any thread; the sender picks it up before its next send). A node it no longer has is stopped
    /// as <see cref="StopNode"/> with a black frame.</summary>
    void Apply(LedSendProgram program);

    /// <summary>The machine master: on, or off with one black frame (latched; sACN adds three terminations) unless
    /// <paramref name="blackFirst"/> is false.</summary>
    void SetMaster(bool on, bool blackFirst);

    /// <summary>Stops one node the same way (its program entry stays until the next program drops it).</summary>
    void StopNode(string nodeId, bool blackFirst);

    LedOutputView View();

    /// <summary>The bytes last sent to a node's universe (DDP: the 512-byte block), and the leader instant of their frame.</summary>
    bool TryMonitor(string nodeId, int universe, Span<byte> slots, out long leaderNs);

    /// <summary>A number that moves exactly when what STATE shows about the nodes moves (allocation-free; read on the engine's tick).</summary>
    int Shown() => 0;

    /// <summary>The machine master as the sender holds it (the last request included).</summary>
    bool Master => true;
}
