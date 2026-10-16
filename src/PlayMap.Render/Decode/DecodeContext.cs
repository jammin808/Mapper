using PlayMap.Core.Media;
using PlayMap.Core.Media.Decode;

namespace PlayMap.Render.Decode;

/// <summary>What one GL context offers decoding: the share decoders are opened with (a dmabuf share on an EGL context that imports,
/// the platform's own share otherwise — wp2-d3d11's D3D11 share on Windows), whether zero-copy is ready and why not, the window-system
/// binding, and the context's upload arena. Made by the render host when the context comes (render thread); its facts are read from
/// any thread.</summary>
public sealed class DecodeContext
{
    public DecodeContext(IGpuShare? share, ZeroCopyPlatform platform, string binding, GlUploadArena? arena, string arenaWords)
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

    public GlUploadArena? Arena { get; }

    /// <summary>Why there is no arena ("" when there is one).</summary>
    public string ArenaWords { get; }
}
