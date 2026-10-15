using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Core.Calibration;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;

namespace PlayMap.Core.Phone;

/// <summary>What a locate run found: the sites chosen (projector raster) and where they land (camera picture), the camera pixels
/// the tags cover (<see cref="Shadow"/>: excluded from the decode), the lit part of the picture, how many candidate sites were
/// whole and in words.</summary>
public sealed record CellLocateResult(
    ImmutableArray<TagSite> Sites,
    ImmutableArray<TagSitePlacement> Chosen,
    bool[] Shadow,
    PixelRect Lit,
    int Found,
    string Words)
{
    /// <summary>The fewest sites a picture's tag needs: two where two exist (so a rolling-shutter mix disagrees), else one.</summary>
    public int MinSites => Math.Min(2, Chosen.Length);

    /// <summary>Two chosen sites at least 30 % of the lit rows apart: a picture that straddles two patterns is caught.</summary>
    public bool RollingCovered { get; init; }
}

/// <summary>
/// Finds where a projector's candidate tag sites land in the camera. The candidates are 4 × 4 cells of
/// c = max(6, round(W/80)) projector pixels, on an evenly spread grid (8 × 5 where they fit) inside a 4 % inset; each cell has an
/// id (site × 16 + cell + 1). Twelve frames name every cell: L0 all cells lit, L1 all dark, L2…L11 a cell lit when bit j−2 of
/// Gray(id) is set (the background stays at one level, mid-grey for a camera that holds its own exposure). Per camera pixel the
/// contrast L0 − L1 must reach 12 levels and each bit must clear the midpoint by a fifth of it, else the pixel names no cell.
/// A cell is found when its pixels (gathered again round their centroid) cover at least 60 % of its site's median and are
/// compact; a site counts when all 16 cells are found and a homography through their centroids fits within 0.5 px (the surface is
/// locally smooth). The sites chosen: the topmost, the bottommost, then the farthest from those, up to three.
/// </summary>
public static class CellLocate
{
    public const int Frames = 12;

    public const int MinPixelContrast = 12;

    public static int CellSize(int width) => Math.Max(6, (int)Math.Round(width / 80.0, MidpointRounding.AwayFromZero));

