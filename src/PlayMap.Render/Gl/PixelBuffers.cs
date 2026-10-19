using PlayMap.Core.Gpu;

namespace PlayMap.Render.Gl;

/// <summary>Streams uploads through a ring of pixel-unpack buffers: the CPU copies a plane into a mapped
/// buffer and the texture update reads from it, so the driver may move the bytes asynchronously and the
/// decoder's buffer can go back to its pool as soon as the copy is done. Rows are repacked tight on the
/// copy, so any source stride works.</summary>
public sealed unsafe class PixelUnpackRing : IDisposable
{
    private readonly GlDevice _device;
    private readonly uint[] _buffers;
    private int _next;
    private bool _disposed;

    public PixelUnpackRing(GlDevice device, int slots = 3)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        _buffers = new uint[Math.Max(1, slots)];
        for (var i = 0; i < _buffers.Length; i++)
            _buffers[i] = device.CreatePixelBuffer(pack: false, 16);
    }

    /// <summary>Uploads <paramref name="rows"/> rows of <paramref name="rowBytes"/> bytes from
    /// <paramref name="source"/> (rows <paramref name="sourceStride"/> bytes apart) into the texture. False
    /// when the driver would not map a buffer (the caller uploads directly instead).</summary>
    public bool Upload(GpuTexture texture, nint source, int sourceStride, int rowBytes, int rows, UploadFormat format)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var buffer = _buffers[_next];
        _next = (_next + 1) % _buffers.Length;
        var bytes = rowBytes * rows;
        var p = _device.MapUnpack(buffer, bytes);
        if (p is null)
            return false;
        if (sourceStride == rowBytes)
        {
            GlDevice.Copy(source, p, (long)bytes);
        }
        else
        {
            for (var y = 0; y < rows; y++)
                GlDevice.Copy(source + (nint)y * sourceStride, p + (long)y * rowBytes, rowBytes);
        }
        _device.UnmapUnpack(buffer);
        _device.UploadFromUnpackBuffer(texture, buffer, 0, format, 0);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        foreach (var b in _buffers)
            _device.DeleteBuffer(b);
        _disposed = true;
    }
}

/// <summary>Reads targets back without stalling: each request queues a read into the next of a ring of
/// pixel-pack buffers with a fence behind it; <see cref="TryCollect"/> maps the oldest only once its fence
/// has passed. With three slots a picture arrives two or three frames after it was drawn and the render
/// thread never waits for the GPU.</summary>
public sealed unsafe class PixelPackRing : IReadbackRing
{
    private struct Slot
    {
        public uint Buffer;
        public int Capacity;
        public nint Fence;
        public int Width;
        public int Height;
        public long Stamp;
        public bool Pending;
    }

    private readonly GlDevice _device;
    private readonly Slot[] _slots;
    private int _write;
    private int _read;
    private bool _disposed;

    public PixelPackRing(GlDevice device, int slots = 3)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        _slots = new Slot[Math.Max(2, slots)];
    }

    /// <summary>Requests queued and not yet collected.</summary>
    public int Pending
    {
        get
        {
            var n = 0;
            foreach (var s in _slots)
                if (s.Pending)
                    n++;
            return n;
        }
    }

    /// <summary>Queues a read of the target (BGRA, top row first). False when every slot is still in flight
    /// (the request is skipped: a preview drops a picture rather than wait).</summary>
    public bool Request(RenderTarget target, long stamp)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ref var slot = ref _slots[_write];
        if (slot.Pending)
            return false;
        var bytes = target.Width * target.Height * 4;
        if (slot.Buffer == 0 || slot.Capacity < bytes)
        {
            if (slot.Buffer != 0)
                _device.DeleteBuffer(slot.Buffer);
            slot.Buffer = _device.CreatePixelBuffer(pack: true, bytes);
            slot.Capacity = bytes;
        }
        _device.ReadIntoPackBuffer(target, slot.Buffer);
        slot.Fence = _device.InsertFence();
        slot.Width = target.Width;
        slot.Height = target.Height;
        slot.Stamp = stamp;
        slot.Pending = true;
        _write = (_write + 1) % _slots.Length;
        return true;
    }

    /// <summary>Hands the oldest finished read to <paramref name="consume"/> (BGRA bytes, width, height,
    /// stamp) and frees its slot; false when the oldest is not finished yet. Never waits.</summary>
    public bool TryCollect<TState>(TState state, ReadbackConsumer<TState> consume)
    {
        ArgumentNullException.ThrowIfNull(consume);
        ref var slot = ref _slots[_read];
        if (!slot.Pending || !_device.IsSignaled(slot.Fence))
            return false;
        _device.DeleteFence(slot.Fence);
        slot.Fence = 0;
        var bytes = slot.Width * slot.Height * 4;
        var p = _device.MapPack(slot.Buffer, bytes);
        if (p is not null)
        {
            consume(state, new ReadOnlySpan<byte>(p, bytes), slot.Width, slot.Height, slot.Stamp);
            _device.UnmapPack(slot.Buffer);
        }
        slot.Pending = false;
        _read = (_read + 1) % _slots.Length;
        return p is not null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        for (var i = 0; i < _slots.Length; i++)
        {
            _device.DeleteFence(_slots[i].Fence);
            if (_slots[i].Buffer != 0)
                _device.DeleteBuffer(_slots[i].Buffer);
            _slots[i] = default;
        }
        _disposed = true;
    }
}

