using System.Collections.Immutable;
using System.Text.Json;
using PlayMap.Core.Calibration;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using PlayMap.Core.Scan.MultiView;
using PlayMap.Engine.Scanning;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>
/// Multi-view scans through the engine's own verbs with no hardware: the simulator's multi-view scene (wall and cylinder, two
/// projectors, a phone at three places — <see cref="SimulatedRig.ViewsScene"/>) as a project with the phone's focal prior 8 % off
/// and the projectors' throw priors 5 % off (Core's multi-view bar), and the measures the tests assert on.
/// </summary>
internal static class MultiViewScanKit
{
    public const string Pj1 = "pj00000001";
    public const string Pj2 = "pj00000002";
    public const string Camera = "cm0000000a";

    public const int ProjectorWidth = 512;
    public const int ProjectorHeight = 320;
    public const int CameraWidth = 480;
    public const int CameraHeight = 360;

    public static readonly MultiViewScene Scene = MultiViewScenes.WallAndCylinder(ProjectorWidth, ProjectorHeight, CameraWidth, CameraHeight);

    private static readonly Lazy<MultiViewSim> SimLazy = new(() => new MultiViewSim(Scene));

    public static MultiViewSim Sim => SimLazy.Value;

    /// <summary>The model's id for the scene's projector j.</summary>
    public static string ModelId(string sceneId) => sceneId == "pj1" ? Pj1 : Pj2;

    public static string SceneId(string modelId) => modelId == Pj1 ? "pj1" : "pj2";

    /// <summary>Two projectors priored 5 % off in throw (one long, one short) and hung roughly where they are; the phone as an
    /// uncalibrated simulated camera, its focal length 8 % off, standing where the scene's first view has it.</summary>
    public static Project Project()
    {
        var p = Core.Model.Project.CreateDefault("Multi-view scan");
        Projector Make(string id, string name, string sceneId, double error)
        {
            var truth = Scene.Projector(sceneId).Device;
            var k = truth.Intrinsics;
            var eye = truth.Pose.Position + new Vec3d(0.05, -0.04, 0.06);
            return new Projector(id, name, k with { Fx = k.Fx * (1 + error), Fy = k.Fy * (1 + error) }, LensDistortion.None,
                Pose.LookAt(eye, new Vec3d(0, 0.9, 0), Vec3d.UnitY), 0, "#FF8800", false, [], SolveLocks.None, 0);
        }
        var phone = Scene.Phone;
        var rig = new CameraRig(Camera, "Phone", CameraSourceKind.Simulated, SimulatedRig.ViewsScene, phone with { Fx = phone.Fx * 1.08, Fy = phone.Fy * 1.08 },
            LensDistortion.None, Scene.Views[0].Pose, false);
        return p.WithStage(s => s with
        {
            Projectors = [Make(Pj1, "Left", "pj1", 0.05), Make(Pj2, "Right", "pj2", -0.05)],
            Cameras = [rig],
        });
    }

    /// <summary>Starts a scan line and waits until the scan it started has ended; answers the scan's id.</summary>
    public static async Task<string> ScanAsync(OpsRig rig, string line, int timeoutMs = 300_000)
    {
        var started = await rig.Ok(line);
        var id = JsonDocument.Parse(started).RootElement.GetProperty("scan").GetString()!;
        Assert.True(await OpsRig.Eventually(() => rig.Scanner.View is { IsRunning: false } v && v.Id == id && v.Stage is "done" or "failed" or "cancelled", timeoutMs),
            "the scan did not end");
        var view = rig.Scanner.View!;
        Assert.True(view.State == "done", $"{line}: {view.State} — {view.Message}");
        return id;
    }

    /// <summary>The similarity that carries a solve onto the world: its track points fitted to the surface points their projector
    /// pixels truly light, once more without those 3 cm out (Core's registration, as registering a scan to a surveyed model does).</summary>
    public static Similarity3 Registration(MultiViewResult result)
    {
        var source = new List<Vec3d>();
        var target = new List<Vec3d>();
        foreach (var t in result.Tracks)
        {
            if (t.Views == 0 || Sim.TruthPoint(SceneId(t.ProjectorId), t.Projector) is not { } truth)
                continue;
            source.Add(t.Point);
            target.Add(truth);
        }
        var fit = Umeyama.Fit(source, target);
        var keep = Enumerable.Range(0, source.Count).Where(i => (fit.Apply(source[i]) - target[i]).Length < 0.03).ToList();
        return Umeyama.Fit([.. keep.Select(i => source[i])], [.. keep.Select(i => target[i])]);
    }

    public static double AngleDeg(Pose a, Pose b) => (a.CameraToWorld.Transposed() * b.CameraToWorld).ToRotationVector().Length * 180 / Math.PI;

    /// <summary>A solved projector's errors against the truth in the world: centre (m), orientation (°), throw (relative).</summary>
    public static (double PositionM, double AngleDeg, double Throw) Errors(SolvedProjector solved, Similarity3 toWorld)
    {
        var truth = Scene.Projector(SceneId(solved.Id)).Device;
        var pose = toWorld.Apply(solved.Pose);
        return ((pose.Position - truth.Pose.Position).Length, AngleDeg(pose, truth.Pose), Math.Abs(solved.Intrinsics.Fx / truth.Intrinsics.Fx - 1));
    }

    /// <summary>The phone knocked about its own vertical axis by the angle that moves its picture <paramref name="pixels"/> across:
    /// the camera moved between two projectors' runs.</summary>
    public static SimDevice Knocked(SimDevice phone, double pixels) =>
        ScanSimulator.Bump(phone, new Vec3d(0, Math.Atan(pixels / phone.Intrinsics.Fx), 0), Vec3d.Zero);

    public static string[] Ids(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];
}
