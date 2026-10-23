using System.Runtime.InteropServices;
using PlayMap.Core.Media;
using PlayMap.Core.Threading;

namespace PlayMap.Media.Video;

/// <summary>A live input's frames in native memory, in the sender's own packed layout (<see cref="LivePlanes"/>), or BGRA/RGBA.
/// One writer (the receive thread) fills a free slot and publishes it; any number of readers acquire the newest published frame.
///
/// <para>Each slot carries a reference count: a published slot holds one reference, each lease one more; the writer claims a slot
/// only at zero (by exchanging it to −1), so a frame a reader holds is never written. A reader that sees a count of zero or less
/// for the slot it read as newest reads again (the writer moved on). When every slot is held the writer's frame is dropped and
/// counted, so a slow reader never stalls the network. Native memory is 64-byte aligned and grows only when a bigger frame comes;
/// lease objects are pooled, so neither side allocates per frame once warm.</para></summary>
public sealed unsafe class LiveFramePool : IDisposable
{
    public const int Alignment = 64;

    internal sealed class Slot
    {
        public int Refs;
        public nint Memory;
        public long Capacity;
        public PixelLayout Layout;
        public int Width;
        public int Height;
        public FramePlane[] Planes = [];
        public long Generation;
        public long SenderTimestampNs = long.MinValue;
        public long ArrivalNs;
        public ColorMatrix Matrix = ColorMatrix.Bt709;
        public ColorRange Range = ColorRange.Limited;
        public TransferFunction Transfer = TransferFunction.Sdr;
    }

    /// <summary>Generations are unique across every pool of the process: a source opened again (a reconnect, a backup taking over)
    /// never hands the renderer a generation its texture already holds.</summary>
    private static long s_generations;

    private readonly Slot[] _slots;
    private readonly Lock _leaseGate = new();
    private readonly Stack<LiveFrameLease> _leases = new();
    private int _newest = -1;
    private int _writing = -1;
    private long _published;
    private long _dropped;
    private int _disposed;

    public LiveFramePool(int slots = 4)
    {
        _slots = new Slot[Math.Max(3, slots)];
        for (var i = 0; i < _slots.Length; i++)
            _slots[i] = new Slot();
        // The render thread rents and returns a lease every frame beside every other reader: a first wait allocates nothing.
        LockWaits.Prepare(_leaseGate);
    }

    /// <summary>Frames published.</summary>
    public long Published => Volatile.Read(ref _published);

    /// <summary>Frames the writer dropped because every slot was held.</summary>
    public long Dropped => Volatile.Read(ref _dropped);

    /// <summary>Slots readers hold now (the published one not counted).</summary>
    public int Held
    {
        get
        {
            var n = 0;
            var newest = Volatile.Read(ref _newest);
            for (var i = 0; i < _slots.Length; i++)
            {
                var refs = Volatile.Read(ref _slots[i].Refs);
                n += i == newest ? Math.Max(0, refs - 1) : Math.Max(0, refs);
            }
            return n;
        }
    }

    /// <summary>Writer: claims a free slot sized for the frame and answers its planes to fill (tight rows,
    /// <see cref="LivePlanes.Stride"/>, or 4·w for RGBA layouts); false when every slot is held (the frame is counted dropped).
    /// One frame is written at a time; <see cref="Publish"/> or <see cref="Abandon"/> ends it.</summary>
    public bool TryBeginWrite(PixelLayout layout, int width, int height, out ReadOnlySpan<FramePlane> planes)
    {
        planes = default;
        if (Volatile.Read(ref _disposed) != 0)
            return false;
        if (_writing >= 0)
            throw new InvalidOperationException("a frame is already being written");
        var newest = Volatile.Read(ref _newest);
        for (var i = 0; i < _slots.Length; i++)
        {
            if (i == newest)
                continue;
            var slot = _slots[i];
            if (Interlocked.CompareExchange(ref slot.Refs, -1, 0) != 0)
                continue;
            Shape(slot, layout, width, height);
            _writing = i;
            planes = slot.Planes;
            return true;
        }
        Interlocked.Increment(ref _dropped);
        return false;
    }

