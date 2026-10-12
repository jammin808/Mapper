using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Vision;

namespace PlayMap.Vision.Drift;

/// <summary>What the watch concludes about the rig.</summary>
public enum DriftState
{
    /// <summary>The live picture matches the reference.</summary>
    Stable,
    /// <summary>A small, sustained shift: warm-up, a creeping mount. Re-solve when convenient.</summary>
    Drifting,
    /// <summary>A large shift: something was knocked. Re-solve now.</summary>
    Bumped,
}

/// <summary>One watched region: where it is in the picture, how far its content moved (pixels, +x right, +y
/// down), how sure the correlation is (0…1), and whether it counts as moved.</summary>
public sealed record DriftRegion(int Index, RectD Area, Vec2d Shift, double Confidence, bool Moved);

/// <summary>One observation: the state after hysteresis and confirmation, the median shift of the confident
/// regions, every region, the words for the desk, and whether the state changed with this frame (the cue for the
/// one-click re-solve).</summary>
public sealed record DriftReport(DriftState State, Vec2d MedianShift, double MedianMagnitude, ImmutableArray<DriftRegion> Regions, bool Changed, string Words);

/// <summary>The watch's thresholds, in pixels of the camera picture. A region moves when its shift exceeds
/// <see cref="DriftAbovePx"/>; the rig is drifting when the median shift exceeds it and back to stable only when
/// it falls under <see cref="StableBelowPx"/> (between the two the state holds — hysteresis); bumped above
/// <see cref="BumpAbovePx"/>. A new state must hold for <see cref="ConfirmFrames"/> frames in a row.</summary>
public sealed record DriftWatchOptions(
    int Columns = 4,
    int Rows = 3,
    int PatchSize = 64,
    double StableBelowPx = 0.6,
    double DriftAbovePx = 1.25,
    double BumpAbovePx = 4.0,
    int ConfirmFrames = 2,
    double MinConfidence = 0.12,
    int MinConfidentRegions = 3)
{
    public static readonly DriftWatchOptions Default = new();
}

/// <summary>Watches a fixed camera's live frames against a reference capture (both grey) for the rig moving:
/// per region of a grid, phase correlation of a Hann-windowed patch (the normalised cross-power spectrum's peak,
/// refined to sub-pixel by a parabola) gives the shift and its confidence; the median over confident regions
/// decides the state, with hysteresis and confirmation so sensor noise and a passing person do not flip it.
/// Allocation per frame is the patches' spectra; the reference's are kept. Not thread-safe: one watcher, one
/// thread.</summary>
public sealed class DriftWatch
{
    private readonly DriftWatchOptions _options;
    private readonly int _patch;
    private readonly RectD[] _areas;
    private readonly double[] _window;
    private Spectrum[] _reference;
    private DriftState? _candidate;
    private int _candidateFrames;

