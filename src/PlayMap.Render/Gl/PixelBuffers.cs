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
public sealed unsafe class PixelPackRing : IDisposable
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
