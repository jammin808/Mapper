using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Site;

namespace PlayMap.Core.Tests.Facade;

/// <summary>Control-point files: a 14-point PNEZD export with its header reads 14 points with the file's decimals exact; the order is
/// sniffed from a header or given; separators of every kind; a duplicate name is refused with both lines; feet convert; SITE CHECK's
/// measure names a 20 cm shift on every point with a red light.</summary>
public sealed class ControlPointFileTests
{
    internal static string Pnezd14()
    {
        var lines = new List<string> { "Point,Northing,Easting,Elevation,Description" };
        for (var i = 0; i < 14; i++)
            lines.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"CP{i + 1},{5_401_234.567 + i * 1.111:0.000},{512_345.123 + i * 2.222:0.000},{112.4 + i * 0.5:0.000},facade corner"));
        return string.Join("\n", lines) + "\n";
    }

    [Fact]
    public void AFourteenPointPnezdFileReadsFourteenPointsExactToItsDecimals()
    {
        var read = ControlPointFile.Parse(new StringReader(Pnezd14()));
        Assert.Equal(ControlOrder.Pnezd, read.Order);
        Assert.Equal("header", read.OrderFrom);
        Assert.Equal(14, read.Points.Count);
        Assert.Empty(read.Skipped);
        for (var i = 0; i < 14; i++)
        {
            var p = read.Points[i];
            Assert.Equal($"CP{i + 1}", p.Name);
            Assert.Equal(double.Parse(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{512_345.123 + i * 2.222:0.000}"), System.Globalization.CultureInfo.InvariantCulture), p.Grid.X);
            Assert.Equal(double.Parse(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{5_401_234.567 + i * 1.111:0.000}"), System.Globalization.CultureInfo.InvariantCulture), p.Grid.Y);
            Assert.Equal("facade corner", p.Code);
            Assert.Equal(i + 2, p.Line);
        }
    }

    [Fact]
    public void OrderAndSeparatorsAndSkippedLines()
    {
        var text = "1 100.5 200.25 3.0 wall\n2\t101\t201\t4\n# a comment\n3;102;202;5;door\nbad line here\n";
        var read = ControlPointFile.Parse(new StringReader(text), ControlOrder.Penzd);
        Assert.Equal(3, read.Points.Count);
        Assert.Equal(new Vec3d(100.5, 200.25, 3), read.Points[0].Grid);
        Assert.Equal("door", read.Points[2].Code);
        Assert.Single(read.Skipped);
        Assert.Equal(5, read.Skipped[0].Line);
        var enh = ControlPointFile.Parse(new StringReader("100 200 3\n101 201 4\n"), ControlOrder.Neh);
        Assert.Equal(new Vec3d(200, 100, 3), enh.Points[0].Grid);
        Assert.Equal("P1", enh.Points[0].Name);
    }

    [Theory]
    [InlineData("Name,Easting,Northing,Height,Code", ControlOrder.Penzd)]
    [InlineData("Number;Northing;Easting;Elev;Desc", ControlOrder.Pnezd)]
    [InlineData("Station N E Z", ControlOrder.Pnezd)]
    [InlineData("X,Y,Z", ControlOrder.Enh)]
    [InlineData("Northing,Easting,Height", ControlOrder.Neh)]
    public void HeadersNameTheirOrder(string header, ControlOrder order)
    {
        var sep = header.Contains(',', StringComparison.Ordinal) ? "," : header.Contains(';', StringComparison.Ordinal) ? ";" : " ";
        var named = order is ControlOrder.Penzd or ControlOrder.Pnezd;
        var row = string.Join(sep, (named ? new[] { "A1" } : []).Concat(["100.5", "200.25", "3"]).Concat(named ? ["kerb"] : []));
        var read = ControlPointFile.Parse(new StringReader(header + "\n" + row + "\n"));
        Assert.Equal(order, read.Order);
        Assert.Equal("header", read.OrderFrom);
        var p = Assert.Single(read.Points);
        var eastFirst = order is ControlOrder.Penzd or ControlOrder.Enh;
        Assert.Equal(eastFirst ? 100.5 : 200.25, p.Grid.X);
    }

    [Fact]
    public void ADuplicateNameIsRefusedWithBothLines()
    {
        var ex = Assert.Throws<FormatException>(() => ControlPointFile.Parse(new StringReader("A,1,2,3\nB,1,2,3\nA,4,5,6\n"), ControlOrder.Penzd));
        Assert.Contains("line 1 and again on line 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UsSurveyFeetConvert()
    {
        var read = ControlPointFile.Parse(new StringReader("A,3937,0,0\n"), ControlOrder.Penzd, ControlPointFile.UnitScale("usft")!.Value);
        Assert.Equal(1200, read.Points[0].Grid.X, 9);
    }

    [Fact]
    public void AModelTwentyCentimetresOffIsNamedAtEveryPointInRed()
    {
        var site = new SiteFrame("EPSG:25832", "", new Vec3d(512_350, 5_401_240, 110), 0, 1, 0);
        var read = ControlPointFile.Parse(new StringReader(Pnezd14()));
        var points = read.Points.Select((r, i) => new ControlPoint("cp" + i, r.Name, r.Grid, 0.01, r.Code, true)).ToList();
        // The model: the plane through the points, 0.2 m behind them along stage +Z (north is −Z, so behind = toward the facade).
        (Vec3d, double) Nearest(Vec3d q) => (q + new Vec3d(0, 0, 0.2), 0.2);
        var result = SiteCheck.Run(points, site, Nearest, "the model");
        Assert.Equal(14, result.Points.Count);
        Assert.All(result.Points, r =>
        {
            Assert.Equal(Light.Red, r.Light);
            Assert.Contains("20 cm", r.Words, StringComparison.Ordinal);
        });
        Assert.Equal(Light.Red, result.Light);
        Assert.Equal(0.2, result.ShiftM, 6);
        Assert.Contains("shifted 20 cm", result.Words, StringComparison.Ordinal);

        var good = SiteCheck.Run(points, site, q => (q, 0.004), "the model");
        Assert.Equal(Light.Green, good.Light);
        Assert.Contains("agrees with the 14 control points", good.Words, StringComparison.Ordinal);
    }
}
