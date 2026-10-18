using System.Collections.Immutable;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Model;

/// <summary>One node of a pen-tool path: an anchor and its two handles, relative to the anchor. A corner
/// node's handles move independently; a smooth node keeps them collinear.</summary>
public readonly record struct BezierNode(Vec2d Point, Vec2d In, Vec2d Out, bool Smooth)
{
    public static BezierNode Corner(double x, double y) => new(new Vec2d(x, y), Vec2d.Zero, Vec2d.Zero, false);
}

/// <summary>A path of cubic Bézier segments (the pen tool's shape). Coordinates are in the space of what
/// owns it: normalised output units for an output mask, UV units for a surface mask.</summary>
public sealed record BezierPath(ImmutableArray<BezierNode> Nodes, bool Closed)
{
    public static readonly BezierPath Empty = new([], true);

    public static BezierPath Rectangle(double x, double y, double w, double h) => new(
        [BezierNode.Corner(x, y), BezierNode.Corner(x + w, y), BezierNode.Corner(x + w, y + h), BezierNode.Corner(x, y + h)], true);
}

public enum MaskMode
{
    /// <summary>Shows only what is inside (the first add starts from nothing).</summary>
    Add,
    /// <summary>Removes what is inside.</summary>
    Subtract,
    /// <summary>Keeps only the overlap with what came before.</summary>
    Intersect,
}

/// <summary>A soft mask: a closed path, how it combines, how far its edge feathers.</summary>
public sealed record Mask(string Id, string Name, BezierPath Path, MaskMode Mode, double Feather, bool Invert, bool Enabled) : IEntity;

/// <summary>The four corners' offsets from the raster's corners, in normalised output units (x ÷ width,
/// y ÷ height). A homography: straight lines stay straight.</summary>
public sealed record CornerPin(Vec2d TopLeft, Vec2d TopRight, Vec2d BottomRight, Vec2d BottomLeft)
{
    public static readonly CornerPin None = new(Vec2d.Zero, Vec2d.Zero, Vec2d.Zero, Vec2d.Zero);

    public bool IsNone => TopLeft == Vec2d.Zero && TopRight == Vec2d.Zero && BottomRight == Vec2d.Zero && BottomLeft == Vec2d.Zero;
}

public enum WarpInterpolation
{
    Linear,
    /// <summary>Smooth through every node; tangents from the neighbours (Patterns' mesh).</summary>
    CatmullRom,
    /// <summary>Smooth, with each node's tangent handles set by hand (the fine-tune mode).</summary>
    Bezier,
}

/// <summary>A free-form warp: a lattice of nodes over the output, each an offset from rest in normalised
/// output units, row-major. In Bézier mode each node also carries its u and v tangent handles (offsets from
/// the Catmull-Rom tangent, so all-zero handles equal the Catmull-Rom surface).</summary>
public sealed record WarpLattice(
    int Columns,
    int Rows,
    ImmutableArray<Vec2d> Offsets,
    WarpInterpolation Interpolation,
    ImmutableArray<Vec2d> HandlesU,
    ImmutableArray<Vec2d> HandlesV)
{
    public const int MinSize = 2;
    public const int MaxSize = 65;

    public static WarpLattice AtRest(int columns, int rows, WarpInterpolation interpolation = WarpInterpolation.CatmullRom)
    {
        columns = Math.Clamp(columns, MinSize, MaxSize);
        rows = Math.Clamp(rows, MinSize, MaxSize);
        var n = columns * rows;
        var zeros = ImmutableArray.CreateRange(Enumerable.Repeat(Vec2d.Zero, n));
        return new WarpLattice(columns, rows, zeros, interpolation, zeros, zeros);
    }

    public bool IsAtRest => Offsets.All(o => o == Vec2d.Zero) && HandlesU.All(h => h == Vec2d.Zero) && HandlesV.All(h => h == Vec2d.Zero);

    public Vec2d Offset(int column, int row) => Offsets[row * Columns + column];
}

/// <summary>The 2D correction after the 3D render (or on a plain screen): corner-pin, then the lattice.</summary>
public sealed record OutputWarp(bool Enabled, CornerPin Pin, WarpLattice? Lattice)
{
    public static readonly OutputWarp None = new(false, CornerPin.None, null);
}

public enum BlendCurve
{
    Linear,
    Cosine,
    SCurve,
    Gamma22,
}

/// <summary>Manual soft-edge widths in output pixels (for plain screens and 2D blends).</summary>
public sealed record EdgeBlend(double Left, double Top, double Right, double Bottom)
{
    public static readonly EdgeBlend None = new(0, 0, 0, 0);
}

/// <summary>How an output joins its neighbours. With <see cref="Auto3D"/> the blend map is computed from
/// the projectors' frusta on the stage's geometry; otherwise the manual edges apply.</summary>
public sealed record BlendSettings(
    bool Auto3D,
    EdgeBlend Edges,
    BlendCurve Curve,
    /// <summary>The hand's γ for the blend's fades, or 0: the output's measured γ (<see cref="MeasuredColor.Gamma"/>) where it has
    /// one, else <see cref="AssumedGamma"/>. <see cref="DrawnGamma"/> is the γ every renderer uses.</summary>
    double Gamma,
    /// <summary>The projector's black as a fraction of its white (0..0.25) for black-level matching.</summary>
    double BlackLevel,
    /// <summary>Compensate brightness for the angle of incidence and the distance to the surface.</summary>
    bool IntensityCompensation)
{
    /// <summary>The γ a blend is drawn through when neither the hand nor a measurement gives one.</summary>
    public const double AssumedGamma = 2.2;

    public static readonly BlendSettings Default = new(true, EdgeBlend.None, BlendCurve.Cosine, 0, 0, false);

    /// <summary>The γ the fades are drawn through: the hand's (<see cref="Gamma"/> above 0), else the measured γ where
    /// <paramref name="measured"/> has one above 0, else <see cref="AssumedGamma"/>.</summary>
    public double DrawnGamma(MeasuredCorrection? measured) =>
        Gamma > 0 ? Gamma : measured?.Color is { Gamma: > 0 } colour ? colour.Gamma : AssumedGamma;
}

