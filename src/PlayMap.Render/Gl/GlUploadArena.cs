using PlayMap.Core.Media;
using PlayMap.Core.Threading;

namespace PlayMap.Render.Gl;

/// <summary>The upload arena of one GL context: a persistently mapped, coherent pixel-unpack buffer that producers (HAP decode
/// threads) write pictures into directly, so the renderer fills its textures from the buffer with no copy of its own.
/// <para>Renting and returning are thread-safe and allocation-free (<see cref="ArenaAllocator"/>). A returned block is not reused at
/// once: the GPU may not have read it yet. <see cref="EndFrame"/> (render thread, once a frame) puts a fence after everything queued
/// so far behind the blocks returned since the last one, and frees the blocks whose fence has passed — so a block the renderer read
/// in one frame is free again a frame or two later, and a block a producer let go unread waits as long, harmlessly.</para>
/// <para>The buffer belongs to the context: <see cref="Dispose"/> (render thread, before the context goes) unmaps it. Producers are
/// told first (the media backend's UseRenderer) and let go of their frames in it; the epoch then moves, so a block returned late is
/// ignored, never freeing a range of a later arena.</para></summary>
public sealed class GlUploadArena : IUploadArena, IDisposable
{
    private const int Batches = 8;
    private static int _arenas;

    private readonly GlDevice _device;
    private readonly ArenaAllocator _allocator;
    private readonly Lock _gate = new();
    private readonly UploadBlock[] _returned;
    private readonly long[] _batchOf;
    private int _returnedCount;
    private readonly nint[] _fences = new nint[Batches];
    private long _nextBatch = 1;
    private long _oldestBatch = 1;
    private uint _buffer;
    private nint _base;

    private GlUploadArena(GlDevice device, uint buffer, nint basePointer, long bytes, int maxBlocks)
    {
        _device = device;
        _buffer = buffer;
        _base = basePointer;
        _allocator = new ArenaAllocator(Interlocked.Increment(ref _arenas), bytes, maxBlocks);
        _returned = new UploadBlock[maxBlocks];
        _batchOf = new long[maxBlocks];
        // Producers' returns and the render thread's fence meet here every frame: a first wait for the other allocates nothing.
        LockWaits.Prepare(_gate);
    }

    /// <summary>An arena of <paramref name="megabytes"/> MB on the device's context, or null with the words why (no persistent
    /// mapping on this GL, or 0 MB asked).</summary>
    public static GlUploadArena? Create(GlDevice device, int megabytes, out string words, int maxBlocks = 64)
    {
        ArgumentNullException.ThrowIfNull(device);
        words = "";
        if (megabytes <= 0)
        {
            words = "decode.uploadArenaMb is 0: frames are copied into the driver";
            return null;
        }
        if (!device.HasBufferStorage)
        {
            words = "this GL has no persistently mapped buffers (buffer storage): frames are copied into the driver";
            return null;
        }
        var bytes = megabytes * 1024L * 1024L;
        var buffer = device.CreatePersistentUnpackBuffer(bytes, out var pointer);
        if (buffer == 0)
        {
            words = "the GL would not map an upload arena: frames are copied into the driver";
            return null;
        }
        return new GlUploadArena(device, buffer, pointer, bytes, maxBlocks);
    }

    public long Epoch => _allocator.Epoch;

    public long CapacityBytes => _allocator.CapacityBytes;

    public long FreeBytes => _allocator.FreeBytes;

    /// <summary>Blocks out (rented, or returned and waiting for their fence).</summary>
    public int Outstanding => _allocator.Outstanding;

    /// <summary>The GL buffer the blocks live in.</summary>
    internal uint Buffer => _buffer;

    /// <summary>True when the block is one of this arena's, of its current epoch.</summary>
    public bool Owns(in UploadBlock block) => !block.IsEmpty && block.Arena == _allocator.Arena && block.Epoch == _allocator.Epoch && _buffer != 0;

    public bool TryRent(long bytes, out UploadBlock block)
    {
        if (_buffer == 0)
        {
            block = default;
            return false;
        }
        return _allocator.TryRent(bytes, _base, out block);
    }

    public void Return(in UploadBlock block)
    {
        if (block.IsEmpty || block.Arena != _allocator.Arena)
            return;
        lock (_gate)
        {
            if (block.Epoch != _allocator.Epoch || _returnedCount >= _returned.Length)
                return;
            _returned[_returnedCount] = block;
            _batchOf[_returnedCount] = 0;
            _returnedCount++;
        }
    }

    /// <summary>Once a frame on the render thread: fences the blocks returned since the last call and frees those whose fence passed.</summary>
    public void EndFrame()
    {
        if (_buffer == 0)
            return;
        // Free what the GPU has finished with, oldest batch first.
        while (_oldestBatch < _nextBatch)
        {
            var slot = (int)(_oldestBatch % Batches);
            if (!_device.IsSignaled(_fences[slot]))
                break;
            _device.DeleteFence(_fences[slot]);
            _fences[slot] = 0;
            Free(_oldestBatch);
            _oldestBatch++;
        }
        if (_nextBatch - _oldestBatch >= Batches)
            return;   // every fence is in flight: the new returns wait for the next frame
        var batch = 0L;
        lock (_gate)
        {
            for (var i = 0; i < _returnedCount; i++)
            {
                if (_batchOf[i] != 0)
                    continue;
                if (batch == 0)
                    batch = _nextBatch;
                _batchOf[i] = batch;
            }
        }
        if (batch == 0)
            return;
        _fences[(int)(batch % Batches)] = _device.InsertFence();
        _nextBatch++;
    }

    private void Free(long batch)
    {
        lock (_gate)
        {
            var kept = 0;
            for (var i = 0; i < _returnedCount; i++)
            {
                if (_batchOf[i] == batch)
                {
                    _allocator.Return(_returned[i]);
                    continue;
                }
                _returned[kept] = _returned[i];
                _batchOf[kept] = _batchOf[i];
                kept++;
            }
            _returnedCount = kept;
        }
    }

    public void Dispose()
    {
        if (_buffer == 0)
            return;
        for (var i = 0; i < Batches; i++)
        {
            _device.DeleteFence(_fences[i]);
            _fences[i] = 0;
        }
        lock (_gate)
            _returnedCount = 0;
        _allocator.Rebuild();
        _device.DeletePersistentBuffer(_buffer);
        _buffer = 0;
        _base = 0;
    }
}
