using PlayMap.Core.Geometry;
using PlayMap.Core.Numerics;
using Xunit.Abstractions;

namespace PlayMap.Core.Tests.Facade;

/// <summary>Geodesy (facade M2) against published numbers: Lantmäteriet's control points for SWEREF 99 TM (GRS 80, a UTM-like zone
/// out to 9° from its meridian), the Ordnance Survey's worked example on the National Grid (Airy 1830) and EPSG Guidance Note 7-2's
/// Luxembourg TM example (International 1924), each within 1 mm; UTM forward then inverse round trips within 1 mm over a zone from
/// 80°S to 84°N; ECEF and ENU round trips; the EPSG UTM families and WKT sniffing, a LAS 1.4 file's WKT record reporting EPSG:25832.</summary>
public sealed class GeodesyTests(ITestOutputHelper log)
{
    private static double Dms(int d, int m, double s) => d + m / 60.0 + s / 3600.0;

    /// <summary>Lantmäteriet, "Kontrollpunkter för SWEREF 99 TM": latitude, longitude → N, E.</summary>
    public static readonly TheoryData<double, double, double, double> Sweref = new()
    {
        { 55, 12.75, 6097106.672, 356083.438 },
        { 57, 19.5, 6326392.707, 773251.054 },
        { 59, 11.25, 6546096.724, 284626.066 },
        { 61, 18.75, 6768593.345, 702745.127 },
        { 65, 21.75, 7225449.115, 817833.405 },
        { 67, 24, 7459745.672, 891298.142 },
        { 69, 21, 7666089.698, 739639.195 },
    };

    [Theory]
    [MemberData(nameof(Sweref))]
    public void Sweref99TmControlPointsAgreeToAMillimetre(double lat, double lon, double north, double east)
    {
        var tm = new TransverseMercator(Ellipsoid.Grs80, 0, 15, 0.9996, 500_000, 0);
        var (e, n) = tm.Forward(lat, lon);
        log.WriteLine($"{lat} {lon}: E {e:0.0000} ({(e - east) * 1000:0.00} mm), N {n:0.0000} ({(n - north) * 1000:0.00} mm)");
        Assert.True(Math.Abs(e - east) <= 0.001, $"E {e} vs {east}");
        Assert.True(Math.Abs(n - north) <= 0.001, $"N {n} vs {north}");
        var (bl, bo) = tm.Inverse(east, north);
        Assert.True(Math.Abs(bl - lat) * 111_000 <= 0.001 && Math.Abs(bo - lon) * 111_000 * Math.Cos(lat * Math.PI / 180) <= 0.001, $"back {bl} {bo}");
    }

    [Fact]
    public void TheOrdnanceSurveysWorkedExampleAgreesToAMillimetre()
    {
        // A guide to coordinate systems in Great Britain, the worked example: Airy 1830, the National Grid's projection.
        var tm = new TransverseMercator(Ellipsoid.Airy1830, 49, -2, 0.9996012717, 400_000, -100_000);
        var (e, n) = tm.Forward(Dms(52, 39, 27.2531), Dms(1, 43, 4.5177));
        log.WriteLine($"E {e:0.0000}, N {n:0.0000}");
        Assert.True(Math.Abs(e - 651409.903) <= 0.001, $"E {e}");
        Assert.True(Math.Abs(n - 313177.270) <= 0.001, $"N {n}");
    }

    [Fact]
    public void EpsgsLuxembourgExampleAgreesToAMillimetre()
    {
        // EPSG Guidance Note 7-2, Transverse Mercator 3D: LUREF / Luxembourg TM, International 1924.
        var tm = new TransverseMercator(Ellipsoid.International1924, Dms(49, 50, 0), Dms(6, 10, 0), 1.0, 80_000, 100_000);
        var (e, n) = tm.Forward(Dms(49, 34, 0.7381), Dms(5, 56, 16.1460));
        log.WriteLine($"E {e:0.0000}, N {n:0.0000}");
        Assert.True(Math.Abs(e - 63444.834) <= 0.001, $"E {e}");
        Assert.True(Math.Abs(n - 70387.372) <= 0.001, $"N {n}");
    }

