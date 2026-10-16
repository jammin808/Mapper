namespace PlayMap.Core.Media;

/// <summary>A block of GPU-visible host memory a producer writes a picture into from any thread.</summary>
public readonly record struct UploadBlock(int Arena, long Offset, long Bytes, nint Pointer, long Epoch)
{
    public bool IsEmpty => Bytes == 0;
}

/// <summary>The renderer's upload arena: producers rent a block, write a picture into it, and the block goes back once the GPU
/// has read it. Renting never blocks; a full arena answers false.</summary>
public interface IUploadArena
{
    long Epoch { get; }
    long CapacityBytes { get; }
    long FreeBytes { get; }
    bool TryRent(long bytes, out UploadBlock block);
    void Return(in UploadBlock block);
}

/// <summary>The bookkeeping of an upload arena, apart from its memory: first-fit ranges of a fixed capacity, aligned, coalesced on
/// return. Renting and returning never allocate and never wait beyond a short lock; at most <see cref="MaxBlocks"/> blocks are out
/// at once. The epoch moves when the arena is rebuilt: every block of an older epoch is gone with its memory, and returning one is
/// ignored, never freeing a range that now belongs to someone else.</summary>
public sealed class ArenaAllocator
{
    /// <summary>Offsets and sizes are rounded up to this (a texture upload from a buffer offset wants it aligned).</summary>
    public const int Alignment = 256;

    private readonly Lock _gate = new();
    // Free ranges sorted by offset, never adjacent (adjacent ones are merged).
    private readonly long[] _freeAt;
    private readonly long[] _freeLength;
    private int _free;
    private int _out;
    private long _outBytes;
    private long _epoch = 1;

    public ArenaAllocator(int arena, long capacityBytes, int maxBlocks = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityBytes, Alignment);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBlocks, 1);
        Arena = arena;
        CapacityBytes = capacityBytes / Alignment * Alignment;
        MaxBlocks = maxBlocks;
        _freeAt = new long[maxBlocks + 1];
        _freeLength = new long[maxBlocks + 1];
        _freeAt[0] = 0;
        _freeLength[0] = CapacityBytes;
        _free = 1;
    }

    public int Arena { get; }

    public long CapacityBytes { get; }

    public int MaxBlocks { get; }

    public long Epoch
    {
        get
        {
            lock (_gate)
                return _epoch;
        }
    }

    public long FreeBytes
    {
        get
        {
            lock (_gate)
                return CapacityBytes - _outBytes;
        }
    }

    /// <summary>Blocks rented and not yet returned.</summary>
    public int Outstanding
    {
        get
        {
            lock (_gate)
                return _out;
        }
    }

    /// <summary>A range of at least <paramref name="bytes"/> (rounded up to the alignment), or false when no free range is that
    /// big or every block is out. <paramref name="basePointer"/> is where the arena's memory starts (0 for bookkeeping alone).</summary>
    public bool TryRent(long bytes, nint basePointer, out UploadBlock block)
    {
        block = default;
        if (bytes <= 0)
            return false;
        var size = (bytes + Alignment - 1) / Alignment * Alignment;
        lock (_gate)
        {
            if (_out >= MaxBlocks)
                return false;
            for (var i = 0; i < _free; i++)
            {
                if (_freeLength[i] < size)
                    continue;
                var at = _freeAt[i];
                if (_freeLength[i] == size)
                    RemoveFree(i);
                else
                {
                    _freeAt[i] += size;
                    _freeLength[i] -= size;
                }
                _out++;
                _outBytes += size;
                block = new UploadBlock(Arena, at, size, basePointer == 0 ? 0 : basePointer + (nint)at, _epoch);
                return true;
            }
        }
        return false;
    }

    /// <summary>Gives a range back. A block of another arena, of an older epoch, or not out is ignored (false).</summary>
    public bool Return(in UploadBlock block)
    {
        if (block.IsEmpty || block.Arena != Arena)
            return false;
        lock (_gate)
        {
            if (block.Epoch != _epoch || _out == 0 || block.Offset < 0 || block.Offset + block.Bytes > CapacityBytes)
                return false;
            // Where it goes in the sorted list; a range that overlaps a free one was never out (a second return): ignored.
            var i = 0;
            while (i < _free && _freeAt[i] < block.Offset)
                i++;
            if ((i > 0 && _freeAt[i - 1] + _freeLength[i - 1] > block.Offset) || (i < _free && block.Offset + block.Bytes > _freeAt[i]))
                return false;
            var mergeLeft = i > 0 && _freeAt[i - 1] + _freeLength[i - 1] == block.Offset;
            var mergeRight = i < _free && block.Offset + block.Bytes == _freeAt[i];
            if (mergeLeft && mergeRight)
            {
                _freeLength[i - 1] += block.Bytes + _freeLength[i];
                RemoveFree(i);
            }
            else if (mergeLeft)
            {
                _freeLength[i - 1] += block.Bytes;
            }
            else if (mergeRight)
            {
                _freeAt[i] = block.Offset;
                _freeLength[i] += block.Bytes;
            }
            else
            {
                for (var k = _free; k > i; k--)
                {
                    _freeAt[k] = _freeAt[k - 1];
                    _freeLength[k] = _freeLength[k - 1];
                }
                _freeAt[i] = block.Offset;
                _freeLength[i] = block.Bytes;
                _free++;
            }
            _out--;
            _outBytes -= block.Bytes;
            return true;
        }
    }

    /// <summary>The arena was rebuilt (its memory went with a context): every block is free again under a new epoch.</summary>
    public long Rebuild()
    {
        lock (_gate)
        {
            _epoch++;
            _freeAt[0] = 0;
            _freeLength[0] = CapacityBytes;
            _free = 1;
            _out = 0;
            _outBytes = 0;
            return _epoch;
        }
    }

    private void RemoveFree(int i)
    {
        for (var k = i; k < _free - 1; k++)
        {
            _freeAt[k] = _freeAt[k + 1];
            _freeLength[k] = _freeLength[k + 1];
        }
        _free--;
    }
}
