using PlayMap.Core.Media;
using PlayMap.Core.Media.Decode;
using PlayMap.Render.Decode;
using PlayMap.Render.Engine;
using PlayMap.Render.Gpu;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-decode's and wp2-zerocopy's hooks into the render host. When the context comes, the first frame with an engine
/// makes the context's <see cref="DecodeContext"/>: the share decoders open with (a dmabuf share on an EGL context that imports
/// dmabufs, else the platform's own — wp2-d3d11's D3D11 share on Windows), whether zero-copy is ready and why not, and the upload
/// arena (<c>decode.uploadArenaMb</c>); the engine is told the block families, the binding, the share and the arena (the media
/// backend opens decoders for them). Each frame the arena fences the blocks returned since the last one. When the context goes,
/// the engine is told first (sources stop writing into the arena and let go of frames in it), then the arena is unmapped. Render
/// thread; nothing allocates per frame once warm.</summary>
public sealed partial class RenderHost
{
    // The families the context samples (−1: no context) and what the engine was last told (−2: nothing yet); the window-system
    // binding the context came through (asked once per context, with it current).
    private volatile int _decodeFormats = -1;
    private int _decodeSent = -2;
    private volatile string _decodeBinding = "";
    private GlDevice? _decodeDevice;
    private DecodeContext? _decodeContext;

    partial void ComposeDecode(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        if (_decodeDevice is { } device && _decodeContext is null)
        {
            _decodeContext = MakeDecodeContext(device, engine.Services.Settings.Decode.UploadArenaMb);
            device.DecodeContext = _decodeContext;
            _decodeSent = -2;
        }
        _decodeContext?.Arena?.EndFrame();
        var formats = _decodeFormats;
        if (formats == _decodeSent)
            return;
        _decodeSent = formats;
        engine.DecodeRenderer(formats < 0 ? null : (BlockFormats)formats, _decodeBinding, formats < 0 ? null : _decodeContext);
    }

    private DecodeContext MakeDecodeContext(GlDevice device, int arenaMb)
    {
        var platform = _platform?.Share;
        IGpuShare? share = platform;
        ZeroCopyPlatform facts;
        if (platform is { Api: GpuApi.D3D11 })
        {
            facts = DecodeLadder.Platform(platform, true, "");
        }
        else if (DmaBufShare.ForCurrentContext(platform, device.Renderer) is { } dmabuf)
        {
            share = dmabuf;
            facts = DecodeLadder.Platform(dmabuf, dmabuf.Ready, dmabuf.Words);
        }
        else
        {
            var words = _decodeBinding == "egl" ? "the EGL display does not import dmabufs: frames come back to system memory"
                : $"the render context is {(_decodeBinding.Length > 0 ? _decodeBinding.ToUpperInvariant() : "not EGL")}: dmabuf imports need an EGL context (an ES or surfaceless context, or Wayland)";
            facts = platform is null ? new ZeroCopyPlatform(GpuApi.None, false, false, words) : DecodeLadder.Platform(platform, false, words);
        }
        var arena = GlUploadArena.Create(device, arenaMb, out var arenaWords);
        return new DecodeContext(share, facts, _decodeBinding, arena, arenaWords);
    }

    partial void OnContextDecode(RenderEngine renderer)
    {
        _decodeBinding = GlDevice.CurrentBinding();
        _decodeDevice = renderer.Device;
        _decodeContext = null;
        _decodeFormats = (int)renderer.Device.BlockFormats;
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
        if (_decodeDevice is { } device)
            device.DecodeContext = null;
        _decodeContext?.Arena?.Dispose();
        _decodeContext = null;
        _decodeDevice = null;
    }
}
