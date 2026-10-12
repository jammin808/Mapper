using SkiaSharp;

namespace PlayMap.Render.Generators.Skia;

/// <summary>The rig card: which screen this is, whether the pixels sent are the pixels shown, where the edges
/// went, and what the chain has done to black, white and grey — one card, read from the ladder.
///
/// Every measurement is a fixed number in this file: the step values, the clipping patches, the gamma
/// solids and the one-to-one fields cannot be configured, so a reading taken in one venue can be quoted in
/// the next. Every structural line lands on whole pixels. The one-to-one fields and the gamma match only
/// mean anything when the card lands one-to-one on the raster; when it does not, the card says so on itself.</summary>
internal sealed class TestCardPattern : ISkiaPattern
{
    /// <summary>The staircase: eleven steps, 0–100 % of code value in tens.</summary>
    public static readonly byte[] Steps = [0, 26, 51, 77, 102, 128, 153, 179, 204, 230, 255];

    /// <summary>Just off black, on black: a crushed chain loses these from the bottom up.</summary>
    public static readonly byte[] NearBlack = [2, 4, 6, 8];

    /// <summary>Just off white, on white: the same test where clipping lives.</summary>
    public static readonly byte[] NearWhite = [253, 251, 249, 247];

    /// <summary>The gamma match: a one-pixel line field is half lit in linear light, so the solid beside it
    /// that matches is 255·0.5^(1/γ) — the label under the one that disappears at a distance is the gamma.</summary>
    public static readonly (byte Value, string Label)[] GammaSolids = [(177, "1.9"), (186, "2.2"), (195, "2.6")];

    /// <summary>75 % primaries and secondaries, full-range RGB.</summary>
    public const byte C75 = 191;

    public void Draw(in PatternCanvas p)
    {
        p.Canvas.Clear(Ink.Ground);
        switch (p.Choice("variant"))
        {
            case "pixel":
                RenderPixel(in p);
                break;
            case "levels":
                RenderLevels(in p);
                break;
            default:
                RenderRig(in p);
                break;
        }
    }

    /// <summary>The geometry of the rig card's bottom strip, shared with the tests that read its steps.</summary>
    public static (int X, int Y, int W, int H) RigStaircase(int w, int h)
    {
        var hair = 1;
        var unit = Math.Min(w, h) / 9f;
        var stripH = (int)MathF.Round(unit * 1.5f);
        var stripY = h - stripH - hair * 3;
        return (hair * 3, stripY, w - hair * 6, (int)(stripH * 0.62f));
    }

    private static void RenderRig(in PatternCanvas p)
    {
        int w = p.W, h = p.H;
        var hair = PatternCanvas.Hairline(1);
        var unit = Math.Min(w, h) / 9f;

        Frame(in p, hair);
        Rulers(in p, hair);

        var stripH = (int)MathF.Round(unit * 1.5f);
        var stripY = h - stripH - hair * 3;
        if (stripY > unit * 2)
        {
            Staircase(in p, hair * 3, stripY, w - hair * 6, (int)(stripH * 0.62f), labels: PatternCanvas.Resolves(unit * 0.22f, 6));
            var clipY = stripY + (int)(stripH * 0.62f);
            var clipH = stripH - (int)(stripH * 0.62f);
            ClipPatches(in p, hair * 3, clipY, w - hair * 6, clipH);
        }

        var pip = (int)MathF.Round(unit * 0.92f);
        if (PatternCanvas.Resolves(pip, 24))
        {
            var m = hair * 3 + (int)MathF.Round(unit * 0.42f);
            OneToOne(in p, m, m, pip, hair, 0);
            OneToOne(in p, w - m - pip, m, pip, hair, 1);
            OneToOne(in p, m, stripY - pip - hair * 2, pip, hair, 2);
            OneToOne(in p, w - m - pip, stripY - pip - hair * 2, pip, hair, 1);
        }

        var midY = (stripY > unit * 2 ? stripY : h) / 2f;
        Centre(in p, w / 2f, midY, unit, hair);
        NotMeasuringNote(in p, unit, hair);

        var gw = (int)MathF.Round(unit * 2.6f);
        var gh = (int)MathF.Round(unit * 0.78f);
        if (PatternCanvas.Resolves(gh, 14) && gw < w / 2 - unit)
            GammaMatch(in p, w / 2 - gw / 2, (int)MathF.Round(midY + unit * 2.1f), gw, gh, hair);

        Header(in p, unit, hair);
    }

