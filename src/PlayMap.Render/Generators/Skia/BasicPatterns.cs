using System.Globalization;
using SkiaSharp;

namespace PlayMap.Render.Generators.Skia;

/// <summary>The alignment grid: lines symmetric about the centre pixel (the point riggers align to), drawn
/// pixel-exact with antialiasing off; minor lines in the line colour at a fifth of its strength.</summary>
internal sealed class GridPattern : ISkiaPattern
{
    public void Draw(in PatternCanvas p)
    {
        var c = p.Canvas;
        var lineColor = p.Color("color", SKColors.White);
        c.Clear(p.Color("background", SKColors.Black));
        int w = p.W, h = p.H;
        int cx = w / 2, cy = h / 2;
        var cell = Math.Max(2, p.Int("cell"));
        var lw = PatternCanvas.Hairline(Math.Max(1, p.Int("line")));
        var minor = PatternCanvas.Hairline(1);

        var subdivisions = p.Int("subdivisions");
        if (subdivisions > 0)
        {
            var step = (float)cell / (subdivisions + 1);
            if (step >= 2 && PatternCanvas.Resolves(step))
            {
                var sub = p.Kit.Fill(lineColor.WithAlpha(0x46));
                for (var x = cx % step; x < w; x += step)
                    Px.LineV(c, (int)MathF.Round(x), 0, h, minor, sub);
                for (var y = cy % step; y < h; y += step)
                    Px.LineH(c, (int)MathF.Round(y), 0, w, minor, sub);
            }
        }

        var line = p.Kit.Fill(lineColor);
        for (var x = cx % cell; x < w; x += cell)
            Px.LineV(c, x - lw / 2, 0, h, lw, line);
        for (var y = cy % cell; y < h; y += cell)
            Px.LineH(c, y - lw / 2, 0, w, lw, line);

        if (p.Flag("diagonals"))
        {
            var diag = p.Kit.StrokeAA(lineColor.WithAlpha(0x46), minor);
            c.DrawLine(0, 0, w, h, diag);
            c.DrawLine(w, 0, 0, h, diag);
        }
        if (p.Flag("border"))
            Px.BorderInside(c, new SKRectI(0, 0, w, h), lw, p.Kit.Fill(Ink.Accent));
        if (p.Flag("centerCross"))
            Px.Cross(c, cx, cy, Math.Min(w, h) / 8, lw + 2, p.Kit.Fill(Ink.Accent));
    }
}

/// <summary>A uniformity and black-level field: one colour at a level, nothing else unless a border is asked.</summary>
internal sealed class FlatFieldPattern : ISkiaPattern
{
    public void Draw(in PatternCanvas p)
    {
        var baseColor = p.Color("color", SKColors.White);
        var k = (float)(p.Num("level") / 100.0);
        var color = new SKColor(
            (byte)MathF.Round(baseColor.Red * k),
            (byte)MathF.Round(baseColor.Green * k),
            (byte)MathF.Round(baseColor.Blue * k));
        p.Canvas.Clear(color);
        if (p.Flag("border"))
        {
            var luma = 0.2126 * color.Red + 0.7152 * color.Green + 0.0722 * color.Blue;
            Px.BorderInside(p.Canvas, new SKRectI(0, 0, p.W, p.H), 1, p.Kit.Fill(luma > 100 ? SKColors.Black : SKColors.White));
        }
    }
}

/// <summary>Circles, crosshair, safe areas, aspect markers — roundness and framing.</summary>
internal sealed class GeometryPattern : ISkiaPattern
{
    private static readonly (float Ratio, string Label)[] Aspects = [(4f / 3f, "4:3"), (1f, "1:1"), (2.39f, "2.39:1")];

