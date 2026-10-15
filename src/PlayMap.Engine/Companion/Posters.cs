using System.Globalization;
using System.Runtime.InteropServices;
using PlayMap.Core.Media;
using SkiaSharp;

namespace PlayMap.Engine.Companion;

/// <summary>Small pictures from decoded frames and flat colours: a decoded frame (BGRA, RGBA, NV12, I420, P010 or grey) is box-filtered
/// down to fit a box in one pass on the CPU (pure C#, no SIMD needed at key size), then encoded as JPEG or PNG with SkiaSharp. A JPEG
/// steps its quality down from 80 by 10 until it fits <see cref="MaxBytes"/>.</summary>
public static class Posters
{
    /// <summary>The most a picture may weigh: its base64 fits a 60 KiB line.</summary>
    public const int MaxBytes = 45 * 1024;

    /// <summary>The layouts a poster is made from; block-compressed and network 4:2:2 frames are not (yet).</summary>
    public static bool CanRead(PixelLayout layout) =>
        layout is PixelLayout.Bgra8 or PixelLayout.Rgba8 or PixelLayout.Nv12 or PixelLayout.Yuv420P or PixelLayout.P010 or PixelLayout.Gray8;

    /// <summary>The size that fits <paramref name="boxW"/>×<paramref name="boxH"/> with the source's aspect (at least 1×1).</summary>
    public static (int W, int H) Fit(int srcW, int srcH, int boxW, int boxH)
    {
        if (srcW <= 0 || srcH <= 0)
            return (Math.Max(1, boxW), Math.Max(1, boxH));
        var scale = Math.Min(boxW / (double)srcW, boxH / (double)srcH);
        scale = Math.Min(scale, 1);
        return (Math.Max(1, (int)Math.Round(srcW * scale)), Math.Max(1, (int)Math.Round(srcH * scale)));
    }

