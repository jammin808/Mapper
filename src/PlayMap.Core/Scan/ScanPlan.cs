using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;

namespace PlayMap.Core.Scan;

/// <summary>A rectangle of whole pixels: (X, Y) is its top-left pixel, it spans Width × Height pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;

    public PixelRect Intersect(PixelRect other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        var r = Math.Min(Right, other.Right);
        var b = Math.Min(Bottom, other.Bottom);
        return r > x && b > y ? new PixelRect(x, y, r - x, b - y) : default;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{X},{Y} {Width}x{Height}");
}

/// <summary>Binary-reflected Gray code: neighbouring values differ in exactly one bit, so a camera pixel on a
/// stripe's edge decodes to one side or the other, never to a far column. Level ℓ is bit ℓ of the code: its
/// edges sit at the odd multiples of 2^ℓ (stripes 2^(ℓ+1) wide, the first one half that), so no two levels
/// share an edge, and the levels from ℓ up place a value within a cell 2^ℓ wide.</summary>
public static class GrayCode
{
    /// <summary>The levels needed to give every value in [0, extent) its own code.</summary>
    public static int LevelsFor(int extent) => extent <= 1 ? 1 : 32 - BitOperations.LeadingZeroCount((uint)(extent - 1));

    public static int Encode(int value) => value ^ (value >> 1);

    /// <summary>Gray → binary (prefix XOR). A code holding only the levels at and above ℓ, shifted down by ℓ,
    /// decodes to the binary value shifted down by ℓ: the coarse levels alone name the coarse stripe.</summary>
    public static int Decode(int gray)
    {
        var g = (uint)gray;
        g ^= g >> 16;
        g ^= g >> 8;
        g ^= g >> 4;
        g ^= g >> 2;
        g ^= g >> 1;
        return (int)g;
    }

    /// <summary>Bit <paramref name="level"/> of the code of <paramref name="value"/>.</summary>
    public static bool Bit(int value, int level) => ((Encode(value) >> level) & 1) != 0;
}

/// <summary>How hard a scan works for its answer.</summary>
public enum ScanMode
{
    /// <summary>Gray code with inverses down to single pixels, both axes, plus white and black: whole projector
    /// pixels (a decoded column is the column's centre), the fewest assumptions, 2 + 4·⌈log₂ n⌉ frames.</summary>
    Fast,

    /// <summary>Gray code with inverses down to the phase period plus a complementary level half a period
    /// finer, then an N-step sinusoid (4 by default, 16 px period): the Gray code names the period, the phase
    /// places the pixel inside it to a fraction of a pixel, and the complementary level moves every Gray edge
    /// off the phase's wrap so no pixel jumps a period. Fine single-pixel stripes — the first casualties of
    /// defocus and a coarse camera — are never shown.</summary>
    Robust,

    /// <summary>Robust's frames with every Gray level coarser than the complementary one XOR-ed with it, so
    /// every projected stripe is fine (Gupta et al., CVPR 2011): long-range interreflection in corners and
    /// niches lights a pattern and its inverse alike and cancels in the comparison, where it would flip a
    /// coarse plain stripe. The decoder XORs the levels back. Not in this mode yet: micro phase shifting
    /// (several close high frequencies instead of the Gray levels, which also survives the short-range
    /// blur of marble, wax, foam and skin) and the ensemble of codes that keeps only the decodes two codes
    /// agree on.</summary>
    Difficult,
}

/// <summary>Which projector axes a scan codes.</summary>
[Flags]
public enum ScanAxes
{
    /// <summary>Vertical stripes: each camera pixel learns its projector column.</summary>
    Columns = 1,

    /// <summary>Horizontal stripes: each camera pixel learns its projector row.</summary>
    Rows = 2,

    /// <summary>Both: the projector point, for triangulation by ray–ray and for the projector-space image.</summary>
    Both = Columns | Rows,
}

/// <summary>One projector axis.</summary>
public enum ScanAxis
{
    None,
    Columns,
    Rows,
}

