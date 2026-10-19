using System.Diagnostics;
using PlayMap.Core.Gpu;
using PlayMap.Core.Media;
using PlayMap.Render.Hap;

namespace PlayMap.Render.Gl;

/// <summary>Pixels to the GPU through the GL backend's pixel-unpack rings (<see cref="PixelUnpackRing"/> for plain pixels, a ring of
/// unpack buffers for block-compressed levels, each made on first use): the copy into a mapped buffer and the texture update from it
/// are issued at once on the device's context, and the copy queue's next value is signalled after them. GL has one stream, so the
/// frame that reads the texture is ordered after the upload; the value says when the GPU has done it.
///
/// The upload arena is a persistently mapped unpack buffer (<see cref="GlUploadArena"/>): a staged block fills its texture from the
/// buffer at the block's offset, with no copy; every submit fences the blocks returned since the last one (<see cref="EndFrame"/>).
/// Block uploads of a megabyte or more count in <see cref="HapUploadMeter"/>.
///
/// Threading: the render thread (the GL context's).</summary>
internal sealed unsafe class GlUploadRing : IUploadRing, IDisposable
{
    private const int BlockSlots = 3;

    private readonly GlDevice _device;
    private readonly FenceRing _copy;
    private PixelUnpackRing? _ring;
    private uint[]? _blocks;
    private int _nextBlock;
    private GlUploadArena? _arena;
    private bool _disposed;

    public GlUploadRing(GlDevice device, FenceRing copy)
    {
        _device = device;
        _copy = copy;
    }

    public IUploadArena? Arena => _arena;

    /// <summary>The open arena as the GL backend reads it.</summary>
    internal GlUploadArena? GlArena => _arena;

    public bool TryUpload(GpuTexture dest, nint source, int sourceStride, int rowBytes, int rows, UploadFormat format, out FenceValue done)
    {
        ArgumentNullException.ThrowIfNull(dest);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ring ??= new PixelUnpackRing(_device);
        if (!_ring.Upload(dest, source, sourceStride, rowBytes, rows, format))
        {
            done = FenceValue.None;
            return false;
        }
        done = _copy.Signal();
        return true;
    }

    /// <summary>The level copied into the next of a ring of unpack buffers and the texture filled from it; a buffer the driver will
    /// not map falls back to a client-memory upload, so the copy is always made.</summary>
    public bool TryUploadCompressed(GpuTexture dest, nint blocks, int bytes, out FenceValue done)
    {
        ArgumentNullException.ThrowIfNull(dest);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_blocks is null)
        {
            _blocks = new uint[BlockSlots];
            for (var i = 0; i < _blocks.Length; i++)
                _blocks[i] = _device.CreatePixelBuffer(pack: false, 16);
        }
        var t0 = Stopwatch.GetTimestamp();
        var buffer = _blocks[_nextBlock];
        _nextBlock = (_nextBlock + 1) % _blocks.Length;
        var p = _device.MapUnpack(buffer, bytes);
        if (p is null)
        {
            _device.UploadCompressed(dest, blocks, bytes);
        }
        else
        {
            GlDevice.Copy(blocks, p, bytes);
            _device.UnmapUnpack(buffer);
            _device.UploadCompressedFromUnpackBuffer(dest, buffer, 0, bytes);
        }
        HapUploadMeter.Add(bytes, Stopwatch.GetTimestamp() - t0);
        done = _copy.Signal();
        return true;
    }

    public IUploadArena? OpenArena(int megabytes, out string words)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CloseArena();
        _arena = GlUploadArena.Create(_device, megabytes, out words);
        return _arena;
    }

    public void CloseArena()
    {
        _arena?.Dispose();
        _arena = null;
    }

    public bool InArena(in UploadBlock block) => _arena is { } arena && arena.Owns(block);

    /// <summary>The level filled from the arena's buffer at the block's offset: the driver reads the producer's bytes where they were
    /// written.</summary>
    public bool TryUploadStaged(GpuTexture dest, in UploadBlock block, nint data, int bytes, out FenceValue done)
    {
        ArgumentNullException.ThrowIfNull(dest);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_arena is not { } arena || !arena.Owns(block) || data < block.Pointer || data + bytes > block.Pointer + (nint)block.Bytes)
        {
            done = FenceValue.None;
            return false;
        }
        var t0 = Stopwatch.GetTimestamp();
        _device.UploadCompressedFromUnpackBuffer(dest, arena.Buffer, (nint)(block.Offset + (data - block.Pointer)), bytes);
        HapUploadMeter.Add(bytes, Stopwatch.GetTimestamp() - t0);
        done = _copy.Signal();
        return true;
    }

    /// <summary>After a frame's submit: fences the arena's blocks returned since the last one, and frees those whose fence passed.</summary>
    public void EndFrame() => _arena?.EndFrame();

    public void Dispose()
    {
        if (_disposed)
            return;
        _ring?.Dispose();
        if (_blocks is not null)
            foreach (var b in _blocks)
                _device.DeleteBuffer(b);
        _blocks = null;
        CloseArena();
        _disposed = true;
    }
}

