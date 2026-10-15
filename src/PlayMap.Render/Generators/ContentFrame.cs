using System.Collections.Concurrent;
using PlayMap.Core.Model;
using PlayMap.Core.Modulation;
using PlayMap.Render.Gpu;

namespace PlayMap.Render.Generators;

/// <summary>Pictures a generator may read (a particle field emits from a source's light): the frame's own resolver. Render thread,
/// during a frame; null while the source has no picture.</summary>
public interface IContentPictures
{
    GpuTexture? Picture(SourceRef source, int width, int height);
}

/// <summary>What content adds to a frame, carried as the frame's modulation bus (which it forwards whole to the bus it wraps, so
/// every other reader of the frame's modulation reads the node's own sources): the item time of each generator a playlist routes
/// now, the pictures generators read, and where the renderer reports what it observed of each generator. One per render host, made
/// and filled on the render thread; nothing in it allocates once its tables are warm.</summary>
public sealed class ContentFrame : IModulationBus
{
    private readonly Dictionary<string, long> _itemTimes = new(StringComparer.Ordinal);
    private IModulationBus _inner = NoModulation.Instance;

    /// <summary>What the renderer observed of each generator (read from any thread).</summary>
    public ContentStats Stats { get; } = new();

    /// <summary>The pictures a generator may read this frame (null: none).</summary>
    public IContentPictures? Pictures { get; set; }

    /// <summary>The node's bus under this frame (none: <see cref="NoModulation"/>).</summary>
    public IModulationBus Inner => _inner;

    /// <summary>This frame over the node's bus: what the frame's modulation becomes.</summary>
    public ContentFrame Over(IModulationBus? inner)
    {
        _inner = inner is ContentFrame self ? self._inner : inner ?? NoModulation.Instance;
        return this;
    }

    /// <summary>Forgets last frame's item times (render thread, once a frame before <see cref="SetItemTime"/>).</summary>
    public void BeginItems() => _itemTimes.Clear();

    /// <summary>A generator a playlist routes now reads its item's clip time (the first playlist that routes it wins).</summary>
    public void SetItemTime(string generatorId, long itemTimeNs) => _itemTimes.TryAdd(generatorId, itemTimeNs);

    public bool TryItemTime(string generatorId, out long itemTimeNs) => _itemTimes.TryGetValue(generatorId, out itemTimeNs);

    // ---- the node's bus, forwarded ----

    public bool TryRead(string source, long leaderNs, out double value01) => _inner.TryRead(source, leaderNs, out value01);

    public bool IsLive(string source) => _inner.IsLive(source);

    public int Resolve(string source) => _inner.Resolve(source);

    public bool TryRead(int slot, long leaderNs, out double value01) => _inner.TryRead(slot, leaderNs, out value01);

    public bool TryReadRaw(string source, long leaderNs, out double value) => _inner.TryReadRaw(source, leaderNs, out value);

    public SourceDeterminism Determinism(string source) => _inner.Determinism(source);

    public void BeginFrame(long leaderNs) => _inner.BeginFrame(leaderNs);
}

/// <summary>Where a generator stands on the renderer: drawing, warming (a particle field settling after a seek or a change: it
/// shows its last picture until it has run one life), faulted (it shows its error card until it changes), or not drawn lately.</summary>
public enum GeneratorState { Idle, Ok, Warming, Faulted }

/// <summary>One generator as the render thread last drew it, read from any thread: each field is written whole (a reader may see one
/// frame's state beside the last frame's time, never a torn value).</summary>
public sealed class GeneratorLive(string generatorId, ContentStats owner)
{
    private const int Window = 120;
    private readonly long[] _ring = new long[Window];
    private readonly long[] _sorted = new long[Window];
    private int _ringCount;
    private int _ringNext;
    private int _state;
    private string _words = "";
    private long _gpuNs;
    private long _gpuP95Ns;
    private long _frame;
    private long _progress = 1_000_000;
    private int _count;
    private long _timeNs;
    private string[] _liveNames = [];
    private double[] _liveValues = [];

    public string GeneratorId { get; } = generatorId;

    public GeneratorState State => (GeneratorState)Volatile.Read(ref _state);

    public string Words => Volatile.Read(ref _words);

    /// <summary>The last measured GPU time of its draw, ms (0 before a measurement lands, and on a context that measures none).</summary>
    public double GpuMs => Volatile.Read(ref _gpuNs) / 1e6;

