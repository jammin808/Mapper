using PlayMap.Core.Media;
using PlayMap.Core.Media.Hap;

namespace PlayMap.Media.Frames;

/// <summary>A lease on a pooled decoded frame. Leases are themselves pooled (no allocation per frame once
/// warm): dispose a lease exactly once and drop the reference — after disposal the object serves another
/// frame. Reading a disposed lease throws.
/// <para>Planes, as the uploader reads them (strides 64-byte aligned; <see cref="FramePlane.Width"/> counts
/// samples): Rgba8/Bgra8 one plane of 4-byte pixels; RgbaHalf one plane of four IEEE halves; Gray8 one plane;
/// Nv12 Y then interleaved UV (width = UV pairs, half height); P010 the same with 16-bit samples, 10 bits in the
/// high bits; Yuv420P/Yuv422P/Yuv444P Y, U, V (chroma halved both ways, horizontally, or not at all); Yuv420P10 Y,
/// U, V of 16-bit little-endian samples with 10 significant low bits. <see cref="VideoFrameLease.Matrix"/>,
/// <see cref="VideoFrameLease.Range"/> and <see cref="VideoFrameLease.Transfer"/> come from the frame's own tags
/// (RGBA layouts are full range). Block-compressed (HAP) layouts: one plane of 4×4 blocks with tight rows (Width and Height
/// count blocks), two for HAP Q Alpha (the YCoCg BC3 plane, then the BC4 alpha plane).</para></summary>
public sealed class MediaFrameLease : VideoFrameLease, IHapPicture
{
    private readonly LeasePool _home;
    private FrameSlot? _slot;

    internal MediaFrameLease(LeasePool home) => _home = home;

    private FrameSlot Slot => Volatile.Read(ref _slot) ?? throw new ObjectDisposedException(nameof(MediaFrameLease));

    public override PixelLayout Layout => Slot.Layout!.Layout;
    public override int Width => Slot.Layout!.Width;
    public override int Height => Slot.Layout!.Height;
    public override IReadOnlyList<FramePlane> Planes => Slot.Planes;
    public override long PtsNs => Slot.PtsNs;
    public override long DurationNs => Slot.DurationNs;
    public override long Generation => Slot.Generation;
    public override ColorMatrix Matrix => Slot.Matrix;
    public override ColorRange Range => Slot.Range;
    public override TransferFunction Transfer => Slot.Transfer;
    public override bool HasAlpha => Slot.HasAlpha;

    public double HdrScale => Slot.HdrScale;

    /// <summary>When the frame was handed over by the decoder, on the process's monotonic clock (ns): a live
    /// source's latency is measured from here to the frame that draws it.</summary>
    public override long ArrivalNs => Slot.ArrivalNs;

    internal void Attach(FrameSlot slot) => Volatile.Write(ref _slot, slot);

    // The base class's once-only flag cannot be re-armed for a pooled object, so the release happens here,
    // guarded by the slot exchange: a second dispose of the same rental finds no slot and does nothing.
    protected override void Release()
    {
        // Intentionally empty: see Dispose(bool).
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var slot = Interlocked.Exchange(ref _slot, null);
            if (slot is not null)
            {
                slot.Release();
                _home.Return(this);
            }
        }
        base.Dispose(disposing);
    }
}

/// <summary>The lease objects of one source, reused so a frame acquire allocates nothing once warm.</summary>
internal sealed class LeasePool
{
    private readonly Lock _gate = new();
    private readonly MediaFrameLease?[] _free;
    private int _count;

    public LeasePool(int capacity) => _free = new MediaFrameLease?[Math.Max(4, capacity)];

    /// <summary>A lease on the slot; the caller has already added the slot reference the lease owns.</summary>
    public MediaFrameLease Rent(FrameSlot slot)
    {
        MediaFrameLease? lease = null;
        lock (_gate)
        {
            if (_count > 0)
            {
                lease = _free[--_count];
                _free[_count] = null;
            }
        }
        lease ??= new MediaFrameLease(this);
        lease.Attach(slot);
        return lease;
    }

    internal void Return(MediaFrameLease lease)
    {
        lock (_gate)
        {
            if (_count < _free.Length)
                _free[_count++] = lease;
        }
    }
}
