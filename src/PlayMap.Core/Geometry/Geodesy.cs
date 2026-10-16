using System.Globalization;
using System.Text.RegularExpressions;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Geometry;

/// <summary>A reference ellipsoid by its semi-major axis and inverse flattening (EPSG's defining parameters).</summary>
public sealed record Ellipsoid(string Name, double A, double InverseFlattening)
{
    public static readonly Ellipsoid Wgs84 = new("WGS 84", 6378137.0, 298.257223563);
    public static readonly Ellipsoid Grs80 = new("GRS 1980", 6378137.0, 298.257222101);
    public static readonly Ellipsoid Airy1830 = new("Airy 1830", 6377563.396, 299.3249646);
    public static readonly Ellipsoid International1924 = new("International 1924", 6378388.0, 297.0);

    public double F => 1 / InverseFlattening;

    /// <summary>The first eccentricity squared.</summary>
    public double E2 => F * (2 - F);

    public double E => Math.Sqrt(E2);

    /// <summary>The third flattening n = f / (2 − f), Krüger's series variable.</summary>
    public double N => F / (2 - F);

    public double B => A * (1 - F);

    /// <summary>The prime vertical radius of curvature at a latitude (radians).</summary>
    public double PrimeVertical(double lat) => A / Math.Sqrt(1 - E2 * Math.Sin(lat) * Math.Sin(lat));

    /// <summary>The meridian radius of curvature at a latitude (radians).</summary>
    public double Meridian(double lat)
    {
        var s = Math.Sin(lat);
        return A * (1 - E2) / Math.Pow(1 - E2 * s * s, 1.5);
    }
}

/// <summary>Geodetic latitude and longitude in degrees and ellipsoidal height in metres.</summary>
public readonly record struct Geodetic(double LatDeg, double LonDeg, double H)
{
    public double Lat => LatDeg * Math.PI / 180;

    public double Lon => LonDeg * Math.PI / 180;
}

/// <summary>Earth-centred, earth-fixed Cartesian coordinates (EPSG method 9602, the geographic/geocentric conversion).</summary>
public static class Ecef
{
    public static Vec3d FromGeodetic(Ellipsoid ellipsoid, Geodetic g)
    {
        ArgumentNullException.ThrowIfNull(ellipsoid);
        var nu = ellipsoid.PrimeVertical(g.Lat);
        var cl = Math.Cos(g.Lat);
        return new Vec3d((nu + g.H) * cl * Math.Cos(g.Lon), (nu + g.H) * cl * Math.Sin(g.Lon), ((1 - ellipsoid.E2) * nu + g.H) * Math.Sin(g.Lat));
    }

    /// <summary>The geodetic coordinates of an ECEF point: latitude by fixed-point iteration on tan φ (converges to below a
    /// nanometre in a few steps away from the centre), longitude and height closed-form.</summary>
    public static Geodetic ToGeodetic(Ellipsoid ellipsoid, Vec3d p)
    {
        ArgumentNullException.ThrowIfNull(ellipsoid);
        var e2 = ellipsoid.E2;
        var r = Math.Sqrt(p.X * p.X + p.Y * p.Y);
        var lon = Math.Atan2(p.Y, p.X);
        if (r < 1e-9)
            return new Geodetic(p.Z >= 0 ? 90 : -90, lon * 180 / Math.PI, Math.Abs(p.Z) - ellipsoid.B);
        var lat = Math.Atan2(p.Z, r * (1 - e2));
        for (var i = 0; i < 10; i++)
        {
            var nu = ellipsoid.PrimeVertical(lat);
            var next = Math.Atan2(p.Z + e2 * nu * Math.Sin(lat), r);
            if (Math.Abs(next - lat) < 1e-15)
            {
                lat = next;
                break;
            }
            lat = next;
        }
        var n = ellipsoid.PrimeVertical(lat);
        var h = Math.Abs(lat) < Math.PI / 4 ? r / Math.Cos(lat) - n : p.Z / Math.Sin(lat) - (1 - e2) * n;
        return new Geodetic(lat * 180 / Math.PI, lon * 180 / Math.PI, h);
    }
}