/// <summary>What one frame of a plan shows.</summary>
public enum ScanFrameKind
{
    /// <summary>Every pixel of the region lit: "this projector reaches here", and the smart scan's picture.</summary>
    White,

    /// <summary>Nothing lit: the room's own light.</summary>
    Black,

    /// <summary>One Gray level as stripes (or its inverse).</summary>
    Gray,

    /// <summary>The Gray level half a phase period finer than the period's own, whose edges fall on the
    /// phase's half-way points so the phase never has to be unwrapped across an uncertain edge.</summary>
    Complementary,

    /// <summary>One step of an N-step sinusoid of a given period.</summary>
    Phase,
}

/// <summary>One frame of a scan plan. <see cref="Level"/> is the Gray level (edges at the odd multiples of 2^Level) for
/// Gray and complementary frames; <see cref="XorCoded"/> says the level is shown XOR-ed with the axis's XOR base
/// level; <see cref="Period"/>, <see cref="PhaseStep"/> and <see cref="PhaseSteps"/> describe a sinusoid
/// frame (its phase is shifted by 2π·step/steps).</summary>
public readonly record struct ScanFrame(
    int Index,
    ScanFrameKind Kind,
    ScanAxis Axis,
    int Level,
    bool Inverse,
    bool XorCoded,
    int Period,
    int PhaseStep,
    int PhaseSteps)
{
    /// <summary>The frame in words, for logs and the desk ("columns bit 5 inverse").</summary>
    public string Name
    {
        get
        {
            var axis = Axis == ScanAxis.Rows ? "rows" : "columns";
            var inverse = Inverse ? " inverse" : "";
            var xor = XorCoded ? " xor" : "";
            return Kind switch
            {
                ScanFrameKind.White => "white",
                ScanFrameKind.Black => "black",
                ScanFrameKind.Gray => string.Create(CultureInfo.InvariantCulture, $"{axis} bit {Level}{xor}{inverse}"),
                ScanFrameKind.Complementary => string.Create(CultureInfo.InvariantCulture, $"{axis} complementary bit {Level}{inverse}"),
                _ => string.Create(CultureInfo.InvariantCulture, $"{axis} phase {Period} px step {PhaseStep + 1}/{PhaseSteps}"),
            };
        }
    }
}

/// <summary>How one axis is coded: the Gray levels from <see cref="Levels"/> − 1 down to
/// <see cref="LowestLevel"/>, then the complementary level (−1 when none), then the phase periods coarse to
/// fine. Codes count from <see cref="Offset"/> (the region's first column or row); <see cref="Extent"/> values
/// are coded.</summary>
public sealed record AxisCode(
    ScanAxis Axis,
    int Offset,
    int Extent,
    int Levels,
    int LowestLevel,
    int ComplementaryLevel,
    int XorBaseLevel,
    ImmutableArray<int> Periods,
    int PhaseSteps)
{
    public bool HasPhase => Periods.Length > 0;

    /// <summary>The finest Gray level shown: the Gray code alone places a pixel within a cell 2^FinestLevel wide.</summary>
    public int FinestLevel => ComplementaryLevel >= 0 ? ComplementaryLevel : LowestLevel;

    /// <summary>Gray levels shown, the complementary one included.</summary>
    public int GrayLevelCount => Levels - LowestLevel + (ComplementaryLevel >= 0 ? 1 : 0);

    public int FrameCount => 2 * GrayLevelCount + Periods.Length * PhaseSteps;
}

/// <summary>What a scan shows. Defaults per mode: Fast — Gray to single pixels; Robust and Difficult — Gray to
/// a 16 px period, the complementary level, and a 4-step sinusoid.</summary>
public sealed record ScanPlanOptions
{
    public ScanMode Mode { get; init; } = ScanMode.Robust;

    public ScanAxes Axes { get; init; } = ScanAxes.Both;

    /// <summary>Limits the scan to a rectangle of the projector (null = the whole raster). Outside it every
    /// frame is black; codes count from the rectangle's corner and the decoder adds it back.</summary>
    public PixelRect? Region { get; init; }

    /// <summary>Shows the Gray level half a period finer than the coarsest phase period (ignored without
    /// phase). Without it, pixels on a period edge can be unwrapped a whole period off.</summary>
    public bool Complementary { get; init; } = true;

