using PlayMap.Core.Geometry;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Facade;

/// <summary>The managed E57 reader against files from the test writer: two stations (ScaledInteger and Float, both with colour, one
/// structured on a row/column grid with invalid cells), each read back point for point within its scale with its pose applied; a
/// single flipped byte fails its page's CRC-32C with the page number in the words; Float single and an unknown codec.</summary>
public sealed class E57Tests
{
    private static (E57TestScan Structured, E57TestScan Loose) TwoStations()
    {
        var rng = new Pcg32(57, 1);
        const int Cols = 40, Rows = 25;
        var gridPts = new Vec3d[Cols * Rows];
        var cells = new (int, int)[Cols * Rows];
        var invalid = new bool[Cols * Rows];
        var colours = new int[Cols * Rows];
        for (var r = 0; r < Rows; r++)
            for (var c = 0; c < Cols; c++)
            {
                var i = r * Cols + c;
                gridPts[i] = new Vec3d(c * 0.05 - 1, 12.5 + 0.02 * Math.Sin(c * 0.3), r * 0.05 + 0.3 * Math.Cos(r * 0.2));
                cells[i] = (r, c);
                invalid[i] = (i % 37) == 5;
                colours[i] = (int)(rng.NextUInt() & 0xFFFFFF);
            }
        var structured = new E57TestScan("Station 1", gridPts)
        {
            Cells = cells, Invalid = invalid, Colours = colours, Storage = "scaled", Scale = 0.0005,
            Rotation = (Math.Cos(0.2), 0, 0, Math.Sin(0.2)), Translation = new Vec3d(512_345.123, 5_401_234.567, 112.4),
        };
        var loose = new Vec3d[1500];
        var looseColours = new int[loose.Length];
        for (var i = 0; i < loose.Length; i++)
        {
            loose[i] = new Vec3d(rng.Range(-20.0, 20.0), rng.Range(5.0, 30.0), rng.Range(0.0, 15.0));
            looseColours[i] = (int)(rng.NextUInt() & 0xFFFFFF);
        }
        var floatScan = new E57TestScan("Station 2", loose)
        {
            Colours = looseColours, Storage = "double", Rotation = (0.9, 0.1, -0.2, 0.3), Translation = new Vec3d(512_360, 5_401_250, 110),
            PerPacket = 211,
        };
        return (structured, floatScan);
    }

    private static Mat3d Rotation((double W, double X, double Y, double Z) q)
    {
        var n = Math.Sqrt(q.W * q.W + q.X * q.X + q.Y * q.Y + q.Z * q.Z);
        double w = q.W / n, x = q.X / n, y = q.Y / n, z = q.Z / n;
        return new Mat3d(
            1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
            2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
            2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y));
    }

    [Fact]
    public void TwoStationsReadBackPointForPointWithinTheirScale()
    {
        var (structured, loose) = TwoStations();
        var bytes = E57TestWriter.Write([structured, loose], "PROJCS[\"ETRS89 / UTM zone 32N\",AUTHORITY[\"EPSG\",\"25832\"]]");
        Assert.Equal(0, bytes.Length % 1024);
        var file = MeshIO.ReadE57(new MemoryStream(bytes));
        Assert.Equal(1, file.Major);
        Assert.Contains("25832", file.CoordinateMetadata, StringComparison.Ordinal);
        Assert.Equal(2, file.Scans.Count);

        var a = file.Scans[0];
        Assert.Equal("Station 1", a.Name);
        Assert.True(a.Structured);
        Assert.Equal(40, a.Points.GridWidth);
        Assert.Equal(25, a.Points.GridHeight);
        Assert.True(a.Points.HasColors);
        var ra = Rotation(structured.Rotation);
        var checkedA = 0;
        for (var i = 0; i < structured.Points.Length; i++)
        {
            var got = a.Points.Points[i];
            if (structured.Invalid![i])
            {
                Assert.False(PointCloud.IsValid(got));
                continue;
            }
            var want = ra * structured.Points[i] + structured.Translation;
            // Within the scale: half a step of rounding on each axis, turned.
            Assert.True((got - want).Length <= structured.Scale * 0.87 + 1e-9, $"{i}: {got} vs {want}");
            Assert.Equal(structured.Colours![i], a.Points.Colors[i]);
            checkedA++;
        }
        Assert.Equal(structured.Points.Length - structured.Invalid!.Count(x => x), checkedA);

        var b = file.Scans[1];
        Assert.False(b.Structured);
        Assert.Equal(loose.Points.Length, b.Points.Count);
        var rb = Rotation(loose.Rotation);
        for (var i = 0; i < loose.Points.Length; i++)
        {
            Assert.True((b.Points.Points[i] - (rb * loose.Points[i] + loose.Translation)).Length < 1e-8, $"{i}");
            Assert.Equal(loose.Colours![i], b.Points.Colors[i]);
        }
    }

    [Fact]
    public void OneFlippedByteFailsItsPagesCrcWithThePageInTheWords()
    {
        var (structured, loose) = TwoStations();
        var bytes = E57TestWriter.Write([structured, loose]);
        var page = bytes.Length / 1024 / 2;
        bytes[page * 1024 + 100] ^= 0x10;
        var ex = Assert.Throws<FormatException>(() => MeshIO.ReadE57(new MemoryStream(bytes)));
        Assert.Contains($"page {page} ", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CRC-32C", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SinglePrecisionFloatsReadAsStored()
    {
        var pts = Enumerable.Range(0, 300).Select(i => new Vec3d(i * 0.1, -i * 0.05, 3 + i * 0.01)).ToArray();
        var bytes = E57TestWriter.Write([new E57TestScan("S", pts) { Storage = "single", PerPacket = 50 }]);
        var scan = MeshIO.ReadE57(new MemoryStream(bytes)).Scans[0];
        for (var i = 0; i < pts.Length; i++)
            Assert.Equal(new Vec3d((float)pts[i].X, (float)pts[i].Y, (float)pts[i].Z), scan.Points.Points[i]);
    }

    [Fact]
    public void ScaledIntegersWithoutLimitsTakeTheWholeInt64Range()
    {
        var pts = Enumerable.Range(0, 120).Select(i => new Vec3d(i * 0.25 - 10, 3.5, -i * 0.125)).ToArray();
        var bytes = E57TestWriter.Write([new E57TestScan("S", pts) { OmitLimits = true, Scale = 0.001, PerPacket = 33 }]);
        var scan = MeshIO.ReadE57(new MemoryStream(bytes)).Scans[0];
        for (var i = 0; i < pts.Length; i++)
            Assert.True((scan.Points.Points[i] - pts[i]).Length < 1e-6, $"{i}: {scan.Points.Points[i]}");
    }

    [Fact]
    public void TheCrcIsCastagnolis()
    {
        // The standard check value of CRC-32C over "123456789".
        Assert.Equal(0xE3069283u, MeshIO.Crc32C("123456789"u8));
    }

    [Fact]
    public void NotAnE57FileIsRefusedWithWords()
    {
        var bytes = new byte[2048];
        Array.Fill(bytes, (byte)7);
        var ex = Assert.Throws<FormatException>(() => MeshIO.ReadE57(new MemoryStream(bytes)));
        Assert.Contains("CRC-32C", ex.Message, StringComparison.Ordinal);
    }
}