/// <summary>A local east-north-up frame at an origin (EPSG method 9836, geocentric/topocentric): metres, east, north and up from
/// the origin, in double.</summary>
public sealed class LocalTangent
{
    private readonly Ellipsoid _ellipsoid;
    private readonly Vec3d _origin;
    private readonly Mat3d _toEnu;

    public LocalTangent(Ellipsoid ellipsoid, Geodetic origin)
    {
        ArgumentNullException.ThrowIfNull(ellipsoid);
        _ellipsoid = ellipsoid;
        Origin = origin;
        _origin = Ecef.FromGeodetic(ellipsoid, origin);
        double sl = Math.Sin(origin.Lat), cl = Math.Cos(origin.Lat), so = Math.Sin(origin.Lon), co = Math.Cos(origin.Lon);
        _toEnu = Mat3d.FromRows(new Vec3d(-so, co, 0), new Vec3d(-sl * co, -sl * so, cl), new Vec3d(cl * co, cl * so, sl));
    }

    public Geodetic Origin { get; }

    public Vec3d ToEnu(Vec3d ecef) => _toEnu * (ecef - _origin);

    public Vec3d FromEnu(Vec3d enu) => _toEnu.Transposed() * enu + _origin;

    public Vec3d ToEnu(Geodetic g) => ToEnu(Ecef.FromGeodetic(_ellipsoid, g));

    public Geodetic ToGeodetic(Vec3d enu) => Ecef.ToGeodetic(_ellipsoid, FromEnu(enu));
}

/// <summary>
/// The transverse Mercator projection by Krüger's series to sixth order in the third flattening n (the coefficients as published by
/// Krüger (1912) and extended by Karney (2011); EPSG Guidance Note 7-2 gives the same series to fourth order). Accurate to well under
/// a millimetre within a few thousand kilometres of the central meridian; the inverse returns latitude through Newton's method on the
/// conformal latitude. Easting and northing in metres; angles in degrees.
/// </summary>
public sealed class TransverseMercator
{
    private readonly double[] _alpha;
    private readonly double[] _beta;
    private readonly double _a;    // the rectifying radius A
    private readonly double _m0;   // A·ξ at the latitude of origin
    private readonly double _e;
    private readonly double _e2;

    public TransverseMercator(Ellipsoid ellipsoid, double latOriginDeg, double lonOriginDeg, double scale, double falseEasting, double falseNorthing)
    {
        ArgumentNullException.ThrowIfNull(ellipsoid);
        Ellipsoid = ellipsoid;
        LatOriginDeg = latOriginDeg;
        LonOriginDeg = lonOriginDeg;
        Scale = scale;
        FalseEasting = falseEasting;
        FalseNorthing = falseNorthing;
        var n = ellipsoid.N;
        double n2 = n * n, n3 = n2 * n, n4 = n3 * n, n5 = n4 * n, n6 = n5 * n;
        _e2 = ellipsoid.E2;
        _e = Math.Sqrt(_e2);
        _a = ellipsoid.A / (1 + n) * (1 + n2 / 4 + n4 / 64 + n6 / 256);
        _alpha =
        [
            n / 2 - 2 * n2 / 3 + 5 * n3 / 16 + 41 * n4 / 180 - 127 * n5 / 288 + 7891 * n6 / 37800,
            13 * n2 / 48 - 3 * n3 / 5 + 557 * n4 / 1440 + 281 * n5 / 630 - 1983433 * n6 / 1935360,
            61 * n3 / 240 - 103 * n4 / 140 + 15061 * n5 / 26880 + 167603 * n6 / 181440,
            49561 * n4 / 161280 - 179 * n5 / 168 + 6601661 * n6 / 7257600,
            34729 * n5 / 80640 - 3418889 * n6 / 1995840,
            212378941 * n6 / 319334400,
        ];
        _beta =
        [
            n / 2 - 2 * n2 / 3 + 37 * n3 / 96 - n4 / 360 - 81 * n5 / 512 + 96199 * n6 / 604800,
            n2 / 48 + n3 / 15 - 437 * n4 / 1440 + 46 * n5 / 105 - 1118711 * n6 / 3870720,
            17 * n3 / 480 - 37 * n4 / 840 - 209 * n5 / 4480 + 5569 * n6 / 90720,
            4397 * n4 / 161280 - 11 * n5 / 504 - 830251 * n6 / 7257600,
            4583 * n5 / 161280 - 108847 * n6 / 3991680,
            20648693 * n6 / 638668800,
        ];
        _m0 = _a * Xi(latOriginDeg * Math.PI / 180, 0).Xi;
    }