    public void Draw(in PatternCanvas p)
    {
        var c = p.Canvas;
        c.Clear(SKColors.Black);
        int w = p.W, h = p.H;
        float cx = w / 2f, cy = h / 2f;
        var hair = PatternCanvas.Hairline(1);

        if (p.Flag("diagonals"))
        {
            var diag = p.Kit.StrokeAA(Ink.SubtleLine, hair);
            c.DrawLine(0, 0, w, h, diag);
            c.DrawLine(w, 0, 0, h, diag);
        }
        if (p.Flag("crosshair"))
        {
            var line = p.Kit.Fill(SKColors.White);
            Px.LineH(c, h / 2, 0, w, hair, line);
            Px.LineV(c, w / 2, 0, h, hair, line);
        }
        if (p.Flag("circles"))
        {
            var r = Math.Min(w, h) / 2f - 2;
            var stroke = p.Kit.StrokeAA(SKColors.White, 2);
            c.DrawCircle(cx, cy, r, stroke);
            c.DrawCircle(cx, cy, r / 2, stroke);
            var cr = Math.Min(w, h) / 8f;
            var corner = p.Kit.StrokeAA(Ink.Accent, 2);
            c.DrawCircle(cr + 2, cr + 2, cr, corner);
            c.DrawCircle(w - cr - 2, cr + 2, cr, corner);
            c.DrawCircle(cr + 2, h - cr - 2, cr, corner);
            c.DrawCircle(w - cr - 2, h - cr - 2, cr, corner);
        }
        if (p.Flag("safeAreas"))
        {
            Safe(in p, 0.93f, "ACTION", Ink.Accent);
            Safe(in p, 0.90f, "TITLE", Ink.Secondary);
        }
        if (p.Flag("aspect"))
        {
            foreach (var (ratio, label) in Aspects)
            {
                var aw = h * ratio;
                if (aw >= w - 2)
                    continue;
                var x0 = (w - aw) / 2f;
                var stroke = p.Kit.StrokeAA(Ink.SubtleLine, hair, p.Kit.DashLong);
                c.DrawLine(x0, 0, x0, h, stroke);
                c.DrawLine(x0 + aw, 0, x0 + aw, h, stroke);
                var font = p.Kit.Regular;
                font.Size = Math.Clamp(h * 0.02f, 10, 28);
                Px.TextCentered(c, label, x0 + aw / 2, font.Size * 1.2f, font, p.Kit.Text(Ink.SubtleLine));
            }
        }
        Px.BorderInside(c, new SKRectI(0, 0, w, h), hair, p.Kit.Fill(SKColors.White));
    }

    private static void Safe(in PatternCanvas p, float pct, string label, SKColor color)
    {
        int w = p.W, h = p.H;
        var sw = w * pct;
        var sh = h * pct;
        var rect = SKRect.Create((w - sw) / 2f, (h - sh) / 2f, sw, sh);
        p.Canvas.DrawRect(rect, p.Kit.StrokeAA(color, 1, p.Kit.DashShort));
        var font = p.Kit.Regular;
        font.Size = Math.Clamp(h * 0.018f, 9, 24);
        Px.TextLeft(p.Canvas, string.Create(CultureInfo.InvariantCulture, $"{label} {pct * 100:0}%"), rect.Left + 8, rect.Top + font.Size + 4, font, p.Kit.Text(color));
    }
}

/// <summary>Colour bars. SMPTE follows the RP 219 HD layout (7/12 + 1/12 + 1/12 + 3/12 bands, W/8 flanks,
/// PLUGE) with BT.709 narrow-range 8-bit values; full range stretches 16–235 to 0–255 for a graphics
/// pipeline. EBU and the full-height 75 % and 100 % bars are the classics.</summary>
internal sealed class BarsPattern : ISkiaPattern
{
    public const byte NLo = 16;
    public const byte NHi = 235;
    public const byte N75 = 180;
    public const byte G40 = 104;
    public const byte G15 = 49;
    public const byte PlugeMinus = 12;
    public const byte PlugePlus2 = 20;
    public const byte PlugePlus4 = 25;

    public void Draw(in PatternCanvas p)
    {
        switch (p.Choice("variant"))
        {
            case "smpte":
                Smpte(in p);
                break;
            case "ebu100":
                FullHeight(in p, 255, includeBlack: true);
                break;
            case "bars75":
                FullHeight(in p, 191, includeBlack: false);
                break;
            default:
                FullHeight(in p, 255, includeBlack: false);
                break;
        }
    }

    /// <summary>The colours of the full-height bars, left to right.</summary>
    public static int FullHeightColors(Span<SKColor> dst, byte a, bool includeBlack)
    {
        var i = 0;
        dst[i++] = a < 255 ? SKColors.White : new SKColor(a, a, a);
        dst[i++] = new SKColor(a, a, 0);
        dst[i++] = new SKColor(0, a, a);
        dst[i++] = new SKColor(0, a, 0);
        dst[i++] = new SKColor(a, 0, a);
        dst[i++] = new SKColor(a, 0, 0);
        dst[i++] = new SKColor(0, 0, a);
        if (includeBlack)
            dst[i++] = SKColors.Black;
        return i;
    }