    /// <summary>Steps per sinusoid (≥ 3). Four cancels the second harmonic a projector's gamma leaves.</summary>
    public int PhaseSteps { get; init; } = 4;

    /// <summary>Sinusoid periods in projector pixels, coarse to fine; null takes the mode's default (none for
    /// Fast, 16 otherwise). The coarsest must be a power of two (the Gray code names its periods); each finer
    /// one is unwrapped by the one before and must be at most its size.</summary>
    public IReadOnlyList<int>? PhasePeriods { get; init; }

    /// <summary>The projector's display gamma: sinusoid frames are pre-distorted by 1/gamma so the light on the
    /// surface is a sinusoid (1 = no pre-distortion). Stripes are 0 and full scale and are not affected.</summary>
    public double ProjectorGamma { get; init; } = 2.2;

    /// <summary>The lit level of a frame (0–255).</summary>
    public byte High { get; init; } = 255;

    /// <summary>The unlit level of a frame (0–255).</summary>
    public byte Low { get; init; }
}

/// <summary>
/// A structured-light scan of one projector: the ordered frames (white, black, then per axis — columns
/// before rows — each Gray level MSB first followed by its inverse, the complementary level and its inverse,
/// then each phase period's steps), and a CPU renderer that draws any frame as gray8 at the projector's
/// resolution for the GPU to show as a texture. Immutable; safe to share between threads.
/// </summary>
public sealed class ScanPlan
{
    private readonly ScanFrame[] _frames;

    private ScanPlan(int width, int height, PixelRect region, ScanPlanOptions options, AxisCode? columns, AxisCode? rows, ScanFrame[] frames)
    {
        ProjectorWidth = width;
        ProjectorHeight = height;
        Region = region;
        Options = options;
        Columns = columns;
        Rows = rows;
        _frames = frames;
        WhiteIndex = Array.FindIndex(frames, f => f.Kind == ScanFrameKind.White);
        BlackIndex = Array.FindIndex(frames, f => f.Kind == ScanFrameKind.Black);
    }

    public int ProjectorWidth { get; }
    public int ProjectorHeight { get; }

    /// <summary>The scanned rectangle of the projector (the whole raster unless limited).</summary>
    public PixelRect Region { get; }

    public ScanPlanOptions Options { get; }
    public ScanMode Mode => Options.Mode;
    public ScanAxes Axes => Options.Axes;

    /// <summary>The column code, or null when columns are not scanned.</summary>
    public AxisCode? Columns { get; }

    /// <summary>The row code, or null when rows are not scanned.</summary>
    public AxisCode? Rows { get; }

    public IReadOnlyList<ScanFrame> Frames => _frames;
    public int Count => _frames.Length;
    public int WhiteIndex { get; }
    public int BlackIndex { get; }

    /// <summary>True when the decode is sub-pixel (the plan shows sinusoids).</summary>
    public bool HasPhase => (Columns?.HasPhase ?? false) || (Rows?.HasPhase ?? false);

    public AxisCode? Code(ScanAxis axis) => axis switch
    {
        ScanAxis.Columns => Columns,
        ScanAxis.Rows => Rows,
        _ => null,
    };