    /// <summary>Writer: publishes the frame written since <see cref="TryBeginWrite"/> as the newest, with its stamps and colour.</summary>
    public void Publish(long senderTimestampNs, long arrivalNs, ColorMatrix matrix = ColorMatrix.Bt709, ColorRange range = ColorRange.Limited,
        TransferFunction transfer = TransferFunction.Sdr)
    {
        var index = _writing;
        if (index < 0)
            throw new InvalidOperationException("no frame is being written");
        _writing = -1;
        var slot = _slots[index];
        if (Volatile.Read(ref _disposed) != 0)
        {
            Volatile.Write(ref slot.Refs, 0);
            TryFree(slot);
            return;
        }
        slot.SenderTimestampNs = senderTimestampNs;
        slot.ArrivalNs = arrivalNs;
        slot.Matrix = matrix;
        slot.Range = range;
        slot.Transfer = transfer;
        slot.Generation = Interlocked.Increment(ref s_generations);
        Interlocked.Increment(ref _published);
        // The published reference, then the index: a reader that finds the index finds the reference already there.
        Volatile.Write(ref slot.Refs, 1);
        var old = Interlocked.Exchange(ref _newest, index);
        if (old >= 0)
            Interlocked.Decrement(ref _slots[old].Refs);
    }

    /// <summary>Writer: gives the slot back unpublished (the frame could not be read whole).</summary>
    public void Abandon()
    {
        var index = _writing;
        if (index < 0)
            return;
        _writing = -1;
        Volatile.Write(ref _slots[index].Refs, 0);
        if (Volatile.Read(ref _disposed) != 0)
            TryFree(_slots[index]);
    }

    /// <summary>Reader, any thread, never blocks: a lease on the newest frame, or null before the first.</summary>
    public LiveFrameLease? Acquire()
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            var index = Volatile.Read(ref _newest);
            if (index < 0)
                return null;
            var slot = _slots[index];
            var refs = Volatile.Read(ref slot.Refs);
            if (refs <= 0)
                continue;
            if (Interlocked.CompareExchange(ref slot.Refs, refs + 1, refs) != refs)
                continue;
            return Rent(slot);
        }
        return null;
    }

    /// <summary>The newest frame's generation (0 before the first).</summary>
    public long NewestGeneration
    {
        get
        {
            var index = Volatile.Read(ref _newest);
            return index < 0 ? 0 : Volatile.Read(ref _slots[index].Generation);
        }
    }

    private LiveFrameLease Rent(Slot slot)
    {
        LiveFrameLease? lease = null;
        lock (_leaseGate)
            _leases.TryPop(out lease);
        lease ??= new LiveFrameLease(this);
        lease.Attach(slot);
        return lease;
    }

    internal void Return(Slot slot, LiveFrameLease lease)
    {
        Interlocked.Decrement(ref slot.Refs);
        lock (_leaseGate)
            _leases.Push(lease);
    }

    private static void Shape(Slot slot, PixelLayout layout, int width, int height)
    {
        var count = LivePlanes.IsPacked(layout) ? LivePlanes.PlaneCount(layout) : 1;
        long bytes = 0;
        for (var i = 0; i < count; i++)
            bytes += Align((long)StrideOf(layout, i, width) * height);
        if (bytes > slot.Capacity)
        {
            if (slot.Memory != 0)
                NativeMemory.AlignedFree((void*)slot.Memory);
            slot.Memory = (nint)NativeMemory.AlignedAlloc((nuint)bytes, Alignment);
            slot.Capacity = bytes;
        }
        if (slot.Layout != layout || slot.Width != width || slot.Height != height || slot.Planes.Length != count)
        {
            if (slot.Planes.Length != count)
                slot.Planes = new FramePlane[count];
            long offset = 0;
            for (var i = 0; i < count; i++)
            {
                var stride = StrideOf(layout, i, width);
                slot.Planes[i] = new FramePlane(slot.Memory + (nint)offset, stride, width, height);
                offset += Align((long)stride * height);
            }
            slot.Layout = layout;
            slot.Width = width;
            slot.Height = height;
        }
        else if (slot.Planes.Length > 0 && slot.Planes[0].Data != slot.Memory)
        {
            long offset = 0;
            for (var i = 0; i < count; i++)
            {
                var stride = StrideOf(layout, i, width);
                slot.Planes[i] = new FramePlane(slot.Memory + (nint)offset, stride, width, height);
                offset += Align((long)stride * height);
            }
        }
    }

    private static int StrideOf(PixelLayout layout, int plane, int width) =>
        LivePlanes.IsPacked(layout) ? LivePlanes.Stride(layout, plane, width)
        : layout is PixelLayout.Bgra8 or PixelLayout.Rgba8 ? 4 * width
        : throw new ArgumentOutOfRangeException(nameof(layout), layout, "a live frame is packed 4:2:2 or BGRA/RGBA");

    private static long Align(long bytes) => (bytes + Alignment - 1) / Alignment * Alignment;

    /// <summary>Frees the slots' memory once no reader holds them; a held slot is freed when its lease ends (the pool is gone then,
    /// so the lease frees it).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        var newest = Interlocked.Exchange(ref _newest, -1);
        if (newest >= 0)
            Interlocked.Decrement(ref _slots[newest].Refs);
        foreach (var slot in _slots)
            TryFree(slot);
    }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal static void TryFree(Slot slot)
    {
        if (Interlocked.CompareExchange(ref slot.Refs, int.MinValue / 2, 0) != 0)
            return;
        var memory = Interlocked.Exchange(ref slot.Memory, 0);
        if (memory != 0)
            NativeMemory.AlignedFree((void*)memory);
        slot.Capacity = 0;
    }
}

