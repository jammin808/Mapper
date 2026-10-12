using PlayMap.Core.Calibration;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Calibration;

/// <summary>Bridged registration finds a projector in a model's frame from a camera's decoded pixels with no
/// hand-placed pins (the camera's pose known, or found from a few picked points first); the lens pre-warp map
/// sends every output pixel to the render pixel its light really shows.</summary>
public class BridgeAndLensTests
{
    private static readonly MeshRaycaster Room = new(Synthetic.Room());

    [Fact]
    public void BridgedRegistrationRecoversAProjectorPoseFromAMeshACameraPoseAndDecodedPairs()
    {
        var camK = Synthetic.Camera(62);
        var projK = Synthetic.Projector(-0.5);
        var (cam, proj, _) = Synthetic.RoomPairs(Room, camK, Synthetic.CameraPose, projK, Synthetic.ProjectorPose, 6000, 21, 0.3, 0.02);
        var camera = new DeviceView(camK, LensDistortion.None, Synthetic.CameraPose);
        var prior = Intrinsics.FromThrowRatio(1920, 1080, 1.38, 0, -0.5);
        var r = BridgedRegistration.SolveProjector(Room, camera, cam, proj, prior, LensDistortion.None,
            BridgeOptions.Default with { Projector = BridgeOptions.Default.Projector with { Locks = SolveLocks.Shift | SolveLocks.Distortion } });
        var s = r.Projector;
        Assert.True(r.RaysHit > 5500, r.Words);
        Assert.True(s.RmsPx < 0.5, r.Words);
        Assert.True(Math.Abs(s.Intrinsics.Fx / projK.Fx - 1) < 0.005, r.Words);
        Assert.True(Vec3d.Distance(s.Pose.Position, Synthetic.ProjectorPose.Position) < 0.01, $"{s.Pose.Position}");
        Assert.True(Synthetic.AngleDegrees(Synthetic.ProjectorPose, s.Pose) < 0.1, r.Words);
        Assert.Contains("camera pixels landed on the model", r.Words);
    }

    [Fact]
    public void WithoutTheCameraPoseAFewPickedPointsFindItFirst()
    {
        var camK = Synthetic.Camera(62);
        var projK = Synthetic.Projector(-0.5);
        var camPose = Synthetic.CameraPose;
        Vec3d[] features =
        [
            new(-0.5, 0.6, 4.3), new(0.4, 0.6, 4.3), new(0.4, 0.6, 5.0), new(-0.5, 0.6, 5.0), new(0.4, 0, 4.3), new(-0.5, 0, 4.3),
            new(-1.4, 0, 6), new(-1.4, 3, 6), new(2.5, 0, 6), new(2.5, 2.4, 6), new(-1.4, 2.2, 3), new(1.8, 0, 2.5), new(-1.4, 0.4, 1.5),
        ];
        var rng = new Pcg32(4);
        var pins = new List<Correspondence>();
        foreach (var f in features)
        {
            if (CameraModel.Project(f, camK, LensDistortion.None, camPose) is not { } px || !CameraModel.InRaster(px, camK))
                continue;
            var to = f - camPose.Position;
            if (Room.Cast(new Ray(camPose.Position, to.Normalized()), to.Length - 1e-3) is not null)
                continue;
            pins.Add(new Correspondence(f, px + Synthetic.Noise(ref rng, 0.5)));
        }
        Assert.True(pins.Count >= 8, $"{pins.Count} visible features");
        var (cam, proj, _) = Synthetic.RoomPairs(Room, camK, camPose, projK, Synthetic.ProjectorPose, 4000, 22, 0.3);
        var options = BridgeOptions.Default with
        {
            Camera = new PoseSolveOptions { Locks = SolveLocks.Focal | SolveLocks.Shift | SolveLocks.Distortion },
            Projector = BridgeOptions.Default.Projector with { Locks = SolveLocks.Shift | SolveLocks.Distortion },
        };
        var r = BridgedRegistration.SolveCameraThenProjector(Room, camK, LensDistortion.None, pins, cam, proj,
            Intrinsics.FromThrowRatio(1920, 1080, 1.4, 0, -0.5), LensDistortion.None, options);
        Assert.NotNull(r.Camera);
        Assert.True(r.Camera.RmsPx < 1, r.Words);
        Assert.True(Vec3d.Distance(r.Projector.Pose.Position, Synthetic.ProjectorPose.Position) < 0.05, r.Words);
        Assert.True(Synthetic.AngleDegrees(Synthetic.ProjectorPose, r.Projector.Pose) < 0.3, r.Words);
        Assert.StartsWith("camera: ", r.Words);

        // Too few camera pins: the bridge says the camera was not found.
        var none = BridgedRegistration.SolveCameraThenProjector(Room, camK, LensDistortion.None, pins.Take(2).ToList(), cam, proj,
            Intrinsics.FromThrowRatio(1920, 1080, 1.4, 0, -0.5), LensDistortion.None, options);
        Assert.False(none.Projector.IsGood);
        Assert.Contains("camera's pose is not found", none.Words);
    }

    [Fact]
    public void ThePrewarpMapSendsEachOutputPixelWhereItsLightReallyGoes()
    {
        var k = Synthetic.Projector(-0.5);
        var lens = new LensDistortion(-0.12, 0.03, 0.001, -0.0005, 0);
        var map = LensPrewarp.Build(k, lens, 96, 54);
        Assert.Equal(96 * 54 * 2, map.Uv.Length);
        for (var y = 0; y < map.Height; y += 5)
        {
            for (var x = 0; x < map.Width; x += 7)
            {
                var src = map.SourcePixel(x, y);
                Assert.NotNull(src);
                // The render pixel's direction, through the lens, lands on this output pixel.
                var n = new Vec2d((src.Value.X - k.Cx) / k.Fx, (src.Value.Y - k.Cy) / k.Fy);
                var d = CameraModel.Distort(n, lens);
                var landed = new Vec2d(k.Fx * d.X + k.Cx, k.Fy * d.Y + k.Cy);
                Assert.True(Vec2d.Distance(landed, map.TexelCentre(x, y)) < 1e-3, $"{landed} vs {map.TexelCentre(x, y)}");
            }
        }

        // Barrel distortion: the edge pixels look just past the render (black there), but they do have a source.
        Assert.True(map.Uv[0] < 0);

        // No distortion: the identity.
        Assert.Equal(new Vec2d(321.5, 77.25), LensPrewarp.SourceFor(new Vec2d(321.5, 77.25), k, LensDistortion.None));

        // A lens folding back on itself has no source beyond its fold: those texels sample the border.
        var folded = new LensDistortion(-1.2, 0, 0, 0, 0);
        Assert.Null(LensPrewarp.SourceFor(new Vec2d(1919, 1079), k, folded));
        Assert.NotNull(LensPrewarp.SourceFor(new Vec2d(k.Cx + 100, k.Cy + 100), k, folded));
        var foldedMap = LensPrewarp.Build(k, folded, 32, 18);
        Assert.Contains(PrewarpMap.NoSource, foldedMap.Uv);
        Assert.Null(foldedMap.SourcePixel(31, 17));
    }
}
