using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;

namespace PlayMap.Engine.Scanning;

/// <summary>
/// The scan simulator standing in for a camera and projectors — demo mode and the tests run the whole pipeline with
/// no hardware. One of <see cref="SimScenes"/>' scenes is the world; its camera is the camera (at the rig's raster,
/// or 320 × 240 for the bare "sim" camera); each scanned projector gets a true device of its own: the first where
/// the scene puts its projector, the others swung around the scene's centre, 20° a step to alternate sides, each at
/// the model projector's raster and lens shift with the scene's throw ratio, aimed so its image centres on the
/// scene. The model's projectors are only priors: the scan finds the truth.
/// </summary>
public sealed class SimulatedRig
{
    /// <summary>The camera id SCAN START takes for the simulator without a camera in the project.</summary>
    public const string CameraId = "sim";

    public const string DefaultScene = "floor-and-box";

    public static readonly IReadOnlyList<string> SceneNames = ["floor-and-box", "wall-and-cylinder", "stepped-facade"];

    private const double StepDegrees = 20;

    private readonly SimScene _scene;
    private readonly Vec3d _target;
    private readonly SimDevice[] _projectors;

    public SimulatedRig(string scene, int cameraWidth, int cameraHeight, IReadOnlyList<Intrinsics> projectorPriors)
    {
        ArgumentNullException.ThrowIfNull(projectorPriors);
        SceneName = SceneNames.Contains(scene, StringComparer.Ordinal) ? scene : DefaultScene;
        var first = projectorPriors.Count > 0 ? projectorPriors[0] : Intrinsics.FromThrowRatio(256, 160, 1.25);
        _scene = Build(SceneName, first.Width, first.Height, cameraWidth, cameraHeight);
        var axis = new Ray(_scene.Projector.Pose.Position, _scene.Projector.Pose.Forward);
        _target = new MeshRaycaster(_scene.Mesh).Cast(axis)?.Point ?? axis.At(3);
        var throwRatio = _scene.Projector.Intrinsics.ThrowRatio;
        _projectors = new SimDevice[projectorPriors.Count];
        for (var j = 0; j < projectorPriors.Count; j++)
        {
            var prior = projectorPriors[j];
            var f = throwRatio * prior.Width;
            var k = new Intrinsics(f, f, prior.Cx, prior.Cy, prior.Width, prior.Height);
            var swing = j == 0 ? 0 : (j % 2 == 1 ? -1 : 1) * StepDegrees * ((j + 1) / 2) * Math.PI / 180;
            var eye = _target + Mat3d.FromRotationVector(Vec3d.UnitY * swing) * (_scene.Projector.Pose.Position - _target);
            _projectors[j] = new SimDevice(k, LensDistortion.None, Aim(eye, _target, k));
        }
    }

    public string SceneName { get; }

    /// <summary>The true camera.</summary>
    public SimDevice Camera => _scene.Camera;

    public int CameraWidth => _scene.Camera.Intrinsics.Width;

    public int CameraHeight => _scene.Camera.Intrinsics.Height;

    public SimDevice Projector(int index) => _projectors[index];

    /// <summary>The scene name a camera rig gives (its source), or the default.</summary>
    public static string SceneOf(CameraRig? rig) =>
        rig is { SourceKind: CameraSourceKind.Simulated } && SceneNames.Contains(rig.Source.Trim().ToLowerInvariant(), StringComparer.Ordinal)
            ? rig.Source.Trim().ToLowerInvariant()
            : DefaultScene;

    /// <summary>A simulator for projector <paramref name="index"/> showing <paramref name="plan"/> (at that
    /// projector's raster).</summary>
    public ScanSimulator Simulator(int index, ScanPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var scene = _scene with { Projector = _projectors[index] };
        return new ScanSimulator(new SimGeometry(scene), plan, new SimOptions { Seed = (ulong)index + 1 });
    }

    /// <summary>The distance between the surfaces the true camera sees at two of its pixels, or null where it sees
    /// nothing — the one measured distance a real scan asks the operator for.</summary>
    public Func<Vec2d, Vec2d, double?> KnownDistance(SimTruth truth)
    {
        ArgumentNullException.ThrowIfNull(truth);
        return (a, b) =>
        {
            if (Seen(truth, a) is not { } pa || Seen(truth, b) is not { } pb)
                return null;
            return Vec3d.Distance(pa, pb);
        };
    }

    private static Vec3d? Seen(SimTruth truth, Vec2d pixel)
    {
        var x = (int)Math.Floor(pixel.X);
        var y = (int)Math.Floor(pixel.Y);
        if (x < 0 || y < 0 || x >= truth.Width || y >= truth.Height)
            return null;
        var p = truth.Points[y * truth.Width + x];
        return PointCloud.IsValid(p) ? p : null;
    }

    /// <summary>A pose at <paramref name="eye"/> whose image centre (not its optical axis: the lens may be shifted)
    /// looks at <paramref name="target"/>, upright.</summary>
    private static Pose Aim(Vec3d eye, Vec3d target, Intrinsics k)
    {
        var look = Pose.LookAt(eye, target, Vec3d.UnitY);
        var r0 = look.CameraToWorld;
        var centre = r0 * new Vec3d((k.Width / 2.0 - k.Cx) / k.Fx, (k.Height / 2.0 - k.Cy) / k.Fy, 1).Normalized();
        var want = (target - eye).Normalized();
        var axis = Vec3d.Cross(centre, want);
        var angle = Math.Atan2(axis.Length, Vec3d.Dot(centre, want));
        if (axis.Length < 1e-12)
            return look;
        var turn = Mat3d.FromRotationVector(axis.Normalized() * angle);
        return new Pose(eye, (turn * r0).NearestRotation().ToRotationVector());
    }

    private static SimScene Build(string name, int pw, int ph, int cw, int ch) => name switch
    {
        "wall-and-cylinder" => SimScenes.WallAndCylinder(pw, ph, cw, ch),
        "stepped-facade" => SimScenes.SteppedFacade(pw, ph, cw, ch),
        _ => SimScenes.FloorAndBox(pw, ph, cw, ch),
    };
}
