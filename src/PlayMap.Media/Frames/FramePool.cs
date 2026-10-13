using System.Runtime.InteropServices;
using PlayMap.Core.Media;

namespace PlayMap.Media.Frames;

/// <summary>The plane geometry of a layout at a size. Strides are 64-byte aligned (an upload never straddles a
/// cache line it did not need). Plane sample widths: RGBA layouts count pixels; NV12/P010's second plane counts
/// interleaved UV pairs; planar chroma counts chroma samples. Two-byte layouts (P010, Yuv420P10) use 16-bit
/// little-endian samples; RgbaHalf uses four IEEE halves per pixel.</summary>
internal sealed class FrameLayout : IEquatable<FrameLayout>
{
    public const int Alignment = 64;

    private FrameLayout(PixelLayout layout, int width, int height)
    {
        Layout = layout;
        Width = width;
        Height = height;
        var cw = (width + 1) / 2;
        var ch = (height + 1) / 2;
        switch (layout)
        {
            case PixelLayout.Rgba8:
            case PixelLayout.Bgra8:
                Add(width, height, width * 4);
                break;
            case PixelLayout.RgbaHalf:
                Add(width, height, width * 8);
                break;
            case PixelLayout.Gray8:
                Add(width, height, width);
                break;
            case PixelLayout.Nv12:
                Add(width, height, width);
                Add(cw, ch, cw * 2);
                break;
            case PixelLayout.P010:
                Add(width, height, width * 2);
                Add(cw, ch, cw * 4);
                break;
            case PixelLayout.Yuv420P:
                Add(width, height, width);
                Add(cw, ch, cw);
                Add(cw, ch, cw);
                break;
            case PixelLayout.Yuv422P:
                Add(width, height, width);
                Add(cw, height, cw);
                Add(cw, height, cw);
                break;
            case PixelLayout.Yuv444P:
                Add(width, height, width);
                Add(width, height, width);
                Add(width, height, width);
                break;
            case PixelLayout.Yuv420P10:
                Add(width, height, width * 2);
                Add(cw, ch, cw * 2);
                Add(cw, ch, cw * 2);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(layout), layout, "unknown layout");
        }
    }

    public PixelLayout Layout { get; }
    public int Width { get; }
    public int Height { get; }
    public int Planes { get; private set; }
    public nuint TotalBytes { get; private set; }

    private readonly int[] _sampleWidth = new int[4];
    private readonly int[] _rows = new int[4];
    private readonly int[] _rowBytes = new int[4];
    private readonly int[] _stride = new int[4];
    private readonly nuint[] _offset = new nuint[4];

    public int SampleWidth(int plane) => _sampleWidth[plane];
    public int Rows(int plane) => _rows[plane];
    public int RowBytes(int plane) => _rowBytes[plane];
    public int Stride(int plane) => _stride[plane];
    public nuint Offset(int plane) => _offset[plane];

    public static FrameLayout For(PixelLayout layout, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        return new FrameLayout(layout, width, height);
    }

    private void Add(int sampleWidth, int rows, int rowBytes)
    {
        var i = Planes++;
        _sampleWidth[i] = sampleWidth;
        _rows[i] = rows;
        _rowBytes[i] = rowBytes;
        _stride[i] = (rowBytes + Alignment - 1) / Alignment * Alignment;
        _offset[i] = TotalBytes;
        TotalBytes += (nuint)_stride[i] * (nuint)rows;
    }

    public bool Equals(FrameLayout? other) =>
        other is not null && other.Layout == Layout && other.Width == Width && other.Height == Height;

    public override bool Equals(object? obj) => Equals(obj as FrameLayout);

    public override int GetHashCode() => HashCode.Combine(Layout, Width, Height);
}

/// <summary>One pooled picture buffer and the metadata of the frame it holds. Reference counted: the queue
/// holds one reference, each lease one more; at zero the slot returns to its pool. Metadata is written only
/// while the producer holds the only reference, so a lease reads it without a lock.</summary>
internal sealed unsafe class FrameSlot
{
    private int _refs;
    private FramePlane[] _planes = [];

    internal FrameSlot(FramePool pool) => Pool = pool;

    public FramePool Pool { get; }
    public byte* Buffer { get; private set; }
    public nuint Capacity { get; private set; }
    public FrameLayout? Layout { get; private set; }
    public IReadOnlyList<FramePlane> Planes => _planes;

    public long PtsNs;
    public long DurationNs;
    public long Generation;
    public long ArrivalNs;
    public ColorMatrix Matrix;
    public ColorRange Range;
    public TransferFunction Transfer;
    public bool HasAlpha;

    /// <summary>Acquired by a consumer at least once (a frame trimmed unseen while playing was dropped).</summary>
    public bool Shown;

    /// <summary>The late counter has counted this frame (once per frame, however many outputs ask).</summary>
    public bool CountedLate;