    [Fact]
    public void UtmRoundTripsWithinAMillimetreOverTheZone()
    {
        var zone = Utm.Zone(32, north: true, Ellipsoid.Grs80);
        var south = Utm.Zone(32, north: false, Ellipsoid.Grs80);
        var rng = new Pcg32(11, 3);
        var worst = 0.0;
        for (var i = 0; i < 4000; i++)
        {
            var lat = rng.Range(-80.0, 84.0);
            var lon = 9 + rng.Range(-3.5, 3.5);
            var tm = lat >= 0 ? zone : south;
            var (e, n) = tm.Forward(lat, lon);
            var (bl, bo) = tm.Inverse(e, n);
            var (e2, n2) = tm.Forward(bl, bo);
            worst = Math.Max(worst, Math.Max(Math.Abs(e2 - e), Math.Abs(n2 - n)));
            Assert.True(Math.Abs(bl - lat) < 1e-9 && Math.Abs(bo - lon) < 1e-9, $"{lat} {lon} came back {bl} {bo}");
        }
        log.WriteLine($"worst round trip {worst * 1000:0.000000} mm");
        Assert.True(worst <= 0.001);
        Assert.Equal(32, Utm.ZoneOf(9.2));
        Assert.Equal(9, Utm.CentralMeridian(32));
        Assert.Equal((500_000.0, 0.0), zone.Forward(0, 9));
        Assert.InRange(zone.PointScale(50, 9), 0.99959, 0.99961);
        // 3° off the meridian at 50°N: k₀(1 + (Δλ cos φ)²/2) ≈ 1.00017.
        Assert.InRange(zone.PointScale(50, 12), 1.00015, 1.00019);
    }

    [Fact]
    public void EcefAndTheLocalTangentRoundTrip()
    {
        var g = new Geodetic(48.7701, 9.1801, 312.4);
        var x = Ecef.FromGeodetic(Ellipsoid.Grs80, g);
        var back = Ecef.ToGeodetic(Ellipsoid.Grs80, x);
        Assert.True(Math.Abs(back.LatDeg - g.LatDeg) < 1e-11 && Math.Abs(back.LonDeg - g.LonDeg) < 1e-11 && Math.Abs(back.H - g.H) < 1e-6);
        // The poles and the equator.
        Assert.Equal(90, Ecef.ToGeodetic(Ellipsoid.Wgs84, Ecef.FromGeodetic(Ellipsoid.Wgs84, new Geodetic(90, 0, 10))).LatDeg, 9);
        Assert.Equal(10, Ecef.ToGeodetic(Ellipsoid.Wgs84, Ecef.FromGeodetic(Ellipsoid.Wgs84, new Geodetic(0, 120, 10))).H, 6);

        var enu = new LocalTangent(Ellipsoid.Grs80, g);
        Assert.True(enu.ToEnu(g).Length < 1e-6);
        // 100 m east along the parallel: east ≈ 100, north 0, down by the curvature (d²/2R ≈ 0.8 mm).
        var east = new Geodetic(g.LatDeg, g.LonDeg + 100 / ((Ellipsoid.Grs80.PrimeVertical(g.Lat) + g.H) * Math.Cos(g.Lat)) * 180 / Math.PI, g.H);
        var v = enu.ToEnu(east);
        Assert.InRange(v.X, 99.999, 100.001);
        Assert.InRange(v.Y, -0.001, 0.001);
        Assert.InRange(v.Z, -0.0012, -0.0004);
        var round = enu.ToGeodetic(new Vec3d(123.4, -56.7, 8.9));
        Assert.True((enu.ToEnu(round) - new Vec3d(123.4, -56.7, 8.9)).Length < 1e-6);
    }