    private static void FullHeight(in PatternCanvas p, byte amplitude, bool includeBlack)
    {
        Span<SKColor> colors = stackalloc SKColor[8];
        var n = FullHeightColors(colors, amplitude, includeBlack);
        for (var i = 0; i < n; i++)
        {
            var x0 = (int)Math.Round(p.W * (double)i / n);
            var x1 = (int)Math.Round(p.W * (double)(i + 1) / n);
            p.Canvas.DrawRect(SKRect.Create(x0, 0, x1 - x0, p.H), p.Kit.Fill(colors[i]));
        }
    }

    private static void Smpte(in PatternCanvas p)
    {
        var full = p.Flag("fullRange");
        var c = p.Canvas;
        int w = p.W, h = p.H;
        var d = w / 8f;
        var barW = (w - 2 * d) / 7f;
        var h1 = h * 7f / 12f;
        var h2 = h * 1f / 12f;
        var h3 = h * 1f / 12f;

        FillX(in p, 0, d, 0, h1, Mono(G40, full));
        ReadOnlySpan<SKColor> bars75 =
        [
            new SKColor(N75, N75, N75), new SKColor(N75, N75, NLo), new SKColor(NLo, N75, N75), new SKColor(NLo, N75, NLo),
            new SKColor(N75, NLo, N75), new SKColor(N75, NLo, NLo), new SKColor(NLo, NLo, N75),
        ];
        for (var i = 0; i < 7; i++)
            FillX(in p, d + i * barW, d + (i + 1) * barW, 0, h1, full ? Stretch(bars75[i]) : bars75[i]);
        FillX(in p, w - d, w, 0, h1, Mono(G40, full));

        var y2 = h1;
        FillX(in p, 0, d, y2, y2 + h2, Map(NLo, NHi, NHi, full));
        FillX(in p, d, w - d, y2, y2 + h2, Mono(N75, full));
        FillX(in p, w - d, w, y2, y2 + h2, Map(NLo, NLo, NHi, full));

        var y3 = y2 + h2;
        FillX(in p, 0, d, y3, y3 + h3, Map(NHi, NHi, NLo, full));
        using (var shader = SKShader.CreateLinearGradient(new SKPoint(d, 0), new SKPoint(w - d, 0), [Mono(NLo, full), Mono(NHi, full)], SKShaderTileMode.Clamp))
        {
            var paint = p.Kit.Fill(SKColors.White);
            paint.Shader = shader;
            c.DrawRect(SKRect.Create(d, y3, w - 2 * d, h3), paint);
            paint.Shader = null;
        }
        FillX(in p, w - d, w, y3, y3 + h3, Map(NHi, NLo, NLo, full));

        var y4 = y3 + h3;
        FillX(in p, 0, d, y4, h, Mono(G15, full));
        FillX(in p, w - d, w, y4, h, Mono(G15, full));
        var x = d;
        ReadOnlySpan<(float Bars, byte Value)> blocks =
        [
            (1.5f, NLo), (2f, NHi), (1f, NLo),
            (0.3f, PlugeMinus), (0.3f, NLo), (0.3f, PlugePlus2), (0.3f, NLo), (0.3f, PlugePlus4), (1f, NLo),
        ];
        foreach (var (bars, value) in blocks)
        {
            var bw = bars * barW;
            FillX(in p, x, x + bw, y4, h, Mono(value, full));
            x += bw;
        }
    }

    private static void FillX(in PatternCanvas p, float x0, float x1, float y0, float y1, SKColor col)
    {
        var xi0 = (int)MathF.Round(x0);
        var xi1 = (int)MathF.Round(x1);
        var yi0 = (int)MathF.Round(y0);
        var yi1 = (int)MathF.Round(y1);
        p.Canvas.DrawRect(SKRect.Create(xi0, yi0, xi1 - xi0, yi1 - yi0), p.Kit.Fill(col));
    }

    public static SKColor Mono(byte v, bool fullRange)
    {
        if (!fullRange)
            return new SKColor(v, v, v);
        var s = StretchByte(v);
        return new SKColor(s, s, s);
    }

    public static SKColor Map(byte r, byte g, byte b, bool fullRange) =>
        fullRange ? new SKColor(StretchByte(r), StretchByte(g), StretchByte(b)) : new SKColor(r, g, b);