    public DriftWatch(ImageGray8 reference, DriftWatchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        _options = options ?? DriftWatchOptions.Default;
        Width = reference.Width;
        Height = reference.Height;
        var cellW = Width / _options.Columns;
        var cellH = Height / _options.Rows;
        _patch = 8;
        while (_patch * 2 <= Math.Min(_options.PatchSize, Math.Min(cellW, cellH)))
            _patch *= 2;
        _areas = new RectD[_options.Columns * _options.Rows];
        for (var r = 0; r < _options.Rows; r++)
            for (var c = 0; c < _options.Columns; c++)
                _areas[r * _options.Columns + c] = new RectD(
                    c * cellW + (cellW - _patch) / 2, r * cellH + (cellH - _patch) / 2, _patch, _patch);
        _window = new double[_patch];
        for (var i = 0; i < _patch; i++)
            _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / _patch);
        _reference = Spectra(reference);
    }

    public int Width { get; }
    public int Height { get; }
    public DriftState State { get; private set; } = DriftState.Stable;

    /// <summary>A new reference (after a re-solve): the state returns to stable.</summary>
    public void Rebase(ImageGray8 reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Width != Width || reference.Height != Height)
            throw new ArgumentException("The reference must keep the camera's size.", nameof(reference));
        _reference = Spectra(reference);
        State = DriftState.Stable;
        _candidate = null;
        _candidateFrames = 0;
    }

    public DriftReport Observe(ImageGray8 live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Width != Width || live.Height != Height)
            throw new ArgumentException("The live frame must be the reference's size.", nameof(live));
        var spectra = Spectra(live);
        var regions = ImmutableArray.CreateBuilder<DriftRegion>(_areas.Length);
        var confident = new List<Vec2d>();
        for (var i = 0; i < _areas.Length; i++)
        {
            var (shift, confidence) = Correlate(_reference[i], spectra[i]);
            var ok = confidence >= _options.MinConfidence;
            if (ok)
                confident.Add(shift);
            regions.Add(new DriftRegion(i, _areas[i], shift, confidence, ok && shift.Length > _options.DriftAbovePx));
        }
        var built = regions.ToImmutable();
        if (confident.Count < _options.MinConfidentRegions)
            return new DriftReport(State, Vec2d.Zero, 0, built, false,
                string.Create(CultureInfo.InvariantCulture, $"too little texture to judge ({confident.Count} of {_areas.Length} regions); {Word(State)} held"));

        var median = new Vec2d(Median(confident.Select(s => s.X)), Median(confident.Select(s => s.Y)));
        var magnitude = Median(confident.Select(s => s.Length));
        var target = magnitude > _options.BumpAbovePx ? DriftState.Bumped
            : magnitude > _options.DriftAbovePx ? DriftState.Drifting
            : magnitude < _options.StableBelowPx ? DriftState.Stable
            : State == DriftState.Bumped ? DriftState.Drifting : State;
        var changed = false;
        if (target == State)
        {
            _candidate = null;
            _candidateFrames = 0;
        }
        else
        {
            _candidateFrames = _candidate == target ? _candidateFrames + 1 : 1;
            _candidate = target;
            if (_candidateFrames >= _options.ConfirmFrames)
            {
                State = target;
                changed = true;
                _candidate = null;
                _candidateFrames = 0;
            }
        }
        var moved = built.Count(r => r.Moved);
        var extent = moved == 0 ? "no region moved"
            : moved == confident.Count ? "the whole frame moved"
            : string.Create(CultureInfo.InvariantCulture, $"{moved} of {confident.Count} regions moved (regions {string.Join(",", built.Where(r => r.Moved).Select(r => r.Index))})");
        var words = string.Create(CultureInfo.InvariantCulture, $"{Word(State)}: median shift {magnitude:0.0} px ({median.X:0.0}, {median.Y:0.0}); {extent}");
        return new DriftReport(State, median, magnitude, built, changed, words);
    }

    private static string Word(DriftState s) => s switch
    {
        DriftState.Stable => "stable",
        DriftState.Drifting => "drifting",
        _ => "bumped",
    };

    private static double Median(IEnumerable<double> values)
    {
        var a = values.Order().ToArray();
        return a.Length == 0 ? 0 : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    private sealed class Spectrum(double[] re, double[] im, double energy)
    {
        public double[] Re { get; } = re;
        public double[] Im { get; } = im;
        public double Energy { get; } = energy;
    }

    private Spectrum[] Spectra(ImageGray8 image)
    {
        var s = new Spectrum[_areas.Length];
        for (var i = 0; i < _areas.Length; i++)
            s[i] = Transform(image, _areas[i]);
        return s;
    }

    /// <summary>The patch, mean removed and Hann windowed, to the frequency domain.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private Spectrum Transform(ImageGray8 image, RectD area)
    {
        var n = _patch;
        var re = new double[n * n];
        var im = new double[n * n];
        int x0 = (int)area.X, y0 = (int)area.Y;
        double mean = 0;
        for (var y = 0; y < n; y++)
        {
            var row = image.Row(y0 + y);
            for (var x = 0; x < n; x++)
                mean += row[x0 + x];
        }
        mean /= n * n;
        double energy = 0;
        for (var y = 0; y < n; y++)
        {
            var row = image.Row(y0 + y);
            for (var x = 0; x < n; x++)
            {
                var v = (row[x0 + x] - mean) * _window[x] * _window[y];
                re[y * n + x] = v;
                energy += v * v;
            }
        }
        Fft.Forward2D(re, im, n);
        return new Spectrum(re, im, energy);
    }

    /// <summary>Phase correlation of the reference and live patches: the shift of the live content and the
    /// correlation peak's height (1 for an exact shifted copy, near 0 for unrelated or flat patches).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private (Vec2d Shift, double Confidence) Correlate(Spectrum reference, Spectrum live)
    {
        var n = _patch;
        if (reference.Energy < 1e-6 * n * n || live.Energy < 1e-6 * n * n)
            return (Vec2d.Zero, 0);
        var re = new double[n * n];
        var im = new double[n * n];
        var mag = new double[n * n];
        double meanMag = 0;
        for (var i = 0; i < re.Length; i++)
        {
            // conj(F_ref) · F_live: its phase is the shift.
            double ar = reference.Re[i], ai = -reference.Im[i], br = live.Re[i], bi = live.Im[i];
            re[i] = ar * br - ai * bi;
            im[i] = ar * bi + ai * br;
            mag[i] = Math.Sqrt(re[i] * re[i] + im[i] * im[i]);
            meanMag += mag[i];
        }
        meanMag /= re.Length;
        // Pure phase correlation whitens every frequency alike, so the ones where the picture has no energy and
        // the sensor noise has all of it carry as much weight as the texture. Each frequency is weighted by
        // |C| / (|C| + ε) (ε half the mean: noise-only frequencies fade) and a Gaussian low-pass, and the surface is
        // divided by the total weight so an exact shifted copy still peaks at 1.
        var eps = 0.5 * meanMag + 1e-12;
        double weightSum = 0;
        for (var i = 0; i < re.Length; i++)
        {
            int fx = i % n, fy = i / n;
            double ux = (fx > n / 2 ? fx - n : fx) / (double)n, uy = (fy > n / 2 ? fy - n : fy) / (double)n;
            var lowPass = Math.Exp(-(ux * ux + uy * uy) / (2 * 0.18 * 0.18));
            var wgt = mag[i] / (mag[i] + eps) * lowPass;
            weightSum += wgt;
            if (mag[i] > 1e-12)
            {
                re[i] = re[i] / mag[i] * wgt;
                im[i] = im[i] / mag[i] * wgt;
            }
            else
            {
                re[i] = 0;
                im[i] = 0;
            }
        }
        if (weightSum <= 1e-12)
            return (Vec2d.Zero, 0);
        Fft.Inverse2D(re, im, n);
        var scale = re.Length / weightSum;
        for (var i = 0; i < re.Length; i++)
            re[i] *= scale;
        var best = 0;
        for (var i = 1; i < re.Length; i++)
            if (re[i] > re[best])
                best = i;
        int bx = best % n, by = best / n;
        double At(int x, int y) => re[((y + n) % n) * n + (x + n) % n];
        var peak = At(bx, by);
        var dx = Parabola(At(bx - 1, by), peak, At(bx + 1, by));
        var dy = Parabola(At(bx, by - 1), peak, At(bx, by + 1));
        double sx = bx + dx, sy = by + dy;
        if (sx > n / 2.0) sx -= n;
        if (sy > n / 2.0) sy -= n;
        return (new Vec2d(sx, sy), Math.Clamp(peak, 0, 1));
    }

    private static double Parabola(double left, double centre, double right)
    {
        var denom = left - 2 * centre + right;
        return Math.Abs(denom) < 1e-12 ? 0 : Math.Clamp(0.5 * (left - right) / denom, -0.5, 0.5);
    }
}

