using PlayMap.Core.Media;

namespace PlayMap.Core.Video;

/// <summary>The CPU twin of the GPU's pack and unpack passes for the network layouts, BT.709 limited range (Y 16–235, chroma 16–240
/// at 8 bits; the same codes ×256 at 16 bits). The shaders do exactly this arithmetic, so tests hold them to it code for code.
///
/// <para>Pack: Y per pixel; the chroma of pair i is co-sited with pixel 2i and filtered [1,2,1]/4 over pixels 2i−1, 2i, 2i+1, an
/// index past either edge clamped to it. Unpack: an even pixel takes its pair's chroma; an odd pixel the mean of its pair's and the
/// next pair's, the last pair standing for the next at the right edge (clamped, never wrapped). Pictures are RGBA, top row first,
/// one value 0..1 per channel.</para></summary>
public static class PackReference
{
    public const float Kr = 0.2126f;
    public const float Kb = 0.0722f;
    public const float Kg = 1 - Kr - Kb;

    /// <summary>Code values (not rounded) of an RGB colour at 8 bits (<paramref name="scale"/> 1) or 16 bits (256).</summary>
    public static (float Y, float Cb, float Cr) Encode(float r, float g, float b, float scale = 1)
    {
        var y = Kr * r + Kg * g + Kb * b;
        var cb = (b - y) / (2 * (1 - Kb));
        var cr = (r - y) / (2 * (1 - Kr));
        return ((16 + 219 * y) * scale, (128 + 224 * cb) * scale, (128 + 224 * cr) * scale);
    }

