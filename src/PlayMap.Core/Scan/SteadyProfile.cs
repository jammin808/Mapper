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
/// Takes a camera's slow auto-exposure drift out of a scan's coded frames. Every coded frame (Gray, inverse, complementary, sinusoid)
/// lights half of the region on average, so over the lit pixels each frame's mean should be the same; each is scaled so its mean
/// equals the median of all their means. The worst drift is a fact: the share by which a frame's mean strayed from the median.
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

    /// <summary>Scales the coded frames in place; answers the worst drift in percent (0 when nothing is lit).</summary>
    public static double Normalise(ScanPlan plan, IList<byte[]> frames, bool[] lit)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(lit);
        var coded = new List<int>();
        for (var i = 0; i < plan.Count; i++)
        {
            if (plan.Frames[i].Kind is not (ScanFrameKind.White or ScanFrameKind.Black))
                coded.Add(i);
        }
        var means = new double[coded.Count];
        var count = 0;
        for (var k = 0; k < lit.Length; k++)
            count += lit[k] ? 1 : 0;
        if (count == 0 || coded.Count == 0)
            return 0;
        for (var j = 0; j < coded.Count; j++)
        {
            var f = frames[coded[j]];
            long sum = 0;
            for (var k = 0; k < lit.Length; k++)
            {
                if (lit[k])
                    sum += f[k];
            }
            means[j] = sum / (double)count;
        }
        var sorted = means.Order().ToArray();
        var median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        if (!(median > 0))
            return 0;
        var worst = 0.0;
        for (var j = 0; j < coded.Count; j++)
        {
            worst = Math.Max(worst, Math.Abs(means[j] / median - 1));
            var scale = median / Math.Max(means[j], 1e-6);
            var f = frames[coded[j]];
            var scaled = new byte[f.Length];
            for (var k = 0; k < f.Length; k++)
                scaled[k] = (byte)Math.Clamp(Math.Round(f[k] * scale), 0, 255);
            frames[coded[j]] = scaled;
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