    private static void RenderPixel(in PatternCanvas p)
    {
        int w = p.W, h = p.H;
        var hair = PatternCanvas.Hairline(1);
        var unit = Math.Min(w, h) / 9f;

        Frame(in p, hair);
        Rulers(in p, hair);

        var pip = (int)MathF.Round(unit * 2.0f);
        if (PatternCanvas.Resolves(pip, 24))
        {
            var m = hair * 3 + (int)MathF.Round(unit * 0.5f);
            OneToOne(in p, m, m, pip, hair, 0);
            OneToOne(in p, w - m - pip, m, pip, hair, 1);
            OneToOne(in p, m, h - m - pip, pip, hair, 2);
            OneToOne(in p, w - m - pip, h - m - pip, pip, hair, 0);
        }

        Centre(in p, w / 2f, h / 2f, unit * 1.25f, hair);
        NotMeasuringNote(in p, unit, hair);
        Header(in p, unit, hair);
    }

    /// <summary>The levels card's staircase rectangle, shared with the tests.</summary>
    public static (int X, int Y, int W, int H) LevelsStaircase(int w, int h)
    {
        var hair = 1;
        var unit = Math.Min(w, h) / 9f;
        var pad = hair * 4;
        var top = (int)MathF.Round(unit * 1.15f);
        var body = h - top - pad;
        return (pad, top, w - pad * 2, (int)(body * 0.34f));
    }

    private static void RenderLevels(in PatternCanvas p)
    {
        int w = p.W, h = p.H;
        var hair = PatternCanvas.Hairline(1);
        var unit = Math.Min(w, h) / 9f;

        Frame(in p, hair);
        var pad = hair * 4;
        var top = (int)MathF.Round(unit * 1.15f);
        var body = h - top - pad;

        var stairH = (int)(body * 0.34f);
        Staircase(in p, pad, top, w - pad * 2, stairH, labels: PatternCanvas.Resolves(unit * 0.22f, 6));

        var clipH = (int)(body * 0.22f);
        ClipPatches(in p, pad, top + stairH, w - pad * 2, clipH);

        var colourH = (int)(body * 0.20f);
        Primaries(in p, pad, top + stairH + clipH + hair * 2, w - pad * 2, colourH);

        var gammaY = top + stairH + clipH + colourH + hair * 4;
        var gammaH = h - pad - gammaY;
        if (gammaH > unit * 0.4f)
            GammaMatch(in p, pad, gammaY, w - pad * 2, gammaH, hair);

        Header(in p, unit, hair);
    }

    /// <summary>The outermost pixel and the third one in: both on all four sides means the signal arrives
    /// whole; one missing is overscan, and the ruler beside it says how much.</summary>
    private static void Frame(in PatternCanvas p, int hair)
    {
        var k = p.Kit;
        Px.BorderInside(p.Canvas, new SKRectI(0, 0, p.W, p.H), hair, k.Fill(Ink.Edge));
        var inset = hair * 3;
        if (p.W > inset * 4 && p.H > inset * 4)
            Px.BorderInside(p.Canvas, new SKRectI(inset, inset, p.W - inset, p.H - inset), hair, k.Fill(Ink.EdgeDim));
    }

    /// <summary>Ticks every ten pixels along the first hundred from each corner, the fiftieth long and the
    /// hundredth longer: the overscan in pixels, not in "a bit".</summary>
    private static void Rulers(in PatternCanvas p, int hair)
    {
        if (!PatternCanvas.Resolves(10, 3))
            return;
        var c = p.Canvas;
        int w = p.W, h = p.H;
        var reach = Math.Min(100, Math.Min(w, h) / 4);
        for (var x = 10; x <= reach; x += 10)
        {
            var len = x % 100 == 0 ? 22 : x % 50 == 0 ? 15 : 8;
            var paint = p.Kit.Fill(x % 50 == 0 ? Ink.Accent : Ink.Edge);
            Px.LineV(c, x, 0, len, hair, paint);
            Px.LineV(c, w - x - hair, 0, len, hair, paint);
            Px.LineV(c, x, h - len, h, hair, paint);
            Px.LineV(c, w - x - hair, h - len, h, hair, paint);
            Px.LineH(c, x, 0, len, hair, paint);
            Px.LineH(c, h - x - hair, 0, len, hair, paint);
            Px.LineH(c, x, w - len, w, hair, paint);
            Px.LineH(c, h - x - hair, w - len, w, hair, paint);
        }
    }

