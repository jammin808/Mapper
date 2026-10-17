namespace PlayMap.Media.Video.Spout;

/// <summary>What a staged read-back gave: the rows were copied, the GPU has not finished the copy yet (try again later — never a
/// wait), or the read failed.</summary>
public enum StagingRead
{
    Ready,
    Pending,
    Failed,
}

/// <summary>A shared texture on Spout's Direct3D 11 device: a sender's (made here, its share handle published) or one a sender
/// published (opened by handle). A receiver copies it into one of three staging slots and reads a slot back later without waiting;
/// a sender uploads rows into it. One thread uses a texture at a time (the receive thread, or the sender's worker).</summary>
public interface ISpoutTexture : IDisposable
{
    uint Handle { get; }

    int Width { get; }

    int Height { get; }

    uint Format { get; }

    /// <summary>The texture carries a keyed mutex (then it is the access lock, at key 0).</summary>
    bool Keyed { get; }

    /// <summary>The ID3D11Texture2D* (a GPU surface's handle).</summary>
    nint Native { get; }

    LockTry AcquireKeyed();

    void ReleaseKeyed();

    /// <summary>Queues a copy of the texture into staging slot <paramref name="slot"/> (0–2) and submits it to the GPU, marking its
    /// end; throws with words when the device cannot.</summary>
    void CopyToStaging(int slot);

    /// <summary>The GPU has finished the copy last queued into <paramref name="slot"/> (its end mark has signalled): a read of the slot
    /// is ready. Never waits.</summary>
    bool Copied(int slot);

    /// <summary>Copies a staged slot's rows (top first) into <paramref name="destination"/> of <paramref name="stride"/> bytes a row,
    /// without waiting for the GPU.</summary>
    StagingRead ReadStaging(int slot, Span<byte> destination, int stride);

    /// <summary>Writes rows (top first, <paramref name="stride"/> bytes apart) over the whole texture.</summary>
    void Upload(ReadOnlySpan<byte> pixels, int stride);

    /// <summary>Submits what was queued (a sender's upload before it tells receivers).</summary>
    void Flush();
}

/// <summary>Spout's Direct3D 11 device: a sender's textures are made on it, a sender's published texture is opened on it.</summary>
public interface ISpoutGpu : IDisposable
{
    /// <summary>"Direct3D 11 · NVIDIA RTX A4000", "Direct3D 11 · WARP".</summary>
    string Describe { get; }

    /// <summary>Opens a published texture by its share handle; null with the words when the device cannot (another adapter's, a
    /// handle gone).</summary>
    ISpoutTexture? Open(uint handle, out string words);

    /// <summary>Makes a shareable texture of a DXGI format (DEFAULT usage, render target and shader resource, shared).</summary>
    ISpoutTexture Create(int width, int height, uint format);
}
