using System.Globalization;
using PlayMap.Core.Model;
using SkiaSharp;

namespace PlayMap.Render.Generators.Skia;

/// <summary>An LED wall's tiles over the picture's raster — partial tiles at the right and bottom edges, as
/// a real wall has — with borders, numbers in data-run order and a faint alternating tint.</summary>
internal sealed class LedWallPattern : ISkiaPattern
{
    private static readonly SKColor TintA = new(0x3E, 0xC1, 0xF3, 0x1E);
    private static readonly SKColor TintB = new(0xF0, 0x3E, 0xAE, 0x1E);

    public void Draw(in PatternCanvas p)
    {
        var c = p.Canvas;
        int w = p.W, h = p.H;
        var tw = Math.Max(8, p.Int("tileWidth"));
        var th = Math.Max(8, p.Int("tileHeight"));
        var cols = (w + tw - 1) / tw;
        var rows = (h + th - 1) / th;
        var numbering = p.Choice("numbering");
        c.Clear(SKColors.Black);

        for (var r = 0; r < rows; r++)
            for (var col = 0; col < cols; col++)
                c.DrawRect(Tile(col, r, tw, th, w, h), p.Kit.Fill(((r + col) & 1) == 0 ? TintA : TintB));

        var hair = PatternCanvas.Hairline(1);
        if (p.Flag("diagonals"))
        {
            var diag = p.Kit.StrokeAA(new SKColor(0xFF, 0xFF, 0xFF, 0x50), hair);
            for (var r = 0; r < rows; r++)
            {
                for (var col = 0; col < cols; col++)
                {
                    var rect = Tile(col, r, tw, th, w, h);
                    c.DrawLine(rect.Left, rect.Top, rect.Right, rect.Bottom, diag);
                    c.DrawLine(rect.Right, rect.Top, rect.Left, rect.Bottom, diag);
                }
            }
        }

        if (p.Flag("borders"))
        {
            // Tile borders as one grid of lines; the outermost lines sit on the picture's edge pixels.
            var line = p.Kit.Fill(SKColors.White);
            for (var col = 0; col <= cols; col++)
                Px.LineV(c, Math.Min(col * tw, w - hair), 0, h, hair, line);
            for (var r = 0; r <= rows; r++)
                Px.LineH(c, Math.Min(r * th, h - hair), 0, w, hair, line);
        }

        if (tw >= 24 && th >= 16)
        {
            var font = p.Kit.Bold;
            font.Size = Math.Clamp(Math.Min(tw, th) * 0.3f, 7, 64);
            for (var r = 0; r < rows; r++)
            {
                for (var col = 0; col < cols; col++)
                {
                    var rect = Tile(col, r, tw, th, w, h);
                    if (rect.Width < 20 || rect.Height < 12)
                        continue;
                    var label = TileLabel(numbering, cols, rows, col, r);
                    Px.TextCentered(c, label, rect.MidX + 1, rect.MidY + 1, font, p.Kit.Text(SKColors.Black));
                    Px.TextCentered(c, label, rect.MidX, rect.MidY, font, p.Kit.Text(SKColors.White));
                }
            }
        }

        Px.Cross(c, w / 2, h / 2, Math.Min(w, h) / 6, 3, p.Kit.Fill(Ink.Accent));
        var partial = cols * tw != w || rows * th != h;
        Px.Chip(c, p.Kit, string.Create(CultureInfo.InvariantCulture,
            $"{cols} × {rows} tiles · {tw}×{th} px · {w}×{h}{(partial ? " · partial edge tiles" : "")}"), w, h);
    }

    private static SKRect Tile(int col, int row, int tw, int th, int w, int h) =>
        new(col * tw, row * th, Math.Min((col + 1) * tw, w), Math.Min((row + 1) * th, h));

    /// <summary>A tile's label: row-col, linear along the rows, or column-major serpentine (data runs down
    /// the first column, up the next).</summary>
    public static string TileLabel(string numbering, int cols, int rows, int col, int row) => numbering switch
    {
        "linear" => (row * cols + col + 1).ToString(CultureInfo.InvariantCulture),
        "serpentine" => (col * rows + ((col & 1) == 0 ? row : rows - 1 - row) + 1).ToString(CultureInfo.InvariantCulture),
        _ => string.Create(CultureInfo.InvariantCulture, $"{row + 1}-{col + 1}"),
    };
}