    /// <summary>A one-to-one field: vertical one-pixel lines (kind 0), horizontal (1) or a one-pixel checker
    /// (2), each exactly half lit. Through any scaler they crawl or go grey at different rates, and which one
    /// breaks says which way the picture was scaled. The border is cyan when the card lands one-to-one and
    /// amber when it cannot be measuring anything.</summary>
    private static void OneToOne(in PatternCanvas p, int x, int y, int size, int hair, int kind)
    {
        if (x < 0 || y < 0 || x + size > p.W || y + size > p.H)
            return;
        var c = p.Canvas;
        var k = p.Kit;
        c.DrawRect(SKRect.Create(x, y, size, size), k.Fill(SKColors.Black));
        var white = k.Fill(SKColors.White);
        var step = hair * 2;
        switch (kind)
        {
            case 0:
                for (var i = x; i + hair <= x + size; i += step)
                    c.DrawRect(SKRect.Create(i, y, hair, size), white);
                break;
            case 1:
                for (var i = y; i + hair <= y + size; i += step)
                    c.DrawRect(SKRect.Create(x, i, size, hair), white);
                break;
            default:
                // Both squares of each cell's diagonal are lit, so the field is half lit like the other two.
                for (var j = y; j + hair <= y + size; j += step)
                {
                    for (var i = x; i + hair <= x + size; i += step)
                    {
                        c.DrawRect(SKRect.Create(i, j, hair, hair), white);
                        if (i + step <= x + size && j + step <= y + size)
                            c.DrawRect(SKRect.Create(i + hair, j + hair, hair, hair), white);
                    }
                }
                break;
        }
        Px.BorderInside(c, new SKRectI(x, y, x + size, y + size), hair, k.Fill(p.Context.NativeRaster ? Ink.Accent : Ink.Warn));
    }

    private static void NotMeasuringNote(in PatternCanvas p, float unit, int hair)
    {
        if (p.Context.NativeRaster || !PatternCanvas.Resolves(unit * 0.2f, 9))
            return;
        var font = p.Kit.Regular;
        font.Size = Math.Clamp(unit * 0.2f, 7, 40);
        Px.TextCentered(p.Canvas, "SCALED — 1:1 AND GAMMA READ ONLY AT NATIVE SIZE", p.W / 2f, p.H - hair * 3 - font.Size * 0.6f,
            font, p.Kit.Text(Ink.Warn));
    }

    /// <summary>The eleven steps, each a whole number of pixels wide.</summary>
    private static void Staircase(in PatternCanvas p, int x, int y, int w, int h, bool labels)
    {
        if (w <= 0 || h <= 0)
            return;
        var c = p.Canvas;
        var n = Steps.Length;
        for (var i = 0; i < n; i++)
        {
            var x0 = x + (int)Math.Round(w * (double)i / n);
            var x1 = x + (int)Math.Round(w * (double)(i + 1) / n);
            var v = Steps[i];
            c.DrawRect(SKRect.Create(x0, y, x1 - x0, h), p.Kit.Fill(new SKColor(v, v, v)));
            if (!labels || x1 - x0 < 18)
                continue;
            var font = p.Kit.Regular;
            font.Size = Math.Clamp(h * 0.28f, 8, 26);
            Px.TextCentered(c, Px.Number(v), (x0 + x1) / 2f, y + h - font.Size * 0.5f, font, p.Kit.Text(v > 140 ? SKColors.Black : SKColors.White));
        }
    }