/// <summary>Read-backs the reader holds in place on GL: each request reads the target (RGBA bytes, top row first) into a free
/// pixel-pack buffer with a fence behind it; <see cref="TryTake"/> maps the oldest once its fence has passed and hands it out, and the
/// buffer stays mapped (GL allows it while that buffer is not used by GL) until the reader lets go of it — the next take unmaps it
/// and frees its slot. A request with no free slot is refused: the render thread never waits for the GPU or the reader.
///
/// Threading: the render thread, but <see cref="Release"/> (any thread, once a read).</summary>
internal sealed unsafe class GlHeldReadbackRing : IHeldReadbackRing
{
    private enum SlotState
    {
        Free,
        Reading,
        Handed,
    }

    private sealed class Slot
    {
        public uint Buffer;
        public int Capacity;
        public nint Fence;
        public SlotState State;
        public long Sequence;
        public int Bytes;
        public int Width;
        public int Height;
        public long Stamp;
        public int Released;
    }

    private readonly GlDevice _device;
    private readonly Slot[] _slots;
    private long _sequence;
    private bool _disposed;

    public GlHeldReadbackRing(GlDevice device, int slots)
    {
        _device = device;
        _slots = new Slot[Math.Max(2, slots)];
        for (var i = 0; i < _slots.Length; i++)
            _slots[i] = new Slot();
    }

    public int Slots => _slots.Length;

    public bool HasFree
    {
        get
        {
            foreach (var s in _slots)
                if (s.State == SlotState.Free)
                    return true;
            return false;
        }
    }

    public int Held
    {
        get
        {
            var n = 0;
            foreach (var s in _slots)
                if (s.State == SlotState.Handed && Volatile.Read(ref s.Released) == 0)
                    n++;
            return n;
        }
    }

    public bool Request(RenderTarget target, long stamp, out int slot)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_disposed, this);
        slot = -1;
        for (var i = 0; i < _slots.Length && slot < 0; i++)
            if (_slots[i].State == SlotState.Free)
                slot = i;
        if (slot < 0)
            return false;
        var s = _slots[slot];
        var bytes = target.Width * target.Height * 4;
        if (s.Buffer == 0 || s.Capacity < bytes)
        {
            if (s.Buffer != 0)
                _device.DeleteBuffer(s.Buffer);
            s.Buffer = _device.CreatePixelBuffer(pack: true, bytes);
            s.Capacity = bytes;
        }
        _device.ReadRgbaIntoPackBuffer(target, s.Buffer, sixteen: false);
        s.Fence = _device.InsertFence();
        s.State = SlotState.Reading;
        s.Sequence = ++_sequence;
        s.Bytes = bytes;
        s.Width = target.Width;
        s.Height = target.Height;
        s.Stamp = stamp;
        return true;
    }

    public bool TryTake(out HeldReadback read)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var s in _slots)
        {
            if (s.State == SlotState.Handed && Volatile.Read(ref s.Released) != 0)
            {
                _device.UnmapPack(s.Buffer);
                s.State = SlotState.Free;
            }
        }
        while (true)
        {
            var oldest = -1;
            for (var i = 0; i < _slots.Length; i++)
                if (_slots[i].State == SlotState.Reading && (oldest < 0 || _slots[i].Sequence < _slots[oldest].Sequence))
                    oldest = i;
            if (oldest < 0 || !_device.IsSignaled(_slots[oldest].Fence))
            {
                read = default;
                return false;
            }
            var s = _slots[oldest];
            _device.DeleteFence(s.Fence);
            s.Fence = 0;
            var p = _device.MapPack(s.Buffer, s.Bytes);
            if (p is null)
            {
                s.State = SlotState.Free;
                continue;
            }
            Volatile.Write(ref s.Released, 0);
            s.State = SlotState.Handed;
            read = new HeldReadback(oldest, (nint)p, s.Bytes, s.Width, s.Height, s.Stamp);
            return true;
        }
    }

    public void Release(int slot) => Volatile.Write(ref _slots[slot].Released, 1);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var s in _slots)
        {
            if (s.State == SlotState.Handed)
                _device.UnmapPack(s.Buffer);
            _device.DeleteFence(s.Fence);
            if (s.Buffer != 0)
                _device.DeleteBuffer(s.Buffer);
            s.Buffer = 0;
            s.Fence = 0;
            s.State = SlotState.Free;
        }
    }
}
