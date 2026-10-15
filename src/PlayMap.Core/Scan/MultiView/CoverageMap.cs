namespace PlayMap.Core.Scan.MultiView;

/// <summary>
/// How many views decoded each cell of one projector's raster (cells of <see cref="Cell"/> pixels, row-major): 0, 1, or
/// 2 for two or more. <see cref="Lit"/> is the raster the fractions are of — the scanned region by default, or a mask
/// the caller knows better (where the light lands on something). <see cref="Seen"/> is the lit share decoded at least
/// once and <see cref="Twice"/> at least twice: "aim at the red" is the lit cells still at 0.
/// </summary>
public sealed record CoverageMap(string ProjectorId, int Width, int Height, int Cell, byte[] Views, bool[] Lit)
{
    public int LitCells => Lit.Count(x => x);

    public int SeenCells => Count(1);

    public int TwiceCells => Count(2);

    public double Seen => LitCells > 0 ? (double)SeenCells / LitCells : 0;

    public double Twice => LitCells > 0 ? (double)TwiceCells / LitCells : 0;

    private int Count(int atLeast)
    {
        var n = 0;
        for (var c = 0; c < Views.Length; c++)
            if (Lit[c] && Views[c] >= atLeast)
                n++;
        return n;
    }

    /// <summary>Counts, for each cell, the views whose decode placed a confident camera pixel in it.</summary>
    public static CoverageMap Build(string projectorId, IReadOnlyList<CorrespondenceMap> views, int cell = 4, bool[]? lit = null,
        float minConfidence = 0.3f)
    {
        ArgumentNullException.ThrowIfNull(views);
        ArgumentOutOfRangeException.ThrowIfLessThan(cell, 1);
        if (views.Count == 0)
            throw new ArgumentException("Coverage needs at least one view.", nameof(views));
        var pw = views[0].ProjectorWidth;
        var ph = views[0].ProjectorHeight;
        if (views.Any(v => v.ProjectorWidth != pw || v.ProjectorHeight != ph))
            throw new ArgumentException("Every view must decode the same projector raster.", nameof(views));
        var w = (pw + cell - 1) / cell;
        var h = (ph + cell - 1) / cell;
        if (lit is not null && lit.Length != w * h)
            throw new ArgumentException("The lit mask is not the cell grid's size.", nameof(lit));
        lit ??= Region(views[0].Region, w, h, cell);
        var counts = new byte[w * h];
        var mark = new bool[w * h];
        foreach (var map in views)
        {
            Array.Clear(mark);
            for (var k = 0; k < map.Flags.Length; k++)
            {
                if ((map.Flags[k] & DecodeStatus.Valid) == 0 || map.Confidence[k] < minConfidence)
                    continue;
                var x = map.ProjectorX[k];
                var y = map.ProjectorY[k];
                if (!(x >= 0) || !(y >= 0) || x >= pw || y >= ph)
                    continue;
                mark[(int)y / cell * w + (int)x / cell] = true;
            }
            for (var c = 0; c < mark.Length; c++)
                if (mark[c] && counts[c] < 2)
                    counts[c]++;
        }
        return new CoverageMap(projectorId, w, h, cell, counts, lit);
    }

    /// <summary>The share of every map's lit cells seen at least once, pooled over projectors.</summary>
    public static double SeenOf(IEnumerable<CoverageMap> maps)
    {
        ArgumentNullException.ThrowIfNull(maps);
        var list = maps.ToList();
        var lit = list.Sum(m => m.LitCells);
        return lit > 0 ? (double)list.Sum(m => m.SeenCells) / lit : 0;
    }

    private static bool[] Region(PixelRect region, int w, int h, int cell)
    {
        var lit = new bool[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var cx = (x + 0.5) * cell;
                var cy = (y + 0.5) * cell;
                lit[y * w + x] = cx >= region.X && cy >= region.Y && cx < region.Right && cy < region.Bottom;
            }
        }
        return lit;
    }
}
