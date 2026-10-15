using System.Collections.Immutable;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;

namespace PlayMap.Core.Phone;

/// <summary>A tag site in a projector's raster: 4 × 4 square cells of <see cref="Cell"/> pixels from <see cref="Rect"/>'s corner.</summary>
public readonly record struct TagSite(int Id, PixelRect Rect, int Cell)
{
    /// <summary>Cell <paramref name="c"/> (0–15, row-major) as a rectangle of the projector's raster.</summary>
    public PixelRect CellRect(int c) => new(Rect.X + (c % 4) * Cell, Rect.Y + (c / 4) * Cell, Cell, Cell);
}

/// <summary>Where a tag site lands in the camera's picture: each cell's centre (row-major, camera pixels) and the rows the site
/// spans.</summary>
public readonly record struct TagSitePlacement(int Id, ImmutableArray<Vec2d> Cells, int RowTop, int RowBottom);

/// <summary>What one site reads: <see cref="Dark"/> when it is not lit enough to count (its reference cells too close), unsure
/// when a pair is too close or the parity is odd, else the value.</summary>
public enum TagSiteState
{
    Dark,
    Unsure,
    Read,
}

/// <summary>One site's reading: its state, value (1–63 when read), reference contrast and the smallest pair difference.</summary>
public readonly record struct TagRead(TagSiteState State, int Value, int Contrast, int Margin);

/// <summary>A picture's tag over all its sites: none lit enough, unsure (a lit site that does not read: skipped), disagreeing
/// (two lit sites read different values — the picture straddles two patterns: a rolling-shutter mix), or read.</summary>
public enum FrameTagOutcome
{
    None,
    Unsure,
    Disagree,
    Read,
}

public readonly record struct FrameTagRead(FrameTagOutcome Outcome, int Value, int ValidSites, int Margin);

/// <summary>
/// The tag every pattern carries so a picture is paired with its pattern by what it shows. Pattern i's value is (i mod 63) + 1, so
/// 0 — a dark site — is never a tag. A site's 16 cells, row-major: cell 0 always lit and cell 15 always dark (references); cells
/// 1…14 are seven differential pairs (2k+1, 2k+2) carrying bits b0…b5 of the value and b6, the even parity of b0…b5 — (lit, dark)
/// for 1, (dark, lit) for 0. A site is lit enough when its references differ by at least <see cref="MinContrast"/> levels; a pair
/// is certain when its difference is at least <see cref="MinPairDelta"/> levels and a fifth of the contrast. Integer arithmetic
/// throughout, so the camera page's reader (tags.js) reads exactly the same values from the same pixels.
/// </summary>
public static class FrameTag
{
    public const int MinContrast = 20;

    public const int MinPairDelta = 6;

    public const int Cells = 16;

    public static int Value(int patternIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(patternIndex);
        return patternIndex % 63 + 1;
    }

