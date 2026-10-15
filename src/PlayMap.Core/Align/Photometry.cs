using System.Globalization;
using PlayMap.Core.Mapping;

namespace PlayMap.Core.Align;

/// <summary>One projector's light as the camera measured it, in camera-linear units: its black and its white (trimmed means over the middle of
/// its footprint where no other output reaches), and its transfer γ from the grey ramp, with the ramp's misfit (RMS of log ratio).</summary>
public sealed record ProjectorPhotometry(double Black, double White, double Gamma, double GammaMisfit, int Samples)
{
    public static readonly ProjectorPhotometry Unknown = new(0, 1, 2.2, double.NaN, 0);

    public bool Measured => Samples > 0 && double.IsFinite(GammaMisfit);

    public string Words => Measured
        ? string.Create(CultureInfo.InvariantCulture, $"γ {Gamma:0.00}, contrast {(Black > 0 ? White / Black : double.PositiveInfinity):0}:1 seen by the camera")
        : "not measured";
}

/// <summary>The camera's levels to linear light, and each projector's black, white and γ from frames the camera took of it alone.
/// The camera is taken to encode with a power of <see cref="CameraGamma"/> (an 8-bit webcam's curve, near enough). Pure.</summary>
public static class Photometry
{
    public const double CameraGamma = 2.2;

    /// <summary>The grey levels the ramp shows, with white (255) and black (0) taken from their own frames.</summary>
    public static readonly IReadOnlyList<byte> RampLevels = [64, 128, 192];

    private static readonly float[] Lut = BuildLut();

    private static float[] BuildLut()
    {
        var lut = new float[256];
        for (var v = 0; v < 256; v++)
            lut[v] = (float)Math.Pow(v / 255.0, CameraGamma);
        return lut;
    }

    /// <summary>A camera level in linear light (0–1).</summary>
    public static float Linear(byte level) => Lut[level];

    /// <summary>A picture in linear light.</summary>
    public static float[] Linear(ReadOnlySpan<byte> picture)
    {
        var o = new float[picture.Length];
        for (var i = 0; i < o.Length; i++)
            o[i] = Lut[picture[i]];
        return o;
    }

    /// <summary>The camera pixels that speak for this projector alone: inside its footprint, well away from its edge (at least 30 % of
    /// its deepest inside distance), and outside every other footprint grown by three pixels.</summary>
    public static bool[] Core(Footprint own, IReadOnlyList<Footprint> others)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(others);
        var w = own.Width;
        var h = own.Height;
        var inv = new bool[own.Mask.Length];
        for (var i = 0; i < inv.Length; i++)
            inv[i] = !own.Mask[i];
        var inside = DistanceTransform.Euclidean(inv, w, h);
        var deepest = 0f;
        for (var i = 0; i < inside.Length; i++)
            if (own.Mask[i] && inside[i] < DistanceTransform.Far)
                deepest = Math.Max(deepest, inside[i]);
        var keep = new bool[inv.Length];
        for (var i = 0; i < keep.Length; i++)
            keep[i] = own.Mask[i] && inside[i] >= 0.3f * deepest;
        foreach (var o in others)
        {
            if (ReferenceEquals(o, own))
                continue;
            var grown = Footprints.Dilate(o.Mask, w, h, 3);
            for (var i = 0; i < keep.Length; i++)
                keep[i] &= !grown[i];
        }
        return keep;
    }

    /// <summary>A projector's black, white and γ: trimmed means of the black picture, the white one and each ramp picture over
    /// <paramref name="core"/>; γ the least-squares slope through the origin of log((grey − black)/(white − black)) on log(level/255).</summary>
    public static ProjectorPhotometry Measure(bool[] core, ReadOnlySpan<byte> black, ReadOnlySpan<byte> white, IReadOnlyList<(byte Level, byte[] Picture)> ramp)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(ramp);
        var idx = new List<int>();
        for (var i = 0; i < core.Length; i++)
            if (core[i])
                idx.Add(i);
        if (idx.Count < 16)
            return ProjectorPhotometry.Unknown;
        var k = TrimmedMean(idx, black);
        var w = TrimmedMean(idx, white);
        if (!(w - k > 1e-4))
            return ProjectorPhotometry.Unknown with { Black = k, White = w };
        double sxy = 0, sxx = 0;
        var points = new List<(double X, double Y)>();
        foreach (var (level, picture) in ramp)
        {
            var g = TrimmedMean(idx, picture);
            var ratio = (g - k) / (w - k);
            if (!(ratio > 1e-4) || ratio >= 1)
                continue;
            var x = Math.Log(level / 255.0);
            var y = Math.Log(ratio);
            sxy += x * y;
            sxx += x * x;
            points.Add((x, y));
        }
        if (points.Count == 0 || sxx <= 0)
            return ProjectorPhotometry.Unknown with { Black = k, White = w, Samples = idx.Count };
        var gamma = sxy / sxx;
        var misfit = Math.Sqrt(points.Sum(p => (p.Y - gamma * p.X) * (p.Y - gamma * p.X)) / points.Count);
        return new ProjectorPhotometry(k, w, Math.Clamp(gamma, BlendMath.MinGamma, BlendMath.MaxGamma), misfit, idx.Count);
    }

    /// <summary>The mean linear light over the pixels with the darkest and brightest tenth left out: the camera's noise dithers its
    /// 8-bit levels, so the mean resolves a fraction of a level where a median could not.</summary>
    private static double TrimmedMean(List<int> idx, ReadOnlySpan<byte> picture)
    {
        Span<int> histogram = stackalloc int[256];
        foreach (var i in idx)
            histogram[picture[i]]++;
        var skip = idx.Count / 10;
        var take = idx.Count - 2 * skip;
        double sum = 0;
        var seen = 0;
        var taken = 0;
        for (var v = 0; v < 256 && taken < take; v++)
        {
            var count = histogram[v];
            var from = Math.Max(0, skip - seen);
            var use = Math.Min(Math.Max(0, count - from), take - taken);
            sum += (double)use * Lut[v];
            taken += use;
            seen += count;
        }
        return taken > 0 ? sum / taken : 0;
    }
}
