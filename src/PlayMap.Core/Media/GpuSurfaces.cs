namespace PlayMap.Core.Media;

/// <summary>The graphics API a device or a picture belongs to.</summary>
public enum GpuApi { None, OpenGl, D3D11, DmaBuf, IoSurface, Cuda }

/// <summary>The layout of a picture on the GPU.</summary>
public enum GpuSurfaceFormat { Nv12, P010, Bgra8, Rgba8, Rgb10A2, Rgba16, Rgba16F, Rgba32F }

/// <summary>How a frame reaches the renderer: the hardware ladder HwDirect → HwGpuCopy → HwCopyBack → Software, HapGpu (HAP),
/// GpuShare (texture-sharing inputs).</summary>
public enum DecodePath { HwDirect, HwGpuCopy, HapGpu, GpuShare, HwCopyBack, Software }

/// <summary>One plane of a dmabuf frame; the fd stays owned by the frame (closed when the lease ends).</summary>
public readonly record struct DmaBufPlane(int Fd, uint Fourcc, int Width, int Height, long Offset, int Pitch, ulong Modifier);

/// <summary>A picture on the GPU, pooled by its producer and valid until the lease that carries it is disposed.</summary>
public sealed class GpuSurface
{
    public GpuApi Api { get; set; }
    public GpuSurfaceFormat Format { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int CodedWidth { get; set; }
    public int CodedHeight { get; set; }
    /// <summary>ID3D11Texture2D* | IOSurfaceRef | CUdeviceptr (plane 0).</summary>
    public nint Handle { get; set; }
    public int Slice { get; set; }
    public long SurfaceId { get; set; }
    public long PoolEpoch { get; set; }
    public DmaBufPlane[] Planes { get; } = new DmaBufPlane[4];
    public int PlaneCount { get; set; }
    public bool Persistent { get; set; }

    /// <summary>The source whose decoder made it (what an import failure is reported against, IGpuShare.ReportImportFailure).</summary>
    public string SourceKey { get; set; } = "";

    /// <summary>Who holds the picture alive (the producer's pooled slot); null for a surface nothing reference-counts.</summary>
    public IGpuSurfaceOwner? Owner { get; set; }

    /// <summary>One more hold on the picture: a renderer that recorded a read of it keeps it until that read completed on the GPU,
    /// past the lease that carried it. Pair every call with <see cref="Release"/>.</summary>
    public void Retain() => Owner?.Retain(this);

    /// <summary>Lets go of one hold; the producer reuses the picture when the last hold (the lease's included) is gone.</summary>
    public void Release() => Owner?.Release(this);
}

/// <summary>The producer side of a <see cref="GpuSurface"/>'s holds: the picture goes back to its decoder only when every hold —
/// the lease's and each renderer's read — has been released. Thread-safe; allocation-free.</summary>
public interface IGpuSurfaceOwner
{
    void Retain(GpuSurface surface);
    void Release(GpuSurface surface);
}

/// <summary>The renderer's device as decoders and texture-share inputs see it: what it can import, the D3D11 device to decode
/// on, its lock. Made by the render platform (wp2-output) and handed to decoders through IMediaBackend.UseGpu.</summary>
public interface IGpuShare
{
    GpuApi Api { get; }
    string Describe { get; }
    nint D3D11Device { get; }
    long AdapterLuid { get; }
    string? DrmRenderNode { get; }
    long DeviceEpoch { get; }
    bool DirectProven { get; }
    bool CanImport(GpuSurfaceFormat format, uint fourcc, ulong modifier);
    void Enter();
    void Exit();
    void ReportImportFailure(string sourceKey, string words);
    bool ImportFailed(string sourceKey, out string words);
}

/// <summary>Block-compressed families the renderer samples on this node, and whether it stages through a persistent buffer.</summary>
[Flags] public enum BlockFormats { None = 0, Bc1 = 1, Bc3 = 2, Bc4 = 4, Bc7 = 8, Bc6h = 16, PersistentStaging = 32 }
