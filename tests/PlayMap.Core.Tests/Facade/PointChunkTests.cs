using PlayMap.Core.Geometry;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Facade;

/// <summary>Chunked LAS reads the same points as the whole-file reader and names its coordinate system from a WKT or GeoKey VLR; the
/// voxel thinner keeps one centroid per occupied cube; the grid cloud file keeps double coordinates.</summary>
public sealed class PointChunkTests
{
    private const string Wkt = "PROJCS[\"ETRS89 / UTM zone 32N\",GEOGCS[\"ETRS89\",DATUM[\"European_Terrestrial_Reference_System_1989\",AUTHORITY[\"EPSG\",\"6258\"]],AUTHORITY[\"EPSG\",\"4258\"]],PROJECTION[\"Transverse_Mercator\"],UNIT[\"metre\",1],AUTHORITY[\"EPSG\",\"25832\"]]";

    private static List<Vec3d> Points(int n, ulong seed)
    {
        var rng = new Pcg32(seed, 9);
        return [.. Enumerable.Range(0, n).Select(_ => new Vec3d(512_000 + rng.Range(0.0, 40.0), 5_401_000 + rng.Range(0.0, 2.0), 100 + rng.Range(0.0, 20.0)))];
    }

    [Fact]
    public void ChunkedLasReadsTheWholeReadersPointsAndItsWktCrs()
    {
        var pts = Points(25_000, 1);
        var colours = pts.Select((_, i) => (i * 7919) & 0xFFFFFF).ToList();
        var bytes = LasTestWriter.Write(pts, colours, new Vec3d(0.001, 0.001, 0.001), new Vec3d(512_000, 5_401_000, 0), Wkt);
        var whole = MeshIO.ReadLas(new MemoryStream(bytes));
        using var source = new LasChunkSource(new MemoryStream(bytes));
        Assert.Equal("EPSG:25832", source.Crs);
        Assert.Equal(pts.Count, source.DeclaredCount);
        Assert.True(source.HasColour);
        var got = new List<Vec3d>();
        var gotColours = new List<int>();
        var buffer = new Vec3d[4096];
        var colourBuffer = new int[4096];
        int n;
        while ((n = source.Read(buffer, colourBuffer, CancellationToken.None)) > 0)
        {
            got.AddRange(buffer.AsSpan(0, n).ToArray());
            gotColours.AddRange(colourBuffer.AsSpan(0, n).ToArray());
        }
        Assert.Equal(whole.Points, got);
        Assert.Equal(whole.Colors, gotColours);
        Assert.Equal(colours, gotColours);
        Assert.True(got.Zip(pts).All(p => (p.First - p.Second).Length < 0.001));
    }

    [Fact]
    public void WithheldPointsAreLeftOutAndADamagedRecordIsRefused()
    {
        var pts = Points(9000, 4);
        var withheld = pts.Select((_, i) => i % 3 == 0).ToList();
        var bytes = LasTestWriter.Write(pts, null, new Vec3d(0.001, 0.001, 0.001), new Vec3d(512_000, 5_401_000, 0), withheld: withheld);
        using var source = new LasChunkSource(new MemoryStream(bytes));
        var got = new List<Vec3d>();
        var buffer = new Vec3d[1000];
        int n;
        while ((n = source.Read(buffer, new int[1000], CancellationToken.None)) > 0)
            got.AddRange(buffer.AsSpan(0, n).ToArray());
        Assert.Equal(6000, got.Count);
        Assert.Equal(3000, source.Withheld);
        Assert.True(got.Zip(pts.Where((_, i) => i % 3 != 0)).All(p => (p.First - p.Second).Length < 0.001));

        var damaged = LasTestWriter.Write(Points(10, 5), null, new Vec3d(0.01, 0.01, 0.01), Vec3d.Zero, Wkt);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(damaged.AsSpan(375 + 20), 60000);
        var ex = Assert.Throws<FormatException>(() => new LasChunkSource(new MemoryStream(damaged)));
        Assert.Contains("runs into its points", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AGeoKeyDirectoryNamesTheProjectedSystem()
    {
        var bytes = LasTestWriter.Write(Points(10, 2), null, new Vec3d(0.01, 0.01, 0.01), Vec3d.Zero, geoKeyEpsg: 25833);
        using var source = new LasChunkSource(new MemoryStream(bytes));
        Assert.Equal("EPSG:25833", source.Crs);
    }

    [Fact]
    public void TheOutermostWktAuthorityIsTheSystem()
    {
        Assert.Equal("EPSG:25832", LasChunkSource.CrsOfWkt(Wkt));
        Assert.Equal("EPSG:32632", LasChunkSource.CrsOfWkt("PROJCRS[\"WGS 84 / UTM zone 32N\",BASEGEOGCRS[\"WGS 84\",ID[\"EPSG\",4326]],ID[\"EPSG\",32632]]"));
        Assert.Equal("", LasChunkSource.CrsOfWkt("LOCAL_CS[\"site\"]"));
    }

    [Fact]
    public void TheVoxelThinnerKeepsOneCentroidPerCube()
    {
        var thinner = new VoxelThinner(0.1);
        Vec3d[] pts = [new(0.01, 0.01, 0.01), new(0.03, 0.05, 0.07), new(0.15, 0.01, 0.01), new(double.NaN, 0, 0)];
        thinner.Add(pts, [0x102030, 0x304050, 0xFF0000, 0]);
        Assert.Equal(3, thinner.PointsSeen);
        var cloud = thinner.ToCloud(withColour: true);
        Assert.Equal(2, cloud.Count);
        Assert.True((cloud.Points[0] - new Vec3d(0.02, 0.03, 0.04)).Length < 1e-12);
        Assert.Equal(0x203040, cloud.Colors[0]);
        Assert.Equal(new Vec3d(0.15, 0.01, 0.01), cloud.Points[1]);
    }

    [Fact]
    public void TheGridCloudFileKeepsDoubles()
    {
        var pts = Points(100, 3);
        var stream = new MemoryStream();
        GridCloudFile.Write(stream, pts, null);
        stream.Position = 0;
        var site = new Core.Model.SiteFrame("EPSG:25832", "", new Vec3d(512_010, 5_401_000, 110), 0, 1, 0);
        var back = MeshIO.ReadPly(stream, new GeometryReadOptions(1, true, site)).Cloud!;
        for (var i = 0; i < pts.Count; i++)
            Assert.True((back.Points[i] - site.ToStage(pts[i])).Length < 1e-9);
    }
}