    /// <summary>The 95th percentile of its last two seconds of measurements, ms.</summary>
    public double GpuMsP95 => Volatile.Read(ref _gpuP95Ns) / 1e6;

    /// <summary>The renderer frame that last drew it.</summary>
    public long LastFrame => Volatile.Read(ref _frame);

    /// <summary>How far a warming generator has come, 0–1 (1 when it draws its picture).</summary>
    public double Progress => Volatile.Read(ref _progress) / 1e6;

    /// <summary>What it draws: particles for a field, 0 for a kind that has no count.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Its own time at its last draw, ns.</summary>
    public long TimeNs => Volatile.Read(ref _timeNs);

    /// <summary>Each modulated number's value at its last draw (the names and values are swapped whole).</summary>
    public (string[] Names, double[] Values) Live()
    {
        var names = Volatile.Read(ref _liveNames);
        var values = Volatile.Read(ref _liveValues);
        var n = Math.Min(names.Length, values.Length);
        return n == names.Length && n == values.Length ? (names, values) : (names[..n], values[..n]);
    }

    /// <summary>What it is doing (render thread; a test's fake renderer).</summary>
    public void Set(GeneratorState state, string words, long frame)
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

    /// <summary>What its last draw came to (render thread; a test's fake renderer).</summary>
    public void Drawn(double progress, int count, long timeNs)
    {
        Volatile.Write(ref _progress, (long)Math.Round(Math.Clamp(progress, 0, 1) * 1e6));
        Volatile.Write(ref _count, count);
        Volatile.Write(ref _timeNs, timeNs);
    }

    /// <summary>A GPU measurement: the last one and the 95th percentile of the window (sorted in a buffer kept for it).</summary>
    internal void Gpu(long ns)
    {
        Volatile.Write(ref _gpuNs, ns);
        _ring[_ringNext] = ns;
        _ringNext = (_ringNext + 1) % Window;
        _ringCount = Math.Min(Window, _ringCount + 1);
        Array.Copy(_ring, _sorted, _ringCount);
        Array.Sort(_sorted, 0, _ringCount);
        Volatile.Write(ref _gpuP95Ns, _sorted[Math.Min(_ringCount - 1, (int)Math.Ceiling(_ringCount * 0.95) - 1)]);
    }

    /// <summary>The names the live values are for (set when its bindings change; render thread).</summary>
    internal void LiveNames(string[] names)
    {
        Volatile.Write(ref _liveValues, new double[names.Length]);
        Volatile.Write(ref _liveNames, names);
    }

    /// <summary>The live values array the render thread writes into (one per name).</summary>
    internal double[] LiveValues => Volatile.Read(ref _liveValues);

    /// <summary>A measured GPU time for tests and fakes.</summary>
    public void Measured(long ns) => Gpu(ns);
}

/// <summary>Every generator the renderer has drawn, by generator id — the facts STATE.content reports (observed, never assumed).
/// Written on the render thread; read from any thread.</summary>
public sealed class ContentStats
{
    private readonly ConcurrentDictionary<string, GeneratorLive> _generators = new(StringComparer.Ordinal);
    private long _frame;
    private long _version;

    /// <summary>The renderer's last frame (0 before the first).</summary>
    public long Frame => Volatile.Read(ref _frame);

    public bool TryGet(string generatorId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GeneratorLive? live) =>
        _generators.TryGetValue(generatorId, out live);

    public int Count => _generators.Count;

    /// <summary>Moves whenever a generator's state or words move (or one is met or forgotten): the host republishes STATE then.</summary>
    public long Version => Volatile.Read(ref _version);

    /// <summary>A generator's entry, made the first time it is met.</summary>
    public GeneratorLive For(string generatorId) => _generators.GetOrAdd(generatorId, static (id, self) => new GeneratorLive(id, self), this);

    /// <summary>The generators met (a snapshot).</summary>
    public IReadOnlyList<string> Ids() => [.. _generators.Keys];

    internal void Forget(string generatorId)
    {
        if (_generators.TryRemove(generatorId, out _))
            Moved();
    }

    internal void Moved() => Interlocked.Increment(ref _version);

    /// <summary>The renderer's frame now (render thread; a test's fake renderer).</summary>
    public void SetFrame(long frame) => Volatile.Write(ref _frame, frame);
}