    private static SKColor Stretch(SKColor c) => new(StretchByte(c.Red), StretchByte(c.Green), StretchByte(c.Blue));

    /// <summary>Narrow range 16–235 stretched to 0–255.</summary>
    public static byte StretchByte(byte v) => (byte)Math.Clamp((int)Math.Round((v - 16) * 255.0 / 219.0), 0, 255);
}

/// <summary>Grey and RGB ramps and stepped wedges, for gamma, banding and bit-depth checks.</summary>
internal sealed class RampPattern : ISkiaPattern
{
    private static readonly SKColor MarkerInk = new(255, 160, 32);

    public void Draw(in PatternCanvas p)
    {
        var c = p.Canvas;
        c.Clear(SKColors.Black);
        int w = p.W, h = p.H;
        var markers = p.Flag("markers");
        switch (p.Choice("variant"))
        {
            case "grayV":
                Gradient(in p, SKRect.Create(0, 0, w, h), SKColors.Black, SKColors.White, vertical: true);
                if (markers)
                    MarkersV(in p);
                break;
            case "rgb":
            {
                var band = h / 4f;
                Gradient(in p, SKRect.Create(0, 0, w, band), SKColors.Black, SKColors.White, false);
                Gradient(in p, SKRect.Create(0, band, w, band), SKColors.Black, SKColors.Red, false);
                Gradient(in p, SKRect.Create(0, 2 * band, w, band), SKColors.Black, new SKColor(0, 255, 0), false);
                Gradient(in p, SKRect.Create(0, 3 * band, w, h - 3 * band), SKColors.Black, SKColors.Blue, false);
                if (markers)
                    MarkersH(in p);
                break;
            }
            case "steps":
            {
                var n = Math.Max(2, p.Int("steps"));
                var font = p.Kit.Regular;
                font.Size = Math.Clamp(h * 0.02f, 9, 22);
                for (var i = 0; i < n; i++)
                {
                    var v = StepValue(i, n);
                    var x0 = (int)Math.Round(w * (double)i / n);
                    var x1 = (int)Math.Round(w * (double)(i + 1) / n);
                    c.DrawRect(SKRect.Create(x0, 0, x1 - x0, h), p.Kit.Fill(new SKColor(v, v, v)));
                    if (markers && n <= 32 && x1 - x0 > font.Size * 2.2f)
                        Px.TextCentered(c, Px.Number(v), (x0 + x1) / 2f, h * 0.94f, font, p.Kit.Text(v > 127 ? SKColors.Black : SKColors.White));
                }
                break;
            }
            default:
                Gradient(in p, SKRect.Create(0, 0, w, h), SKColors.Black, SKColors.White, vertical: false);
                if (markers)
                    MarkersH(in p);
                break;
        }
    }

    /// <summary>The code value of wedge <paramref name="i"/> of <paramref name="n"/>.</summary>
    public static byte StepValue(int i, int n) => (byte)Math.Round(i * 255.0 / (n - 1));

    private static void Gradient(in PatternCanvas p, SKRect rect, SKColor from, SKColor to, bool vertical)
    {
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(rect.Left, rect.Top),
            vertical ? new SKPoint(rect.Left, rect.Bottom) : new SKPoint(rect.Right, rect.Top),
            [from, to], SKShaderTileMode.Clamp);
        var paint = p.Kit.Fill(SKColors.White);
        paint.Shader = shader;
        p.Canvas.DrawRect(rect, paint);
        paint.Shader = null;
    }

    private static void MarkersH(in PatternCanvas p)
    {
        var font = p.Kit.Regular;
        font.Size = Math.Clamp(p.H * 0.018f, 9, 20);
        for (var pct = 0; pct <= 100; pct += 10)
        {
            var x = (int)Math.Round((p.W - 1) * pct / 100.0);
            Px.LineV(p.Canvas, x, 0, (int)(font.Size * 1.6f), 1, p.Kit.Fill(MarkerInk));
            var tx = Math.Clamp(x, font.Size * 1.4f, p.W - font.Size * 1.4f);
            Px.TextCentered(p.Canvas, Px.Number(pct), tx, font.Size * 2.4f, font, p.Kit.Text(MarkerInk));
        }
    }

    private static void MarkersV(in PatternCanvas p)
    {
        var font = p.Kit.Regular;
        font.Size = Math.Clamp(p.H * 0.018f, 9, 20);
        for (var pct = 0; pct <= 100; pct += 10)
        {
            var y = (int)Math.Round((p.H - 1) * pct / 100.0);
            Px.LineH(p.Canvas, y, 0, (int)(font.Size * 1.6f), 1, p.Kit.Fill(MarkerInk));
            var ty = Math.Clamp(y, font.Size, p.H - font.Size);
            Px.TextCentered(p.Canvas, Px.Number(pct), font.Size * 3f, ty, font, p.Kit.Text(MarkerInk));
        }
    }
}