    /// <summary>The frame as BGRA at <paramref name="w"/>×<paramref name="h"/>, each output pixel the mean of the source pixels it covers.</summary>
    public static byte[] ToBgra(VideoFrameLease frame, int w, int h)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!CanRead(frame.Layout))
            throw new NotSupportedException($"a {frame.Layout} frame makes no poster");
        var planes = frame.Planes.Select(Copy).ToArray();
        var full = frame.Range == ColorRange.Full;
        var bt709 = frame.Matrix != ColorMatrix.Bt601;
        var sw = frame.Width;
        var sh = frame.Height;
        var outp = new byte[w * h * 4];
        for (var oy = 0; oy < h; oy++)
        {
            var y0 = oy * sh / h;
            var y1 = Math.Max(y0 + 1, (oy + 1) * sh / h);
            for (var ox = 0; ox < w; ox++)
            {
                var x0 = ox * sw / w;
                var x1 = Math.Max(x0 + 1, (ox + 1) * sw / w);
                // At most 4×4 samples per output pixel: a box over a big frame reads its corners and centre, never every pixel.
                var stepY = Math.Max(1, (y1 - y0) / 4);
                var stepX = Math.Max(1, (x1 - x0) / 4);
                double r = 0, g = 0, b = 0;
                var n = 0;
                for (var y = y0; y < y1; y += stepY)
                {
                    for (var x = x0; x < x1; x += stepX)
                    {
                        var (pr, pg, pb) = Sample(frame.Layout, planes, frame.Planes, x, y, full, bt709);
                        r += pr;
                        g += pg;
                        b += pb;
                        n++;
                    }
                }
                var o = (oy * w + ox) * 4;
                outp[o] = Byte(b / n);
                outp[o + 1] = Byte(g / n);
                outp[o + 2] = Byte(r / n);
                outp[o + 3] = 255;
            }
        }
        return outp;
    }

    private static byte[] Copy(FramePlane p)
    {
        var bytes = new byte[checked(p.Stride * p.Height)];
        Marshal.Copy(p.Data, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte Byte(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    private static (double R, double G, double B) Sample(PixelLayout layout, byte[][] data, IReadOnlyList<FramePlane> planes, int x, int y, bool full, bool bt709)
    {
        switch (layout)
        {
            case PixelLayout.Bgra8:
            {
                var i = y * planes[0].Stride + x * 4;
                return (data[0][i + 2], data[0][i + 1], data[0][i]);
            }
            case PixelLayout.Rgba8:
            {
                var i = y * planes[0].Stride + x * 4;
                return (data[0][i], data[0][i + 1], data[0][i + 2]);
            }
            case PixelLayout.Gray8:
            {
                var v = data[0][y * planes[0].Stride + x];
                return (v, v, v);
            }
            case PixelLayout.Nv12:
            {
                var yy = data[0][y * planes[0].Stride + x];
                var c = (y / 2) * planes[1].Stride + (x / 2) * 2;
                return Yuv(yy, data[1][c], data[1][c + 1], full, bt709);
            }
            case PixelLayout.Yuv420P:
            {
                var yy = data[0][y * planes[0].Stride + x];
                var u = data[1][(y / 2) * planes[1].Stride + x / 2];
                var v = data[2][(y / 2) * planes[2].Stride + x / 2];
                return Yuv(yy, u, v, full, bt709);
            }
            default:
            {
                // P010: 16-bit samples with 10 significant high bits; the top byte is the 8-bit value.
                var yy = data[0][y * planes[0].Stride + x * 2 + 1];
                var c = (y / 2) * planes[1].Stride + (x / 2) * 4;
                return Yuv(yy, data[1][c + 1], data[1][c + 3], full, bt709);
            }
        }
    }

    private static (double R, double G, double B) Yuv(double y, double u, double v, bool full, bool bt709)
    {
        var yn = full ? y / 255.0 : (y - 16) / 219.0;
        var cb = full ? (u - 128) / 255.0 : (u - 128) / 224.0;
        var cr = full ? (v - 128) / 255.0 : (v - 128) / 224.0;
        double r, g, b;
        if (bt709)
        {
            r = yn + 1.5748 * cr;
            g = yn - 0.1873 * cb - 0.4681 * cr;
            b = yn + 1.8556 * cb;
        }
        else
        {
            r = yn + 1.402 * cr;
            g = yn - 0.344136 * cb - 0.714136 * cr;
            b = yn + 1.772 * cb;
        }
        return (r * 255, g * 255, b * 255);
    }

    /// <summary>BGRA pixels as JPEG (quality from 80 down by 10 until it fits) or PNG.</summary>
    public static byte[] Encode(byte[] bgra, int w, int h, bool jpeg)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var image = SKImage.FromPixelCopy(info, bgra, w * 4)
            ?? throw new InvalidOperationException("the picture could not be made");
        if (!jpeg)
        {
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            return png.ToArray();
        }
        byte[] bytes = [];
        for (var q = 80; q >= 10; q -= 10)
        {
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, q);
            bytes = data.ToArray();
            if (bytes.Length <= MaxBytes)
                break;
        }
        return bytes;
    }

    /// <summary>A flat colour (#RRGGBB) at a level (0–1).</summary>
    public static byte[] Flat(string hex, double level, int w, int h)
    {
        var (r, g, b) = ParseHex(hex);
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = Byte(b * level);
            px[i + 1] = Byte(g * level);
            px[i + 2] = Byte(r * level);
            px[i + 3] = 255;
        }
        return Encode(px, w, h, jpeg: false);
    }

    /// <summary>A tile for a generator that is not drawn as a still: a colour from its kind, and its name.</summary>
    public static byte[] Tile(string kind, string name, int w, int h)
    {
        var hue = (uint)kind.Aggregate(17, (a, c) => (a * 31) + c) % 360;
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque))
            ?? throw new InvalidOperationException("the tile could not be drawn");
        var canvas = surface.Canvas;
        canvas.Clear(SKColor.FromHsl(hue, 45, 28));
        using var font = new SKFont(SKTypeface.Default, Math.Max(8, h / 7f));
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        var words = name.Length > 18 ? name[..18] : name;
        canvas.DrawText(words, w / 2f, h / 2f + font.Size / 3, SKTextAlign.Center, font, paint);
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    /// <summary>"#RRGGBB" (or "RRGGBB") as bytes; anything else is grey.</summary>
    public static (int R, int G, int B) ParseHex(string hex)
    {
        var t = (hex ?? "").Trim().TrimStart('#');
        if (t.Length == 6 && int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return ((v >> 16) & 255, (v >> 8) & 255, v & 255);
        return (128, 128, 128);
    }
}