/// <summary>GPU timestamps on GL: <see cref="Slots"/> slots a frame (<see cref="SlotCount"/> to start, grown when a show asks for more,
/// <see cref="Reserve"/>), the last <see cref="Depth"/> frames kept, each slot a query written where the frame's list asks
/// (<see cref="ICommandList.Timestamp"/>) and read back once the GPU has it, never waited on. The queries are made on the first
/// timestamp.
///
/// Three kinds of context: one with a timestamp counter (desktop GL 3.3, where timestamps are core, or ES with
/// EXT_disjoint_timer_query whose counter has bits) writes a timestamp into each slot; one with timer queries but no counter bits
/// (ES whose EXT_disjoint_timer_query reports none, as ANGLE's Direct3D 11 back end does) measures a span as an elapsed-time query
/// from its first slot to its second, one at a time — a span begun while another is open (a generator or an effect inside an
/// output's span) is not measured; one with no timer queries (ES without the extension) measures nothing (<see cref="Slots"/> 0).
/// A result taken across a disjoint event (ES: the GPU's clock jumped) is no measurement.
///
/// Threading: the render thread (the GL context's).</summary>
internal sealed class GlTimers : IGpuTimers, IDisposable
{
    /// <summary>Timestamps a frame may write to start with: two a span, for every output, generator and effect a frame times.</summary>
    public const int SlotCount = 64;

    /// <summary>The most slots a frame may grow to.</summary>
    public const int MaxSlots = 1024;

    /// <summary>Frames whose timestamps are kept to be read.</summary>
    public const int Depth = 4;

    /// <summary>How a context measures.</summary>
    internal enum Kind
    {
        None,
        Timestamps,
        Elapsed,
    }

    private readonly GlDevice _device;
    private readonly long[] _frames = new long[Depth];
    private int _slots = SlotCount;
    private bool[] _written = new bool[Depth * SlotCount];
    private uint[]? _queries;
    private int _open = -1;
    private bool _disposed;

    public GlTimers(GlDevice device)
        : this(device, device.TimestampBits > 0 ? Kind.Timestamps : device.HasTimerQuery ? Kind.Elapsed : Kind.None)
    {
    }

    /// <summary>Timers that measure as <paramref name="mode"/> says (a test stands in a context with no timestamp counter; a context
    /// asked for a kind it lacks measures nothing).</summary>
    internal GlTimers(GlDevice device, Kind mode)
    {
        _device = device;
        Mode = mode == Kind.Timestamps && device.TimestampBits == 0 ? Kind.None
            : mode == Kind.Elapsed && !device.HasTimerQuery ? Kind.None
            : mode;
        Array.Fill(_frames, -1);
    }

    /// <summary>How this context measures.</summary>
    internal Kind Mode { get; }

    /// <summary>Whether this context measures at all.</summary>
    public bool Measures => Mode != Kind.None;

    public int Slots => Measures ? _slots : 0;

