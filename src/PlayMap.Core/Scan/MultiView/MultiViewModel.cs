using System.Collections.Immutable;
using PlayMap.Core.Calibration;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Scan.MultiView;

/// <summary>A camera that took views (a phone, a webcam): its prior optics, and whether they are measured
/// (<see cref="Calibrated"/>: held as given).</summary>
public sealed record MultiViewRig(string Id, Intrinsics Prior, LensDistortion Lens, bool Calibrated);

/// <summary>A projector of the scan: prior optics (datasheet throw and lens shift), its lens (always held), the
/// locks a solve honours, and its stage pose. A held projector keeps <see cref="Prior"/> and <see cref="Stage"/>
/// bit for bit and anchors the frame (re-solve only the projector that moved).</summary>
public sealed record MultiViewProjector(string Id, Intrinsics Prior, LensDistortion Lens, SolveLocks Locks)
{
    /// <summary>Where the stage has the projector; required when held, otherwise a scale hint only.</summary>
    public Pose? Stage { get; init; }

    public bool Held { get; init; }
}

/// <summary>One place the camera stood. <see cref="Pairs"/> are its dense camera ↔ projector pairs per projector id,
/// used only to start the solve (shared camera pixels tie two projectors' scales within a view); a projector
/// without pairs starts from the view's track observations.</summary>
public sealed record MultiViewView(string Id, string RigId)
{
    public ImmutableDictionary<string, IReadOnlyList<CameraProjectorPair>> Pairs { get; init; } =
        ImmutableDictionary<string, IReadOnlyList<CameraProjectorPair>>.Empty;
}

/// <summary>Everything a multi-view solve takes.</summary>
public sealed record MultiViewInput(
    ImmutableArray<MultiViewRig> Rigs,
    ImmutableArray<MultiViewProjector> Projectors,
    ImmutableArray<MultiViewView> Views,
    TrackSet Tracks)
{
    /// <summary>The same scan without one view: its pairs and its observations go; tracks no other view saw go.</summary>
    public MultiViewInput WithoutView(string viewId) => this with
    {
        Views = Views.RemoveAll(v => v.Id == viewId),
        Tracks = Tracks.Without(viewId),
    };

    /// <summary>The same scan with these projectors held at their stage poses and priors.</summary>
    public MultiViewInput Holding(params string[] projectorIds) => this with
    {
        Projectors = [.. Projectors.Select(p => p with { Held = projectorIds.Contains(p.Id, StringComparer.Ordinal) })],
    };
}

/// <summary>How the multi-view solve runs. Priors are 1σ, relative to the prior's value unless named otherwise.</summary>
public sealed record MultiViewOptions
{
    public static readonly MultiViewOptions Default = new();

    /// <summary>Huber's δ (camera pixels): a residual beyond it counts linearly, so a few bad observations cannot
    /// pull the solve.</summary>
    public double HuberPx { get; init; } = 1.5;

    public int MaxIterations { get; init; } = 60;

    /// <summary>After the first adjustment, an observation farther than this (camera pixels) is dropped and the
    /// solve runs again.</summary>
    public double OutlierPx { get; init; } = 2.0;

    /// <summary>A camera rig's focal length prior (none when calibrated: held).</summary>
    public double CameraFocalPrior { get; init; } = 0.10;

    /// <summary>The principal point's prior, as a fraction of the raster's width (cameras and unlocked projectors).</summary>
    public double PrincipalPointPrior { get; init; } = 0.02;

    /// <summary>The camera's k1 and k2 prior (absolute).</summary>
    public double DistortionPrior { get; init; } = 0.05;

    /// <summary>A projector's throw ratio prior, unless its focal length is locked.</summary>
    public double ProjectorThrowPrior { get; init; } = 0.05;

    /// <summary>Two views are chained at the start through a shared projector when they share this many tracks.</summary>
    public int MinSharedTracks { get; init; } = 12;

    public ulong Seed { get; init; } = 1;
}

