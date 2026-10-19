using PlayMap.Core.Gpu;
using PlayMap.Render.Platform;

namespace PlayMap.Render.Sync;

/// <summary>A sync board as its driver reports it: the kind ("nvidia", "fake"), its id and name, firmware, whether it is synced
/// and has a signal, house sync and its rate, a sync cable into a network switch (the board's own warning), whether it is
/// configured (a display is its server or client), and the driver's version words.</summary>
public sealed record FrameLockBoard(
    string Kind,
    int BoardId,
    string Name,
    string Firmware,
    bool Synced,
    bool SignalAvailable,
    bool HouseSync,
    double HouseSyncHz,
    bool CableIntoSwitch,
    bool Configured,
    string Driver);

/// <summary>A framelock tier: board facts (any thread, at most once a second) and swap-group control (render thread, on the output
/// it acts on). Join never blocks a present; every call answers what it did or why not. Outputs are named by id; the presenter is
/// the one the output shows through (null offscreen), for a tier that acts on a swap chain.</summary>
public interface IFrameLockDriver : IDisposable
{
    /// <summary>"nvapi", "fake", "none".</summary>
    string Name { get; }

    /// <summary>Why the tier is what it is ("no NVIDIA driver on this machine"); "" when it works.</summary>
    string Words { get; }

    /// <summary>The board now, or null: no board.</summary>
    FrameLockBoard? Board();

    /// <summary>Swap groups this tier can make (0: none — GeForce, Mesa, or no swap chain to act on).</summary>
    int MaxSwapGroups { get; }

    /// <summary>Joins the output to the swap group and binds the barrier (non-blocking presents); false with words when it did not.</summary>
    bool Join(string outputId, IOutputPresenter? presenter, uint group, uint barrier, out string words);

    /// <summary>Unbinds and leaves (non-blocking); the output presents on its own from its next present.</summary>
    void Leave(string outputId, IOutputPresenter? presenter);

    /// <summary>Whether the output's barrier is bound now.</summary>
    bool BarrierBound(string outputId);

    /// <summary>The board's frame count for the output's swap group (24 bits); false when there is none to read.</summary>
    bool TryFrameCount(string outputId, IOutputPresenter? presenter, out uint count24);

    /// <summary>The tier's own wait for the output's barrier (its waitable, with a timeout of 3 refreshes) did not signal: the
    /// barrier stalled. False for a tier with no waitable. Never judged from how long a present call took, which holds the node's
    /// own work (a software rasteriser, a display server's round trip, a starved thread) and no barrier at all.</summary>
    bool WaitTimedOut(string outputId) => false;
}

/// <summary>The tier on a node with no framelock: no board, no swap groups; every output presents free, measured.</summary>
public sealed class NoFrameLock(string words = "no sync board driver on this node") : IFrameLockDriver
{
    public string Name => "none";

    public string Words { get; } = words;

    public FrameLockBoard? Board() => null;

    public int MaxSwapGroups => 0;

    public bool Join(string outputId, IOutputPresenter? presenter, uint group, uint barrier, out string words)
    {
        words = Words;
        return false;
    }

    public void Leave(string outputId, IOutputPresenter? presenter)
    {
        // Nothing joined, nothing to leave.
    }

    public bool BarrierBound(string outputId) => false;

    public bool TryFrameCount(string outputId, IOutputPresenter? presenter, out uint count24)
    {
        count24 = 0;
        return false;
    }

    public void Dispose()
    {
        // Holds nothing.
    }
}

/// <summary>A scripted board for tests and rehearsals: it joins every output asked, binds its barrier, and advances each joined
/// output's frame count by one per read — until a script stalls it, unsyncs the board, plugs a cable into a switch, or removes the
/// board. Thread-safe (board facts are read off the render thread).</summary>
public sealed class FakeFrameLockDriver : IFrameLockDriver
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, uint> _counts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _bound = new(StringComparer.Ordinal);

    /// <summary>The board's id (8289: Quadro Sync II); null = no board.</summary>
    public int? BoardId { get; set; } = 8289;

    public bool Synced { get; set; } = true;

    public bool HouseSync { get; set; }

    public bool CableIntoSwitch { get; set; }

    public bool Configured { get; set; } = true;

    /// <summary>The counts stop advancing (a barrier that stalled).</summary>
    public bool Stalled { get; set; }

    /// <summary>The barrier's wait times out for every joined output (a barrier that stopped signalling).</summary>
    public bool BarrierWaitTimesOut { get; set; }

    /// <summary>Joins refused with these words (null: joins succeed).</summary>
    public string? RefuseJoins { get; set; }

    /// <summary>Where each output's count starts (near the 24-bit wrap, so a run crosses it).</summary>
    public uint StartCount { get; set; } = 0xFFFF80;

    /// <summary>Joins and leaves done, in order ("join op_1", "leave op_1").</summary>
    public List<string> Calls { get; } = [];

    public string Name => "fake";

    public string Words => "";

    public int MaxSwapGroups => BoardId is null ? 0 : 4;

    public FrameLockBoard? Board()
    {
        lock (_gate)
        {
            if (BoardId is not { } id)
                return null;
            return new FrameLockBoard("fake", id, Core.Sync.SyncBoards.Name(id), "3.6", Synced, Synced, HouseSync, HouseSync ? 60 : 0,
                CableIntoSwitch, Configured, "fake 1.0");
        }
    }

    public bool Join(string outputId, IOutputPresenter? presenter, uint group, uint barrier, out string words)
    {
        lock (_gate)
        {
            Calls.Add("join " + outputId);
            if (BoardId is null || RefuseJoins is not null)
            {
                words = RefuseJoins ?? "no sync board";
                return false;
            }
            _counts.TryAdd(outputId, StartCount);
            if (barrier != 0)
                _bound.Add(outputId);
            words = "";
            return true;
        }
    }

    public void Leave(string outputId, IOutputPresenter? presenter)
    {
        lock (_gate)
        {
            Calls.Add("leave " + outputId);
            _bound.Remove(outputId);
            _counts.Remove(outputId);
        }
    }

    public bool BarrierBound(string outputId)
    {
        lock (_gate)
            return _bound.Contains(outputId);
    }

    public bool TryFrameCount(string outputId, IOutputPresenter? presenter, out uint count24)
    {
        lock (_gate)
        {
            if (!_counts.TryGetValue(outputId, out var c))
            {
                count24 = 0;
                return false;
            }
            if (!Stalled)
                c = (c + 1) & 0xFFFFFF;
            _counts[outputId] = c;
            count24 = c;
            return true;
        }
    }

    public bool WaitTimedOut(string outputId)
    {
        lock (_gate)
            return BarrierWaitTimesOut && _counts.ContainsKey(outputId);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _counts.Clear();
            _bound.Clear();
        }
    }
}