    /// <summary>Black on black and white on white: every patch you cannot see is a code value the chain threw away.</summary>
    private static void ClipPatches(in PatternCanvas p, int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0)
            return;
        var c = p.Canvas;
        var half = w / 2;
        c.DrawRect(SKRect.Create(x, y, half, h), p.Kit.Fill(SKColors.Black));
        c.DrawRect(SKRect.Create(x + half, y, w - half, h), p.Kit.Fill(SKColors.White));
        var font = p.Kit.Regular;
        font.Size = Math.Clamp(h * 0.36f, 7, 20);
        var show = PatternCanvas.Resolves(h * 0.36f, 6);
        Row(in p, NearBlack, x, y, half, h, SKColors.White, show, font);
        Row(in p, NearWhite, x + half, y, half, h, SKColors.Black, show, font);
    }

    private static void Row(in PatternCanvas p, byte[] values, int x0, int y, int half, int h, SKColor ink, bool show, SKFont font)
    {
        var n = values.Length;
        var pad = Math.Max(1, h / 6);
        for (var i = 0; i < n; i++)
        {
            var a = x0 + (int)Math.Round(half * (double)i / n) + pad;
            var b = x0 + (int)Math.Round(half * (double)(i + 1) / n) - pad;
            if (b - a < 2)
                continue;
            var v = values[i];
            p.Canvas.DrawRect(SKRect.Create(a, y + pad, b - a, h - pad * 2), p.Kit.Fill(new SKColor(v, v, v)));
            if (show && b - a > 22)
                Px.TextCentered(p.Canvas, Px.Number(v), (a + b) / 2f, y + h - pad - font.Size * 0.2f, font, p.Kit.Text(ink));
        }
    }

    private static void Primaries(in PatternCanvas p, int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0)
            return;
        Span<SKColor> bars =
        [
            SKColors.White, new SKColor(C75, C75, 0), new SKColor(0, C75, C75), new SKColor(0, C75, 0),
            new SKColor(C75, 0, C75), new SKColor(C75, 0, 0), new SKColor(0, 0, C75), SKColors.Black,
        ];
        for (var i = 0; i < 8; i++)
        {
            var x0 = x + (int)Math.Round(w * (double)i / 8);
            var x1 = x + (int)Math.Round(w * (double)(i + 1) / 8);
            p.Canvas.DrawRect(SKRect.Create(x0, y, x1 - x0, h), p.Kit.Fill(bars[i]));
        }
    }

    private static void GammaMatch(in PatternCanvas p, int x, int y, int w, int h, int hair)
    {
        if (w <= 0 || h <= 0)
            return;
        var c = p.Canvas;
        var cells = GammaSolids.Length + 1;
        var cw = w / cells;
        if (cw < 8)
            return;
        c.DrawRect(SKRect.Create(x, y, cw, h), p.Kit.Fill(SKColors.Black));
        var white = p.Kit.Fill(SKColors.White);
        for (var i = y; i + hair <= y + h; i += hair * 2)
            c.DrawRect(SKRect.Create(x, i, cw, hair), white);
        var font = p.Kit.Regular;
        font.Size = Math.Clamp(h * 0.3f, 7, 22);
        var label = PatternCanvas.Resolves(h * 0.3f, 6);
        for (var i = 0; i < GammaSolids.Length; i++)
        {
            var (v, text) = GammaSolids[i];
            var x0 = x + cw * (i + 1);
            c.DrawRect(SKRect.Create(x0, y, cw, h), p.Kit.Fill(new SKColor(v, v, v)));
            if (label && cw > 20)
                Px.TextCentered(c, text, x0 + cw / 2f, y + h - font.Size * 0.25f, font, p.Kit.Text(SKColors.Black));
        }
        Px.BorderInside(c, new SKRectI(x, y, x + cw * cells, y + h), hair, p.Kit.Fill(Ink.EdgeDim));
    }

    /// <summary>The middle: a circle that is only round when the pixel aspect is, a cross on the exact middle
    /// pixel, and the mark drawn into the card.</summary>
    private static void Centre(in PatternCanvas p, float cx, float cy, float unit, int hair)
    {
        var c = p.Canvas;
        var r = unit * 1.35f;
        if (r * 2 >= Math.Min(p.W, p.H))
            r = Math.Min(p.W, p.H) * 0.34f;
        c.DrawCircle(cx, cy, r, p.Kit.StrokeAA(Ink.Edge, hair));
        c.DrawCircle(cx, cy, r * 0.62f, p.Kit.StrokeAA(Ink.EdgeDim, hair));
        Px.Cross(c, (int)MathF.Round(cx), (int)MathF.Round(cy), (int)(r * 0.28f), hair, p.Kit.Fill(Ink.Accent));
        if (!p.Flag("mark") || !PatternCanvas.Resolves(unit * 0.5f, 10))
            return;
        var icon = r * 0.46f;
        Mark.Icon(c, p.Kit, SKRect.Create(cx - icon / 2, cy - icon * 0.86f, icon, icon));
        var font = p.Kit.Bold;
        font.Size = Math.Clamp(r * 0.26f, 7, 200);
        var ruleW = Mark.WordmarkCentered(c, cx, cy + r * 0.32f, font, p.Kit.Text(SKColors.White));
        var ruleH = Math.Max(1f, r * 0.022f);
        c.DrawRoundRect(SKRect.Create(cx - ruleW / 2, cy + r * 0.42f, ruleW, ruleH), ruleH / 2, ruleH / 2, p.Kit.FillAA(Ink.Secondary));
    }

    /// <summary>Which screen this is, in pixels, with the operator's own words beside it.</summary>
    private static void Header(in PatternCanvas p, float unit, int hair)
    {
        if (!p.Flag("identity") || !PatternCanvas.Resolves(unit * 0.3f, 9))
            return;
        var c = p.Canvas;
        var font = p.Kit.Bold;
        font.Size = Math.Clamp(unit * 0.42f, 9, 160);
        var name = p.Context.Name.Length > 0 ? p.Context.Name.ToUpperInvariant() : "PLAYMAP";
        Px.TextCentered(c, name, p.W / 2f, hair * 4 + font.Size, font, p.Kit.Text(Ink.Edge));
        var sub = p.Kit.Regular;
        sub.Size = Math.Clamp(unit * 0.24f, 7, 80);
        var size = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{p.W} × {p.H}");
        var label = p.Text("label");
        var words = label.Length > 0 ? $"{size}  ·  {label}" : size;
        Px.TextCentered(c, words, p.W / 2f, hair * 4 + font.Size + sub.Size * 1.5f, sub, p.Kit.Text(Ink.Accent));
    }
}

