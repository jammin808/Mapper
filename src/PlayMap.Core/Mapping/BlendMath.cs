using PlayMap.Core.Model;

namespace PlayMap.Core.Mapping;

/// <summary>Soft-edge blending, pure. Where two pictures overlap, each fades across the zone and the other
/// the opposite way; the join is invisible when the two fades add up to one <em>in light</em>. Every curve
/// here is a light fade c(t) with c(0) = 0, c(1) = 1 and c(t) + c(1 − t) = 1, and the signal sent is
/// c(t)^(1/γ), so a display with gamma γ turns it back into c(t) of light and the two sides sum to one.</summary>
public static class BlendMath
{
    public const double MinGamma = 0.5;
    public const double MaxGamma = 3.0;

    /// <summary>The fade in light, 0 at the zone's outer edge (t = 0) to 1 where the full picture begins
    /// (t = 1). Each curve is point-symmetric about (½, ½), so mirrored fades sum to one:
    /// Linear t; Cosine ½ − ½cos πt; SCurve (smoothstep) t²(3 − 2t); Gamma22 the power-2.2 S-curve
    /// ½(2t)^2.2 below the middle and 1 − ½(2 − 2t)^2.2 above (a bare t^2.2 would not sum to one).</summary>
    public static double Curve(BlendCurve curve, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return curve switch
        {
            BlendCurve.Cosine => 0.5 - 0.5 * Math.Cos(Math.PI * t),
            BlendCurve.SCurve => t * t * (3 - 2 * t),
            BlendCurve.Gamma22 => t < 0.5 ? 0.5 * Math.Pow(2 * t, 2.2) : 1 - 0.5 * Math.Pow(2 - 2 * t, 2.2),
            _ => t,
        };
    }

    /// <summary>The signal for a light fraction: light^(1/γ) (γ clamped to 0.5..3; 1 passes it through).</summary>
    public static double ToSignal(double light, double gamma)
    {
        light = Math.Clamp(light, 0, 1);
        gamma = Math.Clamp(gamma, MinGamma, MaxGamma);
        return Math.Abs(gamma - 1) < 1e-9 ? light : Math.Pow(light, 1 / gamma);
    }

    /// <summary>The light a signal makes: signal^γ.</summary>
    public static double ToLight(double signal, double gamma)
    {
        signal = Math.Clamp(signal, 0, 1);
        gamma = Math.Clamp(gamma, MinGamma, MaxGamma);
        return Math.Abs(gamma - 1) < 1e-9 ? signal : Math.Pow(signal, gamma);
    }

    /// <summary>The signal multiplier <paramref name="t"/> of the way into a zone: curve^(1/γ).</summary>
    public static double Weight(BlendCurve curve, double t, double gamma) => ToSignal(Curve(curve, t), gamma);

    /// <summary>The light profile across one axis of a raster: for each of <paramref name="length"/>
    /// pixels (sampled at centres), the product of the near zone's fade-in and the far zone's fade-out.
    /// A zone of w pixels puts t = (x + ½)/w on pixel x, so the neighbour's matching zone — the same
    /// pixels seen from the other side — gets exactly 1 − t.</summary>
    public static void AxisProfile(int length, double nearZone, double farZone, BlendCurve curve, Span<double> light)
    {
        for (var x = 0; x < length; x++)
        {
            var c = x + 0.5;
            var f = 1.0;
            if (nearZone > 0)
                f *= Curve(curve, c / nearZone);
            if (farZone > 0)
                f *= Curve(curve, (length - c) / farZone);
            light[x] = f;
        }
    }