    /// <summary>The seven code bits of a value: b0…b5 the value's bits, b6 their even parity.</summary>
    public static int Code(int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 63);
        var parity = System.Numerics.BitOperations.PopCount((uint)value) & 1;
        return value | (parity << 6);
    }

    /// <summary>Whether cell <paramref name="cell"/> (0–15) of a site showing <paramref name="value"/> is lit.</summary>
    public static bool Cell(int value, int cell)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cell);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cell, 15);
        if (cell == 0)
            return true;
        if (cell == 15)
            return false;
        var k = (cell - 1) / 2;
        var bit = (Code(value) >> k) & 1;
        var first = (cell - 1) % 2 == 0;
        return first ? bit == 1 : bit == 0;
    }

    /// <summary>Reads one site from its 16 cell means (row-major).</summary>
    public static TagRead Read(ReadOnlySpan<int> cellMeans)
    {
        if (cellMeans.Length != Cells)
            throw new ArgumentException("A site has 16 cells.", nameof(cellMeans));
        var contrast = cellMeans[0] - cellMeans[15];
        if (contrast < MinContrast)
            return new TagRead(TagSiteState.Dark, 0, contrast, 0);
        var code = 0;
        var margin = int.MaxValue;
        var certain = true;
        for (var k = 0; k < 7; k++)
        {
            var d = cellMeans[2 * k + 1] - cellMeans[2 * k + 2];
            var a = Math.Abs(d);
            margin = Math.Min(margin, a);
            if (a < MinPairDelta || a * 5 < contrast)
                certain = false;
            if (d > 0)
                code |= 1 << k;
        }
        var value = code & 63;
        var parityOk = (System.Numerics.BitOperations.PopCount((uint)code) & 1) == 0;
        return certain && parityOk && value != 0
            ? new TagRead(TagSiteState.Read, value, contrast, margin)
            : new TagRead(TagSiteState.Unsure, 0, contrast, margin);
    }

    /// <summary>The mean of the 3 × 3 pixels around a cell centre (clamped to the raster), rounded half up — the page's rule.</summary>
    public static int Sample(ReadOnlySpan<byte> gray, int width, int height, Vec2d centre)
    {
        var cx = (int)Math.Floor(centre.X);
        var cy = (int)Math.Floor(centre.Y);
        var sum = 0;
        for (var dy = -1; dy <= 1; dy++)
        {
            var y = Math.Clamp(cy + dy, 0, height - 1);
            for (var dx = -1; dx <= 1; dx++)
                sum += gray[y * width + Math.Clamp(cx + dx, 0, width - 1)];
        }
        return (sum + 4) / 9;
    }

    /// <summary>Reads one placed site in a camera picture.</summary>
    public static TagRead ReadSite(ReadOnlySpan<byte> gray, int width, int height, TagSitePlacement site)
    {
        Span<int> means = stackalloc int[Cells];
        for (var c = 0; c < Cells; c++)
            means[c] = Sample(gray, width, height, site.Cells[c]);
        return Read(means);
    }

    /// <summary>A picture's tag: read when every lit site reads the same value and at least <paramref name="minSites"/> are lit;
    /// disagreeing when two lit sites read different values; unsure when a lit site does not read; none when too few are lit.</summary>
    public static FrameTagRead ReadFrame(ReadOnlySpan<byte> gray, int width, int height, IReadOnlyList<TagSitePlacement> sites, int minSites)
    {
        ArgumentNullException.ThrowIfNull(sites);
        var valid = 0;
        var value = 0;
        var margin = int.MaxValue;
        var unsure = false;
        var disagree = false;
        foreach (var site in sites)
        {
            var r = ReadSite(gray, width, height, site);
            if (r.State == TagSiteState.Dark)
                continue;
            valid++;
            if (r.State == TagSiteState.Unsure)
            {
                unsure = true;
                continue;
            }
            margin = Math.Min(margin, r.Margin);
            if (value == 0)
                value = r.Value;
            else if (value != r.Value)
                disagree = true;
        }
        if (disagree)
            return new FrameTagRead(FrameTagOutcome.Disagree, 0, valid, 0);
        if (unsure)
            return new FrameTagRead(FrameTagOutcome.Unsure, 0, valid, 0);
        if (valid < Math.Max(1, minSites) || value == 0)
            return new FrameTagRead(FrameTagOutcome.None, 0, valid, 0);
        return new FrameTagRead(FrameTagOutcome.Read, value, valid, margin);
    }
}

/// <summary>Paints tag sites into a pattern's raster (after the plan rendered it): each cell at the lit or unlit level its value
/// asks for.</summary>
public static class TagOverlay
{
    public static void Apply(Span<byte> raster, int width, IReadOnlyList<TagSite> sites, int value, byte high, byte low)
    {
        ArgumentNullException.ThrowIfNull(sites);
        foreach (var site in sites)
        {
            for (var c = 0; c < FrameTag.Cells; c++)
                Fill(raster, width, site.CellRect(c), FrameTag.Cell(value, c) ? high : low);
        }
    }

    /// <summary>Every cell of the sites at one level (the latency flashes, the locate frames' references).</summary>
    public static void Fill(Span<byte> raster, int width, IReadOnlyList<TagSite> sites, byte level)
    {
        ArgumentNullException.ThrowIfNull(sites);
        foreach (var site in sites)
            Fill(raster, width, site.Rect, level);
    }

    internal static void Fill(Span<byte> raster, int width, PixelRect r, byte level)
    {
        var height = raster.Length / width;
        var clip = r.Intersect(new PixelRect(0, 0, width, height));
        for (var y = clip.Y; y < clip.Bottom; y++)
            raster.Slice(y * width + clip.X, clip.Width).Fill(level);
    }
}
