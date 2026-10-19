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
/// waits on the returned value (on the GPU). Replaces the pixel-unpack ring and the upload arena's GL half.</summary>
public interface IUploadRing
{
    /// <summary>Copies <paramref name="rows"/> rows of <paramref name="rowBytes"/> bytes from <paramref name="source"/> (rows
    /// <paramref name="sourceStride"/> bytes apart) into the texture; false when the ring has no room (the caller uploads through the
    /// list instead). <paramref name="done"/> is the value the copy completes at.</summary>
    bool TryUpload(GpuTexture dest, nint source, int sourceStride, int rowBytes, int rows, UploadFormat format, out FenceValue done);
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

/// <summary>GPU timestamps, read frames later.</summary>
public interface IGpuTimers
{
    int Slots { get; }

    /// <summary>The elapsed GPU time between two timestamps of a completed frame, µs; false until it completed.</summary>
    bool TryElapsedUs(long frame, int fromSlot, int toSlot, out double microseconds);
}
