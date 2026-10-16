using System.Buffers.Binary;
using System.Globalization;
using PlayMap.Core.Geometry;
using PlayMap.Core.Numerics;
using PlayMap.Core.Site;

namespace PlayMap.Core.Tests.Facade;

/// <summary>The survey readers against damaged and hostile files: every count and offset a header or the XML declares is held to what
/// the file can hold before anything is allocated for it, so a file of a few kilobytes is refused with words (a
/// <see cref="FormatException"/>) instead of claiming gigabytes, looping without end or failing with an arithmetic fault; and the
/// text readers read a semicolon-separated export with decimal commas as the numbers it holds, never as twice as many columns.</summary>
public sealed class HostileSurveyTests
{
    private static Vec3d[] Line(int n) => [.. Enumerable.Range(0, n).Select(i => new Vec3d(i * 0.1, 10, 1 + i * 0.01))];

    private static E57File Read(byte[] bytes) => MeshIO.ReadE57(new MemoryStream(bytes));

    [Fact]
    public void AnE57DeclaringMorePointsThanItsSectionHoldsIsRefusedBeforeItAllocates()
    {
        var bytes = E57TestWriter.Write([new E57TestScan("Station 1", Line(100)) { RecordCount = 1 << 24 }]);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.Throws<FormatException>(() => Read(bytes));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("Station 1", e.Message, StringComparison.Ordinal);
        Assert.True(allocated < 16L * 1024 * 1024, string.Create(CultureInfo.InvariantCulture, $"{allocated / 1048576.0:0.0} MB allocated for a {bytes.Length} byte file"));
    }

    [Fact]
    public void AnE57WithANegativeRecordCountIsRefusedWithWords()
    {
        var bytes = E57TestWriter.Write([new E57TestScan("Station 1", Line(10)) { RecordCount = -5 }]);
        var e = Assert.Throws<FormatException>(() => Read(bytes));
        Assert.Contains("Station 1", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnE57WhoseFieldsCarryNoBitsCannotClaimEndlessPoints()
    {
        // Every coordinate constant: a point costs no bits, so only the declared count would bound the loop.
        var bytes = E57TestWriter.Write([new E57TestScan("Station 1", Line(4)) { Constant = true, RecordCount = 1L << 40 }]);
        var e = Assert.Throws<FormatException>(() => Read(bytes));
        Assert.Contains("Station 1", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnE57GridFarLargerThanItsPointsIsRefusedBeforeItIsAllocated()
    {
        // Two points at opposite corners of a 12 000 × 12 000 grid: 144 M cells (3.5 GB) for two points.
        var bytes = E57TestWriter.Write([new E57TestScan("Station 1", Line(2)) { Cells = [(0, 0), (11_999, 11_999)] }]);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.Throws<FormatException>(() => Read(bytes));
        Assert.Contains("Station 1", e.Message, StringComparison.Ordinal);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 16L * 1024 * 1024);
    }

    [Fact]
    public void AnE57WhoseXmlLiesBeyondTheFileIsRefusedBeforeItIsAllocated()
    {
        var bytes = E57TestWriter.Write([new E57TestScan("Station 1", Line(10))]);
        // The XML length in the header (logical bytes 32–39, inside page 0) set to 60 MB, and page 0's CRC made good again.
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), 60UL * 1024 * 1024);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(1020), MeshIO.Crc32C(bytes.AsSpan(0, 1020)));
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<FormatException>(() => Read(bytes));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 16L * 1024 * 1024);
    }

    private static byte[] Las(int n) =>
        LasTestWriter.Write([.. Enumerable.Range(0, n).Select(i => new Vec3d(512_000 + i, 5_401_000 + i, 100))], null, new Vec3d(0.001, 0.001, 0.001),
            new Vec3d(512_000, 5_401_000, 0));

    [Fact]
    public void ALasHeaderShorterThanItsOwnFieldsIsRefusedRatherThanReadAskew()
    {
        var bytes = Las(20);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(94), 100);
        Assert.Throws<FormatException>(() => new LasChunkSource(new MemoryStream(bytes)));
    }

    [Fact]
    public void ALasDeclaringMorePointsThanALongHoldsIsRefusedWithWords()
    {
        var bytes = Las(20);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(247), ulong.MaxValue);
        Assert.Throws<FormatException>(() => new LasChunkSource(new MemoryStream(bytes)));
    }

    [Fact]
    public void AWholeLasDeclaringMorePointsThanItHoldsIsRefusedBeforeItAllocates()
    {
        var bytes = Las(20);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(247), 1UL << 40);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<FormatException>(() => MeshIO.ReadLas(new MemoryStream(bytes)));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 16L * 1024 * 1024);
        // The same file as written reads its 20 points.
        Assert.Equal(20, MeshIO.ReadLas(new MemoryStream(Las(20))).Count);
    }

    [Fact]
    public void ALasWhoseExtendedRecordsLieBeyondTheFileStillReadsItsPoints()
    {
        var bytes = Las(20);
        // Extended VLRs at an offset past the end of the file (and past what a long holds): no coordinate system, the points as they are.
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(235), ulong.MaxValue - 7);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(243), 1);
        using var source = new LasChunkSource(new MemoryStream(bytes));
        Assert.Equal("", source.Crs);
        var points = new Vec3d[64];
        Assert.Equal(20, source.Read(points, new int[64], CancellationToken.None));
        Assert.Equal(new Vec3d(512_019, 5_401_019, 100), points[19]);
    }

    [Fact]
    public void ASemicolonControlFileWithDecimalCommasReadsItsNumbers()
    {
        const string Text = "Point;Easting;Northing;Height;Code\nCP1;512345,123;5401234,567;112,400;corner\nCP2;512350,5;5401236;113;door\n";
        var read = ControlPointFile.Parse(new StringReader(Text));
        Assert.Equal(ControlOrder.Penzd, read.Order);
        Assert.Empty(read.Skipped);
        Assert.Equal(2, read.Points.Count);
        Assert.Equal(new Vec3d(512_345.123, 5_401_234.567, 112.4), read.Points[0].Grid);
        Assert.Equal("corner", read.Points[0].Code);
        Assert.Equal(new Vec3d(512_350.5, 5_401_236, 113), read.Points[1].Grid);
    }

    [Fact]
    public void ASemicolonPointFileWithDecimalCommasReadsItsNumbers()
    {
        var cloud = MeshIO.ReadXyz(new StringReader("512345,123;5401234,567;112,400\n512346,5;5401235;113\n"));
        Assert.Equal(2, cloud.Count);
        Assert.Equal(new Vec3d(512_345.123, 5_401_234.567, 112.4), cloud.Points[0]);
        Assert.Equal(new Vec3d(512_346.5, 5_401_235, 113), cloud.Points[1]);
    }
}