    /// <summary>The manual soft-edge map of a <paramref name="width"/> × <paramref name="height"/> output:
    /// separable — a horizontal fade times a vertical fade — so in a 2 × 2 the corner the four share sums to
    /// (left + right)(top + bottom) = 1 in light with nothing special done. Weights are signal multipliers
    /// (light through the inverse gamma); the pedestal is the black-level lift (see <see cref="BlackLevel"/>).
    /// The widths are output pixels and are scaled when the map is smaller than the output.</summary>
    public static BlendMap EdgeBlendMap(string outputId, int width, int height, BlendSettings settings, int outputWidth = 0, int outputHeight = 0)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // The caller passes the γ the output draws; a γ of 0 with no measurement behind it is the assumed one.
        var gamma = settings.DrawnGamma(null);
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var sx = outputWidth > 0 ? width / (double)outputWidth : 1;
        var sy = outputHeight > 0 ? height / (double)outputHeight : 1;
        var e = settings.Edges;
        var fx = new double[width];
        var fy = new double[height];
        AxisProfile(width, e.Left * sx, e.Right * sx, settings.Curve, fx);
        AxisProfile(height, e.Top * sy, e.Bottom * sy, settings.Curve, fy);
        var weights = new float[width * height];
        var pedestal = new float[width * height];
        var deepest = BlackLevel.DeepestCoverage(e);
        for (var y = 0; y < height; y++)
        {
            var bandY = (e.Top > 0 && y + 0.5 < e.Top * sy) || (e.Bottom > 0 && height - (y + 0.5) < e.Bottom * sy);
            for (var x = 0; x < width; x++)
            {
                var k = y * width + x;
                weights[k] = (float)ToSignal(fx[x] * fy[y], gamma);
                var bandX = (e.Left > 0 && x + 0.5 < e.Left * sx) || (e.Right > 0 && width - (x + 0.5) < e.Right * sx);
                var coverage = (bandX ? 2 : 1) * (bandY ? 2 : 1);
                pedestal[k] = (float)BlackLevel.Signal(settings.BlackLevel, coverage, deepest, gamma);
            }
        }
        return new BlendMap(outputId, width, height, weights, pedestal);
    }
}

/// <summary>Black-level matching. A projector's black is light, and black does not fade: where two
/// pictures overlap the floor is two blacks, where four share a corner it is four, and a dark scene shows
/// the seams as brighter bands. The cure is to raise the floor everywhere to the deepest overlap's: each
/// projector covering a region adds, in light, <c>black × (deepest − coverage) / coverage</c>, so the
/// region's floor becomes coverage × black + (deepest − coverage) × black = deepest × black. The lift is
/// added light, so it goes through the inverse gamma like the fades do.</summary>
public static class BlackLevel
{
    /// <summary>The deepest overlap a manually blended output takes part in: 2 for a row or a stack, 4 for
    /// a grid's corner, 1 alone.</summary>
    public static int DeepestCoverage(EdgeBlend edges)
    {
        ArgumentNullException.ThrowIfNull(edges);
        return (edges.Left > 0 || edges.Right > 0 ? 2 : 1) * (edges.Top > 0 || edges.Bottom > 0 ? 2 : 1);
    }

    /// <summary>The pedestal one projector adds where <paramref name="coverage"/> projectors reach, in
    /// light (a fraction of its white): black × (deepest − coverage) / coverage; 0 at the deepest.</summary>
    public static double Light(double black, double coverage, double deepest)
    {
        if (black <= 0 || coverage <= 0 || coverage >= deepest)
            return 0;
        return black * (deepest - coverage) / coverage;
    }

    /// <summary>The pedestal as the signal to add (light through the inverse gamma).</summary>
    public static double Signal(double black, double coverage, double deepest, double gamma) =>
        BlendMath.ToSignal(Light(black, coverage, deepest), gamma);

    /// <summary>The regions of a <paramref name="width"/> × <paramref name="height"/> picture by coverage:
    /// between the zones (1), the side and top/bottom bands (2), the corners where they meet (4). Up to
    /// nine rectangles in the picture's pixels, empty zones making no bands.</summary>
    public static IReadOnlyList<(RectD Rect, int Coverage)> Cells(double width, double height, EdgeBlend edges)
    {
        ArgumentNullException.ThrowIfNull(edges);
        var l = Math.Clamp(edges.Left, 0, width);
        var r = Math.Clamp(edges.Right, 0, width - l);
        var t = Math.Clamp(edges.Top, 0, height);
        var b = Math.Clamp(edges.Bottom, 0, height - t);
        var cols = new List<(double X, double W, bool Band)>(3);
        if (l > 0)
            cols.Add((0, l, true));
        if (width - l - r > 0)
            cols.Add((l, width - l - r, false));
        if (r > 0)
            cols.Add((width - r, r, true));
        var rows = new List<(double Y, double H, bool Band)>(3);
        if (t > 0)
            rows.Add((0, t, true));
        if (height - t - b > 0)
            rows.Add((t, height - t - b, false));
        if (b > 0)
            rows.Add((height - b, b, true));
        var cells = new List<(RectD, int)>(cols.Count * rows.Count);
        foreach (var row in rows)
            foreach (var col in cols)
                cells.Add((new RectD(col.X, row.Y, col.W, row.H), (col.Band ? 2 : 1) * (row.Band ? 2 : 1)));
        return cells;
    }
}
