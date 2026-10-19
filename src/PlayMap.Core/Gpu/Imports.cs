using PlayMap.Core.Media;

namespace PlayMap.Core.Gpu;

/// <summary>A decoder's or another device's surface as a texture the passes sample (dmabuf EGLImages and ANGLE's D3D11 slices on
/// GL; D3D12VA resources and shared NT handles on Direct3D 12). The surface is held until the returned value completes.</summary>
public interface ITextureImporter
{
    /// <summary>Whether this surface can be imported here; <paramref name="words"/> says why not.</summary>
    bool CanImport(GpuSurface surface, out string? words);

    /// <summary>The surface's planes as textures; false with words when the import failed, false with no words when every import
    /// slot is still in flight (nothing failed: the caller holds its last picture).</summary>
    bool TryImport(GpuSurface surface, out ImportedPlanes planes, out string? words);
}

/// <summary>An imported picture: its luma (or only) plane, its chroma plane where it has one, the value the import is ready at, the
/// picture's crop inside the planes (its visible size), and the size the planes cover (the decoder's coded size, padding included;
/// 0 = the crop).</summary>
public readonly record struct ImportedPlanes(GpuTexture Luma, GpuTexture? Chroma, FenceValue Ready, int CropWidth, int CropHeight)
{
    public int CodedWidth { get; init; }

    public int CodedHeight { get; init; }
}

/// <summary>A texture and a fence opened by another process or device (previews, Spout's bridge). Null on a backend that shares
/// nothing (GL).</summary>
public interface ITextureExporter
{
    SharedTexture Export(GpuTexture texture);

    SharedFence ExportFence();
}

/// <summary>A texture another process opens: its NT handle, the adapter it lives on, its size and format.</summary>
public sealed record SharedTexture(nint NtHandle, long AdapterLuid, int Width, int Height, TextureFormat Format);

/// <summary>A fence another process waits on: its NT handle and the value it reached when exported.</summary>
public sealed record SharedFence(nint NtHandle, ulong Value);
