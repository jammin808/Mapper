using System.Globalization;

namespace PlayMap.Core.Scan;

/// <summary>
/// The white and black pictures of a scan whose camera never saw them (the steady profile: a camera that keeps its own exposure
/// is shown no full white or black, which would make it ramp). Per camera pixel, among the pattern–inverse pairs of the three
/// coarsest Gray levels of every coded axis, the pair that differs most gives white (the larger) and black (the smaller): a pixel
/// near an edge of one level is far from the edges of the next two, which never coincide with it.
/// </summary>
public static class ReferenceSynthesis
{
    /// <summary>The frames used: pattern and inverse of the three coarsest Gray (or XOR-coded Gray) levels per axis.</summary>
    public static IReadOnlyList<(int Pattern, int Inverse)> Pairs(ScanPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var pairs = new List<(int, int)>();
        foreach (var axis in (ReadOnlySpan<ScanAxis>)[ScanAxis.Columns, ScanAxis.Rows])
        {
            var found = 0;
            for (var i = 0; i + 1 < plan.Count && found < 3; i++)
            {
                var f = plan.Frames[i];
                var g = plan.Frames[i + 1];
                if (f.Axis == axis && f.Kind == ScanFrameKind.Gray && !f.Inverse && g.Kind == ScanFrameKind.Gray && g.Inverse && g.Level == f.Level)
                {
                    pairs.Add((i, i + 1));
                    found++;
                }
            }
        }
        return pairs;
    }

    /// <summary>Writes the plan's white and black frames from its coarse pairs (in place, gray8 frames in plan order).</summary>
    public static void Fill(ScanPlan plan, IList<byte[]> frames)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(frames);
        var pairs = Pairs(plan);
        if (pairs.Count == 0)
            throw new ArgumentException("the plan has no Gray pairs to make its references from", nameof(plan));
        var n = frames[pairs[0].Pattern].Length;
        var white = new byte[n];
        var black = new byte[n];
        for (var k = 0; k < n; k++)
        {
            var best = -1;
            byte hi = 0, lo = 0;
            foreach (var (p, q) in pairs)
            {
                var a = frames[p][k];
                var b = frames[q][k];
                var d = Math.Abs(a - b);
                if (d > best)
                {
                    best = d;
                    hi = Math.Max(a, b);
                    lo = Math.Min(a, b);
                }
            }
            white[k] = hi;
            black[k] = lo;
        }
        frames[plan.WhiteIndex] = white;
        frames[plan.BlackIndex] = black;
    }
}

