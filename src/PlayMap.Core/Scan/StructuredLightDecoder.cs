using System.Globalization;
using PlayMap.Core.Threading;

namespace PlayMap.Core.Scan;

/// <summary>The decoder's thresholds. Levels are in linear camera units of an 8-bit scale (0–255) whatever the
/// frames' type; float frames are read as 0–1 of full scale.</summary>
public sealed record DecodeOptions
{
    public static readonly DecodeOptions Default = new();

    /// <summary>White − black below this is unlit: shadow, outside the projector, or a black surface.</summary>
    public double MinContrast { get; init; } = 16;

    /// <summary>A raw white level at or above this is clipped; the pixel is not used.</summary>
    public double SaturationLevel { get; init; } = 250;

    /// <summary>A bit whose |pattern − inverse| is below max(this, <see cref="BitThresholdRelative"/> ×
    /// contrast) is uncertain (its sign still decides it).</summary>
    public double BitThreshold { get; init; } = 3;

    public double BitThresholdRelative { get; init; } = 0.08;

    /// <summary>A sinusoid's swing below this fraction of white − black cannot place the pixel.</summary>
    public double MinModulation { get; init; } = 0.15;

    /// <summary>The contrast at and above which contrast no longer lowers confidence.</summary>
    public double GoodContrast { get; init; } = 48;

    /// <summary>The relative modulation at and above which modulation no longer lowers confidence.</summary>
    public double GoodModulation { get; init; } = 0.5;

    /// <summary>The camera's encoding gamma: a captured level v is light (v/255)^gamma. 1 for a linear camera.</summary>
    public double CameraGamma { get; init; } = 1.0;

    /// <summary>Uncertain bits a pixel may carry beyond the one per axis a stripe edge explains; more and the
    /// pixel is dropped.</summary>
    public int MaxUncertainBits { get; init; } = 2;

    /// <summary>The camera's read noise (standard deviation, levels). Null measures it from the sinusoids' residual
    /// (an even step count leaves Σ(−1)ⁿIₙ free of the signal). A bit is uncertain below 2√2 noise as well (but
    /// never for want of more than a quarter of the pixel's contrast).</summary>
    public double? NoiseSigma { get; init; }

    /// <summary>In an XOR-coded scan, a pixel within this many projector pixels of an edge of the XOR base is a
    /// suspect: there the base and every XOR-ed level are near even, and one noisy call flips every coarse
    /// level at once.</summary>
    public double XorEdgeBand { get; init; } = 1.0;

    /// <summary>Checks every suspect pixel against its 5 × 5 neighbourhood: one off by whole phase periods is
    /// moved back, one off by anything else is dropped.</summary>
    public bool CleanJumps { get; init; } = true;
}

/// <summary>
/// Decodes a plan's captured frames into a <see cref="CorrespondenceMap"/>. Per camera pixel: lit when white
/// − black reaches the contrast asked for and white is not clipped; each Gray bit is which of the pattern and
/// its inverse was brighter (never a global threshold), and uncertain when the two were too close; the Gray
/// code names a cell; with sinusoids, the N-step phase (atan2) places the pixel inside a period, unwrapped
/// by rounding against the Gray cell's centre — with the complementary level the cells are half a period
/// wide, so the rounding always has a quarter period of slack and edge pixels never jump; finer periods are
/// unwrapped by the coarser result. "Too close" allows for the camera's noise, measured from the sinusoids.
/// A suspect — more close calls than a stripe edge explains, Gray code and phase at odds, or a pixel on an
/// XOR base's edge — is then checked against its trusted neighbours. Rows run in parallel; nothing allocates
/// per pixel.
/// </summary>
public sealed class StructuredLightDecoder
{
    private readonly AxisDecoder? _columns;
    private readonly AxisDecoder? _rows;

    public StructuredLightDecoder(ScanPlan plan, DecodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Plan = plan;
        Options = options ?? DecodeOptions.Default;
        if (plan.Columns is { } columns)
            _columns = new AxisDecoder(plan, columns, Options.XorEdgeBand);
        if (plan.Rows is { } rows)
            _rows = new AxisDecoder(plan, rows, Options.XorEdgeBand);
    }

    public ScanPlan Plan { get; }
    public DecodeOptions Options { get; }