    public byte* PlaneData(int plane) => Buffer + Layout!.Offset(plane);

    internal int Refs => Volatile.Read(ref _refs);

    /// <summary>Sizes the buffer for a layout (reallocating only when it grows or the plane set changes) and
    /// resets the per-frame metadata. Called by the producer on a slot it alone holds.</summary>
    internal void ReservePlanes(int planes)
    {
        if (_planes.Length != planes)
            _planes = new FramePlane[planes];
    }

    internal void Prepare(FrameLayout layout)
    {
        if (Capacity < layout.TotalBytes)
        {
            if (Buffer is not null)
                NativeMemory.AlignedFree(Buffer);
            Buffer = (byte*)NativeMemory.AlignedAlloc(layout.TotalBytes, FrameLayout.Alignment);
            Capacity = layout.TotalBytes;
        }
        if (!layout.Equals(Layout))
        {
            Layout = layout;
            if (_planes.Length != layout.Planes)
                _planes = new FramePlane[layout.Planes];
            for (var i = 0; i < layout.Planes; i++)
                _planes[i] = new FramePlane((nint)(Buffer + layout.Offset(i)), layout.Stride(i), layout.SampleWidth(i), layout.Rows(i));
        }
        else if (_planes.Length > 0 && _planes[0].Data != (nint)(Buffer + layout.Offset(0)))
        {
            for (var i = 0; i < layout.Planes; i++)
                _planes[i] = _planes[i] with { Data = (nint)(Buffer + layout.Offset(i)) };
        }
        PtsNs = 0;
        DurationNs = 0;
        ArrivalNs = 0;
        Matrix = ColorMatrix.Bt709;
        Range = ColorRange.Limited;
        Transfer = TransferFunction.Sdr;
        HasAlpha = false;
        Shown = false;
        CountedLate = false;
    }

    internal void SetFirstReference() => Volatile.Write(ref _refs, 1);

    public void AddRef() => Interlocked.Increment(ref _refs);

    public void Release()
    {
        var left = Interlocked.Decrement(ref _refs);
        if (left == 0)
            Pool.Return(this);
        else if (left < 0)
            throw new InvalidOperationException("A frame was released more often than it was referenced.");
    }

    internal void FreeBuffer()
    {
        if (Buffer is not null)
            NativeMemory.AlignedFree(Buffer);
        Buffer = null;
        Capacity = 0;
        Layout = null;
    }
}

/// <summary>A fixed set of picture buffers for one source, allocated once (64-byte aligned native memory) and
/// reused for every frame. Renting never allocates once warm and never waits: an empty pool answers null and
/// the producer waits on its own signal (raised by <paramref name="onFree"/>). Disposing frees the free
/// buffers at once and each leased one when its last reference ends, so a renderer still holding a lease when
/// its source closes reads valid memory until it lets go.</summary>
internal sealed class FramePool : IDisposable
{
    private readonly Lock _gate = new();
    private readonly FrameSlot?[] _free;
    private readonly Action? _onFree;
    private int _freeCount;
    private bool _disposed;

    public FramePool(int capacity, Action? onFree = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _free = new FrameSlot?[capacity];
        _onFree = onFree;
        // Every slot object exists from the start, so a rent never allocates on the producer's thread however
        // far ahead it runs; a slot's pixels are native memory taken on its first rent.
        for (var i = 0; i < capacity; i++)
            _free[i] = new FrameSlot(this);
        _freeCount = capacity;
    }

    public int Capacity { get; }

    /// <summary>Slots out of the pool (queued or leased).</summary>
    public int InUse
    {
        get
        {
            lock (_gate)
                return Capacity - _freeCount;
        }
    }

    /// <summary>A slot sized for the layout with one reference (the caller's), or null when every slot is in
    /// use.</summary>
    public FrameSlot? TryRent(FrameLayout layout)
    {
        FrameSlot? slot = null;
        lock (_gate)
        {
            if (_disposed)
                return null;
            if (_freeCount > 0)
                slot = _free[--_freeCount];
        }
        if (slot is null)
            return null;
        slot.Prepare(layout);
        slot.SetFirstReference();
        return slot;
    }

    /// <summary>Sizes every free slot's plane table for the layout, so no slot's first rent in the show makes a
    /// managed object (the producer calls this when it first meets the stream's layout).</summary>
    public void Reserve(FrameLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        lock (_gate)
            for (var i = 0; i < _freeCount; i++)
                _free[i]!.ReservePlanes(layout.Planes);
    }

    internal void Return(FrameSlot slot)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                slot.FreeBuffer();
                return;
            }
            _free[_freeCount++] = slot;
        }
        _onFree?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            for (var i = 0; i < _freeCount; i++)
            {
                _free[i]?.FreeBuffer();
                _free[i] = null;
            }
            _freeCount = 0;
        }
    }
}
