using System.Globalization;
using PlayMap.Core.Bus;
using PlayMap.Core.Calibration;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Site;

/// <summary>One control point measured against the model: its distance to the nearest point of the model's surface and its light.</summary>
public sealed record ControlCheckRow(string Id, string Name, double DistanceM, Light Light, string Words);

/// <summary>A model measured against the control points: each point's distance and light, the similarity that best moves the model
/// onto them (shift at the points' centre in metres, turn in degrees, scale in ppm), the worst light, and the words.</summary>
public sealed record SiteCheckResult(IReadOnlyList<ControlCheckRow> Points, double ShiftM, double RotationDeg, double ScalePpm, Light Light,
    string Words);

/// <summary>SITE CHECK's measure (facade.md §3.3): each enabled control point placed by the site frame, its distance to the model's
/// surface; green within max(2σ, 2 cm), amber within 5 cm, red beyond. Umeyama over point ↔ nearest-surface pairs (scale free, three
/// points or more) says whether the model is shifted, turned or scaled, and a scale beyond the grid's own (|k − 1| + 100 ppm) is named
/// as grid against ground metres.</summary>
public static class SiteCheck
{
    public const double GreenM = 0.02;
    public const double AmberM = 0.05;

    /// <param name="nearest">The nearest point of the model's surface to a stage point, and its distance (infinity: none).</param>
    public static SiteCheckResult Run(IReadOnlyList<ControlPoint> points, SiteFrame site, Func<Vec3d, (Vec3d Point, double Distance)> nearest,
        string model)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(nearest);
        var inv = CultureInfo.InvariantCulture;
        var rows = new List<ControlCheckRow>();
        var from = new List<Vec3d>();
        var to = new List<Vec3d>();
        foreach (var cp in points.Where(c => c.Enabled))
        {
            var at = site.ToStage(cp.Grid);
            var (q, d) = nearest(at);
            var light = !double.IsFinite(d) ? Light.Red : d <= Math.Max(2 * cp.SigmaM, GreenM) ? Light.Green : d <= AmberM ? Light.Amber : Light.Red;
            var words = double.IsFinite(d) ? string.Create(inv, $"{cp.Name} {Cm(d)} off the model") : $"{cp.Name}: the model is not near it";
            rows.Add(new ControlCheckRow(cp.Id, cp.Name, d, light, words));
            if (double.IsFinite(d))
            {
                from.Add(q);
                to.Add(at);
            }
        }
        if (rows.Count == 0)
            return new SiteCheckResult(rows, 0, 0, 0, Light.Grey, "no enabled control points — SITE POINTS imports them");
        double shift = 0, turn = 0, ppm = 0;
        if (from.Count >= 3)
        {
            var fit = Umeyama.Fit(from, to, allowScale: true);
            var centre = new Vec3d(from.Average(p => p.X), from.Average(p => p.Y), from.Average(p => p.Z));
            shift = (fit.Apply(centre) - centre).Length;
            turn = fit.AngleDegrees;
            ppm = (fit.Scale - 1) * 1e6;
        }
        var worst = rows.Max(r => r.Light);
        var far = rows.Where(r => r.Light != Light.Green).OrderByDescending(r => r.DistanceM).ToList();
        string Summary()
        {
            if (far.Count == 0)
                return string.Create(inv, $"{model} agrees with the {rows.Count} control points (worst {Cm(rows.Max(r => r.DistanceM))})");
            var named = string.Join(", ", far.Take(6).Select(r => r.Words)) + (far.Count > 6 ? string.Create(inv, $" and {far.Count - 6} more") : "");
            var why = new List<string>();
            var scaleLimit = Math.Abs(site.GridScale - 1) * 1e6 + 100;
            if (Math.Abs(ppm) > scaleLimit && from.Count >= 3)
                why.Add(string.Create(inv, $"the model is {Math.Abs(ppm) / 1e4:0.##} % {(ppm > 0 ? "small" : "large")}: grid against ground metres? (k = {site.GridScale:0.#####})"));
            if (shift > GreenM)
                why.Add(string.Create(inv, $"it is shifted {Cm(shift)}"));
            if (turn > 0.05)
                why.Add(string.Create(inv, $"turned {turn:0.##}°"));
            return named + (why.Count > 0 ? " — " + string.Join(", ", why) : "");
        }
        return new SiteCheckResult(rows, shift, turn, ppm, worst, Summary());
    }

    private static string Cm(double m) => m < 0.1
        ? string.Create(CultureInfo.InvariantCulture, $"{m * 100:0.#} cm")
        : string.Create(CultureInfo.InvariantCulture, $"{m * 100:0} cm");
}
