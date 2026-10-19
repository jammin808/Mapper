using PlayMap.Core.Media;

namespace PlayMap.Core.Gpu;

/// <summary>A decoder's or another device's surface as a texture the passes sample (dmabuf EGLImages and ANGLE's D3D11 slices on
/// GL; D3D12VA resources and shared NT handles on Direct3D 12). Each reader imports through an import of its own
/// (<see cref="OpenImport"/>), so two readers of one source (a clip on a layer and the same clip in a playlist, the show's frame
/// and a draft view's) never share or wait on each other's imports.</summary>
public interface ITextureImporter
{
    /// <summary>Whether this surface can be imported here; <paramref name="words"/> says why not. Allocation-free.</summary>
    bool CanImport(GpuSurface surface, out string? words);

    /// <summary>A reader's own imports (a video set's), held by the reader and disposed with it.</summary>
    ITextureImport OpenImport();
}

/// <summary>One reader's imports. A surface is held (<see cref="GpuSurface.Retain"/>) from the moment it is imported, while the
/// lease that carried it is still alive, until the GPU has finished the frame list the import was made in (the backend fences the
/// hold at that list's submit); it is released once, then. Disposing the import lets go of its planes and of every hold once the
/// GPU has finished with them, never sooner and never by waiting on the GPU.
///
/// Threading: the reader's thread (the render thread), between a frame's begin and its submit for <see cref="TryImport"/>.</summary>
public interface ITextureImport : IDisposable
{
    /// <summary>The surface's planes as textures; false with words when the import failed, false with no words when every import
    /// slot of this reader is still in flight (nothing failed: the caller holds its last picture).</summary>
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