/// <summary>A wall of displays: elements across the picture with their numbers, centres, diagonals and the
/// bezel's loss zone hatched inside each edge.</summary>
internal sealed class VideoWallPattern : ISkiaPattern
{
    public void Draw(in PatternCanvas p)
    {
        var c = p.Canvas;
        int w = p.W, h = p.H;
        var cols = Math.Max(1, p.Int("columns"));
        var rows = Math.Max(1, p.Int("rows"));
        var bezel = Math.Max(0, p.Int("bezel"));
        var ew = Math.Max(1, w / cols);
        var eh = Math.Max(1, h / rows);
        c.Clear(SKColors.Black);
        var num = 1;
        for (var r = 0; r < rows; r++)
        {
            for (var col = 0; col < cols; col++, num++)
            {
                var rect = new SKRectI(col * ew, r * eh, (col + 1) * ew, (r + 1) * eh);
                var diag = p.Kit.StrokeAA(new SKColor(0xFF, 0xFF, 0xFF, 0x46), 1);
                c.DrawLine(rect.Left, rect.Top, rect.Right, rect.Bottom, diag);
                c.DrawLine(rect.Right, rect.Top, rect.Left, rect.Bottom, diag);
                c.DrawCircle(rect.MidX, rect.MidY, Math.Min(ew, eh) * 0.3f, p.Kit.StrokeAA(SKColors.White, 2));
                Px.Cross(c, rect.MidX, rect.MidY, Math.Min(ew, eh) / 10, 1, p.Kit.Fill(SKColors.White));
                if (bezel > 0)
                {
                    var hatch = p.Kit.StrokeAA(new SKColor(0xFF, 0xB0, 0x20, 0x66), 1);
                    var b = Math.Min(bezel, Math.Min(ew, eh) / 4);
                    Px.Hatch(c, new SKRect(rect.Left, rect.Top, rect.Right, rect.Top + b), 8, hatch);
                    Px.Hatch(c, new SKRect(rect.Left, rect.Bottom - b, rect.Right, rect.Bottom), 8, hatch);
                    Px.Hatch(c, new SKRect(rect.Left, rect.Top + b, rect.Left + b, rect.Bottom - b), 8, hatch);
                    Px.Hatch(c, new SKRect(rect.Right - b, rect.Top + b, rect.Right, rect.Bottom - b), 8, hatch);
                }
                Px.BorderInside(c, rect, 2, p.Kit.Fill(SKColors.White));
                if (p.Flag("numbers"))
                {
                    var font = p.Kit.Bold;
                    font.Size = Math.Clamp(Math.Min(ew, eh) * 0.28f, 10, 300);
                    var label = Px.Number(num);
                    Px.TextCentered(c, label, rect.MidX + 2, rect.MidY + 2, font, p.Kit.Text(SKColors.Black));
                    Px.TextCentered(c, label, rect.MidX, rect.MidY, font, p.Kit.Text(Ink.Accent));
                    var sub = p.Kit.Regular;
                    sub.Size = Math.Clamp(Math.Min(ew, eh) * 0.06f, 8, 40);
                    Px.TextCentered(c, string.Create(CultureInfo.InvariantCulture, $"{ew}×{eh}"), rect.MidX, rect.MidY + font.Size * 0.72f, sub, p.Kit.Text(SKColors.White));
                }
            }
        }
        Px.Cross(c, w / 2, h / 2, Math.Min(w, h) / 10, 3, p.Kit.Fill(Ink.Accent));
        var bezelWords = bezel > 0 ? string.Create(CultureInfo.InvariantCulture, $" · bezel {bezel}px") : "";
        Px.Chip(c, p.Kit, string.Create(CultureInfo.InvariantCulture, $"{cols} × {rows} displays · {ew}×{eh}{bezelWords} · {w}×{h}"), w, h);
    }
}