    /// <summary>The RGB (0..1, clamped) of code values at 8 bits (<paramref name="scale"/> 1) or 16 bits (256).</summary>
    public static (float R, float G, float B) Decode(float y, float cb, float cr, float scale = 1)
    {
        var yy = (y / scale - 16) / 219f;
        var u = (cb / scale - 128) / 224f;
        var v = (cr / scale - 128) / 224f;
        var r = yy + 2 * (1 - Kr) * v;
        var b = yy + 2 * (1 - Kb) * u;
        var g = (yy - Kr * r - Kb * b) / Kg;
        return (Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1));
    }

    /// <summary>A picture packed into a layout: UYVY, UYVA, P216 and PA16 as <see cref="LivePlanes"/> lays them out (little-endian
    /// 16-bit samples), BGRA as 4·w bytes a row. <paramref name="rgba"/> holds w·h·4 values.</summary>
    public static byte[] Pack(PixelLayout layout, ReadOnlySpan<float> rgba, int width, int height)
    {
        if (rgba.Length < width * height * 4)
            throw new ArgumentException("the picture is smaller than its size", nameof(rgba));
        if (layout is PixelLayout.Bgra8)
        {
            var bgra = new byte[width * height * 4];
            for (var i = 0; i < width * height; i++)
            {
                bgra[i * 4] = Code8(rgba[i * 4 + 2]);
                bgra[i * 4 + 1] = Code8(rgba[i * 4 + 1]);
                bgra[i * 4 + 2] = Code8(rgba[i * 4]);
                bgra[i * 4 + 3] = Code8(rgba[i * 4 + 3]);
            }
            return bgra;
        }
        if (LivePlanes.Refuse(layout, width, height) is { } why)
            throw new ArgumentException(why, nameof(width));
        var deep = LivePlanes.IsDeep(layout);
        var scale = deep ? 256f : 1f;
        var max = deep ? 65535f : 255f;
        var dst = new byte[LivePlanes.FrameBytes(layout, width, height)];
        var pairs = width / 2;
        for (var row = 0; row < height; row++)
        {
            for (var i = 0; i < pairs; i++)
            {
                var (y0, _, _) = EncodeAt(rgba, width, row, 2 * i, scale);
                var (y1, _, _) = EncodeAt(rgba, width, row, 2 * i + 1, scale);
                var (cb, cr) = ChromaOfPair(rgba, width, row, i, scale);
                if (deep)
                {
                    var yRow = row * 2 * width;
                    Put16(dst, yRow + 4 * i, Round(y0, max));
                    Put16(dst, yRow + 4 * i + 2, Round(y1, max));
                    var cRow = height * 2 * width + row * 2 * width;
                    Put16(dst, cRow + 4 * i, Round(cb, max));
                    Put16(dst, cRow + 4 * i + 2, Round(cr, max));
                }
                else
                {
                    var o = row * 2 * width + 4 * i;
                    dst[o] = (byte)Round(cb, max);
                    dst[o + 1] = (byte)Round(y0, max);
                    dst[o + 2] = (byte)Round(cr, max);
                    dst[o + 3] = (byte)Round(y1, max);
                }
            }
        }
        if (LivePlanes.HasAlpha(layout))
        {
            var start = deep ? 2L * 2 * width * height : 2L * width * height;
            for (var row = 0; row < height; row++)
            {
                for (var x = 0; x < width; x++)
                {
                    var a = rgba[(row * width + x) * 4 + 3];
                    if (deep)
                        Put16(dst, (int)(start + (row * width + x) * 2L), Round(a * 65535f, 65535f));
                    else
                        dst[start + row * width + x] = Code8(a);
                }
            }
        }
        return dst;
    }

    /// <summary>The picture a packed frame unpacks to (RGBA, 0..1), as the GPU's unpack pass reads it.</summary>
    public static float[] Unpack(PixelLayout layout, ReadOnlySpan<byte> frame, int width, int height)
    {
        if (LivePlanes.Refuse(layout, width, height) is { } why)
            throw new ArgumentException(why, nameof(width));
        if (frame.Length < LivePlanes.FrameBytes(layout, width, height))
            throw new ArgumentException("the frame is smaller than its layout", nameof(frame));
        var deep = LivePlanes.IsDeep(layout);
        var scale = deep ? 256f : 1f;
        var pairs = width / 2;
        var rgba = new float[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = x / 2;
                var y = Sample(frame, layout, width, height, row, x, plane: 0);
                var (cb, cr) = ChromaCodes(frame, layout, width, height, row, i);
                if (x % 2 == 1)
                {
                    var (cb2, cr2) = ChromaCodes(frame, layout, width, height, row, Math.Min(i + 1, pairs - 1));
                    cb = (cb + cb2) / 2;
                    cr = (cr + cr2) / 2;
                }
                var (r, g, b) = Decode(y, cb, cr, scale);
                var o = (row * width + x) * 4;
                rgba[o] = r;
                rgba[o + 1] = g;
                rgba[o + 2] = b;
                rgba[o + 3] = LivePlanes.HasAlpha(layout) ? AlphaAt(frame, layout, width, height, row, x) : 1;
            }
        }
        return rgba;
    }

    private static (float Y, float Cb, float Cr) EncodeAt(ReadOnlySpan<float> rgba, int width, int row, int x, float scale)
    {
        var o = (row * width + Math.Clamp(x, 0, width - 1)) * 4;
        return Encode(rgba[o], rgba[o + 1], rgba[o + 2], scale);
    }

    private static (float Cb, float Cr) ChromaOfPair(ReadOnlySpan<float> rgba, int width, int row, int pair, float scale)
    {
        var (_, cbL, crL) = EncodeAt(rgba, width, row, 2 * pair - 1, scale);
        var (_, cbC, crC) = EncodeAt(rgba, width, row, 2 * pair, scale);
        var (_, cbR, crR) = EncodeAt(rgba, width, row, 2 * pair + 1, scale);
        return ((cbL + 2 * cbC + cbR) / 4, (crL + 2 * crC + crR) / 4);
    }

    private static float Sample(ReadOnlySpan<byte> frame, PixelLayout layout, int width, int height, int row, int x, int plane)
    {
        if (LivePlanes.IsDeep(layout))
            return Get16(frame, row * 2 * width + 2 * x);
        return frame[row * 2 * width + 4 * (x / 2) + (x % 2 == 0 ? 1 : 3)];
    }

    private static (float Cb, float Cr) ChromaCodes(ReadOnlySpan<byte> frame, PixelLayout layout, int width, int height, int row, int pair)
    {
        if (LivePlanes.IsDeep(layout))
        {
            var c = height * 2 * width + row * 2 * width + 4 * pair;
            return (Get16(frame, c), Get16(frame, c + 2));
        }
        var o = row * 2 * width + 4 * pair;
        return (frame[o], frame[o + 2]);
    }

    private static float AlphaAt(ReadOnlySpan<byte> frame, PixelLayout layout, int width, int height, int row, int x) =>
        LivePlanes.IsDeep(layout)
            ? Get16(frame, (int)(2L * 2 * width * height + (row * width + x) * 2L)) / 65535f
            : frame[(int)(2L * width * height + row * width + x)] / 255f;

    private static byte Code8(float v) => (byte)Math.Clamp(MathF.Floor(v * 255f + 0.5f), 0, 255);

    /// <summary>Half up, as the shaders round (floor of code + ½), never to even.</summary>
    private static int Round(float code, float max) => (int)Math.Clamp(MathF.Floor(code + 0.5f), 0, max);

    private static void Put16(byte[] dst, int offset, int value)
    {
        dst[offset] = (byte)(value & 0xFF);
        dst[offset + 1] = (byte)(value >> 8);
    }

    private static float Get16(ReadOnlySpan<byte> src, int offset) => src[offset] | (src[offset + 1] << 8);
}
