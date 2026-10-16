using System.Globalization;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Geometry;

/// <summary>How a geometry reader places what it reads: the file's unit (metres per file unit), whether the file is Z-up (survey
/// grids: x east, y north, z up) or Y-up (the stage's own convention), and the site frame its coordinates are rebased into. Every
/// step runs in double on the value as read, and only the rebased stage coordinate is narrowed to float, so a facade half a
/// million metres from its grid's origin keeps millimetres.
/// <para>A file point p becomes the grid point g = (p·UnitScale) for a Z-up file, or (x, −z, y)·UnitScale for a Y-up one, then
/// the stage point <see cref="SiteFrame.ToStage"/>(g). With <see cref="AutoSite"/> and no <see cref="Site"/>, the frame's origin is
/// the grid bounding box's centre rounded to 10 m (so it can be read out loud). <see cref="AsStored"/> leaves every value exactly as
/// stored.</para></summary>
public sealed record GeometryReadOptions(double UnitScale, bool ZUp, SiteFrame? Site, bool AutoSite = false, string Crs = "")
{
    /// <summary>Coordinates exactly as stored (no unit, axis or origin change).</summary>
    public static readonly GeometryReadOptions AsStored = new(1, false, null);

    /// <summary>A frame's origin is rounded to this many metres when a reader picks it.</summary>
    public const double OriginRoundingM = 10;

    /// <summary>A site wider than this (metres) is refused: float spacing passes 0.1 mm beyond it.</summary>
    public const double MaxExtentM = 2000;

    /// <summary>True when every value is kept exactly as stored.</summary>
    public bool IsAsStored => Site is null && !AutoSite && UnitScale == 1 && !ZUp;

    /// <summary>The grid point (E, N, H in metres) of a file point.</summary>
    public Vec3d ToGrid(Vec3d file)
    {
        var s = UnitScale;
        return ZUp ? new Vec3d(file.X * s, file.Y * s, file.Z * s) : new Vec3d(file.X * s, -file.Z * s, file.Y * s);
    }

    /// <summary>The frame a reader rebases into after seeing the file's points: the given site, a new one centred on the points
    /// (<see cref="AutoSite"/>), or none (as stored). Throws <see cref="FormatException"/> when the points span more than
    /// <see cref="MaxExtentM"/> horizontally.</summary>
    public SiteFrame? Resolve(IEnumerable<Vec3d> filePoints)
    {
        ArgumentNullException.ThrowIfNull(filePoints);
        if (IsAsStored)
            return null;
        var any = false;
        var min = new Vec3d(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3d(double.MinValue, double.MinValue, double.MinValue);
        foreach (var p in filePoints)
        {
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z))
                continue;
            var g = ToGrid(p);
            min = Vec3d.Min(min, g);
            max = Vec3d.Max(max, g);
            any = true;
        }
        if (!any)
            return Site ?? (AutoSite ? SiteFrame.None : null);
        var span = Math.Max(max.X - min.X, max.Y - min.Y);
        if (span > MaxExtentM)
        {
            throw new FormatException(string.Create(CultureInfo.InvariantCulture,
                $"the file spans {span:0} m — a site is at most {MaxExtentM:0} m across: split the site"));
        }
        if (Site is { } site)
            return site;
        if (!AutoSite)
            return null;
        var c = (min + max) / 2;
        var origin = new Vec3d(Round(c.X), Round(c.Y), Round(c.Z));
        return new SiteFrame(Crs.Length > 0 ? Crs : "LOCAL", "", origin, 0, 1, 0);
    }

    private static double Round(double v) => Math.Round(v / OriginRoundingM, MidpointRounding.AwayFromZero) * OriginRoundingM;

    /// <summary>The stage point of a file point in the resolved frame (null: as stored).</summary>
    public Vec3d Apply(Vec3d file, SiteFrame? frame) => frame is null ? file : frame.ToStage(ToGrid(file));

    /// <summary>A direction (a normal) of the file in the stage's axes: the unit and origin do not apply.</summary>
    public Vec3d ApplyDirection(Vec3d file, SiteFrame? frame)
    {
        if (frame is null)
            return file;
        // A Y-up file's (x, up, south) is the grid's (east, north, up) = (x, −south, up).
        double east = file.X, north = -file.Z, up = file.Y;
        var grid = ZUp ? file : new Vec3d(east, north, up);
        return frame.ToStage(frame.Origin + grid).Normalized();
    }
}
