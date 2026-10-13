using System.Runtime.InteropServices;
using PlayMap.Core.Media;

namespace PlayMap.Engine.Rendering;

/// <summary>A decoder's frame handed to the renderer under a generation no other decoder can have: the
/// renderer skips an upload whose generation is the one on the GPU, and two decoders both count from one, so a
/// playlist that switches clips would otherwise keep the old clip's picture. The decoder's own generation goes
/// in the low 32 bits, the decoder's epoch (one per opened source) in the high ones. Leases are pooled (none
/// allocated per frame once warm): disposing returns the inner lease to its decoder and this one to the pool.</summary>
internal sealed class RemappedLease : VideoFrameLease
{
    private readonly RemappedLeasePool _home;
    private VideoFrameLease? _inner;
    private long _generation;

    internal RemappedLease(RemappedLeasePool home) => _home = home;

    private VideoFrameLease Inner => Volatile.Read(ref _inner) ?? throw new ObjectDisposedException(nameof(RemappedLease));

    public override PixelLayout Layout => Inner.Layout;
    public override int Width => Inner.Width;
    public override int Height => Inner.Height;
    public override IReadOnlyList<FramePlane> Planes => Inner.Planes;
    public override long PtsNs => Inner.PtsNs;
    public override long DurationNs => Inner.DurationNs;
    public override long Generation => _generation;
    public override ColorMatrix Matrix => Inner.Matrix;
    public override ColorRange Range => Inner.Range;
    public override TransferFunction Transfer => Inner.Transfer;
    public override bool HasAlpha => Inner.HasAlpha;

    internal void Attach(VideoFrameLease inner, long epoch)
    {
        _generation = (epoch << 32) | (inner.Generation & 0xFFFF_FFFFL);
        Volatile.Write(ref _inner, inner);
    }

    // The base class's once-only flag cannot be re-armed for a pooled object, so the release happens in
    // Dispose(bool), guarded by the inner exchange: a second dispose of the same rental does nothing.
    protected override void Release()
    {
        // Intentionally empty: see Dispose(bool).
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _inner, null) is { } inner)
        {
            inner.Dispose();
            _home.Return(this);
        }
        base.Dispose(disposing);
    }
}

/// <summary>The remapped leases of one provider, reused frame to frame.</summary>
internal sealed class RemappedLeasePool
{
    private readonly Lock _gate = new();
    private readonly Stack<RemappedLease> _free = new();

    public VideoFrameLease? Wrap(VideoFrameLease? inner, long epoch)
    {
        if (inner is null)
            return null;
        RemappedLease? lease = null;
        lock (_gate)
            _free.TryPop(out lease);
        lease ??= new RemappedLease(this);
        lease.Attach(inner, epoch);
        return lease;
    }

    public void Return(RemappedLease lease)
    {
        lock (_gate)
            _free.Push(lease);
    }
}

/// <summary>A picture held in a managed array, pinned for the lease's life: a scan frame or a timer view. The
/// array is the owner's; the lease only borrows it until the renderer has uploaded it.</summary>
internal sealed class PinnedLease : VideoFrameLease
{
    private readonly FramePlane[] _planes;
    private readonly Action? _released;
    private GCHandle _pin;

    public PinnedLease(PixelLayout layout, byte[] data, int width, int height, int stride, long generation, Action? released = null)
    {
        Layout = layout;
        Width = width;
        Height = height;
        Generation = generation;
        _released = released;
        _pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        _planes = [new FramePlane(_pin.AddrOfPinnedObject(), stride, width, height)];
    }

    public override PixelLayout Layout { get; }
    public override int Width { get; }
    public override int Height { get; }
    public override IReadOnlyList<FramePlane> Planes => _planes;
    public override long PtsNs => 0;
    public override long DurationNs => 0;
    public override long Generation { get; }
    public override ColorRange Range => ColorRange.Full;

    protected override void Release()
    {
        if (_pin.IsAllocated)
            _pin.Free();
        _released?.Invoke();
    }
}