/// <summary>Per-output colour correction applied last.</summary>
public sealed record ColorTrim(double Brightness, double Contrast, double Gamma, double GainR, double GainG, double GainB, double Lift)
{
    public static readonly ColorTrim Neutral = new(1, 1, 1, 1, 1, 1, 0);

    public bool IsNeutral => this == Neutral;
}

public enum OutputKind
{
    /// <summary>A full-screen window on a display (a projector, an LED processor, a monitor).</summary>
    Display,
    /// <summary>A movable window (a preview, a confidence screen).</summary>
    Window,
    /// <summary>An NDI sender.</summary>
    Ndi,
    /// <summary>Planned, not connected: drawn on the plan and in the previews only.</summary>
    Virtual,
}

public enum OutputMode
{
    /// <summary>Renders the stage from a projector's pose: the 3D map.</summary>
    Projector,
    /// <summary>Shows a rectangle of a canvas: a plain screen, an LED wall, a 2D blend.</summary>
    Canvas,
    /// <summary>Shows one source full-frame.</summary>
    Source,
}

/// <summary>A physical (or planned) picture out of the node.</summary>
public sealed record Output(
    string Id,
    string Name,
    OutputKind Kind,
    /// <summary>Which display: a stable key from the platform's display list ("" = the first free one).</summary>
    string DisplayKey,
    int Width,
    int Height,
    double RefreshHz,
    OutputMode Mode,
    string? ProjectorId,
    /// <summary>Canvas mode: the canvas; Source mode: the source.</summary>
    SourceRef Source,
    /// <summary>Canvas mode: the rectangle of the canvas shown, in canvas pixels.</summary>
    RectD SourceRect,
    OutputWarp Warp,
    BlendSettings Blend,
    ColorTrim Color,
    ImmutableArray<Mask> Masks,
    /// <summary>Where the output sits on the multi-screen plan, in plan pixels.</summary>
    RectD Plan,
    /// <summary>0, 90, 180 or 270 degrees clockwise.</summary>
    int Rotation,
    bool Enabled) : IEntity
{
    /// <summary>The display's input lag, ms (light leaves the lens this long after the vblank), 0–500; 0 = unknown (wp2-output).</summary>
    public double LatencyMs { get; init; }

    /// <summary>Effects on the output's picture before its warp (wp2-fx).</summary>
    public FxChain Fx { get; init; } = FxChain.Empty;

    /// <summary>A camera-measured correction drawn under the operator's warp (wp2-align, wp2-facade).</summary>
    public MeasuredCorrection? Measured { get; init; }

    /// <summary>The calibration ledger's current result for this output (wp2-wall).</summary>
    public string? CalibrationId { get; init; }

    /// <summary>The ledger's fallback result: what CALIBRATION FALLBACK returns to (wp2-wall).</summary>
    public string? FallbackId { get; init; }

    /// <summary>The ledger's staged result, waiting to be accepted (wp2-wall).</summary>
    public string? StagedId { get; init; }

    /// <summary>Bits per colour channel the output presents: 8 or 10 (wp3-present).</summary>
    public int ColorBits { get; init; } = 8;

    /// <summary>The output's HDR signal, or <see cref="HdrMode.Off"/> (wp3-present).</summary>
    public HdrMode Hdr { get; init; } = HdrMode.Off;

    /// <summary>The luminance the content's white is shown at on an HDR10 output, cd/m² (wp3-present).</summary>
    public double NitTarget { get; init; } = DefaultNitTarget;

    /// <summary>Frames queued between the render and the display: 0 (the node decides: 1 for an output showing a live input, else 2),
    /// 1 or 2 (wp3-present).</summary>
    public int FramesInFlight { get; init; }

    /// <summary>The frame marks drawn last on the air, or null for none (wp3-stability).</summary>
    public OutputWatermark? Watermark { get; init; }

    /// <summary>The output shows a live input as fast as it can: one frame in flight, outside the delay ring (wp3-capture).</summary>
    public bool PassThrough { get; init; }

    /// <summary>An HDR10 output's white with nothing typed: the reference white of ITU-R BT.2408, cd/m².</summary>
    public const double DefaultNitTarget = 203;

    /// <summary>The γ the output's blend fades are drawn through (<see cref="BlendSettings.DrawnGamma"/> with its measured layer).</summary>
    public double DrawnBlendGamma() => Blend.DrawnGamma(Measured);
}

/// <summary>The signal an HDR output sends: off (SDR), HDR10 (PQ in BT.2020), or scRGB (linear light, 1.0 = 80 cd/m²).</summary>
public enum HdrMode
{
    Off,
    Hdr10,
    Scrgb,
}

/// <summary>Where an output's frame marks sit: along its top edge, its bottom edge, or both.</summary>
public enum WatermarkPlace
{
    Top,
    Bottom,
    Both,
}

/// <summary>An output's frame marks: their edge, the first block's corner in output pixels, and the block size in pixels (4–32).</summary>
public sealed record OutputWatermark(WatermarkPlace Place, int X, int Y, int Size);