    /// <summary>Timestamps asked of the timers since they were made (each <see cref="Stamp"/> on a context that measures).</summary>
    internal long Stamps { get; private set; }

    /// <summary>Writes the GPU's clock into the frame's slot once the commands before it have run (on a context with no counter: a
    /// span's first slot begins its elapsed-time query unless one is open, its second ends it).</summary>
    public void Stamp(long frame, int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, _slots);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Measures)
            return;
        Stamps++;
        if (_queries is null)
        {
            _queries = new uint[Depth * _slots];
            for (var i = 0; i < _queries.Length; i++)
                _queries[i] = _device.CreateQuery();
        }
        var row = Row(frame);
        if (_frames[row] != frame)
        {
            _frames[row] = frame;
            Array.Clear(_written, row * _slots, _slots);
        }
        var at = row * _slots + slot;
        if (Mode == Kind.Timestamps)
        {
            _device.Timestamp(_queries[at]);
            _written[at] = true;
            return;
        }
        if ((slot & 1) == 0)
        {
            if (_open >= 0)
                return;
            _device.BeginTimeQuery(_queries[at]);
            _open = at;
        }
        else if (_open == at - 1)
        {
            _device.EndTimeQuery();
            _written[at - 1] = _written[at] = true;
            _open = -1;
        }
    }

    /// <summary>At the frame's submit: an elapsed-time query a span left open is ended, unmeasured (queries never span frames).</summary>
    public void EndFrame()
    {
        if (_open < 0)
            return;
        _device.EndTimeQuery();
        _open = -1;
    }

    public bool TryElapsedUs(long frame, int fromSlot, int toSlot, out double microseconds)
    {
        microseconds = 0;
        if (_queries is null || (uint)fromSlot >= (uint)_slots || (uint)toSlot >= (uint)_slots || frame < 0)
            return false;
        var row = Row(frame);
        var from = row * _slots + fromSlot;
        var to = row * _slots + toSlot;
        if (_frames[row] != frame || !_written[from] || !_written[to])
            return false;
        if (Mode == Kind.Elapsed)
        {
            if (toSlot != fromSlot + 1 || !_device.TryQueryResult(_queries[from], out var elapsed) || elapsed < 0)
                return false;
            microseconds = elapsed / 1000.0;
            return true;
        }
        if (!_device.TryQueryResult(_queries[to], out var end) || !_device.TryQueryResult(_queries[from], out var start) || end < 0 || start < 0)
            return false;
        microseconds = (end - start) / 1000.0;
        return true;
    }

    public bool Holds(long frame) => _queries is not null && frame >= 0 && _frames[Row(frame)] == frame;

    /// <summary>Grows the slots a frame may write to at least <paramref name="slots"/> (doubling, at most <see cref="MaxSlots"/>): the
    /// queries made so far keep their frames' timestamps, each row's in its place. Between frames, or before a frame's first stamp.</summary>
    public void Reserve(int slots)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Measures || slots <= _slots || _slots >= MaxSlots || _open >= 0)
            return;
        var grown = _slots;
        while (grown < slots && grown < MaxSlots)
            grown *= 2;
        grown = Math.Min(grown, MaxSlots);
        var written = new bool[Depth * grown];
        uint[]? queries = _queries is null ? null : new uint[Depth * grown];
        for (var row = 0; row < Depth; row++)
        {
            Array.Copy(_written, row * _slots, written, row * grown, _slots);
            if (queries is null)
                continue;
            Array.Copy(_queries!, row * _slots, queries, row * grown, _slots);
            for (var i = _slots; i < grown; i++)
                queries[row * grown + i] = _device.CreateQuery();
        }
        _written = written;
        _queries = queries;
        _slots = grown;
    }

    private static int Row(long frame) => (int)(((frame % Depth) + Depth) % Depth);

    public void Dispose()
    {
        if (_disposed)
            return;
        EndFrame();
        if (_queries is not null)
            foreach (var q in _queries)
                _device.DeleteQuery(q);
        _queries = null;
        _disposed = true;
    }
}