/// <summary>A camera rig as solved: its optics, whether they were held, and the focal length's relative 1σ (NaN when held).</summary>
public sealed record SolvedRig(string Id, Intrinsics Intrinsics, LensDistortion Lens, bool Held, double FocalSigma);

/// <summary>A view as solved: the camera's pose, its residual RMS over its inlier observations (camera pixels), its
/// observations and inliers, and the RMS per projector it saw. A view that disagrees with the rest spreads its error
/// over every view in a joint solve; <see cref="MultiViewCalibration.Influence"/> is what names it.</summary>
public sealed record SolvedView(string Id, string RigId, Pose Pose, double RmsPx, int Observations, int Inliers)
{
    public int Outliers => Observations - Inliers;

    public ImmutableDictionary<string, double> RmsByProjector { get; init; } = ImmutableDictionary<string, double>.Empty;
}

/// <summary>A projector as solved: optics and pose (the stage's own when held), the RMS of the camera residuals of
/// its tracks, its tracks and inliers, and the 1σ of its position along its own axis (the solve's units).</summary>
public sealed record SolvedProjector(string Id, Intrinsics Intrinsics, LensDistortion Lens, Pose Pose, bool Held,
    double RmsPx, int Tracks, int Inliers, double AxialSigma, string Words)
{
    public DeviceView View => new(Intrinsics, Lens, Pose);
}

/// <summary>A track as solved: the 3D point its projector pixel lights, the RMS of its camera residuals and how many
/// views still count it.</summary>
public sealed record SolvedTrack(string ProjectorId, Vec2d Projector, Vec3d Point, double RmsPx, int Views);

/// <summary>A view's weight on the solve: the residual with it and without it (camera pixels).</summary>
public sealed record ViewInfluence(string ViewId, double RmsWithPx, double RmsWithoutPx)
{
    /// <summary>How much the residual falls when the view is dropped (≤ 0 when it does not).</summary>
    public double Gain => RmsWithPx - RmsWithoutPx;
}

/// <summary>A view the solve could not use, and why, in words an operator acts on.</summary>
public sealed record LeftOutView(string ViewId, string Words);

/// <summary>
/// The result of a multi-view solve: every view, projector and rig in one frame. Without held projectors the frame
/// is the first chained view's camera and lengths are in its first baseline (<see cref="Metric"/> false); with two
/// or more held projectors it is the stage's (metric when the stage is); with one held projector it is the stage's
/// up to scale, which then comes from the stage's baselines.
/// </summary>
public sealed record MultiViewResult(
    ImmutableArray<SolvedView> Views,
    ImmutableArray<SolvedProjector> Projectors,
    ImmutableArray<SolvedRig> Rigs,
    ImmutableArray<SolvedTrack> Tracks,
    double RmsPx,
    bool Metric,
    ImmutableArray<LeftOutView> LeftOut,
    int Iterations,
    string Words)
{
    public bool Solved => !Views.IsDefaultOrEmpty && !Projectors.IsDefaultOrEmpty;

    public SolvedView View(string id) => Views.Single(v => v.Id == id);

    public SolvedProjector Projector(string id) => Projectors.Single(p => p.Id == id);

    public SolvedRig Rig(string id) => Rigs.Single(r => r.Id == id);

    /// <summary>The camera of a view as a device (the rig's solved optics at the view's pose).</summary>
    public DeviceView CameraOf(string viewId)
    {
        var v = View(viewId);
        var r = Rig(v.RigId);
        return new DeviceView(r.Intrinsics, r.Lens, v.Pose);
    }

    /// <summary>Every pose and point carried into another frame (registered to a model, scaled to a known
    /// distance); held projectors move with it, so transform only a result that held none.</summary>
    public MultiViewResult Transformed(Similarity3 s, bool metric) => this with
    {
        Views = [.. Views.Select(v => v with { Pose = s.Apply(v.Pose) })],
        Projectors = [.. Projectors.Select(p => p with { Pose = s.Apply(p.Pose), AxialSigma = p.AxialSigma * s.Scale })],
        Tracks = [.. Tracks.Select(t => t with { Point = s.Apply(t.Point) })],
        Metric = metric,
    };
}
