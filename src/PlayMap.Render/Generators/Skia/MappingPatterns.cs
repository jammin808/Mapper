using System.Globalization;
using SkiaSharp;

namespace PlayMap.Render.Generators.Skia;

/// <summary>Which lamp is which: the output's number huge in its colour, its name and raster under it, an
/// orientation mark at the top, and optionally the raster's frame in the colour and a coarse grid behind.
/// The same drawing, without its ground, is the identification card the engine lays over a live output.</summary>
internal sealed class IdentifyPattern : ISkiaPattern
{
    public void Draw(in PatternCanvas p) => DrawCard(p.Canvas, p.Kit, p.W, p.H, p.Context, p.Flag("grid"), p.Flag("frame"), ground: true);

    public static void DrawCard(SKCanvas c, SkiaKit kit, int w, int h, GeneratorContext ctx, bool grid, bool frame, bool ground)
    {
        var color = Px.Color(ctx.Color, Ink.Accent);
        c.Clear(ground ? SKColors.Black : SKColors.Transparent);
        var s = Math.Min(w, h);
        if (grid)
        {
            var line = kit.Fill(color.WithAlpha(0x50));
            var cell = Math.Max(8, s / 6);
            for (var x = (w / 2) % cell; x < w; x += cell)
                Px.LineV(c, x, 0, h, 1, line);
            for (var y = (h / 2) % cell; y < h; y += cell)
                Px.LineH(c, y, 0, w, 1, line);
        }
        if (frame)
            Px.BorderInside(c, new SKRectI(0, 0, w, h), Math.Max(2, s / 40), kit.Fill(color));
        if (!ground)
        {
            var card = SKRect.Create(w * 0.2f, h * 0.18f, w * 0.6f, h * 0.64f);
            c.DrawRoundRect(card, s * 0.03f, s * 0.03f, kit.FillAA(new SKColor(0, 0, 0, 0xC8)));
        }

        var big = kit.Bold;
        big.Size = Math.Max(10, s * 0.42f);
        var number = ctx.Number > 0 ? Px.Number(ctx.Number) : "?";
        Px.TextCentered(c, number, w / 2f, h * 0.44f, big, kit.Text(color));

        var name = kit.Bold;
        name.Size = Math.Max(8, s * 0.075f);
        Px.TextCentered(c, ctx.Name.Length > 0 ? ctx.Name.ToUpperInvariant() : "OUTPUT", w / 2f, h * 0.70f, name, kit.Text(SKColors.White));
        var sub = kit.Regular;
        sub.Size = Math.Max(7, s * 0.04f);
        Px.TextCentered(c, string.Create(CultureInfo.InvariantCulture, $"{w} × {h}"), w / 2f, h * 0.70f + name.Size * 1.2f, sub, kit.Text(color));

        // The top of the raster, so a projector hung upside down or turned says so at a glance.
        var arrow = Math.Max(6f, s * 0.04f);
        var path = kit.Path;
        path.Reset();
        path.MoveTo(w / 2f, h * 0.05f);
        path.LineTo(w / 2f + arrow, h * 0.05f + arrow * 1.2f);
        path.LineTo(w / 2f - arrow, h * 0.05f + arrow * 1.2f);
        path.Close();
        c.DrawPath(path, kit.FillAA(color));
        path.Reset();
        var top = kit.Regular;
        top.Size = Math.Max(7, arrow * 0.9f);
        Px.TextCentered(c, "TOP", w / 2f, h * 0.05f + arrow * 2.1f, top, kit.Text(color));
    }
}

/// <summary>Red, green and blue crosshatch on the same pixels: white where a panel's channels converge,
/// coloured fringes where they do not (and where a lens splits colour). Lines are symmetric about the centre
/// like the grid's, with a dot at every cell's centre.</summary>
internal sealed class ConvergencePattern : ISkiaPattern
{
    public void Draw(in PatternCanvas p)
    {
        var c = p.Canvas;
        c.Clear(SKColors.Black);
        int w = p.W, h = p.H;
        var spacing = Math.Max(8, p.Int("spacing"));
        var color = p.Choice("channels") switch
        {
            "red" => new SKColor(255, 0, 0),
            "green" => new SKColor(0, 255, 0),
            "blue" => new SKColor(0, 0, 255),
            _ => SKColors.White,
        };
        var line = p.Kit.Fill(color);
        int cx = w / 2, cy = h / 2;
        for (var x = cx % spacing; x < w; x += spacing)
            Px.LineV(c, x, 0, h, 1, line);
        for (var y = cy % spacing; y < h; y += spacing)
            Px.LineH(c, y, 0, w, 1, line);
        var dot = p.Kit.Fill(color);
        for (var x = cx % spacing + spacing / 2; x < w; x += spacing)
            for (var y = cy % spacing + spacing / 2; y < h; y += spacing)
                c.DrawRect(SKRect.Create(x - 1, y - 1, 3, 3), dot);
    }