    public Ellipsoid Ellipsoid { get; }
    public double LatOriginDeg { get; }
    public double LonOriginDeg { get; }
    public double Scale { get; }
    public double FalseEasting { get; }
    public double FalseNorthing { get; }

    /// <summary>tan of the conformal latitude from tan of the geodetic latitude.</summary>
    private double TauPrime(double tau)
    {
        var sigma = Math.Sinh(_e * Atanh(_e * tau / Math.Sqrt(1 + tau * tau)));
        return tau * Math.Sqrt(1 + sigma * sigma) - sigma * Math.Sqrt(1 + tau * tau);
    }

    private static double Atanh(double x) => 0.5 * Math.Log((1 + x) / (1 - x));

    /// <summary>The Gauss–Schreiber coordinates (ξ, η) of a point after Krüger's series, longitude relative to the central meridian.</summary>
    private (double Xi, double Eta) Xi(double lat, double dlon)
    {
        var tp = TauPrime(Math.Tan(lat));
        var xiP = Math.Atan2(tp, Math.Cos(dlon));
        var etaP = Asinh(Math.Sin(dlon) / Math.Sqrt(tp * tp + Math.Cos(dlon) * Math.Cos(dlon)));
        double xi = xiP, eta = etaP;
        for (var j = 1; j <= 6; j++)
        {
            xi += _alpha[j - 1] * Math.Sin(2 * j * xiP) * Math.Cosh(2 * j * etaP);
            eta += _alpha[j - 1] * Math.Cos(2 * j * xiP) * Math.Sinh(2 * j * etaP);
        }
        return (xi, eta);
    }

    private static double Asinh(double x) => Math.Log(x + Math.Sqrt(x * x + 1));

    /// <summary>Easting and northing of a latitude and longitude (degrees).</summary>
    public (double E, double N) Forward(double latDeg, double lonDeg)
    {
        var dlon = Wrap(lonDeg - LonOriginDeg) * Math.PI / 180;
        var (xi, eta) = Xi(latDeg * Math.PI / 180, dlon);
        return (FalseEasting + Scale * _a * eta, FalseNorthing + Scale * (_a * xi - _m0));
    }

    /// <summary>Latitude and longitude (degrees) of an easting and northing.</summary>
    public (double LatDeg, double LonDeg) Inverse(double easting, double northing)
    {
        var xi = (northing - FalseNorthing + Scale * _m0) / (Scale * _a);
        var eta = (easting - FalseEasting) / (Scale * _a);
        double xiP = xi, etaP = eta;
        for (var j = 1; j <= 6; j++)
        {
            xiP -= _beta[j - 1] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
            etaP -= _beta[j - 1] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
        }
        var tp = Math.Sin(xiP) / Math.Sqrt(Math.Sinh(etaP) * Math.Sinh(etaP) + Math.Cos(xiP) * Math.Cos(xiP));
        var dlon = Math.Atan2(Math.Sinh(etaP), Math.Cos(xiP));
        // τ from τ′ by Newton's method (Karney 2011, eq. 19–21).
        var tau = tp;
        for (var i = 0; i < 8; i++)
        {
            var tpi = TauPrime(tau);
            var step = (tp - tpi) / Math.Sqrt(1 + tpi * tpi) * (1 + (1 - _e2) * tau * tau) / ((1 - _e2) * Math.Sqrt(1 + tau * tau));
            tau += step;
            if (Math.Abs(step) < 1e-15 * Math.Max(1, Math.Abs(tau)))
                break;
        }
        return (Math.Atan(tau) * 180 / Math.PI, Wrap(LonOriginDeg + dlon * 180 / Math.PI));
    }

