using System.Globalization;
using PlayMap.Core.Calibration;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Align;

/// <summary>What the rectification may assume about one output's projector: its raster, its principal point (where its lens axis
/// meets the raster) and its focal length in pixels with a relative uncertainty.</summary>
public sealed record ProjectorPrior(int Width, int Height, double Cx, double Cy, double Focal, double FocalSigma)
{
    /// <summary>A projector nobody described: centred lens, a throw of 1.5, ±30 %.</summary>
    public static ProjectorPrior Unknown(int width, int height) => new(width, height, width / 2.0, height / 2.0, 1.5 * width, 0.3);
}

/// <summary>The screen's plane as the canvas measures it: a homography from the undistorted normalised camera point to plane
/// coordinates (x right, y down), metric up to scale when <see cref="Source"/> is "corners" or "projectors", the camera's own view
/// otherwise; the words say which.</summary>
public sealed record Rectification(Mat3d ToPlane, string Source, string Words)
{
    private readonly Mat3d _fromPlane = Homography.Inverse(ToPlane);

    public Vec2d Apply(Vec2d undistorted) => ToPlane.TransformPoint(undistorted);

    public Vec2d Inverse(Vec2d plane) => _fromPlane.TransformPoint(plane);
}

/// <summary>
/// Metric rectification of the screen's plane in the camera's undistorted picture. From the screen's corners (four clicks, top-left
/// clockwise) the plane is the rectangle of the canvas's aspect, exactly. Without them, from the projectors: each projector's pixels
/// are square and its principal point known, so the plane → projector homography G = H·T⁻¹ must have K⁻¹G's first two columns
/// orthogonal and of equal length; the unknowns are T's four degrees beyond a similarity (the plane's vanishing line l and the
/// affine α, β of Liebowitz and Zisserman) and each projector's focal length, held to its prior. Two projectors pin it; one cannot,
/// and a fit that fails or puts the vanishing line through the picture falls back to the camera's own view with words. The result is
/// turned so the projectors' median "up" is up and their "right" is right. Pure.
/// </summary>
public static class PlaneRectify
{
    /// <summary>How far an orthogonality or equal-length condition may be off before it costs as much as one prior sigma.</summary>
    private const double ConditionSigma = 0.01;

    /// <summary>The plane from the screen's corners (undistorted normalised camera points, TL TR BR BL) as [0, aspect] × [0, 1].</summary>
    public static Rectification FromCorners(IReadOnlyList<Vec2d> corners, double aspect)
    {
        ArgumentNullException.ThrowIfNull(corners);
        if (corners.Count != 4)
            throw new ArgumentException("Four corners, top-left clockwise.", nameof(corners));
        if (!(aspect > 0))
            throw new ArgumentOutOfRangeException(nameof(aspect));
        Span<Vec2d> from = [corners[0], corners[1], corners[2], corners[3]];
        Span<Vec2d> to = [new(0, 0), new(aspect, 0), new(aspect, 1), new(0, 1)];
        var h = Homography.QuadToQuad(from, to);
        return new Rectification(h, "corners", "the canvas is the screen's corners as clicked");
    }

    /// <summary>The plane from the projectors' homographies (undistorted normalised camera point → raster pixel) and priors.
    /// <paramref name="coverage"/> are undistorted points the plane must keep in front of the vanishing line.</summary>
    public static Rectification FromProjectors(IReadOnlyList<Mat3d> homographies, IReadOnlyList<ProjectorPrior> priors, IReadOnlyList<Vec2d> coverage)
    {
        ArgumentNullException.ThrowIfNull(homographies);
        ArgumentNullException.ThrowIfNull(priors);
        ArgumentNullException.ThrowIfNull(coverage);
        if (homographies.Count != priors.Count)
            throw new ArgumentException("One prior per homography.", nameof(priors));
        if (homographies.Count < 2)
            return Fronto(homographies, priors, "one projector cannot tell the screen's slant: the canvas is the camera's view — ALIGN CORNERS gives an exact one");
        var n = homographies.Count;
        var init = new double[4 + n];
        init[3] = 1;
        for (var j = 0; j < n; j++)
            init[4 + j] = priors[j].Focal;
        var lm = LevenbergMarquardt.Solve(new Conditions(homographies, priors), init, maxIterations: 200, tolerance: 1e-14);
        var p = lm.Parameters;
        var t = ToPlane(p[0], p[1], p[2], p[3]);
        var ok = double.IsFinite(lm.FinalRms) && p[3] > 1e-3 && coverage.All(c => p[0] * c.X + p[1] * c.Y + 1 > 0.2);
        if (!ok)
            return Fronto(homographies, priors, "the projectors could not tell the screen's slant: the canvas is the camera's view — ALIGN CORNERS gives an exact one");
        var spread = Enumerable.Range(0, n).Max(j => Math.Abs(p[4 + j] / priors[j].Focal - 1));
        var words = string.Create(CultureInfo.InvariantCulture,
            $"the canvas is rectified from the projectors (square pixels, centred lenses); their focal lengths came out within {spread * 100:0.#} % of the priors");
        return new Rectification(Orient(t, homographies, priors), "projectors", words);
    }