    [Fact]
    public void TheEpsgUtmFamiliesAndWktAreRecognised()
    {
        var etrs = CrsInfo.Of("EPSG:25832")!;
        Assert.Equal((CrsKind.Projected, "ETRS89 / UTM zone 32N", 32, true), (etrs.Kind, etrs.Name, etrs.Zone, etrs.North));
        Assert.Same(Ellipsoid.Grs80, etrs.Ellipsoid);
        Assert.Equal("WGS 84 / UTM zone 33S", CrsInfo.Of("32733")!.Name);
        Assert.Equal("NAD83 / UTM zone 18N", CrsInfo.Of("EPSG:26918")!.Name);
        Assert.Equal(CrsKind.Geographic, CrsInfo.Of("EPSG:4326")!.Kind);
        Assert.Equal(CrsKind.Local, CrsInfo.Of("LOCAL")!.Kind);
        Assert.Null(CrsInfo.Of("EPSG:2056"));
        Assert.Null(CrsInfo.Of("EPSG:25840"));
        Assert.Equal("", CrsInfo.NameOf("LOCAL"));

        Assert.Equal("EPSG:25832", CrsInfo.SniffWkt("PROJCS[\"ETRS89 / UTM zone 32N\",GEOGCS[\"ETRS89\"],PROJECTION[\"Transverse_Mercator\"]]"));
        Assert.Equal("EPSG:32633", CrsInfo.SniffWkt("PROJCS[\"WGS_1984_UTM_Zone_33N\",GEOGCS[\"GCS_WGS_1984\"]]"));
        Assert.Equal("EPSG:25832", CrsInfo.SniffWkt("PROJCRS[\"x\",ID[\"EPSG\",25832]]"));
        Assert.Equal("EPSG:25832", CrsInfo.SniffWkt("AUTHORITY[\"EPSG\",\"25832\"]"));
        Assert.Equal("", CrsInfo.SniffWkt("PROJCS[\"CH1903+ / LV95\"]"));
        // Authorities nested in the datum, the base system or a unit name those, not the system: the UTM name decides.
        Assert.Equal("EPSG:25832", CrsInfo.SniffWkt(
            "PROJCS[\"ETRS89 / UTM zone 32N\",GEOGCS[\"ETRS89\",DATUM[\"ETRS89\",AUTHORITY[\"EPSG\",\"6258\"]],AUTHORITY[\"EPSG\",\"4258\"]],UNIT[\"metre\",1,AUTHORITY[\"EPSG\",\"9001\"]]]"));
        Assert.Equal("", CrsInfo.SniffWkt("PROJCS[\"custom\",GEOGCS[\"WGS 84\",AUTHORITY[\"EPSG\",\"4326\"]],UNIT[\"metre\",1,AUTHORITY[\"EPSG\",\"9001\"]]]"));
        // A compound system is its horizontal part's.
        Assert.Equal("EPSG:25832", CrsInfo.SniffWkt(
            "COMPD_CS[\"ETRS89 / UTM 32N + DHHN2016\",PROJCS[\"ETRS89 / UTM zone 32N\",GEOGCS[\"ETRS89\"],AUTHORITY[\"EPSG\",\"25832\"]],VERT_CS[\"DHHN2016 height\",AUTHORITY[\"EPSG\",\"7837\"]],AUTHORITY[\"EPSG\",\"9999\"]]"));
        Assert.Equal("EPSG:25833", CrsInfo.SniffWkt(
            "COMPD_CS[\"x\",PROJCS[\"ETRS89 / UTM zone 33N\",GEOGCS[\"ETRS89\",AUTHORITY[\"EPSG\",\"4258\"]]],VERT_CS[\"h\",AUTHORITY[\"EPSG\",\"7837\"]]]"));

        // A geographic point to the site's UTM and back; a grid scale near the meridian is the projection's 0.9996 less the height.
        var p = etrs.FromGeodetic(new Geodetic(48.77, 9.18, 300));
        var g = etrs.ToGeodetic(p);
        Assert.True(Math.Abs(g.LatDeg - 48.77) < 1e-10 && Math.Abs(g.LonDeg - 9.18) < 1e-10 && g.H == 300);
        Assert.InRange(etrs.GridScaleAt(p), 0.99955, 0.99956);
    }

    [Fact]
    public void ALas14FileWithAWktRecordReportsItsSystem()
    {
        var pts = Enumerable.Range(0, 100).Select(i => new Vec3d(512_000 + i, 5_401_000 + i, 100)).ToList();
        foreach (var wkt in new[]
        {
            "PROJCS[\"ETRS89 / UTM zone 32N\",GEOGCS[\"ETRS89\",AUTHORITY[\"EPSG\",\"4258\"]],PROJECTION[\"Transverse_Mercator\"],AUTHORITY[\"EPSG\",\"25832\"]]",
            "PROJCS[\"ETRS89 / UTM zone 32N\",GEOGCS[\"ETRS89\"],PROJECTION[\"Transverse_Mercator\"],UNIT[\"metre\",1]]",
        })
        {
            var bytes = LasTestWriter.Write(pts, null, new Vec3d(0.001, 0.001, 0.001), new Vec3d(512_000, 5_401_000, 0), wkt);
            Assert.Equal(1, bytes[24]);
            Assert.Equal(4, bytes[25]);
            using var source = new LasChunkSource(new MemoryStream(bytes));
            Assert.Equal("EPSG:25832", source.Crs);
            Assert.Equal("ETRS89 / UTM zone 32N", CrsInfo.NameOf(source.Crs));
        }
    }
}
