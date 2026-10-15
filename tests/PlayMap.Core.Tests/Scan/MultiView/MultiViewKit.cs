using System.Collections.Immutable;
using PlayMap.Core.Calibration;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using PlayMap.Core.Scan.MultiView;

namespace PlayMap.Core.Tests.Scan.MultiView;

/// <summary>The multi-view scan the tests share: the wall-and-cylinder scene lit by two projectors and seen by a phone
/// from three places, each view of each projector simulated and decoded once; the inputs a solve takes, with the
/// phone's focal length prior 8 % off and the projectors' throw priors 5 % off; and the measures the tests assert on.</summary>
internal static class MultiViewKit
{
    /// <summary>The phone's focal length prior, relative to the truth.</summary>
    public const double PhoneFocalPriorError = 0.08;

    /// <summary>The projectors' throw-ratio priors, relative to the truth (one long, one short).</summary>
    public static readonly ImmutableDictionary<string, double> ThrowPriorError =
        ImmutableDictionary<string, double>.Empty.Add("pj1", 0.05).Add("pj2", -0.05);

    public const string Rig = "phone";

    private static readonly Lazy<MultiViewSim> SimLazy = new(() => new MultiViewSim(MultiViewScenes.WallAndCylinder()));
    private static readonly Lazy<IReadOnlyList<MultiViewCapture>> CapturesLazy = new(() => Sim.CaptureAll());
    private static readonly Lazy<IReadOnlyList<ViewDecode>> DecodesLazy = new(() =>
        [.. Captures.Select(c => new ViewDecode(c.ViewId, c.ProjectorId, new InverseCorrespondence(c.Map)))]);
    private static readonly Lazy<MultiViewInput> InputLazy = new(() => MakeInput(TrackSet.Build(Decodes)));
    private static readonly Lazy<MultiViewResult> SolvedLazy = new(() => MultiViewCalibration.Solve(Input));
    private static readonly Lazy<Similarity3> ToWorldLazy = new(() => Registration(Solved));
    private static readonly Lazy<IReadOnlyList<ProjectorDepthMap>> DenseLazy = new(() =>
        [.. Solved.Projectors.Select(p => ProjectorDepth.Build(p, DepthViews(Solved, p.Id)))]);
    private static readonly Lazy<IReadOnlyList<ProjectorDepthMap>> WorldDenseLazy = new(() =>
    {
        var world = Solved.Transformed(ToWorld, metric: true);
        return [.. world.Projectors.Select(p => ProjectorDepth.Build(p, DepthViews(world, p.Id)))];
    });

    public static MultiViewSim Sim => SimLazy.Value;

    public static MultiViewScene Scene => Sim.Scene;

    public static IReadOnlyList<MultiViewCapture> Captures => CapturesLazy.Value;

    public static IReadOnlyList<ViewDecode> Decodes => DecodesLazy.Value;

    /// <summary>The scan as a solve takes it: the phone uncalibrated, nothing held.</summary>
    public static MultiViewInput Input => InputLazy.Value;

    /// <summary>The scan solved once.</summary>
    public static MultiViewResult Solved => SolvedLazy.Value;

    /// <summary>The shared solve's frame carried onto the world (<see cref="Registration"/>).</summary>
    public static Similarity3 ToWorld => ToWorldLazy.Value;

    /// <summary>Dense depth of every projector of the shared solve, from all three views.</summary>
    public static IReadOnlyList<ProjectorDepthMap> Dense => DenseLazy.Value;

    /// <summary>Dense depth of every projector from the shared solve carried onto the world (metres).</summary>
    public static IReadOnlyList<ProjectorDepthMap> WorldDense => WorldDenseLazy.Value;

    /// <summary>The views of one projector as dense depth takes them from a solve.</summary>
    public static List<DepthView> DepthViews(MultiViewResult result, string projectorId) =>
        [.. Decodes.Where(d => d.ProjectorId == projectorId && result.Views.Any(v => v.Id == d.ViewId))
            .Select(d => new DepthView(d.ViewId, result.CameraOf(d.ViewId), d.Inverse))];