/// <summary>In-place radix-2 complex FFT, 1D and square 2D (rows then columns). The inverse is scaled by 1/N per
/// dimension, so a forward and an inverse give back the input.</summary>
internal static class Fft
{
    public static void Forward2D(double[] re, double[] im, int n) => Transform2D(re, im, n, inverse: false);

    public static void Inverse2D(double[] re, double[] im, int n) => Transform2D(re, im, n, inverse: true);

    private static void Transform2D(double[] re, double[] im, int n, bool inverse)
    {
        var rowRe = new double[n];
        var rowIm = new double[n];
        for (var y = 0; y < n; y++)
        {
            Array.Copy(re, y * n, rowRe, 0, n);
            Array.Copy(im, y * n, rowIm, 0, n);
            Transform(rowRe, rowIm, inverse);
            Array.Copy(rowRe, 0, re, y * n, n);
            Array.Copy(rowIm, 0, im, y * n, n);
        }
        for (var x = 0; x < n; x++)
        {
            for (var y = 0; y < n; y++)
            {
                rowRe[y] = re[y * n + x];
                rowIm[y] = im[y * n + x];
            }
            Transform(rowRe, rowIm, inverse);
            for (var y = 0; y < n; y++)
            {
                re[y * n + x] = rowRe[y];
                im[y * n + x] = rowIm[y];
            }
        }
    }

    /// <summary>Iterative Cooley–Tukey on a power-of-two length.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Transform(double[] re, double[] im, bool inverse)
    {
        var n = re.Length;
        if ((n & (n - 1)) != 0)
            throw new ArgumentException("The length must be a power of two.", nameof(re));
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = 2 * Math.PI / len * (inverse ? 1 : -1);
            double wr = Math.Cos(angle), wi = Math.Sin(angle);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    var tr = re[b] * cr - im[b] * ci;
                    var ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    var next = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = next;
                }
            }
        }
        if (inverse)
        {
            for (var i = 0; i < n; i++)
            {
                re[i] /= n;
                im[i] /= n;
            }
        }
    }
}