/// <summary>
/// Takes a camera's slow auto-exposure drift out of a scan's coded frames, measured against the picture rather than assumed of it.
/// A pattern and its inverse light complementary pixels, as do two sinusoid steps half a period apart, so at every lit pixel the two
/// lights sum to white plus black. A phone's picture is not its light: its response curve bends mid-levels (a binary pattern shows
/// only white and black, which no curve can bend apart, but a sinusoid's steps it does). So the curve is found first — the power
/// <see cref="ResponseGamma"/> under which the sinusoid pairs sum best — and the pictures are read through it as light. Each pair's
/// two gains are then the least-squares fit of a·first + b·second = white + black over the lit pixels (gain = 1/a, 1/b): what the
/// picture shows varies from pixel to pixel and pins the two apart, whatever it is. A coarse pair that made the references is fitted
/// against the references of the other pairs. Each frame is scaled (as light) to the median gain. The drift is that fact: how far
/// the farthest frame's gain stood from the median, in percent — a camera that holds still reads near zero whatever the picture
/// shows and whatever its curve.
/// </summary>
public static class GainNormaliser
{
    /// <summary>The lit mask of synthesised (or captured) references: white − black at least <paramref name="minContrast"/>.</summary>
    public static bool[] LitMask(ScanPlan plan, IList<byte[]> frames, int minContrast = 16)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(frames);
        var white = frames[plan.WhiteIndex];
        var black = frames[plan.BlackIndex];
        var lit = new bool[white.Length];
        for (var k = 0; k < lit.Length; k++)
            lit[k] = white[k] - black[k] >= minContrast && white[k] < 250;
        return lit;
    }

    /// <summary>The coded frames that pair up into a complementary sum: each pattern with its inverse, each sinusoid step with the one
    /// half a period on (when the step count is even).</summary>
    public static IReadOnlyList<(int First, int Second)> ComplementaryPairs(ScanPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var pairs = new List<(int, int)>();
        for (var i = 0; i + 1 < plan.Count; i++)
        {
            var f = plan.Frames[i];
            var g = plan.Frames[i + 1];
            if (f.Kind is ScanFrameKind.Gray or ScanFrameKind.Complementary && g.Kind == f.Kind && !f.Inverse && g.Inverse && g.Level == f.Level
                && g.Axis == f.Axis)
                pairs.Add((i, i + 1));
        }
        for (var i = 0; i < plan.Count; i++)
        {
            var f = plan.Frames[i];
            if (f.Kind != ScanFrameKind.Phase || f.PhaseSteps % 2 != 0 || f.PhaseStep >= f.PhaseSteps / 2)
                continue;
            var j = i + f.PhaseSteps / 2;
            if (j < plan.Count && plan.Frames[j] is { Kind: ScanFrameKind.Phase } g && g.Axis == f.Axis && g.Period == f.Period
                && g.PhaseStep == f.PhaseStep + f.PhaseSteps / 2)
                pairs.Add((i, j));
        }
        return pairs;
    }

    /// <summary>The camera's response curve as a power (light = level^γ): the one, from 1 to 2.6, under which the sinusoid pairs'
    /// fits leave the least residual (1 when the plan has no sinusoids: binary pairs are read alike under any curve).</summary>
    public static double ResponseGamma(ScanPlan plan, IList<byte[]> frames, bool[] lit)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(lit);
        var phase = ComplementaryPairs(plan).Where(p => plan.Frames[p.First].Kind == ScanFrameKind.Phase).ToList();
        if (phase.Count == 0)
            return 1;
        var best = 1.0;
        var bestResidual = double.PositiveInfinity;
        for (var step = 0; step <= 32; step++)
        {
            var gamma = 1 + step * 0.05;
            var light = Light(gamma);
            var residual = 0.0;
            foreach (var (first, second) in phase)
                residual += Fit(frames[first], frames[second], frames[plan.WhiteIndex], frames[plan.BlackIndex], lit, light, 4).Residual;
            if (residual < bestResidual)
            {
                bestResidual = residual;
                best = gamma;
            }
        }
        return best;
    }

    /// <summary>A level's light under a response power, on the same 0–255 scale.</summary>
    private static double[] Light(double gamma)
    {
        var lut = new double[256];
        for (var v = 0; v < 256; v++)
            lut[v] = 255 * Math.Pow(v / 255.0, gamma);
        return lut;
    }

    /// <summary>a·x + b·y = white + black over the lit pixels, as light; the residual relative to the sum's own size.</summary>
    private static (double A, double B, int N, double Residual) Fit(byte[] x, byte[] y, byte[] white, byte[] black, bool[] lit, double[] light, int stride)
    {
        double xx = 0, xy = 0, yy = 0, xt = 0, yt = 0, tt = 0;
        var n = 0;
        for (var k = 0; k < lit.Length; k += stride)
        {
            if (!lit[k] || x[k] >= 250 || y[k] >= 250)
                continue;
            double lx = light[x[k]], ly = light[y[k]], t = light[white[k]] + light[black[k]];
            xx += lx * lx;
            xy += lx * ly;
            yy += ly * ly;
            xt += lx * t;
            yt += ly * t;
            tt += t * t;
            n++;
        }
        var det = xx * yy - xy * xy;
        if (n < 64 || !(Math.Abs(det) > 1e-9 * xx * yy))
            return (0, 0, n, double.PositiveInfinity);
        var a = (xt * yy - yt * xy) / det;
        var b = (yt * xx - xt * xy) / det;
        // Σ(a·x + b·y − t)² expanded over the sums already taken.
        var residual = a * a * xx + b * b * yy + tt + 2 * a * b * xy - 2 * a * xt - 2 * b * yt;
        return (a, b, n, Math.Max(0, residual) / tt);
    }

    /// <summary>Each coded frame's gain against the references (1 where it cannot be read), read as light through the camera's
    /// response curve.</summary>
    public static IReadOnlyDictionary<int, double> Gains(ScanPlan plan, IList<byte[]> frames, bool[] lit) =>
        Gains(plan, frames, lit, ResponseGamma(plan, frames, lit));

    private static Dictionary<int, double> Gains(ScanPlan plan, IList<byte[]> frames, bool[] lit, double gamma)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(lit);
        var light = Light(gamma);
        var white = frames[plan.WhiteIndex];
        var black = frames[plan.BlackIndex];
        var coarse = ReferenceSynthesis.Pairs(plan);
        var gains = new Dictionary<int, double>();
        foreach (var (first, second) in ComplementaryPairs(plan))
        {
            var own = -1;
            for (var q = 0; q < coarse.Count; q++)
            {
                if (coarse[q].Pattern == first && coarse[q].Inverse == second)
                    own = q;
            }
            double xx = 0, xy = 0, yy = 0, xt = 0, yt = 0;
            var n = 0;
            var x = frames[first];
            var y = frames[second];
            for (var k = 0; k < lit.Length; k += 2)
            {
                if (!lit[k] || x[k] >= 250 || y[k] >= 250)
                    continue;
                double t;
                if (own < 0)
                {
                    t = light[white[k]] + light[black[k]];
                }
                else
                {
                    // This pair made the references wherever it differed most: its sum is taken from the others'.
                    var (hi, lo) = ReferencesWithout(frames, coarse, own, k);
                    if (hi - lo < 16)
                        continue;
                    t = light[hi] + light[lo];
                }
                double lx = light[x[k]], ly = light[y[k]];
                xx += lx * lx;
                xy += lx * ly;
                yy += ly * ly;
                xt += lx * t;
                yt += ly * t;
                n++;
            }
            var det = xx * yy - xy * xy;
            if (n < 64 || !(Math.Abs(det) > 1e-9 * xx * yy))
                continue;
            var a = (xt * yy - yt * xy) / det;
            var b = (yt * xx - xt * xy) / det;
            if (a > 0 && b > 0)
            {
                gains[first] = 1 / a;
                gains[second] = 1 / b;
            }
        }
        for (var i = 0; i < plan.Count; i++)
        {
            if (plan.Frames[i].Kind is not (ScanFrameKind.White or ScanFrameKind.Black))
                gains.TryAdd(i, 1);
        }
        return gains;
    }

    private static (int High, int Low) ReferencesWithout(IList<byte[]> frames, IReadOnlyList<(int Pattern, int Inverse)> pairs, int skip, int k)
    {
        var best = -1;
        int hi = 0, lo = 0;
        for (var q = 0; q < pairs.Count; q++)
        {
            if (q == skip)
                continue;
            int a = frames[pairs[q].Pattern][k], b = frames[pairs[q].Inverse][k];
            if (Math.Abs(a - b) > best)
            {
                best = Math.Abs(a - b);
                hi = Math.Max(a, b);
                lo = Math.Min(a, b);
            }
        }
        return (hi, lo);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        var n = values.Count;
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2;
    }

    /// <summary>Scales the coded frames in place (as light, through the camera's response curve) to their median gain; answers the
    /// worst drift in percent (0 when nothing is lit).</summary>
    public static double Normalise(ScanPlan plan, IList<byte[]> frames, bool[] lit)
    {
        var gamma = ResponseGamma(plan, frames, lit);
        var gains = Gains(plan, frames, lit, gamma);
        if (gains.Count == 0 || !lit.Contains(true))
            return 0;
        var median = Median([.. gains.Values]);
        if (!(median > 0))
            return 0;
        var worst = 0.0;
        foreach (var (i, g) in gains)
        {
            worst = Math.Max(worst, Math.Abs(g / median - 1));
            // Light scaled by s is a level scaled by s^(1/γ) under a power curve.
            var scale = Math.Pow(median / Math.Max(g, 1e-6), 1 / gamma);
            var f = frames[i];
            var scaled = new byte[f.Length];
            for (var k = 0; k < f.Length; k++)
                scaled[k] = (byte)Math.Clamp(Math.Round(f[k] * scale), 0, 255);
            frames[i] = scaled;
        }
        return Math.Round(worst * 100, 2);
    }

    /// <summary>The steady profile's whole preparation of a capture: references from the coarse pairs, the drift scaled out of the
    /// coded frames, then the references made again from the scaled frames. Answers the worst drift in percent.</summary>
    public static double PrepareSteady(ScanPlan plan, IList<byte[]> frames)
    {
        ReferenceSynthesis.Fill(plan, frames);
        var drift = Normalise(plan, frames, LitMask(plan, frames));
        ReferenceSynthesis.Fill(plan, frames);
        return drift;
    }

    public static string Words(double driftPct) =>
        string.Create(CultureInfo.InvariantCulture, $"the camera's brightness drifted at most {driftPct:0.0} % and was levelled");
}
