using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using PlayMap.Core.Media;

namespace PlayMap.Media.Frames;

/// <summary>The plane geometry of a layout at a size. Strides are 64-byte aligned (an upload never straddles a
/// cache line it did not need), except a block-compressed plane's, whose rows are tight and whose Width and Height count 4×4
/// blocks. Plane sample widths: RGBA layouts count pixels; NV12/P010's second plane counts
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
            case PixelLayout.Bc1:
            case PixelLayout.Bc4:
                AddBlocks(width, height, 8);
                break;
            case PixelLayout.Bc3:
            case PixelLayout.Bc3YCoCg:
            case PixelLayout.Bc7:
            case PixelLayout.Bc6hUfloat:
            case PixelLayout.Bc6hSfloat:
                AddBlocks(width, height, 16);
                break;
            case PixelLayout.Bc3YCoCgBc4:
                AddBlocks(width, height, 16);
                AddBlocks(width, height, 8);
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

    // A block-compressed plane: Width and Height count 4×4 blocks, rows are tight (HAP's chunks fill a plane contiguously and
    // a compressed upload takes tight rows); the plane still starts on an aligned offset.
    private void AddBlocks(int width, int height, int blockBytes)
    {
        var i = Planes++;
        var blocksWide = (width + 3) / 4;
        var blocksHigh = (height + 3) / 4;
        _sampleWidth[i] = blocksWide;
        _rows[i] = blocksHigh;
        _rowBytes[i] = blocksWide * blockBytes;
        _stride[i] = blocksWide * blockBytes;
        var start = (TotalBytes + Alignment - 1) / Alignment * Alignment;
        _offset[i] = start;
        TotalBytes = start + (nuint)_stride[i] * (nuint)blocksHigh;
    }

    /// <summary>True for the block-compressed layouts, whose planes count 4×4 blocks with tight rows.</summary>
    public bool IsBlocks => PlayMap.Core.Media.Hap.HapCodecs.IsBlockLayout(Layout);

    public bool Equals(FrameLayout? other) =>
        other is not null && other.Layout == Layout && other.Width == Width && other.Height == Height;

    public override bool Equals(object? obj) => Equals(obj as FrameLayout);

    public override int GetHashCode() => HashCode.Combine(Layout, Width, Height);
}

/// <summary>One pooled picture buffer and the metadata of the frame it holds. Reference counted: the queue
/// holds one reference, each lease one more; at zero the slot returns to its pool. Metadata is written only
/// while the producer holds the only reference, so a lease reads it without a lock.</summary>
internal sealed unsafe class FrameSlot : IGpuSurfaceOwner
{
    private int _refs;
    private FramePlane[] _planes = [];
    private byte* _own;
    private nuint _ownCapacity;
    private IUploadArena? _stagedIn;

    internal FrameSlot(FramePool pool) => Pool = pool;

    public FramePool Pool { get; }

    /// <summary>Where the planes are: the slot's own native buffer, or a block of the renderer's upload arena.</summary>
    public byte* Buffer { get; private set; }

    public nuint Capacity { get; private set; }
    public FrameLayout? Layout { get; private set; }

    /// <summary>The planes of a CPU picture; none for a GPU picture.</summary>
    public IReadOnlyList<FramePlane> Planes => IsGpu ? [] : _planes;

    /// <summary>The block of the upload arena the planes were written into (empty when they are in the slot's own buffer).</summary>
    public UploadBlock Staged { get; private set; }

    /// <summary>The picture is on the GPU (<see cref="Surface"/>): a decoder surface the slot holds through <see cref="HwFrame"/>.</summary>
    public bool IsGpu { get; private set; }

    /// <summary>The layout and visible size of a GPU picture (its planes are the surface's).</summary>
    public FrameLayout? GpuLayout { get; private set; }

    /// <summary>The GPU picture's description, made once per slot and reused.</summary>
    public GpuSurface? Surface { get; private set; }

    /// <summary>An AVFrame* the slot owns (allocated once, on the producer's thread) holding the decoder's surface while the picture
    /// is out: unreferenced when the slot returns to its pool, so the surface goes back to the decoder only then.</summary>
    public nint HwFrame;

    public long PtsNs;
    public long DurationNs;
    public long Generation;
    public long ArrivalNs;
    public ColorMatrix Matrix;
    public ColorRange Range;
    public TransferFunction Transfer;
    public bool HasAlpha;