/// <summary>A lease on one live frame: the pool's slot until disposed. Leases are pooled: dispose once and drop the reference.</summary>
public sealed class LiveFrameLease : VideoFrameLease
{
    private readonly LiveFramePool _home;
    private LiveFramePool.Slot? _slot;

    internal LiveFrameLease(LiveFramePool home) => _home = home;

    private LiveFramePool.Slot Slot => Volatile.Read(ref _slot) ?? throw new ObjectDisposedException(nameof(LiveFrameLease));

    public override PixelLayout Layout => Slot.Layout;
    public override int Width => Slot.Width;
    public override int Height => Slot.Height;
    public override IReadOnlyList<FramePlane> Planes => Slot.Planes;
    public override long PtsNs => Slot.SenderTimestampNs != long.MinValue ? Slot.SenderTimestampNs : Slot.ArrivalNs;
    public override long DurationNs => 0;
    public override long Generation => Slot.Generation;
    public override ColorMatrix Matrix => Slot.Matrix;
    public override ColorRange Range => Slot.Range;
    public override TransferFunction Transfer => Slot.Transfer;
    public override bool HasAlpha => LivePlanes.HasAlpha(Slot.Layout) || Slot.Layout is PixelLayout.Bgra8 or PixelLayout.Rgba8;
    public override long ArrivalNs => Slot.ArrivalNs;
    public override long SenderTimestampNs => Slot.SenderTimestampNs;

    internal void Attach(LiveFramePool.Slot slot) => Volatile.Write(ref _slot, slot);

    // The base class's once-only flag cannot be re-armed for a pooled object: the release happens in Dispose(bool), guarded by the
    // slot exchange, so a second dispose of the same rental finds no slot and does nothing.
    protected override void Release()
    {
        // Intentionally empty: see Dispose(bool).
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _slot, null) is { } slot)
        {
            _home.Return(slot, this);
            if (_home.IsDisposed)
                LiveFramePool.TryFree(slot);
        }
        base.Dispose(disposing);
    }
}
