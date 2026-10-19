using PlayMap.Core.Media;
using PlayMap.Core.Media.Decode;

namespace PlayMap.Render.Decode;

/// <summary>What one device offers decoding: the share decoders are opened with (a dmabuf share on an EGL context that imports, the
/// platform's own share otherwise — the D3D11 share of the DXGI path on Windows), whether zero-copy is ready and why not, the
/// window-system binding, and the device's upload arena. Made by the render host when the device comes (render thread); its facts are
/// read from any thread.</summary>
public sealed class DecodeContext
{
    public DecodeContext(IGpuShare? share, ZeroCopyPlatform platform, string binding, IUploadArena? arena, string arenaWords)
    {
        Share = share;
        Platform = platform;
        Binding = binding;
        Arena = arena;
        ArenaWords = arenaWords;
    }

    public IGpuShare? Share { get; }

    public ZeroCopyPlatform Platform { get; }

    /// <summary>"egl", "glx", "wgl", "cgl" or "".</summary>
    public string Binding { get; }

    public IUploadArena? Arena { get; }

    /// <summary>Why there is no arena ("" when there is one).</summary>
    public string ArenaWords { get; }
}