    /// <summary>A HAP HDR frame's factor to SDR reference white (1 for everything else).</summary>
    public double HdrScale = 1;

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
        if (_ownCapacity < layout.TotalBytes)
        {
            if (_own is not null)
                NativeMemory.AlignedFree(_own);
            _own = (byte*)NativeMemory.AlignedAlloc(layout.TotalBytes, FrameLayout.Alignment);
            _ownCapacity = layout.TotalBytes;
        }
        Place(layout, _own, _ownCapacity);
    }

    /// <summary>As <see cref="Prepare"/>, with the planes written into a block of the renderer's upload arena (returned to it when
    /// the slot comes back); the slot's own buffer is not touched.</summary>
    internal void PrepareStaged(FrameLayout layout, IUploadArena arena, in UploadBlock block)
    {
        Place(layout, (byte*)block.Pointer, (nuint)block.Bytes);
        _stagedIn = arena;
        Staged = block;
    }

    /// <summary>Readies the slot for a GPU picture of the layout's visible size; the producer fills <see cref="Surface"/> (made here
    /// once) and <see cref="HwFrame"/>.</summary>
    internal GpuSurface PrepareGpu(FrameLayout layout)
    {
        Surface ??= new GpuSurface();
        Surface.Owner = this;
        GpuLayout = layout;
        IsGpu = true;
        ResetMetadata();
        return Surface;
    }

    private void Place(FrameLayout layout, byte* buffer, nuint capacity)
    {
        var moved = Buffer != buffer;
        Buffer = buffer;
        Capacity = capacity;
        IsGpu = false;
        if (!layout.Equals(Layout) || moved)
        {
            Layout = layout;
            if (_planes.Length != layout.Planes)
                _planes = new FramePlane[layout.Planes];
            for (var i = 0; i < layout.Planes; i++)
                _planes[i] = new FramePlane((nint)(Buffer + layout.Offset(i)), layout.Stride(i), layout.SampleWidth(i), layout.Rows(i));
        }
        ResetMetadata();
    }

    private void ResetMetadata()
    {
        PtsNs = 0;
        DurationNs = 0;
        ArrivalNs = 0;
        Matrix = ColorMatrix.Bt709;
        Range = ColorRange.Limited;
        Transfer = TransferFunction.Sdr;
        HasAlpha = false;
        HdrScale = 1;
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

    void IGpuSurfaceOwner.Retain(GpuSurface surface) => AddRef();

    void IGpuSurfaceOwner.Release(GpuSurface surface) => Release();

    /// <summary>The slot came back with no reference left: what it held of the decoder (a surface) or of the arena (a block) goes
    /// back to them now.</summary>
    internal void LetGo()
    {
        if (HwFrame != 0)
            ffmpeg.av_frame_unref((AVFrame*)HwFrame);
        IsGpu = false;
        if (_stagedIn is { } arena)
        {
            arena.Return(Staged);
            _stagedIn = null;
            Staged = default;
            Layout = null;
            Buffer = null;
        }
    }

    internal void FreeBuffer()
    {
        LetGo();
        if (_own is not null)
            NativeMemory.AlignedFree(_own);
        _own = null;
        _ownCapacity = 0;
        Buffer = null;
        Capacity = 0;
        Layout = null;
        if (HwFrame != 0)
        {
            var f = (AVFrame*)HwFrame;
            ffmpeg.av_frame_free(&f);
            HwFrame = 0;
        }
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

    internal Lock Gate => _gate;

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

    /// <summary>A slot for a GPU picture with one reference (the caller's), or null when every slot is in use. No buffer is taken.</summary>
    public FrameSlot? TryRentGpu(FrameLayout layout, out GpuSurface surface)
    {
        surface = null!;
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
        surface = slot.PrepareGpu(layout);
        slot.SetFirstReference();
        return slot;
    }

    /// <summary>A slot whose planes go into a block of the upload arena, or null when every slot is in use or the arena has no
    /// room (the caller rents a plain slot instead). The block goes back to the arena when the slot comes back.</summary>
    public FrameSlot? TryRentStaged(FrameLayout layout, IUploadArena arena)
    {
        ArgumentNullException.ThrowIfNull(arena);
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
        if (!arena.TryRent((long)layout.TotalBytes, out var block) || block.Pointer == 0)
        {
            lock (_gate)
                _free[_freeCount++] = slot;
            return null;
        }
        slot.PrepareStaged(layout, arena, block);
        slot.SetFirstReference();
        return slot;
    }

    internal void Return(FrameSlot slot)
    {
        slot.LetGo();
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
