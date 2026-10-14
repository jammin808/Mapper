using PlayMap.Core.Media;
using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-decode's hooks into the render host: the block families the context samples go to the engine (and through it
/// to the media backend, which opens HAP for them) on the first frame after the context comes, and "none" when it goes. Render
/// thread; nothing allocates per frame.</summary>
public sealed partial class RenderHost
{
    // The families the context samples (−1: no context) and what the engine was last told (−2: nothing yet).
    private volatile int _decodeFormats = -1;
    private int _decodeSent = -2;

    partial void ComposeDecode(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        var formats = _decodeFormats;
        if (formats == _decodeSent)
            return;
        _decodeSent = formats;
        engine.DecodeRenderer(formats < 0 ? null : (BlockFormats)formats);
    }

    partial void OnContextDecode(RenderEngine renderer)
    {
        _decodeFormats = (int)renderer.Device.BlockFormats;
    }

    partial void OnContextLostDecode()
    {
        _decodeFormats = -1;
        if (Volatile.Read(ref _engine) is { } engine)
        {
            _decodeSent = -1;
            engine.DecodeRenderer(null);
        }
    }
}