    /// <summary>The candidate sites of a projector raster, row-major.</summary>
    public static IReadOnlyList<TagSite> Candidates(int width, int height, int cols = 8, int rows = 5)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 32);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 32);
        var c = CellSize(width);
        var s = 4 * c;
        var insetX = (int)Math.Ceiling(width * 0.04);
        var insetY = (int)Math.Ceiling(height * 0.04);
        var availW = width - 2 * insetX;
        var availH = height - 2 * insetY;
        cols = Fit(cols, availW, s, c);
        rows = Fit(rows, availH, s, c);
        var sites = new List<TagSite>(cols * rows);
        for (var r = 0; r < rows; r++)
        {
            for (var k = 0; k < cols; k++)
            {
                var x = insetX + (cols == 1 ? (availW - s) / 2 : (int)Math.Round(k * (availW - s) / (double)(cols - 1)));
                var y = insetY + (rows == 1 ? (availH - s) / 2 : (int)Math.Round(r * (availH - s) / (double)(rows - 1)));
                sites.Add(new TagSite(sites.Count, new PixelRect(x, y, s, s), c));
            }
        }
        return sites;
    }

    /// <summary>As many sites along an axis as leave at least a cell between neighbours.</summary>
    private static int Fit(int want, int avail, int size, int cell)
    {
        var n = Math.Max(1, want);
        while (n > 1 && (avail - size) / (double)(n - 1) < size + cell)
            n--;
        return avail >= size ? n : 0;
    }

    public static int CellId(int site, int cell) => site * FrameTag.Cells + cell + 1;

    /// <summary>Locate frame <paramref name="j"/> (0–11) into a projector raster.</summary>
    public static void RenderFrame(int j, Span<byte> raster, int width, int height, IReadOnlyList<TagSite> sites, byte background,
        byte high = 255, byte low = 0)
    {
        ArgumentNullException.ThrowIfNull(sites);
        ArgumentOutOfRangeException.ThrowIfNegative(j);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(j, Frames);
        raster[..(width * height)].Fill(background);
        foreach (var site in sites)
        {
            for (var c = 0; c < FrameTag.Cells; c++)
            {
                var lit = j switch
                {
                    0 => true,
                    1 => false,
                    _ => ((GrayCode.Encode(CellId(site.Id, c)) >> (j - 2)) & 1) != 0,
                };
                TagOverlay.Fill(raster, width, site.CellRect(c), lit ? high : low);
            }
        }
    }

    private sealed class CellStats
    {
        public double N;
        public double Sx;
        public double Sy;
        public double Sxx;
        public double Syy;
        public double Sxy;

        public Vec2d Centre => new(Sx / N, Sy / N);

        public void Add(double x, double y)
        {
            N++;
            Sx += x;
            Sy += y;
            Sxx += x * x;
            Syy += y * y;
            Sxy += x * y;
        }

        /// <summary>The ratio of the longer to the shorter axis of the pixels' spread.</summary>
        public double Elongation
        {
            get
            {
                var mx = Sx / N;
                var my = Sy / N;
                var a = Sxx / N - mx * mx;
                var b = Sxy / N - mx * my;
                var d = Syy / N - my * my;
                var tr = a + d;
                var disc = Math.Sqrt(Math.Max(0, (a - d) * (a - d) / 4 + b * b));
                var l1 = tr / 2 + disc;
                var l2 = Math.Max(tr / 2 - disc, 1e-9);
                return Math.Sqrt(l1 / l2);
            }
        }
    }

    /// <summary>Decodes the twelve locate pictures (gray8, camera raster) against the candidates and chooses up to
    /// <paramref name="maxSites"/> sites.</summary>
    public static CellLocateResult Decode(int width, int height, IReadOnlyList<byte[]> frames, IReadOnlyList<TagSite> sites, int maxSites = 3,
        double maxFitRmsPx = 0.5)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(sites);
        if (frames.Count != Frames)
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"a locate run is {Frames} pictures, not {frames.Count}"), nameof(frames));
        var n = width * height;
        foreach (var f in frames)
        {
            if (f is null || f.Length < n)
                throw new ArgumentException("a locate picture is smaller than the camera's raster", nameof(frames));
        }
        var maxId = sites.Count * FrameTag.Cells;
        var ids = new int[n];
        int litX0 = width, litY0 = height, litX1 = -1, litY1 = -1;
        for (var k = 0; k < n; k++)
        {
            int on = frames[0][k], off = frames[1][k];
            var contrast = on - off;
            if (contrast < MinPixelContrast)
                continue;
            var mid2 = on + off; // twice the midpoint
            var margin2 = (2 * contrast + 4) / 5; // twice a fifth of the contrast
            var gray = 0;
            var sure = true;
            for (var j = 2; j < Frames; j++)
            {
                var v2 = 2 * frames[j][k];
                if (v2 >= mid2 + margin2)
                    gray |= 1 << (j - 2);
                else if (v2 > mid2 - margin2)
                {
                    sure = false;
                    break;
                }
            }
            if (!sure)
                continue;
            var id = GrayCode.Decode(gray);
            if (id < 1 || id > maxId)
                continue;
            ids[k] = id;
            int x = k % width, y = k / width;
            litX0 = Math.Min(litX0, x);
            litY0 = Math.Min(litY0, y);
            litX1 = Math.Max(litX1, x);
            litY1 = Math.Max(litY1, y);
        }
        var lit = litX1 < 0 ? default : new PixelRect(litX0, litY0, litX1 - litX0 + 1, litY1 - litY0 + 1);

        // First pass: each cell's centroid; second: only its pixels near that centroid, so a stray pixel misnamed at another cell's
        // edge does not drag it.
        var first = new CellStats?[maxId + 1];
        for (var k = 0; k < n; k++)
        {
            if (ids[k] == 0)
                continue;
            (first[ids[k]] ??= new CellStats()).Add(k % width + 0.5, k / width + 0.5);
        }
        var cells = new CellStats?[maxId + 1];
        for (var k = 0; k < n; k++)
        {
            var id = ids[k];
            if (id == 0 || first[id] is not { } f)
                continue;
            var c = f.Centre;
            var reach = 1.5 * Math.Sqrt(f.N) + 1;
            double x = k % width + 0.5, y = k / width + 0.5;
            if (Math.Abs(x - c.X) <= reach && Math.Abs(y - c.Y) <= reach)
                (cells[id] ??= new CellStats()).Add(x, y);
        }

        var whole = new List<(TagSite Site, TagSitePlacement Placement, Mat3d H, Vec2d Centre)>();
        foreach (var site in sites)
        {
            var stats = new CellStats[FrameTag.Cells];
            var complete = true;
            for (var c = 0; c < FrameTag.Cells; c++)
            {
                if (cells[CellId(site.Id, c)] is not { N: >= 9 } s)
                {
                    complete = false;
                    break;
                }
                stats[c] = s;
            }
            if (!complete)
                continue;
            var areas = stats.Select(s => s.N).Order().ToArray();
            var medianArea = (areas[7] + areas[8]) / 2;
            var elong = stats.Select(s => s.Elongation).Order().ToArray();
            var medianElong = (elong[7] + elong[8]) / 2;
            if (stats.Any(s => s.N < 0.6 * medianArea || s.Elongation > 4 * medianElong))
                continue;
            var from = new Vec2d[FrameTag.Cells];
            var to = new Vec2d[FrameTag.Cells];
            for (var c = 0; c < FrameTag.Cells; c++)
            {
                var r = site.CellRect(c);
                from[c] = new Vec2d(r.X + r.Width / 2.0, r.Y + r.Height / 2.0);
                to[c] = stats[c].Centre;
            }
            if (Homography.Fit(from, to) is not { } h)
                continue;
            var sq = 0.0;
            for (var c = 0; c < FrameTag.Cells; c++)
                sq += (Homography.Apply(h, from[c]) - to[c]).LengthSquared;
            if (Math.Sqrt(sq / FrameTag.Cells) > maxFitRmsPx)
                continue;
            var top = (int)Math.Floor(to.Min(p => p.Y) - Math.Sqrt(medianArea) / 2);
            var bottom = (int)Math.Ceiling(to.Max(p => p.Y) + Math.Sqrt(medianArea) / 2);
            var centre = new Vec2d(to.Average(p => p.X), to.Average(p => p.Y));
            whole.Add((site, new TagSitePlacement(site.Id, [.. to], Math.Max(0, top), Math.Min(height - 1, bottom)), h, centre));
        }

        var chosen = new List<(TagSite Site, TagSitePlacement Placement, Mat3d H, Vec2d Centre)>();
        if (whole.Count > 0 && maxSites > 0)
        {
            chosen.Add(whole.MinBy(w => w.Centre.Y));
            if (maxSites > 1 && whole.Count > 1)
                chosen.Add(whole.Where(w => w.Site.Id != chosen[0].Site.Id).MaxBy(w => w.Centre.Y));
            while (chosen.Count < maxSites && chosen.Count < whole.Count)
            {
                var next = whole.Where(w => chosen.All(c => c.Site.Id != w.Site.Id))
                    .MaxBy(w => chosen.Min(c => (c.Centre - w.Centre).Length));
                chosen.Add(next);
            }
        }

        var shadow = new bool[n];
        foreach (var c in chosen)
            MarkShadow(shadow, width, height, c.Site, c.H);

        var rolling = chosen.Count >= 2 && lit.Height > 0
            && chosen.Max(c => c.Centre.Y) - chosen.Min(c => c.Centre.Y) >= 0.3 * lit.Height;
        var words = chosen.Count switch
        {
            0 => "the camera sees none of the tag places: pairing by timing, keep the phone very still",
            1 => "one tag place seen: pairing by tag, rolling-shutter check limited",
            _ when !rolling => string.Create(CultureInfo.InvariantCulture, $"{chosen.Count} tag places seen close together: rolling-shutter check limited"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{chosen.Count} tag places of {whole.Count} whole: pairing by tag"),
        };
        return new CellLocateResult([.. chosen.Select(c => c.Site)], [.. chosen.Select(c => c.Placement)], shadow, lit, whole.Count, words)
        {
            RollingCovered = rolling,
        };
    }

    /// <summary>Marks the camera pixels whose projector point (through the site's homography) lies within half a cell of the site.</summary>
    private static void MarkShadow(bool[] shadow, int width, int height, TagSite site, Mat3d h)
    {
        var grow = site.Cell / 2.0;
        var r = site.Rect;
        Span<Vec2d> corners =
        [
            Homography.Apply(h, new Vec2d(r.X - grow, r.Y - grow)), Homography.Apply(h, new Vec2d(r.Right + grow, r.Y - grow)),
            Homography.Apply(h, new Vec2d(r.Right + grow, r.Bottom + grow)), Homography.Apply(h, new Vec2d(r.X - grow, r.Bottom + grow)),
        ];
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var p in corners)
        {
            x0 = Math.Min(x0, p.X);
            y0 = Math.Min(y0, p.Y);
            x1 = Math.Max(x1, p.X);
            y1 = Math.Max(y1, p.Y);
        }
        var inv = Homography.Inverse(h);
        for (var y = Math.Max(0, (int)Math.Floor(y0) - 2); y <= Math.Min(height - 1, (int)Math.Ceiling(y1) + 2); y++)
        {
            for (var x = Math.Max(0, (int)Math.Floor(x0) - 2); x <= Math.Min(width - 1, (int)Math.Ceiling(x1) + 2); x++)
            {
                var q = Homography.Apply(inv, new Vec2d(x + 0.5, y + 0.5));
                if (q.X >= r.X - grow && q.X <= r.Right + grow && q.Y >= r.Y - grow && q.Y <= r.Bottom + grow)
                    shadow[y * width + x] = true;
            }
        }
    }
}
