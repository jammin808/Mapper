using System.Globalization;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Mapping;

/// <summary>What PLAN LIGHT assumes: the light that reaches the wall after the lens and the air (derate), the projector's evenness
/// (centre to corner), the surface's reflectance, the ambient light and the target.</summary>
public sealed record LightPlanOptions
{
    public static readonly LightPlanOptions Default = new();

    /// <summary>The share of the rated lumens that reaches the wall (Jazé's 0.60–0.70 outdoors).</summary>
    public double Derate { get; init; } = 0.7;

    /// <summary>The corner's light over the centre's (ANSI uniformity).</summary>
    public double Uniformity { get; init; } = 0.85;

    /// <summary>The surface's albedo, 0–1.</summary>
    public double Albedo { get; init; } = 0.5;

    /// <summary>The ambient illuminance on the surface, lux.</summary>
    public double AmbientLux { get; init; }

    /// <summary>The p5 illuminance wanted, lux (100 in town).</summary>
    public double TargetLux { get; init; } = 100;

    /// <summary>The sample spacing (m); 0: the coverage analysis's own.</summary>
    public double SampleSpacing { get; init; }
}

/// <summary>One surface's predicted light: area (m²) and lit share, illuminance p5/p50/p95 (lux), luminance p50 (cd/m²), contrast
/// against the ambient at p5, pixel pitch p50/p95 (mm, the finest projector), the deepest stack reaching it, and the lumens short of
/// the target at p5 (0 when it is met).</summary>
public sealed record SurfaceLight(string SurfaceId, string Name, double AreaM2, double Coverage, double LuxP5, double LuxP50, double LuxP95,
    double NitsP50, double ContrastP5, double PitchMmP50, double PitchMmP95, int StackDepth, double DeficitLm, string Words);

public sealed record LightPlanReport(IReadOnlyList<SurfaceLight> Surfaces, string Words);

