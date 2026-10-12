using System.Collections.Immutable;
using PlayMap.Devices.Model;

namespace PlayMap.Devices.Commands;

/// <summary>A lens motor move: to an absolute raw position, or by a number of steps (+ forward/up/right).
/// Which a driver takes is its protocol's: Christie and Panasonic document absolute shift positions, Barco's
/// Pulse documents steps.</summary>
public sealed record LensMove(LensAxis Axis, int Value, bool Absolute)
{
    public static LensMove To(LensAxis axis, int position) => new(axis, position, true);

    public static LensMove By(LensAxis axis, int steps) => new(axis, steps, false);
}

/// <summary>Internal edge blending: on/off and the blend width per side in the projector's own units (pixels on
/// the models whose table we read). 0 on a side = that side off.</summary>
public sealed record EdgeBlendSettings(bool Enabled, int Left = 0, int Right = 0, int Top = 0, int Bottom = 0)
{
    public static readonly EdgeBlendSettings Off = new(false);
}

/// <summary>A black-level lift (raise the projector's black outside the overlap to match it), per channel in the
/// projector's own units.</summary>
public sealed record BlackLevelSettings(bool Enabled, int Red, int Green, int Blue);

/// <summary>Colour gains (white balance "high"), per channel, in the projector's own units.</summary>
public sealed record ColorGains(int Red, int Green, int Blue);

/// <summary>A warp grid: rows × columns of output-space points (pixels of the projector's native raster),
/// row-major from the top-left. The format a projector would take is vendor-specific; no driver ships an upload
/// (see docs/PROJECTORS.md for why).</summary>
public sealed record WarpGrid(int Columns, int Rows, ImmutableArray<(double X, double Y)> Points)
{
    public bool IsValid => Columns >= 2 && Rows >= 2 && !Points.IsDefault && Points.Length == Columns * Rows;
}

/// <summary>A test pattern by the driver's own name ("white", "grid"…); <see cref="Off"/> turns patterns off.</summary>
public static class TestPatterns
{
    public const string Off = "off";

    /// <summary>The pattern a driver shows to identify a projector when nothing else is asked.</summary>
    public const string Identify = "identify";
}