    /// <summary>The colour a channel choice draws its lines in (white for all three).</summary>
    public static (byte R, byte G, byte B) LineColor(string channels) => channels switch
    {
        "red" => (255, 0, 0),
        "green" => (0, 255, 0),
        "blue" => (0, 0, 255),
        _ => (255, 255, 255),
    };
}

/// <summary>A UV checker: squares across the picture (rows follow its shape so the squares are square),
/// each tinted by its column's hue and labelled with its column letter and row number — "A1" top-left — so
/// how a surface's UVs land, turn and stretch reads straight off the object.</summary>
internal sealed class UvCheckerPattern : ISkiaPattern
{
    public void Draw(in PatternCanvas p)
    {
        var c = p.Canvas;
        int w = p.W, h = p.H;
        var cols = Math.Max(2, p.Int("divisions"));
        var rows = RowsFor(cols, w, h);
        var labels = p.Flag("labels");
        var font = p.Kit.Bold;
        for (var r = 0; r < rows; r++)
        {
            for (var col = 0; col < cols; col++)
            {
                var (x0, y0, x1, y1) = Cell(col, r, cols, rows, w, h);
                var (cr, cg, cb) = CellColor(col, r, cols);
                c.DrawRect(SKRect.Create(x0, y0, x1 - x0, y1 - y0), p.Kit.Fill(new SKColor(cr, cg, cb)));
                if (!labels || x1 - x0 < 14 || y1 - y0 < 10)
                    continue;
                font.Size = Math.Clamp(Math.Min(x1 - x0, y1 - y0) * 0.3f, 6, 120);
                var ink = (r + col) % 2 == 0 ? SKColors.Black : SKColors.White;
                Px.TextCentered(c, Label(col, r), (x0 + x1) / 2f, (y0 + y1) / 2f, font, p.Kit.Text(ink));
            }
        }
    }

    public static int RowsFor(int cols, int w, int h) => Math.Max(1, (int)Math.Round(cols * (double)h / Math.Max(1, w)));

    /// <summary>A cell's pixel rectangle [x0, x1) × [y0, y1).</summary>
    public static (int X0, int Y0, int X1, int Y1) Cell(int col, int row, int cols, int rows, int w, int h) =>
        ((int)Math.Round(w * (double)col / cols), (int)Math.Round(h * (double)row / rows),
         (int)Math.Round(w * (double)(col + 1) / cols), (int)Math.Round(h * (double)(row + 1) / rows));

    /// <summary>A cell's colour: its column's hue, light and dark alternating like a checker.</summary>
    public static (byte R, byte G, byte B) CellColor(int col, int row, int cols)
    {
        var light = (col + row) % 2 == 0;
        var color = SKColor.FromHsv(col * 360f / cols, light ? 45 : 70, light ? 95 : 45);
        return (color.Red, color.Green, color.Blue);
    }

    /// <summary>"A1", "B3", … "AA12": column letters, row numbers from one.</summary>
    public static string Label(int col, int row)
    {
        Span<char> letters = stackalloc char[4];
        var n = 0;
        var v = col;
        do
        {
            letters[n++] = (char)('A' + v % 26);
            v = v / 26 - 1;
        }
        while (v >= 0 && n < letters.Length);
        letters[..n].Reverse();
        return string.Concat(letters[..n], (row + 1).ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>The card a faulted generator shows until its settings change: what failed and how to retry,
/// in colours no content uses, so a fault never reads as a look.</summary>
internal static class ErrorCard
{
    public static void Draw(SKCanvas c, SkiaKit kit, int w, int h, string title, string detail)
    {
        c.Clear(new SKColor(0x20, 0x06, 0x0A));
        var s = Math.Min(w, h);
        Px.BorderInside(c, new SKRectI(0, 0, w, h), Math.Max(2, s / 60), kit.Fill(new SKColor(0xFF, 0x3B, 0x30)));
        var head = kit.Bold;
        head.Size = Math.Max(9, s * 0.08f);
        Px.TextCentered(c, "GENERATOR FAULT", w / 2f, h * 0.36f, head, kit.Text(new SKColor(0xFF, 0x3B, 0x30)));
        var name = kit.Bold;
        name.Size = Math.Max(8, s * 0.05f);
        Px.TextCentered(c, title, w / 2f, h * 0.50f, name, kit.Text(SKColors.White));
        var body = kit.Regular;
        body.Size = Math.Max(7, s * 0.035f);
        var text = detail.Length > 90 ? detail[..89] + "…" : detail;
        Px.TextCentered(c, text, w / 2f, h * 0.62f, body, kit.Text(new SKColor(0xD8, 0xB4, 0xB8)));
        Px.TextCentered(c, "Disabled until its settings change.", w / 2f, h * 0.72f, body, kit.Text(new SKColor(0xA8, 0x8A, 0x8E)));
    }
}
