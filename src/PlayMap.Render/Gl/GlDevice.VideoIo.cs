using PlayMap.Core.Gpu;
using Silk.NET.OpenGL;

namespace PlayMap.Render.Gl;

/// <summary>wp2-videoio's use of the device: a read of a packed send target into a pack buffer as RGBA bytes (8-bit targets) or RGBA
/// 16-bit words (16-bit targets), top row first, queued and returned at once. RGBA reads are core on both dialects, so a send never
/// depends on EXT_read_format_bgra (a BGRA send swaps in its pack pass instead).</summary>
public sealed partial class GlDevice
{
    internal unsafe void ReadRgbaIntoPackBuffer(RenderTarget target, uint buffer, bool sixteen)
    {
        BindTarget(target);
        _gl.BindBuffer(GLEnum.PixelPackBuffer, buffer);
        _gl.PixelStore(GLEnum.PackAlignment, 4);
        _gl.ReadPixels(0, 0, (uint)target.Width, (uint)target.Height, GLEnum.Rgba, sixteen ? GLEnum.UnsignedShort : GLEnum.UnsignedByte, null);
        _gl.BindBuffer(GLEnum.PixelPackBuffer, 0);
        Check("read send");
    }
}