    /// <summary>The camera's own view of the plane, turned upright by the projectors.</summary>
    public static Rectification Fronto(IReadOnlyList<Mat3d> homographies, IReadOnlyList<ProjectorPrior> priors, string words)
    {
        ArgumentNullException.ThrowIfNull(homographies);
        ArgumentNullException.ThrowIfNull(priors);
        return new Rectification(Orient(Mat3d.Identity, homographies, priors), "camera", words);
    }

    /// <summary>T = A(α, β)·P(l): the projective part sends the vanishing line (l1, l2, 1) to infinity, the affine part fixes angles.</summary>
    private static Mat3d ToPlane(double l1, double l2, double alpha, double beta) =>
        new Mat3d(1 / beta, -alpha / beta, 0, 0, 1, 0, 0, 0, 1) * new Mat3d(1, 0, 0, 0, 1, 0, l1, l2, 1);

    /// <summary>The plane turned (and mirrored when needed) so the projectors' median raster "up" points to −y and "right" to +x, and
    /// scaled so a small step at the picture's centre keeps its length.</summary>
    private static Mat3d Orient(Mat3d t, IReadOnlyList<Mat3d> homographies, IReadOnlyList<ProjectorPrior> priors)
    {
        var ups = new List<double>();
        var rights = 0;
        for (var j = 0; j < homographies.Count; j++)
        {
            var toCam = Homography.Inverse(homographies[j]);
            var k = priors[j];
            var centre = new Vec2d(k.Width / 2.0, k.Height / 2.0);
            var p0 = t.TransformPoint(toCam.TransformPoint(centre));
            var up = t.TransformPoint(toCam.TransformPoint(centre - new Vec2d(0, k.Height / 4.0))) - p0;
            var right = t.TransformPoint(toCam.TransformPoint(centre + new Vec2d(k.Width / 4.0, 0))) - p0;
            ups.Add(Math.Atan2(up.Y, up.X));
            if (Vec2d.Cross(right, up) < 0)
                rights++;
        }
        ups.Sort();
        var angle = ups[ups.Count / 2];
        // Turn so that "up" (angle) points to −π/2.
        var turn = -Math.PI / 2 - angle;
        var c = Math.Cos(turn);
        var s = Math.Sin(turn);
        var rot = new Mat3d(c, -s, 0, s, c, 0, 0, 0, 1);
        // With y down, a raster's right × up is negative; a plane where most projectors read the other way is mirrored.
        var mirror = rights * 2 < homographies.Count ? new Mat3d(-1, 0, 0, 0, 1, 0, 0, 0, 1) : Mat3d.Identity;
        var m = mirror * rot * t;
        var a = m.TransformPoint(Vec2d.Zero);
        var b = m.TransformPoint(new Vec2d(0.01, 0));
        var len = (b - a).Length;
        var scale = len > 0 && double.IsFinite(len) ? 0.01 / len : 1;
        return new Mat3d(scale, 0, 0, 0, scale, 0, 0, 0, 1) * m;
    }

    /// <summary>The conditions: for each projector, orthogonality and equal length of K⁻¹G's first two columns; its focal held to its
    /// prior.</summary>
    private sealed class Conditions(IReadOnlyList<Mat3d> homographies, IReadOnlyList<ProjectorPrior> priors) : ILeastSquaresProblem
    {
        public int ParameterCount => 4 + homographies.Count;

        public int ResidualCount => 3 * homographies.Count;

        public void Residuals(ReadOnlySpan<double> parameters, Span<double> residuals)
        {
            var (l1, l2, alpha, beta) = (parameters[0], parameters[1], parameters[2], parameters[3]);
            // T⁻¹ = P⁻¹·A⁻¹.
            var inv = new Mat3d(1, 0, 0, 0, 1, 0, -l1, -l2, 1) * new Mat3d(beta, alpha, 0, 0, 1, 0, 0, 0, 1);
            for (var j = 0; j < homographies.Count; j++)
            {
                var k = priors[j];
                var f = parameters[4 + j];
                var g = homographies[j] * inv;
                var kinv = new Mat3d(1 / f, 0, -k.Cx / f, 0, 1 / f, -k.Cy / f, 0, 0, 1);
                var m = kinv * g;
                var m1 = m.Column(0);
                var m2 = m.Column(1);
                var n1 = m1.LengthSquared;
                var n2 = m2.LengthSquared;
                var denom = Math.Sqrt(n1 * n2);
                residuals[3 * j] = denom > 0 ? Vec3d.Dot(m1, m2) / denom / ConditionSigma : 1e3;
                residuals[3 * j + 1] = n1 + n2 > 0 ? (n1 - n2) / (n1 + n2) / ConditionSigma : 1e3;
                residuals[3 * j + 2] = (f / k.Focal - 1) / Math.Max(1e-3, k.FocalSigma);
            }
        }

        public bool Jacobian(ReadOnlySpan<double> parameters, DenseMatrix jacobian) => false;
    }
}