    /// <summary>The phone as it truly stood at a view.</summary>
    public static DeviceView TruthCamera(string view) => new(Scene.Phone, Scene.PhoneLens, Scene.View(view).Pose);

    public static MultiViewCapture Capture(string view, string projector) => Captures.Single(c => c.ViewId == view && c.ProjectorId == projector);

    public static ViewDecode Decode(string view, string projector) => Decodes.Single(c => c.ViewId == view && c.ProjectorId == projector);

    /// <summary>The solve input over a track set: priors off as stated, the dense pairs each view decoded (every
    /// second pixel) to start from, and each projector's stage pose at its truth (used only when held).</summary>
    public static MultiViewInput MakeInput(TrackSet tracks)
    {
        var phone = Scene.Phone;
        var prior = phone with { Fx = phone.Fx * (1 + PhoneFocalPriorError), Fy = phone.Fy * (1 + PhoneFocalPriorError) };
        var projectors = Scene.Projectors.Select(p =>
        {
            var k = p.Device.Intrinsics;
            var e = 1 + ThrowPriorError[p.Id];
            return new MultiViewProjector(p.Id, k with { Fx = k.Fx * e, Fy = k.Fy * e }, LensDistortion.None, SolveLocks.None) { Stage = p.Device.Pose };
        }).ToImmutableArray();
        var views = Scene.Views.Select(v => new MultiViewView(v.Id, Rig)
        {
            Pairs = Captures.Where(c => c.ViewId == v.Id).ToImmutableDictionary(
                c => c.ProjectorId, c => (IReadOnlyList<CameraProjectorPair>)c.Map.Pairs(minConfidence: 0.3f, stride: 2)),
        }).ToImmutableArray();
        return new MultiViewInput([new MultiViewRig(Rig, prior, LensDistortion.None, false)], projectors, views, tracks);
    }

    /// <summary>The similarity that carries a solve's frame onto the world: the best fit of the solved track points
    /// to the surface points their projector pixels truly light (as registering a scan to a surveyed model does).
    /// Every projector is then compared with its truth in the world.</summary>
    public static Similarity3 Registration(MultiViewResult result)
    {
        var source = new List<Vec3d>();
        var target = new List<Vec3d>();
        foreach (var t in result.Tracks)
        {
            if (t.Views == 0 || Sim.TruthPoint(t.ProjectorId, t.Projector) is not { } truth)
                continue;
            source.Add(t.Point);
            target.Add(truth);
        }
        var fit = Umeyama.Fit(source, target);
        // Once more without the tracks the fit leaves farther than 3 cm (misdecodes at depth edges).
        var keep = Enumerable.Range(0, source.Count).Where(i => (fit.Apply(source[i]) - target[i]).Length < 0.03).ToList();
        return Umeyama.Fit([.. keep.Select(i => source[i])], [.. keep.Select(i => target[i])]);
    }

    /// <summary>The angle between two orientations, in degrees.</summary>
    public static double AngleDeg(Pose a, Pose b) =>
        (a.CameraToWorld.Transposed() * b.CameraToWorld).ToRotationVector().Length * 180 / Math.PI;

    /// <summary>The world-space errors of a solved projector against the truth: centre (m), orientation (°), throw (relative).</summary>
    public static (double PositionM, double AngleDeg, double Throw) Errors(SolvedProjector solved, Similarity3 toWorld)
    {
        var truth = Scene.Projector(solved.Id).Device;
        var pose = toWorld.Apply(solved.Pose);
        return ((pose.Position - truth.Pose.Position).Length, AngleDeg(pose, truth.Pose),
            Math.Abs(solved.Intrinsics.Fx / truth.Intrinsics.Fx - 1));
    }
}