/// <summary>The edge-blend setup card: a continuous alignment grid, hue-coded projector frames, hatched
/// overlaps with their edges and centre lines, and the blend curve as opposing fade ramps — along the row
/// and, for a grid of rows, across it. The grey check fills flat 50 % to expose a doubled join. The native
/// raster each projector covers is derived from the picture: N·native − (N − 1)·overlap = the picture.</summary>
internal sealed class BlendPattern : ISkiaPattern
{
    public void Draw(in PatternCanvas p)
    {
        var c = p.Canvas;
        int w = p.W, h = p.H;
        var n = Math.Max(2, p.Int("projectors"));
        var rows = Math.Max(1, p.Int("rows"));
        var (nativeW, overlap) = Native(w, n, p.Int("overlap"));
        var (nativeH, overlapAcross) = rows > 1 ? Native(h, rows, p.Int("overlap")) : (h, 0);
        var curve = CurveOf(p.Choice("curve"));
        var count = n * rows;

        if (p.Flag("grayCheck"))
        {
            c.Clear(new SKColor(128, 128, 128));
            var tick = p.Kit.Fill(SKColors.Black);
            for (var i = 1; i < n; i++)
            {
                Tick(c, tick, i * (nativeW - overlap), true, w, h);
                Tick(c, tick, (i - 1) * (nativeW - overlap) + nativeW - 1, true, w, h);
            }
            for (var j = 1; j < rows; j++)
            {
                Tick(c, tick, j * (nativeH - overlapAcross), false, w, h);
                Tick(c, tick, (j - 1) * (nativeH - overlapAcross) + nativeH - 1, false, w, h);
            }
            return;
        }

        c.Clear(SKColors.Black);
        var grid = p.Kit.Fill(new SKColor(0xFF, 0xFF, 0xFF, 0x64));
        const int cell = 96;
        for (var x = (w / 2) % cell; x < w; x += cell)
            Px.LineV(c, x, 0, h, 1, grid);
        for (var y = (h / 2) % cell; y < h; y += cell)
            Px.LineH(c, y, 0, w, 1, grid);

        var hue = p.Flag("hue");
        for (var j = 0; j < rows; j++)
        {
            for (var i = 0; i < n; i++)
            {
                var number = j * n + i + 1;
                var color = hue ? Px.Hue(number - 1, count) : Ink.Accent;
                var x0 = i * (nativeW - overlap);
                var y0 = j * (nativeH - overlapAcross);
                var rect = SKRect.Create(x0 + 1.5f, y0 + 1.5f, nativeW - 3, nativeH - 3);
                c.DrawRect(rect, p.Kit.StrokeAA(color, 3, p.Kit.DashLong));
                var font = p.Kit.Bold;
                font.Size = Math.Clamp(Math.Min(rect.Width, rect.Height) * 0.08f, 14, 90);
                var label = "P" + Px.Number(number);
                Px.TextCentered(c, label, rect.MidX + 2, rect.Top + rect.Height * 0.14f + 2, font, p.Kit.Text(SKColors.Black));
                Px.TextCentered(c, label, rect.MidX, rect.Top + rect.Height * 0.14f, font, p.Kit.Text(color));
            }
        }

        for (var i = 1; i < n; i++)
            Zone(in p, i * (nativeW - overlap), (i - 1) * (nativeW - overlap) + nativeW, true, curve);
        for (var j = 1; j < rows; j++)
            Zone(in p, j * (nativeH - overlapAcross), (j - 1) * (nativeH - overlapAcross) + nativeH, false, curve);

        var rig = rows > 1 ? string.Create(CultureInfo.InvariantCulture, $"{n}×{rows}") : Px.Number(n);
        Px.Chip(c, p.Kit, string.Create(CultureInfo.InvariantCulture,
            $"{rig} × {nativeW}×{nativeH} · overlap {overlap}px · {curve} · canvas {w}×{h}"), w, h, top: true);
    }

    /// <summary>The native span of each of <paramref name="count"/> projectors whose overlapping rasters make
    /// up <paramref name="total"/> pixels, and the overlap clamped to leave each at least 8 px of its own.</summary>
    public static (int Native, int Overlap) Native(int total, int count, int overlap)
    {
        overlap = Math.Max(0, overlap);
        var native = (total + (count - 1) * overlap) / count;
        if (overlap > native - 8)
        {
            overlap = Math.Max(0, Math.Min(overlap, (total - 8 * count) / Math.Max(1, count - 1)));
            native = (total + (count - 1) * overlap) / count;
        }
        return (native, overlap);
    }

    public static BlendCurve CurveOf(string choice) => choice switch
    {
        "linear" => BlendCurve.Linear,
        "scurve" => BlendCurve.SCurve,
        "gamma22" => BlendCurve.Gamma22,
        _ => BlendCurve.Cosine,
    };