    /// <summary>Decodes gray8 camera frames, one per plan frame in plan order, each <paramref name="width"/> ×
    /// <paramref name="height"/> row-major. <paramref name="whiteColour"/> (0xRRGGBB per pixel) is kept for the
    /// smart scan.</summary>
    public CorrespondenceMap Decode(int width, int height, IReadOnlyList<byte[]> frames, int[]? whiteColour = null)
    {
        var images = Check(width, height, frames, whiteColour);
        var lut = new float[256];
        for (var v = 0; v < 256; v++)
            lut[v] = (float)(255 * Math.Pow(v / 255.0, Options.CameraGamma));
        var map = NewMap(width, height, images[Plan.WhiteIndex], whiteColour);
        Run(new ByteFrames(images, lut), map);
        return map;
    }

    /// <summary>Decodes float camera frames (0–1 of full scale), one per plan frame in plan order.</summary>
    public CorrespondenceMap Decode(int width, int height, IReadOnlyList<float[]> frames, int[]? whiteColour = null)
    {
        var images = Check(width, height, frames, whiteColour);
        var whiteSource = images[Plan.WhiteIndex];
        var white = new byte[width * height];
        for (var k = 0; k < white.Length; k++)
            white[k] = (byte)Math.Clamp(Math.Round(whiteSource[k] * 255.0), 0, 255);
        var map = NewMap(width, height, white, whiteColour);
        Run(new FloatFrames(images, (float)Options.CameraGamma), map);
        return map;
    }