    /// <summary>The plan for a projector of <paramref name="width"/> × <paramref name="height"/>.</summary>
    public static ScanPlan Create(int width, int height, ScanPlanOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        options ??= new ScanPlanOptions();
        var full = new PixelRect(0, 0, width, height);
        var region = options.Region is { } r ? r.Intersect(full) : full;
        if (region.IsEmpty)
            throw new ArgumentException("The scan region lies outside the projector.", nameof(options));
        if (options.PhaseSteps < 3)
            throw new ArgumentException("A sinusoid needs at least three steps.", nameof(options));
        if (!(options.ProjectorGamma > 0))
            throw new ArgumentException("The projector gamma must be positive.", nameof(options));
        if (options.High <= options.Low)
            throw new ArgumentException("The lit level must be above the unlit level.", nameof(options));
        if ((options.Axes & ScanAxes.Both) == 0)
            throw new ArgumentException("A scan codes at least one axis.", nameof(options));
        var periods = ResolvePeriods(options);

        var frames = new List<ScanFrame>
        {
            new(0, ScanFrameKind.White, ScanAxis.None, 0, false, false, 0, 0, 0),
            new(1, ScanFrameKind.Black, ScanAxis.None, 0, false, false, 0, 0, 0),
        };
        AxisCode? columns = null;
        AxisCode? rows = null;
        if ((options.Axes & ScanAxes.Columns) != 0)
            columns = AddAxis(frames, ScanAxis.Columns, region.X, region.Width, options, periods);
        if ((options.Axes & ScanAxes.Rows) != 0)
            rows = AddAxis(frames, ScanAxis.Rows, region.Y, region.Height, options, periods);
        return new ScanPlan(width, height, region, options, columns, rows, [.. frames]);
    }

    private static ImmutableArray<int> ResolvePeriods(ScanPlanOptions options)
    {
        if (options.Mode == ScanMode.Fast && options.PhasePeriods is null)
            return [];
        var periods = options.PhasePeriods is { } p ? p.ToImmutableArray() : [16];
        for (var i = 0; i < periods.Length; i++)
        {
            if (periods[i] < 4)
                throw new ArgumentException("A sinusoid period is at least 4 pixels.", nameof(options));
            if (i > 0 && periods[i] > periods[i - 1])
                throw new ArgumentException("Sinusoid periods run coarse to fine.", nameof(options));
        }
        if (periods.Length > 0 && !BitOperations.IsPow2(periods[0]))
            throw new ArgumentException("The coarsest sinusoid period is a power of two, so the Gray code names its periods.", nameof(options));
        return periods;
    }

    private static AxisCode AddAxis(List<ScanFrame> frames, ScanAxis axis, int offset, int extent, ScanPlanOptions options, ImmutableArray<int> periods)
    {
        var levels = GrayCode.LevelsFor(extent);
        var lowest = 0;
        var complementary = -1;
        if (periods.Length > 0)
        {
            lowest = Math.Min(BitOperations.Log2((uint)periods[0]), levels);
            // The complementary level only matters while the Gray code names periods at all.
            if (options.Complementary && lowest >= 1 && lowest < levels)
                complementary = lowest - 1;
        }
        var finest = complementary >= 0 ? complementary : lowest;
        // XOR base: the finest level shown, which every coarser level is XOR-ed with — only when there is a
        // coarser level to carry.
        var xorBase = options.Mode == ScanMode.Difficult && levels - 1 > finest ? finest : -1;
        for (var level = levels - 1; level >= lowest; level--)
        {
            var xored = xorBase >= 0 && level > xorBase;
            frames.Add(new ScanFrame(frames.Count, ScanFrameKind.Gray, axis, level, false, xored, 0, 0, 0));
            frames.Add(new ScanFrame(frames.Count, ScanFrameKind.Gray, axis, level, true, xored, 0, 0, 0));
        }
        if (complementary >= 0)
        {
            frames.Add(new ScanFrame(frames.Count, ScanFrameKind.Complementary, axis, complementary, false, false, 0, 0, 0));
            frames.Add(new ScanFrame(frames.Count, ScanFrameKind.Complementary, axis, complementary, true, false, 0, 0, 0));
        }
        foreach (var period in periods)
        {
            for (var step = 0; step < options.PhaseSteps; step++)
                frames.Add(new ScanFrame(frames.Count, ScanFrameKind.Phase, axis, 0, false, false, period, step, options.PhaseSteps));
        }
        return new AxisCode(axis, offset, extent, levels, lowest, complementary, xorBase, periods, options.PhaseSteps);
    }

    /// <summary>Renders frame <paramref name="index"/> as gray8, row-major, at the projector's resolution.</summary>
    public byte[] Render(int index)
    {
        var buffer = new byte[ProjectorWidth * ProjectorHeight];
        Render(index, buffer);
        return buffer;
    }

