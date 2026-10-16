using System.Collections.Immutable;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Model;

public enum ScreenShape { Auto, Flat, Cylinder }
public enum CanvasFit { Inside, Corners }
public enum ColorMatchMode { Off, White }

/// <summary>The screen as measured.</summary>
public sealed record ScreenModel(ScreenShape Shape, Vec3d Origin, Vec3d Axis, Vec3d Up, double RadiusM, double ArcDeg,
    double WidthM, double HeightM, double RmsM, bool Metric);

/// <summary>Where the canvas lands on the measured screen.</summary>
/// <param name="CameraCorners">TL TR BR BL, 0–1 of the camera picture; [] when unset.</param>
public sealed record AlignTarget(CanvasFit Fit, double Aspect, ImmutableArray<Vec2d> CameraCorners, double MarginPct)
{
    public static readonly AlignTarget Default = new(CanvasFit.Inside, 0, [], 0);
}

/// <summary>What an alignment run measures and fits.</summary>
public sealed record AlignOptions
{
    public static readonly AlignOptions Default = new();
    public ColorMatchMode Color { get; init; } = ColorMatchMode.White;
    public bool BlackLevel { get; init; } = true;
    /// <summary>How far the measured blend evens out the white, 0 (not at all) to 1 (down to the dimmest): a camera's vignetting and a
    /// gain screen's angle bias what it measures, so partly.</summary>
    public double Uniformity { get; init; } = 0.6;
    public BlendCurve Curve { get; init; } = BlendCurve.Cosine;
    /// <summary>Fraction of the raster side (as BlendSolveOptions).</summary>
    public double EdgeWidth { get; init; } = 0.15;
    public int GridColumns { get; init; } = 33;
    public int GridRows { get; init; } = 19;
    /// <summary>"FAST" | "ROBUST".</summary>
    public string Mode { get; init; } = "FAST";
}

/// <summary>Outputs aligned together through one camera onto one canvas. Its results live in the calibration ledger.</summary>
public sealed record AlignGroup(string Id, string Name, string CameraId, ImmutableArray<string> OutputIds, string CanvasId,
    ScreenShape Shape, ScreenModel? Screen, AlignTarget Target, AlignOptions Options, string LastRunId) : IEntity;

/// <summary>Texture coordinates at a regular grid of output positions, in File (PFM, under the project folder).</summary>
public sealed record MeasuredWarp(int Columns, int Rows, string File, string Hash, double ResidualPx);

/// <summary>A blend weight and pedestal map at a quarter of the raster, in File.</summary>
public sealed record MeasuredBlend(int Width, int Height, string File, string Hash);

/// <summary>Diagonal colour gains and the transfer γ in linear light (WHITE matching); a full matrix is a later field.</summary>
public sealed record MeasuredColor(double GainR, double GainG, double GainB, double Gamma, double Level);

/// <summary>A camera-measured correction of one output, drawn under the operator's warp: a measured warp grid (align), a small
/// residual lattice and gains (a stack member, facade), a measured blend and colour. One record, whoever measured it.</summary>
public sealed record MeasuredCorrection(MeasuredWarp? Warp, WarpLattice? Lattice, MeasuredBlend? Blend, MeasuredColor? Color,
    double ResidualPx, string ScanId, string StackId, string RunId, long MeasuredUnixMs)
{
    /// <summary>The poses and optics a stack member's lattice was fitted against (null when no stack fitted one, or it was fitted before
    /// they were kept). A lattice whose projectors no longer stand so was fitted to poses that are gone
    /// (<see cref="PlayMap.Core.Stacks.StackPoses"/>).</summary>
    public FittedPoses? FittedTo { get; init; }
}

/// <summary>A stack member against its master as a lattice was fitted to them: the member's pose in the master's own frame (so a move
/// of the whole site leaves it as it is), and each one's optics.</summary>
public sealed record FittedPoses(Pose Relative, Intrinsics Master, LensDistortion MasterLens, Intrinsics Member, LensDistortion MemberLens);

/// <summary>A projector's own processing as read before PlayMap reset it.</summary>
public sealed record NativeSnapshot(long ReadUnixMs, ImmutableSortedDictionary<string, string> Values, ImmutableArray<string> Unsupported,
    bool Neutral);