/// <summary>
/// The light plan (facade §3.7, predicted): at every coverage sample x, each projector i that reaches it gives
/// E_i(x) = derate·Φ_i·density_i(x)²/(W_i·H_i)·U_i(x) lux — its lumens spread over its raster, the pixel density (projector pixels per
/// metre on the surface, exact for any angle and distance) saying how large its pixels land — with U_i = 1 − (1 − u)·r² falling off
/// to the corners (r: 0 at the raster's centre, 1 at a corner). Projectors in one stack add; separate projectors or stacks overlapping
/// share the point evenly (an even blend). Luminance L = ρE/π; contrast (E + E_a)/E_a; pitch = 1000/density of the finest projector.
/// Percentiles are by area. A projector with no lumens set lights nothing here (the words name it).
/// </summary>
public static class LightPlan
{
    public static LightPlanReport Analyse(StageScene scene, Stage stage, IReadOnlyList<string>? surfaceIds = null, LightPlanOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(stage);
        options ??= LightPlanOptions.Default;
        var projectors = stage.Projectors;
        var report = CoverageAnalysis.Analyse(scene, projectors, new CoverageOptions { SampleSpacing = options.SampleSpacing }, ct);
        var group = new int[projectors.Length];
        for (var q = 0; q < group.Length; q++)
        {
            var id = projectors[q].Id;
            var stack = stage.Stacks.FirstOrDefault(s => s.ProjectorIds.Contains(id, StringComparer.Ordinal));
            group[q] = stack is null ? q : projectors.IndexOf(projectors.FirstOrDefault(p => stack.ProjectorIds.Contains(p.Id, StringComparer.Ordinal))!);
        }
        var rotations = projectors.Select(p => p.Pose.CameraToWorld.Transposed()).ToArray();
        var surfaces = new List<SurfaceLight>();
        var unlit = projectors.Where(p => p.Lumens <= 0).Select(p => p.Name).ToList();
        for (var si = 0; si < scene.Surfaces.Count; si++)
        {
            ct.ThrowIfCancellationRequested();
            var surface = scene.Surfaces[si];
            if (surfaceIds is not null && !surfaceIds.Contains(surface.Id, StringComparer.Ordinal))
                continue;
            var mine = report.Samples.Where(x => x.Surface == si).ToArray();
            var area = mine.Sum(x => x.Area);
            var lit = mine.Where(x => x.Reach != 0).Sum(x => x.Area);
            var luxAt = new double[mine.Length];
            var depthAt = new int[mine.Length];
            var reached = new ulong[mine.Length];
            var projectorCount = Math.Min(64, projectors.Length);
            Parallel.For(0, mine.Length, new ParallelOptions { CancellationToken = ct }, () => new int[projectorCount], (i, _, counts) =>
            {
                var s = mine[i];
                if (s.Reach == 0)
                    return counts;
                // The pixel density at the point from a millimetre's step along the surface's two tangents.
                var t1 = Math.Abs(s.Normal.Y) < 0.9 ? Vec3d.Cross(s.Normal, Vec3d.UnitY).Normalized() : Vec3d.Cross(s.Normal, Vec3d.UnitX).Normalized();
                var t2 = Vec3d.Cross(s.Normal, t1).Normalized();
                Array.Clear(counts);
                int groupCount = 0, deepest = 0;
                for (var q = 0; q < projectorCount; q++)
                {
                    if ((s.Reach & (1UL << q)) == 0)
                        continue;
                    if (counts[group[q]]++ == 0)
                        groupCount++;
                    deepest = Math.Max(deepest, counts[group[q]]);
                }
                var sum = 0.0;
                ulong used = 0;
                for (var q = 0; q < projectorCount; q++)
                {
                    if ((s.Reach & (1UL << q)) == 0)
                        continue;
                    var p = projectors[q];
                    if (p.Lumens <= 0)
                        continue;
                    var k = p.Intrinsics;
                    var at = CameraModel.ProjectCamera(rotations[q] * (s.Point - p.Pose.Position), k, p.Lens);
                    var a1 = CameraModel.ProjectCamera(rotations[q] * (s.Point + t1 * 1e-3 - p.Pose.Position), k, p.Lens);
                    var a2 = CameraModel.ProjectCamera(rotations[q] * (s.Point + t2 * 1e-3 - p.Pose.Position), k, p.Lens);
                    if (at is not { } px || a1 is not { } b1 || a2 is not { } b2)
                        continue;
                    var density = Math.Sqrt(Math.Abs(Vec2d.Cross((b1 - px) / 1e-3, (b2 - px) / 1e-3)));
                    var dx = (px.X - k.Width / 2.0) / (k.Width / 2.0);
                    var dy = (px.Y - k.Height / 2.0) / (k.Height / 2.0);
                    var r2 = Math.Min(1, (dx * dx + dy * dy) / 2);
                    var u = 1 - (1 - Math.Clamp(options.Uniformity, 0, 1)) * r2;
                    sum += options.Derate * p.Lumens * density * density / ((double)k.Width * k.Height) * u;
                    used |= 1UL << q;
                }
                luxAt[i] = groupCount > 0 ? sum / groupCount : 0;
                depthAt[i] = deepest;
                reached[i] = used;
                return counts;
            }, _ => { });
            var lux = new List<(double Value, double Area)>();
            var pitch = new List<(double Value, double Area)>();
            var depth = 0;
            ulong reachAll = 0;
            for (var i = 0; i < mine.Length; i++)
            {
                if (mine[i].Reach == 0)
                    continue;
                lux.Add((luxAt[i], mine[i].Area));
                if (mine[i].Density > 0)
                    pitch.Add((1000 / mine[i].Density, mine[i].Area));
                depth = Math.Max(depth, depthAt[i]);
                reachAll |= reached[i];
            }
            var reachLumens = Enumerable.Range(0, projectorCount).Where(q => (reachAll & (1UL << q)) != 0).ToList();
            if (area <= 0)
                continue;
            var p5 = Quantile(lux, 0.05);
            var p50 = Quantile(lux, 0.5);
            var p95 = Quantile(lux, 0.95);
            var ambient = Math.Max(options.AmbientLux, 0);
            var contrast = ambient > 0 ? (p5 + ambient) / ambient : double.PositiveInfinity;
            var lumensReaching = reachLumens.Sum(q => projectors[q].Lumens);
            var deficit = p5 >= options.TargetLux ? 0 : p5 > 0 ? (options.TargetLux - p5) / p5 * lumensReaching : double.PositiveInfinity;
            var words = string.Create(CultureInfo.InvariantCulture,
                $"{surface.Name}: {p5:0} lux at p5, {p50:0} at p50 (target {options.TargetLux:0})")
                + (deficit > 0 && double.IsFinite(deficit) ? string.Create(CultureInfo.InvariantCulture, $" — {deficit:0} lm short") : "")
                + (ambient > 0 ? string.Create(CultureInfo.InvariantCulture, $"; contrast {contrast:0.0}:1 against {ambient:0} lux") : "");
            surfaces.Add(new SurfaceLight(surface.Id, surface.Name, area, lit / area, p5, p50, p95, options.Albedo * p50 / Math.PI, contrast,
                Quantile(pitch, 0.5), Quantile(pitch, 0.95), depth, deficit, words));
        }
        var summary = string.Join("; ", surfaces.Select(s => s.Words))
            + (unlit.Count > 0 ? $" ({string.Join(", ", unlit)} {(unlit.Count == 1 ? "has" : "have")} no lumens set and light nothing here)" : "");
        return new LightPlanReport(surfaces, summary.Length > 0 ? summary : "no visible surface to light");
    }

    /// <summary>The q-quantile weighted by area (0 for none).</summary>
    private static double Quantile(List<(double Value, double Area)> values, double q)
    {
        if (values.Count == 0)
            return 0;
        values.Sort((a, b) => a.Value.CompareTo(b.Value));
        var total = values.Sum(v => v.Area);
        var want = q * total;
        double run = 0;
        foreach (var (value, a) in values)
        {
            run += a;
            if (run >= want)
                return value;
        }
        return values[^1].Value;
    }
}