/// <summary>PlayMap's mark, drawn from primitives (a file that did not travel can never be what is missing):
/// a dark tile with a light grid and a cyan play triangle, over the letter-spaced wordmark.</summary>
internal static class Mark
{
    private static readonly SKColor Tile = new(0x17, 0x1A, 0x21);
    private static readonly SKColor Mist = new(0xB8, 0xC0, 0xCC);
    private static readonly string[] Letters = ["P", "L", "A", "Y", "M", "A", "P"];

    public static void Icon(SKCanvas c, SkiaKit kit, SKRect r)
    {
        var s = r.Width;
        if (s < 2)
            return;
        var radius = s * 0.2f;
        c.DrawRoundRect(r, radius, radius, kit.FillAA(Tile));
        var grid = kit.StrokeAA(Mist.WithAlpha(0xB0), Math.Max(1f, s * 0.04f));
        for (var i = 1; i < 3; i++)
        {
            var d = s * i / 3f;
            c.DrawLine(r.Left + d, r.Top + s * 0.12f, r.Left + d, r.Bottom - s * 0.12f, grid);
            c.DrawLine(r.Left + s * 0.12f, r.Top + d, r.Right - s * 0.12f, r.Top + d, grid);
        }
        var path = kit.Path;
        path.Reset();
        path.MoveTo(r.Left + s * 0.36f, r.Top + s * 0.28f);
        path.LineTo(r.Left + s * 0.76f, r.MidY);
        path.LineTo(r.Left + s * 0.36f, r.Bottom - s * 0.28f);
        path.Close();
        c.DrawPath(path, kit.FillAA(Ink.Accent));
        path.Reset();
    }

    /// <summary>The wordmark centred on a point; returns its width.</summary>
    public static float WordmarkCentered(SKCanvas c, float cx, float baseline, SKFont font, SKPaint paint)
    {
        var spacing = font.Size * 0.14f;
        float width = 0;
        foreach (var letter in Letters)
            width += font.MeasureText(letter) + spacing;
        width -= spacing;
        var x = cx - width / 2;
        foreach (var letter in Letters)
        {
            c.DrawText(letter, x, baseline, SKTextAlign.Left, font, paint);
            x += font.MeasureText(letter) + spacing;
        }
        return width;
    }
}
