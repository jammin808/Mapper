using System.Globalization;
using System.Text;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Site;

namespace PlayMap.Core.Tests.Facade;

/// <summary>The site frame: a surveyed OBJ at E 512 345.123 / N 5 401 234.567 lands in stage coordinates exact to 0.1 mm (read in
/// double, rebased, then narrowed) and round-trips back to its grid coordinates; the same file read as stored is off by decimetres,
/// which is why rebasing comes first. A frame picked from a file centres on it at a 10 m round; a stage move keeps every grid point.</summary>
public sealed class SiteFrameTests
{
    private static readonly Vec3d Origin = new(512_345.123, 5_401_234.567, 112.4);

    /// <summary>A facade's worth of vertices around the origin (E, N, H with three decimals, Z-up) and two faces a row.</summary>
    private static (string Obj, List<Vec3d> Grid) Facade()
    {
        var sb = new StringBuilder();
        var grid = new List<Vec3d>();
        const int Cols = 21, Rows = 11;
        for (var r = 0; r < Rows; r++)
            for (var c = 0; c < Cols; c++)
            {
                var g = new Vec3d(Math.Round(Origin.X + c * 2.0 - 20 + 0.001 * r, 3), Math.Round(Origin.Y + 0.25 * Math.Sin(c), 3),
                    Math.Round(Origin.Z + r * 2.0, 3));
                grid.Add(g);
                sb.Append(CultureInfo.InvariantCulture, $"v {g.X:0.000} {g.Y:0.000} {g.Z:0.000}\n");
            }
        for (var r = 0; r + 1 < Rows; r++)
            for (var c = 0; c + 1 < Cols; c++)
            {
                var a = r * Cols + c + 1;
                sb.Append(CultureInfo.InvariantCulture, $"f {a} {a + 1} {a + Cols + 1}\nf {a} {a + Cols + 1} {a + Cols}\n");
            }
        return (sb.ToString(), grid);
    }

    [Fact]
    public void ASurveyedObjLandsInStageCoordinatesExactToATenthOfAMillimetreAndRoundTrips()
    {
        var (obj, grid) = Facade();
        var site = new SiteFrame("EPSG:25832", "ETRS89 / UTM zone 32N", Origin, 0, 1, 50);
        var file = MeshIO.ReadObj(new StringReader(obj), new GeometryReadOptions(1, true, site));
        var mesh = file.Mesh!;
        Assert.Equal(site, file.Site);
        Assert.Equal(grid.Count, mesh.VertexCount);
        double worst = 0, worstBack = 0;
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            // The reader numbers vertices by first use in the faces: match each to its grid point.
            var i = Nearest(grid, site.ToGrid(mesh.Position(v)));
            worst = Math.Max(worst, (mesh.Position(v) - site.ToStage(grid[i])).Length);
            worstBack = Math.Max(worstBack, (site.ToGrid(mesh.Position(v)) - grid[i]).Length);
        }
        Assert.True(worst <= 1e-4, $"{worst * 1000:0.000} mm");
        Assert.True(worstBack <= 1e-4, $"{worstBack * 1000:0.000} mm");
        // Stage axes: +X east, +Y up, +Z south.
        var first = mesh.Position(0);
        Assert.Equal(grid[0].X - Origin.X, first.X, 3);
        Assert.Equal(grid[0].Z - Origin.Z, first.Y, 3);
        Assert.Equal(-(grid[0].Y - Origin.Y), first.Z, 3);
    }

    private static int Nearest(List<Vec3d> grid, Vec3d p)
    {
        var best = 0;
        for (var i = 1; i < grid.Count; i++)
            if ((grid[i] - p).LengthSquared < (grid[best] - p).LengthSquared)
                best = i;
        return best;
    }

    [Fact]
    public void TheSameFileReadAsStoredIsOffByDecimetres()
    {
        var (obj, grid) = Facade();
        var raw = MeshIO.ReadObj(new StringReader(obj)).Mesh!;
        var worst = Enumerable.Range(0, raw.VertexCount).Max(v => (raw.Position(v) - grid[Nearest(grid, raw.Position(v))]).Length);
        // A float near 5.4 million steps 0.5 m: the file's millimetres are gone before anything else happens.
        Assert.True(worst > 0.05, $"{worst:0.000} m");
        Assert.True(worst <= 0.25 * Math.Sqrt(3) + 1e-9, $"{worst:0.000} m");
    }

    [Fact]
    public void APickedFrameCentresOnTheFileAtATenMetreRound()
    {
        var (obj, grid) = Facade();
        var file = MeshIO.ReadObj(new StringReader(obj), new GeometryReadOptions(1, true, null, AutoSite: true, Crs: "EPSG:25832"));
        var site = file.Site!;
        Assert.Equal("EPSG:25832", site.Crs);
        Assert.Equal(new Vec3d(512_350, 5_401_230, 120), site.Origin);
        var worst = Enumerable.Range(0, file.Mesh!.VertexCount).Max(v => (file.Mesh.Position(v) - site.ToStage(grid[Nearest(grid, site.ToGrid(file.Mesh.Position(v)))])).Length);
        Assert.True(worst <= 1e-4, $"{worst * 1000:0.000} mm");
    }

    [Fact]
    public void AFileWiderThanASiteIsRefusedWithWords()
    {
        var obj = "v 500000 5400000 0\nv 503000 5400000 0\nv 500000 5400010 0\nf 1 2 3\n";
        var ex = Assert.Throws<FormatException>(() => MeshIO.ReadObj(new StringReader(obj), new GeometryReadOptions(1, true, null, AutoSite: true)));
        Assert.Contains("split the site", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AsStoredReadsAreUnchanged()
    {
        var (obj, _) = Facade();
        var a = MeshIO.ReadObj(new StringReader(obj)).Mesh!;
        var b = MeshIO.ReadObj(new StringReader(obj), GeometryReadOptions.AsStored).Mesh!;
        Assert.Equal(a.Positions, b.Positions);
        var ply = new MemoryStream();
        MeshIO.WritePly(ply, a);
        ply.Position = 0;
        var c = MeshIO.ReadPly(ply).Mesh!;
        Assert.Equal(a.Positions, c.Positions);
    }

    [Fact]
    public void AStageMoveBetweenFramesKeepsEveryGridPointAndTurnsPoses()
    {
        var from = new SiteFrame("EPSG:25832", "", Origin, 0, 1, 0);
        var to = new SiteFrame("EPSG:25832", "", Origin + new Vec3d(30, -12, 4), 20, 1, 0);
        var move = StageMove.Between(from, to);
        var rng = new Pcg32(3, 3);
        for (var i = 0; i < 50; i++)
        {
            var p = new Vec3d(rng.Range(-50.0, 50.0), rng.Range(0.0, 30.0), rng.Range(-50.0, 50.0));
            Assert.True((to.ToGrid(move.Apply(p)) - from.ToGrid(p)).Length < 1e-9);
        }
        Assert.True(Math.Abs(Math.Abs(move.R.ToRotationVector().Y) - 20 * Math.PI / 180) < 1e-9);
    }
}
