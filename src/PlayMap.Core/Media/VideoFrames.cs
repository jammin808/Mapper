using PlayMap.Core.Model;

namespace PlayMap.Core.Media;

/// <summary>How a decoded picture's bytes are laid out. The renderer converts every YUV layout to RGB on
/// the GPU (no CPU conversion on the hot path).</summary>
public enum PixelLayout
{
    Rgba8,
    Bgra8,
    /// <summary>Y plane + interleaved UV plane, 4:2:0, 8-bit (the hardware decoders' native output).</summary>
    Nv12,
    /// <summary>NV12 with 16-bit samples, 10 significant bits in the high bits.</summary>
    P010,
    /// <summary>Three planes, 4:2:0, 8-bit.</summary>
    Yuv420P,
    /// <summary>Three planes, 4:2:2, 8-bit.</summary>
    Yuv422P,
    /// <summary>Three planes, 4:4:4, 8-bit.</summary>
    Yuv444P,
    /// <summary>Three planes, 4:2:0, 16-bit little-endian, 10 significant low bits.</summary>
    Yuv420P10,
    Gray8,
    /// <summary>RGBA, 16-bit float per channel.</summary>
    RgbaHalf,
    /// <summary>Block-compressed (HAP): a plane's Width/Height count 4×4 blocks; Stride = blocks × block bytes.</summary>
    Bc1,
    Bc3,
    Bc3YCoCg,
    Bc3YCoCgBc4,
    Bc4,
    Bc7,
    Bc6hUfloat,
    Bc6hSfloat,
    /// <summary>4:2:2 from network senders (NDI, OMT), by plane (stride in bytes for width w):
    /// Uyvy — one packed plane, 2·w; Uyva — the UYVY plane (2·w) then an 8-bit alpha plane (w);
    /// P216 — semi-planar: a 16-bit Y plane (2·w) then an interleaved 16-bit CbCr plane (2·w, full height: 4:2:2 halves only
    /// the horizontal);
    /// Pa16 — P216's two planes then a 16-bit alpha plane (2·w). LiveFramePool sizes slots and VideoTextureSet uploads by these.</summary>
    Uyvy,
    Uyva,
    P216,
    Pa16,
    /// <summary>RGBA, 16-bit unsigned normalised (a CPU texture-share path).</summary>
    Rgba16,
}

public enum ColorMatrix
{
    Bt601,
    Bt709,
    Bt2020,
}

public enum ColorRange
{
    /// <summary>Video range: Y 16–235, chroma 16–240 (scaled for higher bit depths).</summary>
    Limited,
    Full,
}

public enum TransferFunction
{
    /// <summary>BT.709/sRGB-like SDR.</summary>
    Sdr,
    /// <summary>SMPTE ST 2084 (HDR10), tone-mapped to SDR for projection.</summary>
    Pq,
    Hlg,
    Linear,
}

/// <summary>One plane of a picture: a pointer valid until the frame's lease ends, its row pitch in bytes,
/// and its size in samples.</summary>
public readonly record struct FramePlane(nint Data, int Stride, int Width, int Height);

/// <summary>A decoded picture handed from a decoder to the renderer. The planes belong to the source's
/// pool; the renderer uploads them and disposes the lease, which returns the buffers. A lease never
/// outlives the upload (a fence of one frame), so a source's pool never waits on a slow output.</summary>
public abstract class VideoFrameLease : IDisposable
{
    private int _disposed;

    public abstract PixelLayout Layout { get; }
    public abstract int Width { get; }
    public abstract int Height { get; }
    public abstract IReadOnlyList<FramePlane> Planes { get; }

    /// <summary>The frame's presentation time on its media timeline, nanoseconds.</summary>
    public abstract long PtsNs { get; }

    public abstract long DurationNs { get; }

    /// <summary>Rises by one for every distinct picture a source publishes (an upload is skipped when the
    /// generation is the one already on the GPU).</summary>
    public abstract long Generation { get; }

    public virtual ColorMatrix Matrix => Height > 576 ? ColorMatrix.Bt709 : ColorMatrix.Bt601;
    public virtual ColorRange Range => ColorRange.Limited;
    public virtual TransferFunction Transfer => TransferFunction.Sdr;

    /// <summary>Whether the picture carries alpha (RGBA layouts with a real alpha channel).</summary>
    public virtual bool HasAlpha => false;

    /// <summary>The picture on the GPU, or null for a CPU picture (Planes). A lease with a surface may have no planes.</summary>
    public virtual GpuSurface? Surface => null;

    /// <summary>Where the planes live when written into the renderer's upload arena (empty otherwise); disposed only after the GPU read it.</summary>
    public virtual UploadBlock Staged => default;

    /// <summary>The monotonic instant the frame arrived (ns; 0 = unknown).</summary>
    public virtual long ArrivalNs => 0;

    /// <summary>The sender's own timestamp, ns (long.MinValue = none).</summary>
    public virtual long SenderTimestampNs => long.MinValue;

    /// <summary>Returns the buffers to the source's pool. Runs once.</summary>
    protected abstract void Release();

    protected virtual void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            Release();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>Health a source reports, read by STATE, the context and the readiness check.</summary>
public sealed record SourceHealth(
    bool Ready,
    string Status,
    long FramesDecoded,
    long FramesDropped,
    long FramesLate,
    double DecodeMsP95,
    string Decoder,
    bool Hardware)
{
    /// <summary>How frames reach the renderer.</summary>
    public DecodePath Path { get; init; } = DecodePath.Software;

    /// <summary>The picture never passes through system memory on its way to the renderer.</summary>
    public bool ZeroCopy => Path is DecodePath.HwDirect or DecodePath.HwGpuCopy or DecodePath.HapGpu or DecodePath.GpuShare;

    public int GpuCopies { get; init; }

    /// <summary>"vaapi", "d3d11va", "cuda", "".</summary>
    public string Device { get; init; } = "";

    /// <summary>Why not a better path, in words.</summary>
    public string Why { get; init; } = "";

    public long BusBytesPerFrame { get; init; }
    public long VramBytes { get; init; }
    public int Surfaces { get; init; }
    public double ImportMsP95 { get; init; }
    public HapFacts? Hap { get; init; }
}

/// <summary>What a HAP source measured (STATE.decode.sources[].hap).</summary>
public sealed record HapFacts(string Codec, string Texture, int Chunks, double RawShare, string SecondStage, double StageMsP95,
    long UploadBytes, string Staging, long Corrupt, string LastRefusal, string Hdr);

/// <summary>A picture source the renderer draws from: a decoded clip, a still, a capture device, a web page.
/// The renderer asks for the frame to show at a media time (from the timeline anchor at the predicted
/// vblank); the source answers with the latest decoded frame at or before it, never blocking — a source that
/// has nothing yet answers null and the renderer holds what it had.</summary>
public interface IVideoSource : IDisposable
{
    string Key { get; }

    MediaInfo Info { get; }

    SourceHealth Health { get; }

    /// <summary>The frame for a media time, or null when none is decoded yet. Never blocks.</summary>
    VideoFrameLease? Acquire(long mediaNs);

    /// <summary>A hint that the timeline is at or will soon reach a media time: seek, preroll, keep the
    /// queue ahead of it. Never blocks.</summary>
    void Prepare(long mediaNs, bool playing, double rate);

    /// <summary>True when the frame at the media time is decoded and waiting (preroll done).</summary>
    bool IsReadyAt(long mediaNs);
}