/// <summary>Siemens star, line pairs and type samples — focus and resolution.</summary>
internal sealed class FocusPattern : ISkiaPattern
{
    public void Draw(in PatternCanvas p)
    {
        var c = p.Canvas;
        c.Clear(SKColors.Black);
        int w = p.W, h = p.H;
        float cx = w / 2f, cy = h / 2f;

        // An alternating border hash, like a camera chart's: an instant crop check.
        var hash = p.Kit.Fill(SKColors.White);
        const int hashLen = 8;
        var hashW = PatternCanvas.Hairline(2);
        for (var x = 0; x < w; x += hashLen * 2)
        {
            Px.LineH(c, 0, x, Math.Min(x + hashLen, w), hashW, hash);
            Px.LineH(c, h - hashW, x, Math.Min(x + hashLen, w), hashW, hash);
        }
        for (var y = 0; y < h; y += hashLen * 2)
        {
            Px.LineV(c, 0, y, Math.Min(y + hashLen, h), hashW, hash);
            Px.LineV(c, w - hashW, y, Math.Min(y + hashLen, h), hashW, hash);
        }

        if (p.Flag("star"))
            SiemensStar(in p, cx, cy, Math.Min(w, h) * 0.33f, 72);

        if (p.Flag("linePairs"))
        {
            var block = Math.Min(w, h) / 6;
            LinePairs(in p, 12, 12, block, vertical: true);
            LinePairs(in p, w - 12 - block, 12, block, vertical: false);
            LinePairs(in p, 12, h - 12 - block, block, vertical: false);
            LinePairs(in p, w - 12 - block, h - 12 - block, block, vertical: true);
        }

        if (p.Flag("text"))
        {
            var y = h * 0.16f;
            var x = w * 0.05f;
            ReadOnlySpan<int> sizes = [10, 14, 20, 28, 40];
            foreach (var size in sizes)
            {
                if (y > h * 0.8f)
                    break;
                var font = p.Kit.Regular;
                font.Size = size;
                Px.TextLeft(c, string.Create(CultureInfo.InvariantCulture, $"{size}px  The quick brown fox 0123456789"), x, y, font, p.Kit.Text(SKColors.White));
                y += size * 1.6f;
            }
        }
        Px.Chip(c, p.Kit, string.Create(CultureInfo.InvariantCulture, $"{w} × {h}"), w, h);
    }

    private static void SiemensStar(in PatternCanvas p, float cx, float cy, float radius, int spokes)
    {
        var path = p.Kit.Path;
        path.Reset();
        for (var i = 0; i < spokes; i += 2)
        {
            var a0 = (float)(i * 2 * Math.PI / spokes);
            var a1 = (float)((i + 1) * 2 * Math.PI / spokes);
            path.MoveTo(cx, cy);
            path.LineTo(cx + radius * MathF.Cos(a0), cy + radius * MathF.Sin(a0));
            path.LineTo(cx + radius * MathF.Cos(a1), cy + radius * MathF.Sin(a1));
            path.Close();
        }
        p.Canvas.DrawPath(path, p.Kit.FillAA(SKColors.White));
        p.Canvas.DrawCircle(cx, cy, radius * 0.02f + 2, p.Kit.FillAA(SKColors.Black));
        path.Reset();
    }

    /// <summary>Four sub-bands of 1, 2, 3 and 4 px on/off pairs.</summary>
    private static void LinePairs(in PatternCanvas p, int x, int y, int size, bool vertical)
    {
        var paint = p.Kit.Fill(SKColors.White);
        var band = size / 4;
        for (var b = 0; b < 4; b++)
        {
            var pitch = b + 1;
            var o0 = b * band;
            for (var i = 0; i < band; i += pitch * 2)
            {
                if (vertical)
                    Px.LineV(p.Canvas, x + o0 + i, y, y + size, pitch, paint);
                else
                    Px.LineH(p.Canvas, y + o0 + i, x, x + size, pitch, paint);
            }
        }
    }
}