    /// <summary>The point scale factor at a latitude and longitude: the grid's length over the ellipsoid's along a short meridian step.</summary>
    public double PointScale(double latDeg, double lonDeg)
    {
        const double d = 1e-5;
        var (e0, n0) = Forward(latDeg - d, lonDeg);
        var (e1, n1) = Forward(latDeg + d, lonDeg);
        var grid = Math.Sqrt((e1 - e0) * (e1 - e0) + (n1 - n0) * (n1 - n0));
        var ground = Ellipsoid.Meridian(latDeg * Math.PI / 180) * 2 * d * Math.PI / 180;
        return grid / ground;
    }

    private static double Wrap(double deg)
    {
        deg %= 360;
        return deg > 180 ? deg - 360 : deg < -180 ? deg + 360 : deg;
    }
}

/// <summary>Universal Transverse Mercator: 6° zones from 180°W, scale 0.9996 on the central meridian, false easting 500 km, false
/// northing 10 000 km in the south.</summary>
public static class Utm
{
    public const double ScaleFactor = 0.9996;

    public static int ZoneOf(double lonDeg) => Math.Clamp((int)Math.Floor((lonDeg + 180) / 6) + 1, 1, 60);

    public static double CentralMeridian(int zone) => -183 + 6 * zone;

    public static TransverseMercator Zone(int zone, bool north, Ellipsoid ellipsoid)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(zone, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(zone, 60);
        return new TransverseMercator(ellipsoid, 0, CentralMeridian(zone), ScaleFactor, 500_000, north ? 0 : 10_000_000);
    }
}

public enum CrsKind { Unknown, Local, Projected, Geographic }

/// <summary>
/// A coordinate system PlayMap can convert, from its EPSG code (the families of EPSG's dataset: WGS 84 / UTM north 326zz and south
/// 327zz, ETRS89 / UTM 258zz (zones 28–38), NAD83 / UTM 269zz (zones 1–23), geographic WGS 84 (4326, 3D 4979) and ETRS89 (4258)), or
/// sniffed from WKT: its outermost EPSG authority, else a projected system named by datum and UTM zone. Anything else is Unknown: a
/// file in it is refused with its words, not guessed at.
/// </summary>
public sealed partial record CrsInfo(string Code, string Name, CrsKind Kind, string Datum, Ellipsoid Ellipsoid, int Zone, bool North)
{
    public static readonly CrsInfo Local = new("LOCAL", "local", CrsKind.Local, "", Ellipsoid.Grs80, 0, true);

    /// <summary>The projection of a projected system (null otherwise).</summary>
    public TransverseMercator? Projection => Kind == CrsKind.Projected ? Utm.Zone(Zone, North, Ellipsoid) : null;

    /// <summary>The name a site frame shows for a code ("ETRS89 / UTM zone 32N"), "" for LOCAL or a code not recognised.</summary>
    public static string NameOf(string? code) => Of(code) is { Kind: CrsKind.Projected or CrsKind.Geographic } c ? c.Name : "";

