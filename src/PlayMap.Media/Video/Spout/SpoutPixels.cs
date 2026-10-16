using PlayMap.Core.Model;

namespace PlayMap.Media.Video.Spout;

/// <summary>A send stage's packed picture as the BGRA a Spout texture holds: BGRA8 as it is; UYVY and UYVA (BT.709, limited range;
/// UYVA's alpha plane kept), P216 and PA16 (their high bytes, as UYVY) converted. Pure; writes into a buffer the caller keeps.</summary>
public static class SpoutPixels
{
    /// <summary>Converts <paramref name="frame"/>'s planes (the first at <paramref name="stride"/>, the rest tight after it) into
    /// <paramref name="bgra"/> (width·4 bytes a row); false for a format it does not know.</summary>
    public static bool ToBgra(ReadOnlySpan<byte> frame, int stride, SendFormat format, int width, int height, Span<byte> bgra)
    {
        if (bgra.Length < width * height * 4)
            return false;
        switch (format)
        {
            case SendFormat.Bgra8:
                for (var y = 0; y < height; y++)
                    frame.Slice(y * stride, width * 4).CopyTo(bgra.Slice(y * width * 4, width * 4));
                return true;
            case SendFormat.Uyvy or SendFormat.Uyva:
                Uyvy(frame, stride, width, height, bgra, deep: false, alpha: format == SendFormat.Uyva);
                return true;
            case SendFormat.P216 or SendFormat.Pa16:
                Uyvy(frame, stride, width, height, bgra, deep: true, alpha: format == SendFormat.Pa16);
                return true;
            default:
                return false;
        }
    }

    private static void Uyvy(ReadOnlySpan<byte> frame, int stride, int width, int height, Span<byte> bgra, bool deep, bool alpha)
    {
        var lumaPlane = stride * height;
        var chromaStart = deep ? lumaPlane : 0;
        var alphaStart = deep ? 2 * lumaPlane : lumaPlane;
        for (var y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * width * 4, width * 4);
            for (var x = 0; x < width; x++)
            {
                int luma, cb, cr;
                if (deep)
                {
                    // P216: Y plane of 16-bit samples, then an interleaved Cb Cr plane, both full height; the high byte of each.
                    luma = frame[y * stride + 2 * x + 1];
                    var pair = chromaStart + y * stride + (x & ~1) * 2;
                    cb = frame[pair + 1];
                    cr = frame[pair + 3];
                }
                else
                {
                    var quad = y * stride + (x & ~1) * 2;
                    cb = frame[quad];
                    luma = frame[quad + ((x & 1) == 0 ? 1 : 3)];
                    cr = frame[quad + 2];
                }
                // BT.709 limited range to full-range RGB, in fixed point (×1024).
                var c = 1192 * (luma - 16);
                var d = cb - 128;
                var e = cr - 128;
                row[x * 4 + 2] = Clamp((c + 1836 * e + 512) >> 10);
                row[x * 4 + 1] = Clamp((c - 218 * d - 546 * e + 512) >> 10);
                row[x * 4] = Clamp((c + 2163 * d + 512) >> 10);
                row[x * 4 + 3] = alpha
                    ? deep ? frame[alphaStart + y * 2 * width + 2 * x + 1] : frame[alphaStart + y * width + x]
                    : (byte)255;
            }
        }
    }

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);
}
