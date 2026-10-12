using System.Globalization;
using System.Reflection;
using SkiaSharp;

namespace PlayMap.Render.Generators.Skia;

/// <summary>The embedded Inter faces, so a test pattern's type is identical on every machine (stripped-down
/// render nodes included). Falls back to the platform's default face if the resources cannot be read.</summary>
internal static class Typefaces
{
    public static readonly SKTypeface Regular = Load("Inter-Regular.ttf") ?? SKTypeface.Default;
    public static readonly SKTypeface SemiBold = Load("Inter-SemiBold.ttf") ?? SKTypeface.Default;

    private static SKTypeface? Load(string file)
    {
        using var stream = typeof(Typefaces).Assembly.GetManifestResourceStream("PlayMap.Render.Assets." + file);
        if (stream is null)
            return null;
        using var data = SKData.Create(stream);
        return data is null ? null : SKTypeface.FromData(data);
    }
}

/// <summary>Paints and fonts a pattern draws with, reused call to call: a pattern draws on one thread, and
/// every getter mutates its one cached object — use the returned paint before asking for another of the
/// same kind.</summary>
internal sealed class SkiaKit : IDisposable
{
    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _fillAA = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _strokeAA = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly SKPaint _text = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

    public SKFont Regular { get; } = new(Typefaces.Regular, 16) { Edging = SKFontEdging.Antialias, Subpixel = true };
    public SKFont Bold { get; } = new(Typefaces.SemiBold, 16) { Edging = SKFontEdging.Antialias, Subpixel = true };
    public SKPath Path { get; } = new();

    public SKPathEffect DashShort { get; } = SKPathEffect.CreateDash([4, 4], 0);
    public SKPathEffect DashLong { get; } = SKPathEffect.CreateDash([10, 6], 0);

    /// <summary>Aliased fill — pixel-exact rectangles.</summary>
    public SKPaint Fill(SKColor color)
    {
        _fill.Color = color;
        _fill.Shader = null;
        return _fill;
    }

    public SKPaint FillAA(SKColor color)
    {
        _fillAA.Color = color;
        _fillAA.Shader = null;
        return _fillAA;
    }

    public SKPaint StrokeAA(SKColor color, float width = 1, SKPathEffect? dash = null)
    {
        _strokeAA.Color = color;
        _strokeAA.StrokeWidth = width;
        _strokeAA.PathEffect = dash;
        return _strokeAA;
    }

    public SKPaint Text(SKColor color)
    {
        _text.Color = color;
        return _text;
    }

    public void Dispose()
    {
        _fill.Dispose();
        _fillAA.Dispose();
        _strokeAA.Dispose();
        _text.Dispose();
        Regular.Dispose();
        Bold.Dispose();
        Path.Dispose();
        DashShort.Dispose();
        DashLong.Dispose();
    }
}

/// <summary>Pixel-exact drawing: coordinates are whole pixel indices and a "line at x" fills exactly pixel
/// column x (antialiasing off).</summary>
internal static class Px
{
    private static readonly string[] Numbers = BuildNumbers();

    private static string[] BuildNumbers()
    {
        var all = new string[1000];
        for (var i = 0; i < all.Length; i++)
            all[i] = i.ToString(CultureInfo.InvariantCulture);
        return all;
    }

    /// <summary>A whole number's text from a table (0–999), so a pattern drawn per frame does not allocate it.</summary>
    public static string Number(int n) => n is >= 0 and < 1000 ? Numbers[n] : n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Horizontal line occupying rows [y, y + width).</summary>
    public static void LineH(SKCanvas c, int y, int x0, int x1, int width, SKPaint fill) =>
        c.DrawRect(SKRect.Create(x0, y, x1 - x0, width), fill);

    /// <summary>Vertical line occupying columns [x, x + width).</summary>
    public static void LineV(SKCanvas c, int x, int y0, int y1, int width, SKPaint fill) =>
        c.DrawRect(SKRect.Create(x, y0, width, y1 - y0), fill);