    private T[][] Check<T>(int width, int height, IReadOnlyList<T[]> frames, int[]? whiteColour)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (frames.Count != Plan.Count)
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The plan has {Plan.Count} frames but {frames.Count} were captured."), nameof(frames));
        var images = new T[frames.Count][];
        for (var i = 0; i < images.Length; i++)
        {
            images[i] = frames[i] ?? throw new ArgumentException("A frame is missing.", nameof(frames));
            if (images[i].Length < width * height)
                throw new ArgumentException("A frame is smaller than the camera's raster.", nameof(frames));
        }
        if (whiteColour is not null && whiteColour.Length < width * height)
            throw new ArgumentException("The colour image is smaller than the camera's raster.", nameof(whiteColour));
        return images;
    }

    private CorrespondenceMap NewMap(int width, int height, byte[] white, int[]? whiteColour) =>
        new(width, height, Plan.ProjectorWidth, Plan.ProjectorHeight, Plan.Region, Plan.Axes, Plan.HasPhase)
        {
            White = white,
            WhiteColour = whiteColour,
        };

    private void Run<TFrames>(TFrames frames, CorrespondenceMap map) where TFrames : struct, IFrames
    {
        var uncertain = new long[map.Height];
        var decided = new long[map.Height];
        var noise = Options.NoiseSigma ?? EstimateNoise(frames, map.Width, map.Height);
        // Pixels whose decode stands or falls with one near-even call (the XOR base's) must be confirmed by
        // their neighbours, not merely not contradicted.
        var mustConfirm = new bool[map.Width * map.Height];
        Parallel.For(0, map.Height, Compute.Options, y => DecodeRow(frames, map, y, noise, uncertain, decided, mustConfirm));
        if (Options.CleanJumps)
            CleanJumps(map, mustConfirm);
        map.Recount(uncertain.Sum(), decided.Sum(), noise);
    }

    /// <summary>The read noise from the first even-stepped sinusoid: for a pure sinusoid Σ(−1)ⁿIₙ is zero, so over
    /// lit pixels it is noise of standard deviation √N·σ (robustly, by the median absolute value). 0 when the
    /// plan has no such sinusoid or too few lit pixels.</summary>
    private double EstimateNoise<TFrames>(TFrames frames, int width, int height) where TFrames : struct, IFrames
    {
        var axis = _columns is { HasEvenPhase: true } ? _columns : _rows is { HasEvenPhase: true } ? _rows : null;
        if (axis is null)
            return 0;
        var steps = axis.FirstPhaseFrames;
        var samples = new List<double>();
        var stride = Math.Max(1, (int)Math.Sqrt(width * (double)height / 20000));
        for (var y = 0; y < height; y += stride)
        {
            for (var x = 0; x < width; x += stride)
            {
                var k = y * width + x;
                var contrast = frames.Linear(Plan.WhiteIndex, k) - frames.Linear(Plan.BlackIndex, k);
                if (contrast < Options.MinContrast || frames.Raw(Plan.WhiteIndex, k) >= Options.SaturationLevel)
                    continue;
                double r = 0;
                for (var n = 0; n < steps.Length; n++)
                    r += (n % 2 == 0 ? 1 : -1) * frames.Linear(steps[n], k);
                samples.Add(Math.Abs(r));
            }
        }
        if (samples.Count < 50)
            return 0;
        samples.Sort();
        return 1.4826 * samples[samples.Count / 2] / Math.Sqrt(steps.Length);
    }

    private void DecodeRow<TFrames>(TFrames frames, CorrespondenceMap map, int y, double noise, long[] uncertainPerRow, long[] decidedPerRow,
        bool[] mustConfirm)
        where TFrames : struct, IFrames
    {
        var o = Options;
        var noiseFloor = 2 * Math.Sqrt(2) * noise;
        var minContrast = (float)o.MinContrast;
        var saturation = (float)o.SaturationLevel;
        var white = Plan.WhiteIndex;
        var black = Plan.BlackIndex;
        var hasPhase = Plan.HasPhase;
        var w = map.Width;
        long uncertainBits = 0;
        long decidedBits = 0;
        for (var x = 0; x < w; x++)
        {
            var k = y * w + x;
            var contrast = frames.Linear(white, k) - frames.Linear(black, k);
            map.Contrast[k] = Math.Max(0, contrast);
            if (contrast < minContrast)
                continue;
            var flags = DecodeStatus.Lit;
            if (frames.Raw(white, k) >= saturation)
            {
                map.Flags[k] = flags | DecodeStatus.Saturated;
                continue;
            }
            // A mis-measured noise (a projector gamma left in the sinusoids reads as noise) never makes more than
            // a quarter of the contrast too close to call.
            var tau = (float)Math.Max(Math.Max(o.BitThreshold, o.BitThresholdRelative * contrast), Math.Min(noiseFloor, 0.25 * contrast));
            var excess = 0;
            var modulation = double.PositiveInfinity;
            var inconsistent = false;
            var suspect = false;
            var outOfRange = false;
            var px = double.NaN;
            var py = double.NaN;
            if (_columns is { } columns)
            {
                var a = columns.Decode(frames, k, tau, contrast);
                decidedBits += columns.BitCount;
                uncertainBits += a.Uncertain;
                excess += Math.Max(0, a.Uncertain - 1);
                modulation = Math.Min(modulation, a.Modulation);
                inconsistent |= a.Inconsistent;
                suspect |= a.NearXorEdge;
                mustConfirm[k] |= a.BaseUncertain;
                outOfRange |= !columns.TryPlace(a.Position, out px);
            }
            if (_rows is { } rows)
            {
                var a = rows.Decode(frames, k, tau, contrast);
                decidedBits += rows.BitCount;
                uncertainBits += a.Uncertain;
                excess += Math.Max(0, a.Uncertain - 1);
                modulation = Math.Min(modulation, a.Modulation);
                inconsistent |= a.Inconsistent;
                suspect |= a.NearXorEdge;
                mustConfirm[k] |= a.BaseUncertain;
                outOfRange |= !rows.TryPlace(a.Position, out py);
            }
            if (excess > 0 || inconsistent || suspect)
                flags |= DecodeStatus.Uncertain;
            if (outOfRange)
                flags |= DecodeStatus.OutOfRange;
            else if (hasPhase && modulation < o.MinModulation)
                flags |= DecodeStatus.LowModulation;
            else if (excess <= o.MaxUncertainBits)
            {
                flags |= DecodeStatus.Valid;
                var confidence = Math.Clamp(contrast / o.GoodContrast, 0, 1) / (1 + excess);
                if (hasPhase)
                    confidence *= Math.Clamp(modulation / o.GoodModulation, 0, 1);
                if (inconsistent)
                    confidence *= 0.5;
                map.ProjectorX[k] = (float)px;
                map.ProjectorY[k] = (float)py;
                map.Confidence[k] = (float)confidence;
            }
            map.Flags[k] = flags;
        }
        uncertainPerRow[y] = uncertainBits;
        decidedPerRow[y] = decidedBits;
    }

    /// <summary>Every valid suspect pixel is compared with the median of its trusted 5 × 5 neighbours: within
    /// the neighbourhood's own spread it stands; off by whole phase periods it is moved back; otherwise it is
    /// dropped. Trusted neighbours are valid and not suspect, so a cluster of suspects cannot vote itself in.
    /// With too few trusted neighbours to judge, a suspect stands — unless it must be confirmed.</summary>
    private void CleanJumps(CorrespondenceMap map, bool[] mustConfirm)
    {
        var w = map.Width;
        var h = map.Height;
        var snapX = (float[])map.ProjectorX.Clone();
        var snapY = (float[])map.ProjectorY.Clone();
        var snapFlags = (DecodeStatus[])map.Flags.Clone();
        const DecodeStatus suspect = DecodeStatus.Valid | DecodeStatus.Uncertain;
        Parallel.For(0, h, Compute.Options, y =>
        {
            Span<float> values = stackalloc float[24];
            Span<float> spread = stackalloc float[24];
            for (var x = 0; x < w; x++)
            {
                var k = y * w + x;
                if ((snapFlags[k] & suspect) != suspect)
                    continue;
                var corrected = false;
                var confirm = mustConfirm[k];
                var ok = _columns is null || Reconcile(snapX, snapFlags, map.ProjectorX, x, y, w, h, _columns, confirm, values, spread, ref corrected);
                ok = ok && (_rows is null || Reconcile(snapY, snapFlags, map.ProjectorY, x, y, w, h, _rows, confirm, values, spread, ref corrected));
                if (!ok)
                {
                    map.Flags[k] = (snapFlags[k] & ~DecodeStatus.Valid) | DecodeStatus.Rejected;
                    map.ProjectorX[k] = float.NaN;
                    map.ProjectorY[k] = float.NaN;
                    map.Confidence[k] = 0;
                }
                else if (corrected)
                {
                    map.Flags[k] |= DecodeStatus.Corrected;
                }
            }
        });
    }

    private static bool Reconcile(float[] snap, DecodeStatus[] flags, float[] target, int x, int y, int w, int h, AxisDecoder axis,
        bool mustConfirm, Span<float> values, Span<float> spread, ref bool corrected)
    {
        var n = 0;
        for (var dy = -2; dy <= 2; dy++)
        {
            var yy = y + dy;
            if (yy < 0 || yy >= h)
                continue;
            for (var dx = -2; dx <= 2; dx++)
            {
                var xx = x + dx;
                if (xx < 0 || xx >= w || (dx == 0 && dy == 0))
                    continue;
                var f = flags[yy * w + xx];
                if ((f & DecodeStatus.Valid) != 0 && (f & DecodeStatus.Uncertain) == 0)
                    values[n++] = snap[yy * w + xx];
            }
        }
        if (n < 6)
            return !mustConfirm;
        var near = values[..n];
        near.Sort();
        var median = Median(near);
        for (var i = 0; i < n; i++)
            spread[i] = Math.Abs(near[i] - median);
        var deviations = spread[..n];
        deviations.Sort();
        var tolerance = Math.Max(axis.MinTolerance, 4 * 1.4826 * Median(deviations));
        var k = y * w + x;
        var v = snap[k];
        var d = v - median;
        if (Math.Abs(d) <= tolerance)
            return true;
        if (axis.FinePeriod > 0)
        {
            var shift = axis.FinePeriod * Math.Round(-d / axis.FinePeriod);
            var moved = v + shift;
            if (shift != 0 && Math.Abs(d + shift) <= tolerance && moved >= axis.Offset && moved <= axis.Offset + axis.Extent)
            {
                target[k] = (float)moved;
                corrected = true;
                return true;
            }
        }
        return false;
    }

    private static float Median(Span<float> sorted)
    {
        var n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : 0.5f * (sorted[n / 2 - 1] + sorted[n / 2]);
    }

    /// <summary>Reads frame samples in linear camera units (0–255) and as captured.</summary>
    private interface IFrames
    {
        float Linear(int frame, int pixel);
        float Raw(int frame, int pixel);
    }

    private readonly record struct ByteFrames(byte[][] Images, float[] Lut) : IFrames
    {
        public float Linear(int frame, int pixel) => Lut[Images[frame][pixel]];
        public float Raw(int frame, int pixel) => Images[frame][pixel];
    }

    private readonly record struct FloatFrames(float[][] Images, float Gamma) : IFrames
    {
        public float Linear(int frame, int pixel)
        {
            var v = Images[frame][pixel];
            return Gamma == 1f ? v * 255f : 255f * MathF.Pow(Math.Max(v, 0f), Gamma);
        }

        public float Raw(int frame, int pixel) => Images[frame][pixel] * 255f;
    }

    private readonly record struct AxisResult(double Position, int Uncertain, double Modulation, bool Inconsistent, bool NearXorEdge, bool BaseUncertain);

    private sealed record PhaseSet(int Period, int[] Frames, double[] Sin, double[] Cos);

    /// <summary>One axis's frame indices in decode order and what the decode of a pixel needs.</summary>
    private sealed class AxisDecoder
    {
        private readonly int[] _pattern;
        private readonly int[] _inverse;
        private readonly bool[] _xored;
        private readonly int _baseSlot;
        private readonly int _finestLevel;
        private readonly bool _complementary;
        private readonly PhaseSet[] _phases;
        private readonly double _xorEdge;
        private readonly double _xorEdgeBand;

        public AxisDecoder(ScanPlan plan, AxisCode code, double xorEdgeBand)
        {
            Offset = code.Offset;
            Extent = code.Extent;
            var index = new Dictionary<(ScanFrameKind, int, bool, int, int), int>();
            foreach (var f in plan.Frames)
            {
                if (f.Axis == code.Axis)
                    index[(f.Kind, f.Level, f.Inverse, f.Period, f.PhaseStep)] = f.Index;
            }
            var pattern = new List<int>();
            var inverse = new List<int>();
            var xored = new List<bool>();
            for (var level = code.Levels - 1; level >= code.LowestLevel; level--)
            {
                pattern.Add(index[(ScanFrameKind.Gray, level, false, 0, 0)]);
                inverse.Add(index[(ScanFrameKind.Gray, level, true, 0, 0)]);
                xored.Add(code.XorBaseLevel >= 0 && level > code.XorBaseLevel);
            }
            if (code.ComplementaryLevel >= 0)
            {
                pattern.Add(index[(ScanFrameKind.Complementary, code.ComplementaryLevel, false, 0, 0)]);
                inverse.Add(index[(ScanFrameKind.Complementary, code.ComplementaryLevel, true, 0, 0)]);
                xored.Add(false);
            }
            _pattern = [.. pattern];
            _inverse = [.. inverse];
            _xored = [.. xored];
            // The XOR base is the finest level shown, which is always the last slot.
            _baseSlot = code.XorBaseLevel >= 0 ? _pattern.Length - 1 : -1;
            _finestLevel = code.FinestLevel;
            _complementary = code.ComplementaryLevel >= 0;
            _phases = new PhaseSet[code.Periods.Length];
            for (var p = 0; p < _phases.Length; p++)
            {
                var period = code.Periods[p];
                var n = code.PhaseSteps;
                var frames = new int[n];
                var sin = new double[n];
                var cos = new double[n];
                for (var s = 0; s < n; s++)
                {
                    frames[s] = index[(ScanFrameKind.Phase, 0, false, period, s)];
                    sin[s] = Math.Sin(2 * Math.PI * s / n);
                    cos[s] = Math.Cos(2 * Math.PI * s / n);
                }
                _phases[p] = new PhaseSet(period, frames, sin, cos);
            }
            BitCount = _pattern.Length;
            FinePeriod = _phases.Length > 0 ? _phases[^1].Period : 0;
            // The XOR base's edges sit at the odd multiples of 2^base; with the coarsest period P = 2^lowest that
            // is half way through each period (base = lowest − 1) or at its wrap (base = lowest).
            _xorEdge = -1;
            if (code.XorBaseLevel >= 0 && _phases.Length > 0 && code.XorBaseLevel >= code.LowestLevel - 1)
            {
                _xorEdge = code.XorBaseLevel == code.LowestLevel - 1 ? _phases[0].Period / 2.0 : 0;
                _xorEdgeBand = xorEdgeBand;
            }
            HasEvenPhase = _phases.Length > 0 && _phases[0].Frames.Length % 2 == 0;
            FirstPhaseFrames = _phases.Length > 0 ? _phases[0].Frames : [];
            MinTolerance = FinePeriod > 0 ? FinePeriod / 4.0 : 1.5 * (1 << _finestLevel);
        }

        public int Offset { get; }
        public int Extent { get; }
        public int BitCount { get; }

        /// <summary>The first sinusoid has an even step count (its residual measures the noise).</summary>
        public bool HasEvenPhase { get; }

        public int[] FirstPhaseFrames { get; }

        /// <summary>The finest sinusoid period, 0 without phase: the step a jump error comes in.</summary>
        public double FinePeriod { get; }

        /// <summary>The smallest disagreement with the neighbours the jump cleanup acts on.</summary>
        public double MinTolerance { get; }

        /// <summary>The coded position (pixels from the region's edge, continuous) of one camera pixel.</summary>
        public AxisResult Decode<TFrames>(TFrames frames, int k, float tau, float contrast) where TFrames : struct, IFrames
        {
            var baseBit = 0;
            var baseUncertain = false;
            if (_baseSlot >= 0)
            {
                var d = frames.Linear(_pattern[_baseSlot], k) - frames.Linear(_inverse[_baseSlot], k);
                baseBit = d > 0 ? 1 : 0;
                baseUncertain = Math.Abs(d) < tau;
            }
            var gray = 0;
            var uncertain = 0;
            for (var i = 0; i < _pattern.Length; i++)
            {
                var d = frames.Linear(_pattern[i], k) - frames.Linear(_inverse[i], k);
                var bit = d > 0 ? 1 : 0;
                // On an edge of the XOR base every XOR-ed level is as close to call as the base itself: one
                // uncertainty, not many. A wrong call there flips every coarse level together, which moves the
                // unwrapped phase by whole periods — the jump cleanup's business, so the pixel is a suspect.
                if (Math.Abs(d) < tau && !(baseUncertain && _xored[i]))
                    uncertain++;
                if (_xored[i])
                    bit ^= baseBit;
                gray = (gray << 1) | bit;
            }
            // The levels shown name a cell 2^finest wide; its centre is the Gray code's own estimate.
            var cell = (double)(1 << _finestLevel);
            var u = (GrayCode.Decode(gray) + 0.5) * cell;
            var modulation = double.PositiveInfinity;
            var inconsistent = false;
            var nearXorEdge = false;
            for (var p = 0; p < _phases.Length; p++)
            {
                var set = _phases[p];
                double s = 0, c = 0;
                for (var n = 0; n < set.Frames.Length; n++)
                {
                    var v = frames.Linear(set.Frames[n], k);
                    s += v * set.Sin[n];
                    c += v * set.Cos[n];
                }
                var amplitude = 2.0 / set.Frames.Length * Math.Sqrt(s * s + c * c);
                modulation = Math.Min(modulation, 2 * amplitude / contrast);
                var phi = Math.Atan2(s, c);
                if (phi < 0)
                    phi += 2 * Math.PI;
                var wrapped = phi / (2 * Math.PI) * set.Period;
                var unwrapped = wrapped + set.Period * Math.Round((u - wrapped) / set.Period);
                // With the complementary level the Gray cell holds the pixel to ± half a cell; further off
                // means a Gray bit or the phase is wrong, and the rounding may have picked the wrong period.
                if (p == 0 && _complementary && Math.Abs(unwrapped - u) > 0.75 * cell)
                    inconsistent = true;
                if (p == 0 && _xorEdge >= 0)
                {
                    var fromEdge = Math.Abs(wrapped - _xorEdge);
                    nearXorEdge = Math.Min(fromEdge, set.Period - fromEdge) < _xorEdgeBand;
                }
                u = unwrapped;
            }
            return new AxisResult(u, uncertain, modulation, inconsistent, nearXorEdge || baseUncertain, baseUncertain);
        }

        /// <summary>The projector coordinate of a coded position; false when it lies outside the region (half a
        /// pixel of slack is clamped in).</summary>
        public bool TryPlace(double coded, out double position)
        {
            if (coded < -0.5 || coded > Extent + 0.5)
            {
                position = double.NaN;
                return false;
            }
            position = Offset + Math.Clamp(coded, 0, Extent);
            return true;
        }
    }
}
