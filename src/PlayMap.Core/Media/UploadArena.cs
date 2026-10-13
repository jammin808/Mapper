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