    /// <summary>A border exactly <paramref name="width"/> pixels wide, inside the rectangle.</summary>
    public static void BorderInside(SKCanvas c, SKRectI r, int width, SKPaint fill)
    {
        if (r.Width <= 0 || r.Height <= 0)
            return;
        var w = Math.Min(width, Math.Min(r.Width, r.Height) / 2);
        if (w <= 0)
            w = 1;
        c.DrawRect(SKRect.Create(r.Left, r.Top, r.Width, w), fill);
        c.DrawRect(SKRect.Create(r.Left, r.Bottom - w, r.Width, w), fill);
        c.DrawRect(SKRect.Create(r.Left, r.Top + w, w, r.Height - 2 * w), fill);
        c.DrawRect(SKRect.Create(r.Right - w, r.Top + w, w, r.Height - 2 * w), fill);
    }

    /// <summary>A centre cross with arms of the given half length and thickness.</summary>
    public static void Cross(SKCanvas c, int cx, int cy, int halfLen, int thickness, SKPaint fill)
    {
        var t = Math.Max(1, thickness);
        var off = t / 2;
        LineH(c, cy - off, cx - halfLen, cx + halfLen, t, fill);
        LineV(c, cx - off, cy - halfLen, cy + halfLen, t, fill);
    }

    /// <summary>A diagonal hatch inside a rectangle (blend zones, bezels).</summary>
    public static void Hatch(SKCanvas c, SKRect r, float spacing, SKPaint stroke)
    {
        c.Save();
        c.ClipRect(r);
        var span = r.Width + r.Height;
        for (var d = -r.Height; d < span; d += spacing)
            c.DrawLine(r.Left + d, r.Top, r.Left + d + r.Height, r.Bottom, stroke);
        c.Restore();
    }

    /// <summary>Text with its centre at (cx, cy).</summary>
    public static void TextCentered(SKCanvas c, string text, float cx, float cy, SKFont font, SKPaint paint)
    {
        var m = font.Metrics;
        var baseline = cy - (m.Ascent + m.Descent) / 2;
        c.DrawText(text, cx, baseline, SKTextAlign.Center, font, paint);
    }

    public static void TextLeft(SKCanvas c, string text, float x, float baseline, SKFont font, SKPaint paint) =>
        c.DrawText(text, x, baseline, SKTextAlign.Left, font, paint);

    /// <summary>A rounded translucent chip with centred text at the bottom (or top) centre of the picture —
    /// the patterns' info line.</summary>
    public static void Chip(SKCanvas c, SkiaKit kit, string text, int w, int h, bool top = false)
    {
        var size = ChipText(h);
        var font = kit.Bold;
        font.Size = size;
        var tw = font.MeasureText(text);
        var padX = size * 0.7f;
        var padY = size * 0.42f;
        var boxW = tw + padX * 2;
        var boxH = size + padY * 2;
        var margin = Math.Max(6f, Math.Min(w, h) * 0.02f);
        var rect = SKRect.Create((w - boxW) / 2, top ? margin : h - margin - boxH, boxW, boxH);
        c.DrawRoundRect(rect, rect.Height * 0.24f, rect.Height * 0.24f, kit.FillAA(Ink.ChipBg));
        TextCentered(c, text, rect.MidX, rect.MidY, font, kit.Text(SKColors.White));
    }

    public static float ChipText(int h) => Math.Clamp(h * 0.024f, 11, 40);

    public static SKColor Hue(int index, int count, float saturation = 78, float value = 100) =>
        SKColor.FromHsv(count <= 0 ? 0 : index * 360f / count, saturation, value);

    public static SKColor Color(string? hex, SKColor fallback) =>
        PlayMap.Core.Content.ColorRgb.TryParsePacked(hex, out var rgb)
            ? new SKColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : fallback;
}

/// <summary>The patterns' own ink. Deliberately not a show's brand: an engineering card's greys must look the
/// same next to the same colours in every venue.</summary>
internal static class Ink
{
    public static readonly SKColor Ground = new(0x0B, 0x0C, 0x10);
    public static readonly SKColor Edge = SKColors.White;
    public static readonly SKColor EdgeDim = new(0xFF, 0xFF, 0xFF, 0x5A);
    public static readonly SKColor Accent = new(0x3E, 0xC1, 0xF3);
    public static readonly SKColor Secondary = new(0xF0, 0x3E, 0xAE);
    public static readonly SKColor Warn = new(0xFF, 0x8A, 0x00);
    public static readonly SKColor SubtleLine = new(0xFF, 0xFF, 0xFF, 0x46);
    public static readonly SKColor ChipBg = new(0x00, 0x00, 0x00, 0xB4);
}
