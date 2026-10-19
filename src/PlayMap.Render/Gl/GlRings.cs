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

/// <summary>GPU timestamps on GL: <see cref="SlotCount"/> slots a frame, the last <see cref="Depth"/> frames kept, each a timestamp
/// query written where the frame's list asks (<see cref="ICommandList.Timestamp"/>) and read back once the GPU has it, never waited
/// on. Timestamp queries are core on desktop GL 3.3 and not on ES 3.0: there a timestamp is nothing and no time is answered
/// (<see cref="Slots"/> 0). The queries are made on the first timestamp.
///
/// Threading: the render thread (the GL context's).</summary>
internal sealed class GlTimers : IGpuTimers, IDisposable
{
    /// <summary>Timestamps a frame may write: two a span, for every output, generator and effect a frame times.</summary>
    public const int SlotCount = 64;

    /// <summary>Frames whose timestamps are kept to be read.</summary>
    public const int Depth = 4;

    private readonly GlDevice _device;
    private readonly long[] _frames = new long[Depth];
    private readonly bool[] _written = new bool[Depth * SlotCount];
    private uint[]? _queries;
    private bool _disposed;

    public GlTimers(GlDevice device)
    {
        _device = device;
        Measures = device.Caps.IsCore;
        Array.Fill(_frames, -1);
    }

    /// <summary>Whether this context writes timestamps at all.</summary>
    public bool Measures { get; }

    public int Slots => Measures ? SlotCount : 0;

    /// <summary>Writes the GPU's clock into the frame's slot once the commands before it have run.</summary>
    public void Stamp(long frame, int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, SlotCount);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Measures)
            return;
        if (_queries is null)
        {
            _queries = new uint[Depth * SlotCount];
            for (var i = 0; i < _queries.Length; i++)
                _queries[i] = _device.CreateQuery();
        }
        var row = Row(frame);
        if (_frames[row] != frame)
        {
            _frames[row] = frame;
            Array.Clear(_written, row * SlotCount, SlotCount);
        }
        _device.Timestamp(_queries[row * SlotCount + slot]);
        _written[row * SlotCount + slot] = true;
    }

    public bool TryElapsedUs(long frame, int fromSlot, int toSlot, out double microseconds)
    {
        microseconds = 0;
        if (_queries is null || (uint)fromSlot >= SlotCount || (uint)toSlot >= SlotCount || frame < 0)
            return false;
        var row = Row(frame);
        if (_frames[row] != frame || !_written[row * SlotCount + fromSlot] || !_written[row * SlotCount + toSlot])
            return false;
        if (!_device.TryQueryResult(_queries[row * SlotCount + toSlot], out var to)
            || !_device.TryQueryResult(_queries[row * SlotCount + fromSlot], out var from) || to < 0 || from < 0)
            return false;
        microseconds = (to - from) / 1000.0;
        return true;
    }

    private static int Row(long frame) => (int)(((frame % Depth) + Depth) % Depth);

    public void Dispose()
    {
        if (_disposed)
            return;
        if (_queries is not null)
            foreach (var q in _queries)
                _device.DeleteQuery(q);
        _queries = null;
        _disposed = true;
    }
}