    /// <summary>"EPSG:25832", "25832", "LOCAL"; null for anything not recognised.</summary>
    public static CrsInfo? Of(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;
        var c = code.Trim();
        if (c.Equals("LOCAL", StringComparison.OrdinalIgnoreCase))
            return Local;
        if (c.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase))
            c = c[5..];
        if (!int.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out var epsg))
            return null;
        return epsg switch
        {
            4326 => new("EPSG:4326", "WGS 84", CrsKind.Geographic, "WGS 84", Ellipsoid.Wgs84, 0, true),
            4979 => new("EPSG:4979", "WGS 84 (3D)", CrsKind.Geographic, "WGS 84", Ellipsoid.Wgs84, 0, true),
            4258 => new("EPSG:4258", "ETRS89", CrsKind.Geographic, "ETRS89", Ellipsoid.Grs80, 0, true),
            >= 32601 and <= 32660 => Projected(epsg, "WGS 84", Ellipsoid.Wgs84, epsg - 32600, true),
            >= 32701 and <= 32760 => Projected(epsg, "WGS 84", Ellipsoid.Wgs84, epsg - 32700, false),
            >= 25828 and <= 25838 => Projected(epsg, "ETRS89", Ellipsoid.Grs80, epsg - 25800, true),
            >= 26901 and <= 26923 => Projected(epsg, "NAD83", Ellipsoid.Grs80, epsg - 26900, true),
            _ => null,
        };
    }

    private static CrsInfo Projected(int epsg, string datum, Ellipsoid ellipsoid, int zone, bool north) =>
        new(string.Create(CultureInfo.InvariantCulture, $"EPSG:{epsg}"),
            string.Create(CultureInfo.InvariantCulture, $"{datum} / UTM zone {zone}{(north ? "N" : "S")}"), CrsKind.Projected, datum, ellipsoid, zone, north);

    /// <summary>The EPSG code of a projected UTM system named by datum, zone and hemisphere, or "".</summary>
    public static string UtmCode(string datum, int zone, bool north) => (datum.ToUpperInvariant(), north) switch
    {
        ("WGS 84" or "WGS84", true) when zone is >= 1 and <= 60 => string.Create(CultureInfo.InvariantCulture, $"EPSG:{32600 + zone}"),
        ("WGS 84" or "WGS84", false) when zone is >= 1 and <= 60 => string.Create(CultureInfo.InvariantCulture, $"EPSG:{32700 + zone}"),
        ("ETRS89", true) when zone is >= 28 and <= 38 => string.Create(CultureInfo.InvariantCulture, $"EPSG:{25800 + zone}"),
        ("NAD83", true) when zone is >= 1 and <= 23 => string.Create(CultureInfo.InvariantCulture, $"EPSG:{26900 + zone}"),
        _ => "",
    };

    [GeneratedRegex(@"(?:AUTHORITY|ID)\[\s*""EPSG""\s*,\s*""?(\d+)""?", RegexOptions.IgnoreCase)]
    private static partial Regex WktAuthority();

    [GeneratedRegex(@"(?:PROJCS|PROJCRS|PROJECTEDCRS)\[\s*""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex WktProjectedName();

    [GeneratedRegex(@"(WGS[\s_]*(?:19)?84|ETRS[\s_]*(?:19)?89|NAD[\s_]*(?:19)?83)(?![0-9]).*?UTM[\s_]*zone[\s_]*(\d{1,2})\s*([NS])\b", RegexOptions.IgnoreCase)]
    private static partial Regex UtmName();

    /// <summary>The EPSG code a WKT text names: the authority of its root element itself (WKT1 and WKT2 put it last, at the root's own
    /// level — an authority nested in a unit, a datum or the base geographic system names that, not the system); for a compound system
    /// its horizontal part's; else a UTM system named by datum and zone in its projected name ("ETRS89 / UTM zone 32N",
    /// "WGS_1984_UTM_Zone_33N"); "" when it names neither.</summary>
    public static string SniffWkt(string wkt)
    {
        ArgumentNullException.ThrowIfNull(wkt);
        var text = wkt.TrimStart();
        var compound = text.StartsWith("COMPD_CS", StringComparison.OrdinalIgnoreCase) || text.StartsWith("COMPOUNDCRS", StringComparison.OrdinalIgnoreCase);
        var want = compound ? 2 : 1;
        foreach (Match m in WktAuthority().Matches(text))
        {
            // A text that is an authority alone (as E57's coordinate metadata may be) names its system directly.
            if (m.Index == 0)
                return "EPSG:" + m.Groups[1].Value;
            if (Depth(text, m.Index) != want)
                continue;
            // In a compound system the first element at depth 2 is the horizontal one; its own authority is what is wanted.
            if (!compound || FirstChildEnds(text) > m.Index)
                return "EPSG:" + m.Groups[1].Value;
        }
        if (WktProjectedName().Match(text) is not { Success: true } named || UtmName().Match(named.Groups[1].Value) is not { Success: true } utm)
            return "";
        var datum = utm.Groups[1].Value.ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal) switch
        {
            "WGS84" or "WGS1984" => "WGS 84",
            "ETRS89" or "ETRS1989" => "ETRS89",
            _ => "NAD83",
        };
        var zone = int.Parse(utm.Groups[2].Value, CultureInfo.InvariantCulture);
        return UtmCode(datum, zone, utm.Groups[3].Value.Equals("N", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The bracket depth at a position (0 outside the root element).</summary>
    private static int Depth(string text, int at)
    {
        var depth = 0;
        var quoted = false;
        for (var i = 0; i < at; i++)
        {
            var c = text[i];
            if (c == '"')
                quoted = !quoted;
            else if (!quoted && c is '[' or '(')
                depth++;
            else if (!quoted && c is ']' or ')')
                depth--;
        }
        return depth;
    }

    /// <summary>Where the root's first child element (after the root's name) ends, or −1.</summary>
    private static int FirstChildEnds(string text)
    {
        var depth = 0;
        var quoted = false;
        var opened = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (quoted)
                continue;
            if (c is '[' or '(')
            {
                depth++;
                if (depth == 2)
                    opened = true;
            }
            else if (c is ']' or ')')
            {
                if (depth == 2 && opened)
                    return i + 1;
                depth--;
            }
        }
        return -1;
    }

    /// <summary>Geodetic coordinates to this system's coordinates: a projected system's (E, N, h), a geographic one's (lon, lat, h).</summary>
    public Vec3d FromGeodetic(Geodetic g)
    {
        switch (Kind)
        {
            case CrsKind.Projected:
                var (e, n) = Projection!.Forward(g.LatDeg, g.LonDeg);
                return new Vec3d(e, n, g.H);
            case CrsKind.Geographic:
                return new Vec3d(g.LonDeg, g.LatDeg, g.H);
            default:
                throw new InvalidOperationException($"{Code} has no geodetic coordinates");
        }
    }

    /// <summary>This system's coordinates (as <see cref="FromGeodetic"/> writes them) to geodetic.</summary>
    public Geodetic ToGeodetic(Vec3d c)
    {
        switch (Kind)
        {
            case CrsKind.Projected:
                var (lat, lon) = Projection!.Inverse(c.X, c.Y);
                return new Geodetic(lat, lon, c.Z);
            case CrsKind.Geographic:
                return new Geodetic(c.Y, c.X, c.Z);
            default:
                throw new InvalidOperationException($"{Code} has no geodetic coordinates");
        }
    }

    /// <summary>The grid scale at a point of a projected system: the projection's point scale times the elevation factor R/(R + h)
    /// (1 for any other kind).</summary>
    public double GridScaleAt(Vec3d c)
    {
        if (Kind != CrsKind.Projected)
            return 1;
        var g = ToGeodetic(c);
        var r = Math.Sqrt(Ellipsoid.Meridian(g.Lat) * Ellipsoid.PrimeVertical(g.Lat));
        return Projection!.PointScale(g.LatDeg, g.LonDeg) * r / (r + g.H);
    }
}
