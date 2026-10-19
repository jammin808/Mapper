using PlayMap.Core.Gpu;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Decode;
using PlayMap.Render.Decode;
using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-decode's and wp2-zerocopy's hooks into the render host. When the device comes, the first frame with an engine
/// makes its <see cref="DecodeContext"/>: the share decoders open with (the platform's imports where it opens any — a dmabuf share on
/// an EGL context that imports dmabufs — else the platform's own, the D3D11 share of the DXGI path on Windows), whether zero-copy is
/// ready and why not, and the device's upload arena (<c>decode.uploadArenaMb</c>, opened through its upload ring); the engine is told
/// the block families, the binding, the share and the arena (the media backend opens decoders for them). Every submit of the device
/// fences the arena's returned blocks. When the device goes, the engine is told first (sources stop writing into the arena and let go
/// of frames in it), then the arena is closed. Render thread; nothing allocates per frame once warm.</summary>
public sealed partial class RenderHost
{
    // The families the context samples (−1: no context) and what the engine was last told (−2: nothing yet); the window-system
    // binding the context came through (asked once per context, with it current).
    private volatile int _decodeFormats = -1;
    private int _decodeSent = -2;
    private volatile string _decodeBinding = "";
#pragma warning disable CA2213 // The renderer's device: its arena is closed by OnContextLostDecode on this thread, the device goes with the platform.
    private IGpuDevice? _decodeDevice;
#pragma warning restore CA2213
    private DecodeContext? _decodeContext;

    partial void ComposeDecode(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        if (_decodeDevice is { } device && _decodeContext is null)
        {
            _decodeContext = MakeDecodeContext(device, engine.Services.Settings.Decode.UploadArenaMb);
            _decodeSent = -2;
        }
        var formats = _decodeFormats;
        if (formats == _decodeSent)
            return;
        _decodeSent = formats;
        engine.DecodeRenderer(formats < 0 ? null : (BlockFormats)formats, _decodeBinding, formats < 0 ? null : _decodeContext);
    }

    private DecodeContext MakeDecodeContext(IGpuDevice device, int arenaMb)
    {
        var platform = _platform?.Share;
        IGpuShare? share = platform;
        ZeroCopyPlatform facts;
        if (platform is { Api: GpuApi.D3D11 })
        {
            // Decoders go on the renderer's own device: ready when it was made with video support and multithread protection.
            facts = DecodeLadder.Platform(platform, platform.ZeroCopyWords.Length == 0, platform.ZeroCopyWords);
        }
        else if (_platform?.OpenImports() is { } imports)
        {
            share = imports;
            facts = DecodeLadder.Platform(imports, imports.ZeroCopyWords.Length == 0, imports.ZeroCopyWords);
        }
        else
        {
            var words = NoImportWords(_decodeBinding);
            facts = platform is null ? new ZeroCopyPlatform(GpuApi.None, false, false, words) : DecodeLadder.Platform(platform, false, words);
        }
        var arena = device.Uploads.OpenArena(arenaMb, out var arenaWords);
        return new DecodeContext(share, facts, _decodeBinding, arena, arenaWords);
    }

    /// <summary>Why a GL context through <paramref name="binding"/> takes no decoder's surfaces, in this platform's own terms: on
    /// Linux the way in is an EGL display that imports dmabufs; on Windows zero-copy is the Direct3D 11 output path's (a GL context
    /// there imports nothing); elsewhere no decoder shares its frames with GL.</summary>
    internal static string NoImportWords(string binding)
    {
        var named = binding.Length > 0 ? binding.ToUpperInvariant() : null;
        if (OperatingSystem.IsLinux())
        {
            return binding == "egl" ? "the EGL display does not import dmabufs: frames come back to system memory"
                : $"the render context is {named ?? "not EGL"}: dmabuf imports need an EGL context (an ES or surfaceless context, or Wayland)";
        }
        if (OperatingSystem.IsWindows())
            return $"the render context is {named ?? "OpenGL"}: zero-copy on Windows needs the Direct3D 11 output path (PRESENT PATH DXGI)";
        return $"the render context is {named ?? "OpenGL"}: no decoder on this platform shares its frames with GL";
    }

    partial void OnContextDecode(RenderEngine renderer)
    {
        _decodeBinding = _platform?.ImportBinding ?? "";
        _decodeDevice = renderer.Device;
        _decodeContext = null;
        _decodeFormats = (int)_decodeDevice.Features.Blocks;
    }

    partial void OnContextLostDecode()
    {
        _decodeFormats = -1;
        if (Volatile.Read(ref _engine) is { } engine)
        {
            _decodeSent = -1;
            // Sources let go of the arena before its memory goes (UseRenderer returns once none is writing into it).
            engine.DecodeRenderer(null);
        }
        _decodeDevice?.Uploads.CloseArena();
        _decodeContext = null;
        _decodeDevice = null;
    }
}
