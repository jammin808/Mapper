using PlayMap.Core.Numerics;
using PlayMap.Core.Phone;
using PlayMap.Core.Scan;

namespace PlayMap.Core.Tests.Phone;

/// <summary>The locate run: candidates spread over the raster without touching, every chosen site's cells placed where the
/// simulator's truth puts them, the tag footprint marked for the decode to leave out, and a camera that sees no sites says so.</summary>
public sealed class CellLocateTests
{
    [Theory]
    [InlineData(1920, 1080, 24, 40)]
    [InlineData(256, 160, 6, 40)]
    [InlineData(64, 48, 6, 2)]
    public void CandidatesAreSpreadInsideTheInsetAndNeverTouch(int w, int h, int cell, int count)
    {
        var sites = CellLocate.Candidates(w, h);
        Assert.Equal(count, sites.Count);
        Assert.All(sites, s => Assert.Equal(cell, s.Cell));
        Assert.All(sites, s => Assert.True(s.Rect.X >= Math.Ceiling(w * 0.04) && s.Rect.Right <= w - Math.Ceiling(w * 0.04), s.ToString()));
        for (var a = 0; a < sites.Count; a++)
        {
            for (var b = a + 1; b < sites.Count; b++)
                Assert.True(sites[a].Rect.Intersect(sites[b].Rect).IsEmpty, $"{sites[a]} touches {sites[b]}");
        }
        Assert.True(CellLocate.CellId(sites.Count - 1, 15) <= 1023, "ten locate bits name every cell");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(96)]
    public void ChosenSitesLandWhereTheTruthPutsThem(byte background)
    {
        var kit = TagKit.Floor();
        var located = kit.Locate(background);
        Assert.InRange(located.Chosen.Length, 2, 3);
        Assert.True(located.Found >= located.Chosen.Length);
        var truth = kit.Simulator.Truth;
        foreach (var (site, placement) in located.Sites.Zip(located.Chosen))
        {
            Assert.Equal(site.Id, placement.Id);
            for (var c = 0; c < FrameTag.Cells; c++)
            {
                // Where the truth says the cell's projector pixels are seen: the mean of the camera pixels inside it.
                var r = site.CellRect(c);
                double sx = 0, sy = 0;
                var n = 0;
                for (var k = 0; k < truth.Lit.Length; k++)
                {
                    if (!truth.Lit[k] || truth.ProjectorX[k] < r.X || truth.ProjectorX[k] >= r.Right || truth.ProjectorY[k] < r.Y || truth.ProjectorY[k] >= r.Bottom)
                        continue;
                    sx += k % kit.Width + 0.5;
                    sy += k / kit.Width + 0.5;
                    n++;
                }
                Assert.True(n > 0);
                var expected = new Vec2d(sx / n, sy / n);
                var error = (placement.Cells[c] - expected).Length;
                // The page samples 3 × 3 pixels round each centre of a cell at least 6 projector pixels wide: a pixel is ample.
                Assert.True(error < 1.0, $"site {site.Id} cell {c}: {placement.Cells[c]} against {expected} ({error:0.00} px)");
            }
            Assert.True(placement.RowTop < placement.Cells.Min(p => p.Y) && placement.RowBottom > placement.Cells.Max(p => p.Y));
        }
        // The footprint covers each chosen site's cells and little else.
        var shadow = located.Shadow.Count(x => x);
        Assert.InRange(shadow, located.Chosen.Length * 16 * 9, kit.Width * kit.Height / 10);
        foreach (var p in located.Chosen.SelectMany(s => s.Cells))
            Assert.True(located.Shadow[(int)p.Y * kit.Width + (int)p.X]);
        Assert.False(located.Lit.IsEmpty);
    }

    [Fact]
    public void ACameraThatSeesNoSitesSaysPairingIsByTiming()
    {
        var kit = TagKit.Floor();
        var dark = new byte[kit.Width * kit.Height];
        var located = CellLocate.Decode(kit.Width, kit.Height, [.. Enumerable.Repeat(dark, CellLocate.Frames)], kit.Candidates);
        Assert.Empty(located.Chosen);
        Assert.Equal(0, located.MinSites);
        Assert.Contains("pairing by timing", located.Words, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => CellLocate.Decode(kit.Width, kit.Height, [dark], kit.Candidates));
    }
}