    /// <summary>Renders frame <paramref name="index"/> into <paramref name="destination"/> (gray8, row-major,
    /// at least projector width × height bytes). Outside the region every frame is the unlit level.</summary>
    public void Render(int index, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _frames.Length);
        var w = ProjectorWidth;
        var size = w * ProjectorHeight;
        if (destination.Length < size)
            throw new ArgumentException("The destination is smaller than the projector's raster.", nameof(destination));
        var frame = _frames[index];
        var target = destination[..size];
        target.Fill(Options.Low);
        var region = Region;
        switch (frame.Kind)
        {
            case ScanFrameKind.Black:
                return;
            case ScanFrameKind.White:
                for (var y = region.Y; y < region.Bottom; y++)
                    target.Slice(y * w + region.X, region.Width).Fill(Options.High);
                return;
        }
        var code = Code(frame.Axis) ?? throw new InvalidOperationException("The frame's axis is not coded.");
        if (frame.Axis == ScanAxis.Columns)
        {
            var line = target.Slice(region.Y * w + region.X, region.Width);
            for (var t = 0; t < region.Width; t++)
                line[t] = Value(frame, code, t);
            for (var y = region.Y + 1; y < region.Bottom; y++)
                line.CopyTo(target.Slice(y * w + region.X, region.Width));
        }
        else
        {
            for (var t = 0; t < region.Height; t++)
                target.Slice((region.Y + t) * w + region.X, region.Width).Fill(Value(frame, code, t));
        }
    }

    /// <summary>The level frame <paramref name="index"/> shows at projector pixel (x, y).</summary>
    public byte ValueAt(int index, int x, int y)
    {
        var frame = _frames[index];
        if (!Region.Contains(x, y))
            return Options.Low;
        return frame.Kind switch
        {
            ScanFrameKind.White => Options.High,
            ScanFrameKind.Black => Options.Low,
            _ => Value(frame, Code(frame.Axis)!, frame.Axis == ScanAxis.Columns ? x - Region.X : y - Region.Y),
        };
    }

    /// <summary>The level at coded position <paramref name="t"/> (pixels from the region's edge) on the frame's axis.</summary>
    private byte Value(in ScanFrame frame, AxisCode code, int t)
    {
        if (frame.Kind == ScanFrameKind.Phase)
        {
            // Pixel t's centre is at t + 0.5: the decoder's continuous coordinate puts a column's centre there.
            var phase = 2 * Math.PI * ((t + 0.5) / frame.Period - (double)frame.PhaseStep / frame.PhaseSteps);
            var light = 0.5 + 0.5 * Math.Cos(phase);
            var level = Math.Pow(light, 1 / Options.ProjectorGamma);
            return (byte)Math.Round(Options.Low + (Options.High - Options.Low) * level);
        }
        var bit = GrayCode.Bit(t, frame.Level);
        if (frame.XorCoded)
            bit ^= GrayCode.Bit(t, code.XorBaseLevel);
        if (frame.Inverse)
            bit = !bit;
        return bit ? Options.High : Options.Low;
    }

    /// <summary>The plan in words ("Robust scan of 1920×1080: 52 frames, columns 7 Gray levels + complementary
    /// + 4-step phase of 16 px; rows …").</summary>
    public string Describe()
    {
        var parts = new List<string>();
        foreach (var code in (ReadOnlySpan<AxisCode?>)[Columns, Rows])
        {
            if (code is null)
                continue;
            var name = code.Axis == ScanAxis.Columns ? "columns" : "rows";
            var gray = code.Levels - code.LowestLevel;
            var text = string.Create(CultureInfo.InvariantCulture, $"{name} {gray} Gray levels");
            if (code.XorBaseLevel >= 0)
                text += " (XOR-coded)";
            if (code.ComplementaryLevel >= 0)
                text += " + complementary";
            if (code.HasPhase)
                text += string.Create(CultureInfo.InvariantCulture, $" + {code.PhaseSteps}-step phase of {string.Join("/", code.Periods)} px");
            parts.Add(text);
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"{Mode} scan of {Region.Width}x{Region.Height}: {Count} frames, {string.Join("; ", parts)}");
    }
}
