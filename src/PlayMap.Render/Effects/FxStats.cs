using System.Collections.Concurrent;

namespace PlayMap.Render.Effects;

/// <summary>Where a slot stands on the renderer: not drawn lately, waiting for its program, drawing, faulted (it shows its input
/// until its slot changes), bypassed (bypass on or mix 0: the input passes untouched), or of a kind this node does not have.</summary>
public enum FxSlotState
{
    Idle,
    Compiling,
    Ok,
    Faulted,
    Bypassed,
    Missing,
}

/// <summary>One slot as the render thread last drew it, read from any thread: each field is written whole by the render thread
/// (a reader may see one frame's state beside the last frame's time, never a torn value).</summary>
public sealed class FxSlotLive(string slotId, FxStats owner)
{
    private int _state;
    private string _words = "";
    private long _gpuNs;
    private long _frame;
    private int _history;

    public string SlotId { get; } = slotId;

    public FxSlotState State => (FxSlotState)Volatile.Read(ref _state);

    public string Words => Volatile.Read(ref _words);

    /// <summary>The slot's last measured GPU time (its passes and its mix), ms; 0 before a measurement arrives.</summary>
    public double GpuMs => Volatile.Read(ref _gpuNs) / 1e6;

    /// <summary>The renderer frame that last drew or skipped it.</summary>
    public long LastFrame => Volatile.Read(ref _frame);

    /// <summary>Its kind integrates frame to frame (persistent buffers): not a pure function of show time.</summary>
    public bool History => Volatile.Read(ref _history) != 0;

    internal void Set(FxSlotState state, string words, long frame)
    {
        var moved = Volatile.Read(ref _state) != (int)state || !string.Equals(Volatile.Read(ref _words), words, StringComparison.Ordinal)
            || Volatile.Read(ref _frame) == 0;
        Volatile.Write(ref _state, (int)state);
        if (!ReferenceEquals(Volatile.Read(ref _words), words))
            Volatile.Write(ref _words, words);
        Volatile.Write(ref _frame, frame);
        if (moved)
            owner.Moved();
    }

    internal void Touch(long frame) => Volatile.Write(ref _frame, frame);

    internal void Gpu(long ns) => Volatile.Write(ref _gpuNs, ns);

    internal void Kind(bool history) => Volatile.Write(ref _history, history ? 1 : 0);
}

/// <summary>Every slot the renderer has met, by slot id, and the renderer's frame — the facts STATE.fx reports (observed, never
/// assumed). Written on the render thread; read from any thread.</summary>
public sealed class FxStats
{
    private readonly ConcurrentDictionary<string, FxSlotLive> _slots = new(StringComparer.Ordinal);
    private long _frame;
    private long _version;

    /// <summary>The renderer's last frame (0 before the first).</summary>
    public long Frame => Volatile.Read(ref _frame);

    public bool TryGet(string slotId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FxSlotLive? live) => _slots.TryGetValue(slotId, out live);

    public int Count => _slots.Count;

    /// <summary>Moves whenever a slot's state or words move (or a slot is met or forgotten): the host republishes STATE then.</summary>
    public long Version => Volatile.Read(ref _version);

    internal FxSlotLive For(string slotId) => _slots.GetOrAdd(slotId, static (id, self) => new FxSlotLive(id, self), this);

    internal void Forget(string slotId)
    {
        if (_slots.TryRemove(slotId, out _))
            Moved();
    }

    internal void Moved() => Interlocked.Increment(ref _version);

    internal void SetFrame(long frame) => Volatile.Write(ref _frame, frame);
}
