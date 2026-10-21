using PlayMap.Core.Media;

namespace PlayMap.Core.Gpu;

/// <summary>A device's queues: the direct queue the frame draws on, and the copy queue uploads and read-backs ride.</summary>
public enum QueueKind
{
    Direct,
    Copy,
}

/// <summary>A point on one queue's timeline. Values on one queue only grow; a queue's work completes in order, so a value is complete
/// once any later value of the same queue is. <see cref="None"/> (0) is complete from the start.</summary>
public readonly record struct FenceValue(QueueKind Queue, ulong Value)
{
    public static readonly FenceValue None = new(QueueKind.Direct, 0);
}

/// <summary>Pixels to the GPU off the render thread: a lane records copies into its own list and submits them; the render list
/// waits on the returned value (on the GPU). Replaces the pixel-unpack ring and the upload arena's GL half.
///
/// The device's upload arena is GPU-visible host memory producers (HAP decode threads) write pictures into directly, so a texture is
/// filled from the bytes where they were written: opened once a decode context needs it (<see cref="OpenArena"/>), its returned blocks
/// fenced by every frame's submit (a block the GPU may still read is not rented again), closed before the device goes, once its
/// producers let go of it (<see cref="CloseArena"/>). A block of an arena that is gone is never read.
///
/// Threading: the render thread, but <see cref="IUploadArena"/>'s renting and returning (any thread).</summary>
public interface IUploadRing
{
    /// <summary>Copies <paramref name="rows"/> rows of <paramref name="rowBytes"/> bytes from <paramref name="source"/> (rows
    /// <paramref name="sourceStride"/> bytes apart) into the texture; false when the ring has no room (the caller uploads through the
    /// list instead). <paramref name="done"/> is the value the copy completes at.</summary>
    bool TryUpload(GpuTexture dest, nint source, int sourceStride, int rowBytes, int rows, UploadFormat format, out FenceValue done);

    /// <summary>Copies a block-compressed texture's whole level (<paramref name="bytes"/> of 4×4 blocks, tight rows) into it; false when
    /// the ring has no room (the caller uploads through the list instead).</summary>
    bool TryUploadCompressed(GpuTexture dest, nint blocks, int bytes, out FenceValue done);

    /// <summary>The open arena, or null.</summary>
    IUploadArena? Arena { get; }

    /// <summary>Opens the arena of <paramref name="megabytes"/> MB (an open one is closed first); null with the words why when the
    /// device has none to give (0 MB asked, or no persistently mapped memory).</summary>
    IUploadArena? OpenArena(int megabytes, out string words);

    /// <summary>Lets the arena's memory go (its producers let go of their blocks first); nothing when none is open.</summary>
    void CloseArena();

    /// <summary>Whether a block is the open arena's, of its epoch (a block of an arena that is gone is never read).</summary>
    bool InArena(in UploadBlock block);

    /// <summary>Fills a block-compressed texture's whole level from <paramref name="bytes"/> bytes at <paramref name="data"/> inside
    /// <paramref name="block"/> of the open arena, read where the producer wrote them (no copy); false, reading nothing, when the
    /// block is not the arena's.</summary>
    bool TryUploadStaged(GpuTexture dest, in UploadBlock block, nint data, int bytes, out FenceValue done);
}

/// <summary>Receives a finished read-back: the BGRA bytes (valid only during the call), the size and the stamp the request
/// carried.</summary>
public delegate void ReadbackConsumer<in TState>(TState state, ReadOnlySpan<byte> bgra, int width, int height, long stamp);

/// <summary>Pictures back to the CPU, never waited on: a request after a pass, collected frames later when its value completed.
/// Replaces the pixel-pack ring; the send stage, LED sampling and previews use it, each reader through a ring of its own
/// (<see cref="IGpuDevice.CreateReadbackRing"/>), since a ring hands its reads back in its own order. A ring a reader made is the
/// reader's to dispose; the device's own (<see cref="IGpuDevice.Readbacks"/>) goes with the device.</summary>
public interface IReadbackRing : IDisposable
{
    /// <summary>Queues a read of the target (BGRA, top row first); false when every slot is still in flight (the request is
    /// skipped: a reader drops a picture rather than wait).</summary>
    bool Request(RenderTarget target, long stamp);

    /// <summary>Hands the oldest finished read to <paramref name="consume"/> and frees its slot; false when the oldest has not
    /// finished. Never waits.</summary>
    bool TryCollect<TState>(TState state, ReadbackConsumer<TState> consume);

    /// <summary>Requests queued and not yet collected.</summary>
    int Pending { get; }
}

/// <summary>A finished read the reader holds: its slot, its bytes (valid until the reader lets go of the slot), their count, the
/// target's size and the stamp the request carried.</summary>
public readonly record struct HeldReadback(int Slot, nint Data, int Bytes, int Width, int Height, long Stamp);

/// <summary>Pictures back to the CPU that the reader reads in place: each request reads the target (RGBA bytes, top row first: the
/// byte order a pass packed into its channels) into a free slot; a finished read is handed out mapped and stays valid until the
/// reader lets go of its slot (<see cref="Release"/>, from any thread), and the slot is free again at the ring's next
/// <see cref="TryTake"/> after that. Never waits: a request with no free slot is refused.
///
/// Threading: the render thread, but <see cref="Release"/> (any thread, once a read).</summary>
public interface IHeldReadbackRing : IDisposable
{
    int Slots { get; }

    /// <summary>A slot is free for a request.</summary>
    bool HasFree { get; }

    /// <summary>Reads handed out and not yet let go.</summary>
    int Held { get; }

    /// <summary>Queues a read of the target into a free slot (<paramref name="slot"/>); false when none is free.</summary>
    bool Request(RenderTarget target, long stamp, out int slot);

    /// <summary>Frees the slots let go of, then hands out the oldest finished read; false when none has finished. Never waits.</summary>
    bool TryTake(out HeldReadback read);

    /// <summary>The reader is done with the read in <paramref name="slot"/> (any thread, once).</summary>
    void Release(int slot);
}

/// <summary>GPU timestamps, written where a frame's list asks (<see cref="ICommandList.Timestamp"/>) into the frame's slots and read
/// frames later, never waited on. A frame's slots are kept until a later frame writes them again; a backend keeps the last few
/// frames' (<see cref="Holds"/>). A span is a pair of slots (first, first + 1) a frame writes around its work.
///
/// Threading: the render thread.</summary>
public interface IGpuTimers
{
    /// <summary>Timestamps a frame may write; 0 where the device measures nothing.</summary>
    int Slots { get; }

    /// <summary>The elapsed GPU time between two timestamps of a completed frame, µs; false until it completed, and false for a pair
    /// the device did not measure.</summary>
    bool TryElapsedUs(long frame, int fromSlot, int toSlot, out double microseconds);

    /// <summary>Whether the frame's timestamps are still kept to be read: false once a later frame wrote its slots again (a span of it
    /// that has not landed by then never will).</summary>
    bool Holds(long frame);

    /// <summary>Asks for at least <paramref name="slots"/> timestamps a frame from the next frame on: a backend grows its slots once,
    /// between frames, where it can (the frames a show draws then measure every span they ask for), and keeps what it has where it
    /// cannot.</summary>
    void Reserve(int slots);
}