    /// <summary>Fade-in weight (0→1) across a blend zone: monotonic, 0 ↦ 0, 1 ↦ 1.</summary>
    public static double Curve(BlendCurve curve, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return curve switch
        {
            BlendCurve.Cosine => 0.5 - 0.5 * Math.Cos(Math.PI * t),
            BlendCurve.SCurve => t * t * (3 - 2 * t),
            BlendCurve.Gamma22 => Math.Pow(t, 2.2),
            _ => t,
        };
    }

    private static void Zone(in PatternCanvas p, int z0, int z1, bool horizontal, BlendCurve curve)
    {
        var c = p.Canvas;
        int w = p.W, h = p.H;
        var zone = horizontal ? SKRect.Create(z0, 0, z1 - z0, h) : SKRect.Create(0, z0, w, z1 - z0);
        Px.Hatch(c, zone, 24, p.Kit.StrokeAA(new SKColor(0xFF, 0xFF, 0xFF, 0x2A), 1));
        var edge = p.Kit.Fill(Ink.Accent);
        var mid = (z0 + z1) / 2;
        if (horizontal)
        {
            Px.LineV(c, z0, 0, h, 1, edge);
            Px.LineV(c, z1 - 1, 0, h, 1, edge);
            c.DrawLine(mid + 0.5f, 0, mid + 0.5f, h, p.Kit.StrokeAA(Ink.Secondary, 1, p.Kit.DashShort));
        }
        else
        {
            Px.LineH(c, z0, 0, w, 1, edge);
            Px.LineH(c, z1 - 1, 0, w, 1, edge);
            c.DrawLine(0, mid + 0.5f, w, mid + 0.5f, p.Kit.StrokeAA(Ink.Secondary, 1, p.Kit.DashShort));
        }

        var zw = z1 - z0;
        if (zw > 2)
        {
            if (horizontal)
            {
                var stripH = (int)Math.Clamp(h * 0.1f, 8, 160);
                var yOut = (int)(h * 0.3f) - stripH;
                var yIn = (int)(h * 0.7f);
                for (var x = 0; x < zw; x++)
                {
                    var t = (double)x / (zw - 1);
                    var vIn = (byte)Math.Round(255 * Curve(curve, t));
                    var vOut = (byte)Math.Round(255 * Curve(curve, 1 - t));
                    Px.LineV(c, z0 + x, yOut, yOut + stripH, 1, p.Kit.Fill(new SKColor(vOut, vOut, vOut)));
                    Px.LineV(c, z0 + x, yIn, yIn + stripH, 1, p.Kit.Fill(new SKColor(vIn, vIn, vIn)));
                }
            }
            else
            {
                var stripW = (int)Math.Clamp(w * 0.1f, 8, 160);
                var xOut = (int)(w * 0.3f) - stripW;
                var xIn = (int)(w * 0.7f);
                for (var y = 0; y < zw; y++)
                {
                    var t = (double)y / (zw - 1);
                    var vIn = (byte)Math.Round(255 * Curve(curve, t));
                    var vOut = (byte)Math.Round(255 * Curve(curve, 1 - t));
                    Px.LineH(c, z0 + y, xOut, xOut + stripW, 1, p.Kit.Fill(new SKColor(vOut, vOut, vOut)));
                    Px.LineH(c, z0 + y, xIn, xIn + stripW, 1, p.Kit.Fill(new SKColor(vIn, vIn, vIn)));
                }
            }
        }

        var font = p.Kit.Regular;
        font.Size = Math.Clamp(Math.Min(w, h) * 0.022f, 10, 30);
        var label = "OVERLAP " + Px.Number(zw) + "px";
        var lx = horizontal ? (z0 + z1) / 2f : w / 2f;
        var ly = horizontal ? h * 0.94f : (z0 + z1) / 2f;
        Px.TextCentered(c, label, lx + 1, ly + 1, font, p.Kit.Text(SKColors.Black));
        Px.TextCentered(c, label, lx, ly, font, p.Kit.Text(SKColors.White));
    }

    private static void Tick(SKCanvas c, SKPaint paint, int pos, bool horizontal, int w, int h)
    {
        if (horizontal)
        {
            Px.LineV(c, pos, 0, (int)(h * 0.03f), 1, paint);
            Px.LineV(c, pos, h - (int)(h * 0.03f), h, 1, paint);
        }
        else
        {
            Px.LineH(c, pos, 0, (int)(w * 0.03f), 1, paint);
            Px.LineH(c, pos, w - (int)(w * 0.03f), w, 1, paint);
        }
    }
}
