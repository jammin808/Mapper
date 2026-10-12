using PlayMap.Core.Numerics;

namespace PlayMap.Core.Calibration;

/// <summary>The perspective-three-point problem (Grunert's solution): three world points and their unit
/// bearings in a calibrated device give up to four poses. The pose solver uses it to start from four or five
/// pins off one plane, where neither the DLT (six or more) nor a plane homography applies.</summary>
internal static class P3P
{
    /// <summary>Every world→camera (R, t) with R·pᵢ + t on bearing jᵢ (unit vectors in the device frame).</summary>
    public static List<(Mat3d R, Vec3d T)> Solve(Vec3d p1, Vec3d p2, Vec3d p3, Vec3d j1, Vec3d j2, Vec3d j3)
    {
        var poses = new List<(Mat3d, Vec3d)>(4);
        var a2 = (p2 - p3).LengthSquared;
        var b2 = (p1 - p3).LengthSquared;
        var c2 = (p1 - p2).LengthSquared;
        if (a2 < 1e-24 || b2 < 1e-24 || c2 < 1e-24)
            return poses;
        var ca = Vec3d.Dot(j2, j3);
        var cb = Vec3d.Dot(j1, j3);
        var cg = Vec3d.Dot(j1, j2);
        var amc = (a2 - c2) / b2;
        var apc = (a2 + c2) / b2;
        var bmc = (b2 - c2) / b2;
        var bma = (b2 - a2) / b2;
        // Grunert's quartic in v = s₃/s₁ (Haralick et al.'s form).
        var a4 = (amc - 1) * (amc - 1) - 4 * c2 / b2 * ca * ca;
        var a3 = 4 * (amc * (1 - amc) * cb - (1 - apc) * ca * cg + 2 * c2 / b2 * ca * ca * cb);
        var a2q = 2 * (amc * amc - 1 + 2 * amc * amc * cb * cb + 2 * bmc * ca * ca - 4 * apc * ca * cb * cg + 2 * bma * cg * cg);
        var a1 = 4 * (-amc * (1 + amc) * cb + 2 * a2 / b2 * cg * cg * cb - (1 - apc) * ca * cg);
        var a0 = (1 + amc) * (1 + amc) - 4 * a2 / b2 * cg * cg;
        foreach (var v in Polynomial.RealRoots(a4, a3, a2q, a1, a0))
        {
            var den = 2 * (cg - v * ca);
            if (Math.Abs(den) < 1e-14)
                continue;
            var u = ((-1 + amc) * v * v - 2 * amc * cb * v + 1 + amc) / den;
            var s1Sq = b2 / (1 + v * v - 2 * v * cb);
            if (!(s1Sq > 0))
                continue;
            var s1 = Math.Sqrt(s1Sq);
            var s2 = u * s1;
            var s3 = v * s1;
            if (s2 <= 0 || s3 <= 0)
                continue;
            var fit = Umeyama.Fit([p1, p2, p3], [j1 * s1, j2 * s2, j3 * s3], allowScale: false);
            poses.Add((fit.Rotation, fit.Translation));
        }
        return poses;
    }
}

/// <summary>Real roots of low-degree polynomials.</summary>
internal static class Polynomial
{
    /// <summary>The real roots of a₄x⁴ + a₃x³ + a₂x² + a₁x + a₀ (lower degree when leading terms vanish):
    /// the stationary points split the line into monotone pieces, each sign change is bisected and polished.</summary>
    public static List<double> RealRoots(double a4, double a3, double a2, double a1, double a0)
    {
        var scale = Math.Max(Math.Max(Math.Abs(a4), Math.Abs(a3)), Math.Max(Math.Max(Math.Abs(a2), Math.Abs(a1)), Math.Abs(a0)));
        if (scale < 1e-300)
            return [];
        double[] c = [a0 / scale, a1 / scale, a2 / scale, a3 / scale, a4 / scale];
        var degree = 4;
        while (degree > 0 && Math.Abs(c[degree]) < 1e-14)
            degree--;
        if (degree == 0)
            return [];
        if (degree == 1)
            return [-c[0] / c[1]];
        // Stationary points from the derivative's roots (one degree lower, recursively).
        var d = new double[5];
        for (var k = 1; k <= degree; k++)
            d[k - 1] = k * c[k];
        var stationary = RealRoots(d[4], d[3], d[2], d[1], d[0]);
        stationary.Sort();
        // Cauchy's bound holds every root.
        var bound = 1.0;
        for (var k = 0; k < degree; k++)
            bound = Math.Max(bound, 1 + Math.Abs(c[k] / c[degree]));
        var edges = new List<double> { -bound };
        edges.AddRange(stationary.Where(x => x > -bound && x < bound));
        edges.Add(bound);
        double P(double x)
        {
            var y = 0.0;
            for (var k = degree; k >= 0; k--)
                y = y * x + c[k];
            return y;
        }
        var roots = new List<double>();
        for (var i = 0; i + 1 < edges.Count; i++)
        {
            double lo = edges[i], hi = edges[i + 1];
            double flo = P(lo), fhi = P(hi);
            if (flo == 0)
            {
                roots.Add(lo);
                continue;
            }
            if (Math.Sign(flo) == Math.Sign(fhi))
                continue;
            for (var it = 0; it < 200 && hi - lo > 1e-15 * Math.Max(1, Math.Abs(lo)); it++)
            {
                var mid = 0.5 * (lo + hi);
                var fm = P(mid);
                if (Math.Sign(fm) == Math.Sign(flo))
                {
                    lo = mid;
                    flo = fm;
                }
                else
                {
                    hi = mid;
                }
            }
            roots.Add(0.5 * (lo + hi));
        }
        if (Math.Abs(P(edges[^1])) == 0)
            roots.Add(edges[^1]);
        // A double root touches zero at a stationary point without a sign change.
        foreach (var s in stationary)
            if (Math.Abs(P(s)) < 1e-12 && !roots.Exists(r => Math.Abs(r - s) < 1e-9))
                roots.Add(s);
        return roots;
    }
}
