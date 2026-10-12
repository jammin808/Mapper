using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Mapping;

/// <summary>Brightness falloff from the projectors' own geometry, and the gain that evens it out. A projector
/// lights a point with irradiance ∝ lumens × cos θ / d² × cos⁴ α — θ the angle of incidence on the surface,
/// d the throw, α the angle off the lens axis (the natural vignetting of a flat image plane). Evening it out
/// can only take light away (a projector cannot exceed its white), so full uniformity brings every point down
/// to the dimmest; the uniformity setting trades that against brightness: gain = (target / E)^u, 0 none,
/// 1 full equalisation. Pure.</summary>
public static class IntensityCompensation
{
    /// <summary>The relative irradiance projector <paramref name="p"/> puts on a surface point with unit
    /// normal <paramref name="normal"/> (either side), lumens counted when <paramref name="useLumens"/> and
    /// known (unknown lumens count as 1).</summary>
    public static double Irradiance(Projector p, Vec3d point, Vec3d normal, bool useLumens = true)
    {
        ArgumentNullException.ThrowIfNull(p);
        var to = point - p.Pose.Position;
        var d2 = to.LengthSquared;
        if (d2 <= 0)
            return 0;
        var dir = to / Math.Sqrt(d2);
        var cosTheta = Math.Abs(Vec3d.Dot(dir, normal));
        var cosAlpha = Math.Max(0, Vec3d.Dot(dir, p.Pose.Forward));
        var c2 = cosAlpha * cosAlpha;
        var lumens = useLumens && p.Lumens > 0 ? p.Lumens : 1;
        return lumens * cosTheta / d2 * c2 * c2;
    }

    /// <summary>The gain that brings irradiance <paramref name="e"/> toward <paramref name="target"/>:
    /// (target / e)^uniformity, never above 1 (a projector cannot add light it does not have).</summary>
    public static double Gain(double e, double target, double uniformity)
    {
        uniformity = Math.Clamp(uniformity, 0, 1);
        if (uniformity <= 0 || e <= 0 || target <= 0)
            return 1;
        return Math.Min(1, Math.Pow(target / e, uniformity));
    }

    /// <summary>The level full uniformity equalises to: the given low percentile of the irradiance over the
    /// covered pixels (0 = the very dimmest; a little above 0 ignores a few grazing pixels that would
    /// otherwise darken everything).</summary>
    public static double Target(IEnumerable<float> irradiance, double percentile = 0)
    {
        ArgumentNullException.ThrowIfNull(irradiance);
        var values = irradiance.Where(e => e > 0).ToArray();
        if (values.Length == 0)
            return 0;
        Array.Sort(values);
        var k = (int)Math.Clamp(Math.Floor(Math.Clamp(percentile, 0, 1) * (values.Length - 1)), 0, values.Length - 1);
        return values[k];
    }
}
