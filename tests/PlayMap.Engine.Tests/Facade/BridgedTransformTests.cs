using PlayMap.Core.Calibration;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Engine.Scanning;

namespace PlayMap.Engine.Tests.Facade;

/// <summary>A scan bridges into the stage as placed: a surface placed with a 3 m offset gives the bridged projector the same pose as the
/// same mesh baked at the offset (≤ 1e-6 m), where the mesh file's raw coordinates would put it 3 m off.</summary>
public sealed class BridgedTransformTests
{
    private static MeshData Box(Vec3d offset)
    {
        // A wall with a step: two planes 1.5 m apart, so the bridge has depth to solve on.
        var p = new List<float>();
        var idx = new List<uint>();
        void Quad(Vec3d a, Vec3d b, Vec3d c, Vec3d d)
        {
            var at = (uint)(p.Count / 3);
            foreach (var v in new[] { a, b, c, d })
                p.AddRange([(float)(v.X + offset.X), (float)(v.Y + offset.Y), (float)(v.Z + offset.Z)]);
            idx.AddRange([at, at + 1, at + 2, at, at + 2, at + 3]);
        }
        Quad(new(-6, 0, 0), new(0, 0, 0), new(0, 6, 0), new(-6, 6, 0));
        Quad(new(0, 0, 1.5), new(6, 0, 1.5), new(6, 6, 1.5), new(0, 6, 1.5));
        Quad(new(0, 0, 0), new(0, 0, 1.5), new(0, 6, 1.5), new(0, 6, 0));
        return new MeshData([.. p], [], [], [.. idx]);
    }

    private static Pose Bridge(MeshRaycaster model, Vec3d offset)
    {
        var camK = Intrinsics.FromFieldOfView(1280, 720, 70);
        var camera = new DeviceView(camK, LensDistortion.None, Pose.LookAt(new Vec3d(-2, 3, 14) + offset, new Vec3d(0, 3, 0) + offset, Vec3d.UnitY));
        var projK = Intrinsics.FromThrowRatio(1920, 1080, 1.4);
        var projector = new DeviceView(projK, LensDistortion.None, Pose.LookAt(new Vec3d(3, 3.5, 13) + offset, new Vec3d(0, 3, 0.5) + offset, Vec3d.UnitY));
        var truth = new MeshRaycaster(Box(offset));
        var cam = new List<Vec2d>();
        var proj = new List<Vec2d>();
        for (var y = 20; y < 720; y += 20)
            for (var x = 20; x < 1280; x += 20)
            {
                var px = new Vec2d(x, y);
                if (truth.Cast(camera.Ray(px)) is { } hit && projector.Project(hit.Point) is { } pp && pp.X is > 0 and < 1920 && pp.Y is > 0 and < 1080)
                {
                    cam.Add(px);
                    proj.Add(pp);
                }
            }
        var prior = Intrinsics.FromThrowRatio(1920, 1080, 1.45);
        return BridgedRegistration.SolveProjector(model, camera, cam, proj, prior, LensDistortion.None).Projector.Pose;
    }

    [Fact]
    public void ASurfacePlacedThreeMetresAwayBridgesAsTheMeshBakedThere()
    {
        var folder = Path.Combine(Path.GetTempPath(), "playmap-bridge-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(folder, "meshes"));
        try
        {
            var offset = new Vec3d(3, 0, 0);
            using (var s = File.Create(Path.Combine(folder, "meshes", "raw.ply")))
                MeshIO.WritePly(s, Box(Vec3d.Zero));
            using (var s = File.Create(Path.Combine(folder, "meshes", "baked.ply")))
                MeshIO.WritePly(s, Box(offset));
            static Project With(string meshPath, Transform3 placed)
            {
                var p = Project.CreateDefault("Bridge");
                return p with
                {
                    Stage = p.Stage with
                    {
                        Meshes = [new MeshAsset("ms_bridge1", "Wall", MeshOrigin.Imported, meshPath, "", "", ParamSet.Empty, 12, 6)],
                        Surfaces = [new Surface("sf_bridge1", "Wall", "ms_bridge1", placed, [], SurfaceContent.Empty, 1, [], true, false, "#FFFFFF", [])],
                    },
                };
            }
            var placed = BridgeModel.Of(With("meshes/raw.ply", Transform3.Identity with { Position = offset }), folder)!;
            var baked = BridgeModel.Of(With("meshes/baked.ply", Transform3.Identity), folder)!;
            var raw = new MeshRaycaster(MeshIO.Load(Path.Combine(folder, "meshes", "raw.ply")).Mesh!);

            var a = Bridge(placed, offset);
            var b = Bridge(baked, offset);
            Assert.True((a.Position - b.Position).Length <= 1e-6, $"{a.Position} vs {b.Position}");
            var ra = Mat3d.FromRotationVector(a.Rotation).ToArray();
            var rb = Mat3d.FromRotationVector(b.Rotation).ToArray();
            Assert.True(ra.Zip(rb).Max(x => Math.Abs(x.First - x.Second)) <= 1e-6);
            // What the raw file would have said: the projector solved against the wall where it is not.
            var wrong = Bridge(raw, offset);
            Assert.True((wrong.Position - b.Position).Length > 1, $"{wrong.Position} vs {b.Position}");
            // No surface on an imported mesh: nothing to bridge into.
            Assert.Null(BridgeModel.Of(Project.CreateDefault("Empty"), folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
