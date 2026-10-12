using System.Collections.Immutable;
using PlayMap.Core.Content;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests;

/// <summary>The document: its file form, field edits by path, the single writer with undo, the generator
/// catalogue, and the camera model the renderer and the solvers share.</summary>
public class ModelTests
{
    [Fact]
    public void AFreshProjectPutsTheTestCardOnOneOutput()
    {
        var p = Project.CreateDefault("Show");
        var output = Assert.Single(p.Outputs);
        Assert.Equal(SourceKind.Canvas, output.Source.Kind);
        var layer = Assert.Single(p.Canvases[0].Layers);
        Assert.Equal("pattern.testcard", p.FindGenerator(layer.Source.Id)!.Kind);
    }

    [Fact]
    public void TheProjectRoundTripsThroughItsFile()
    {
        var p = Project.CreateDefault("Show") with
        {
            Stage = Stage.Empty with
            {
                Projectors =
                [
                    new Projector("pj1", "Left", Intrinsics.FromThrowRatio(1920, 1080, 1.5, 0, 0.5), LensDistortion.None,
                        Pose.LookAt(new Vec3d(0, 1, -5), Vec3d.Zero, Vec3d.UnitY), 12000, "#FF8800", false,
                        [new CalibrationPin("pin1", new Vec3d(1, 2, 3), new Vec2d(100, 200), true)], SolveLocks.Shift, 0.4),
                ],
            },
        };
        var json = ProjectJson.Serialize(p);
        Assert.Contains("\"mode\": \"canvas\"", json, StringComparison.Ordinal);
        var back = ProjectJson.Deserialize(json);
        Assert.Equal(p.Name, back.Name);
        Assert.Equal(p.Stage.Projectors[0].Pins[0].World, back.Stage.Projectors[0].Pins[0].World);
        Assert.Equal(SolveLocks.Shift, back.Stage.Projectors[0].Locks);
        Assert.Equal(ProjectJson.Serialize(p), ProjectJson.Serialize(back));
    }

    [Fact]
    public void AFileWithAListMissingReadsItAsEmpty()
    {
        var json = ProjectJson.Serialize(Project.CreateDefault()).Replace("\"media\": [],", "", StringComparison.Ordinal);
        var p = ProjectJson.Deserialize(json);
        Assert.False(p.Media.IsDefault);
        Assert.Empty(p.Media);
    }

    [Fact]
    public void AFieldIsSetByItsPath()
    {
        var output = Project.CreateDefault().Outputs[0];
        var renamed = ProjectJson.SetPath(output, "name", "Projector A");
        Assert.Equal("Projector A", renamed.Name);
        var pinned = ProjectJson.SetPath(output, "warp.pin.topLeft", "{\"x\":0.01,\"y\":-0.02}");
        Assert.Equal(new Vec2d(0.01, -0.02), pinned.Warp.Pin.TopLeft);
        var moded = ProjectJson.SetPath(output, "mode", "\"projector\"");
        Assert.Equal(OutputMode.Projector, moded.Mode);
        Assert.Throws<EditRefusedException>(() => ProjectJson.SetPath(output, "warp.nonsense", "1"));
        Assert.Throws<EditRefusedException>(() => ProjectJson.SetPath(output, "width", "\"wide\""));
    }

    [Fact]
    public void TheStoreUndoesRedoesAndCoalescesADrag()
    {
        var now = 0L;
        var store = new ProjectStore(Project.CreateDefault(), () => now);
        var r1 = store.Apply("rename", p => p with { Name = "A" });
        Assert.True(r1.Changed);
        for (var i = 1; i <= 5; i++)
        {
            now += 100;
            var x = i * 0.01;
            store.Apply("drag pin", p => p with { Outputs = p.Outputs.Update("op_1", o => o with { Warp = o.Warp with { Pin = o.Warp.Pin with { TopLeft = new Vec2d(x, 0) } } }) }, "pin op_1 tl");
        }
        Assert.Equal(0.05, store.Project.Outputs[0].Warp.Pin.TopLeft.X, 9);
        Assert.Equal("drag pin", store.UndoLabel);
        store.Undo();
        Assert.Equal(0, store.Project.Outputs[0].Warp.Pin.TopLeft.X);
        Assert.Equal("A", store.Project.Name);
        store.Undo();
        Assert.Equal("Untitled", store.Project.Name);
        Assert.False(store.Undo().Changed);
        store.Redo();
        Assert.Equal("A", store.Project.Name);
        var none = store.Apply("rename", p => p with { Name = "A" });
        Assert.False(none.Changed);
        var refused = store.Apply("remove", _ => throw new EditRefusedException("no surface sf9"));
        Assert.False(refused.Changed);
        Assert.Equal("no surface sf9", refused.Effect);
    }

    [Fact]
    public void EveryGeneratorDefaultIsValidForItsOwnSpec()
    {
        foreach (var kind in GeneratorCatalog.All)
        {
            foreach (var p in kind.Params)
            {
                Assert.True(p.TryNormalise(p.Default, out var n, out var why), $"{kind.Id}.{p.Name}: {why}");
                Assert.Equal(p.Default, n);
            }
        }
        Assert.Equal("pattern.testcard", GeneratorCatalog.FindLoose("testcard")!.Id);
        var grid = GeneratorCatalog.Find("pattern.grid")!;
        Assert.True(grid.Param("cell")!.TryNormalise("99999", out var clamped, out _));
        Assert.Equal("4096", clamped);
        Assert.False(grid.Param("color")!.TryNormalise("blue", out _, out _));
    }

    [Fact]
    public void ACameraProjectsAndUnprojectsThroughItsLens()
    {
        var k = Intrinsics.FromThrowRatio(1920, 1080, 1.2, 0, 0.4);
        var lens = new LensDistortion(-0.08, 0.02, 0.001, -0.0005, 0);
        var pose = Pose.LookAt(new Vec3d(0.5, 1.5, -6), new Vec3d(0, 1, 0), Vec3d.UnitY);
        var world = new Vec3d(0.7, 1.4, 0.3);
        var px = CameraModel.Project(world, k, lens, pose)!.Value;
        var ray = CameraModel.PixelRay(px, k, lens, pose);
        var toPoint = (world - pose.Position).Normalized();
        Assert.Equal(1, Vec3d.Dot(ray.Direction, toPoint), 9);
        Assert.Null(CameraModel.Project(new Vec3d(0, 1, -10), k, lens, pose));
    }

    [Fact]
    public void LookAtPutsTheTargetOnTheOpticalAxisAndUpUp()
    {
        var pose = Pose.LookAt(new Vec3d(0, 0, -5), Vec3d.Zero, Vec3d.UnitY);
        var k = Intrinsics.FromFieldOfView(1000, 1000, 60);
        var c = CameraModel.Project(Vec3d.Zero, k, LensDistortion.None, pose)!.Value;
        Assert.Equal(500, c.X, 9);
        Assert.Equal(500, c.Y, 9);
        var up = CameraModel.Project(new Vec3d(0, 1, 0), k, LensDistortion.None, pose)!.Value;
        Assert.True(up.Y < 500);
        var right = CameraModel.Project(new Vec3d(1, 0, 0), k, LensDistortion.None, pose)!.Value;
        // A right-handed world with +y up, seen looking along +z: world +x is on the image's left.
        Assert.True(right.X < 500);
    }

    [Fact]
    public void TheGpuMatricesLandEveryPointOnTheSamePixel()
    {
        var k = Intrinsics.FromThrowRatio(1920, 1080, 1.4, 0.05, 0.3);
        var pose = Pose.LookAt(new Vec3d(1, 2, -4), new Vec3d(0, 0.5, 0), Vec3d.UnitY);
        var (view, proj) = CameraModel.GlMatrices(k, pose, 0.1, 100);
        foreach (var w in new[] { new Vec3d(0, 0, 0), new Vec3d(0.5, 1, 0.2), new Vec3d(-0.3, 0.2, 1) })
        {
            var px = CameraModel.Project(w, k, LensDistortion.None, pose)!.Value;
            var clip = System.Numerics.Vector4.Transform(System.Numerics.Vector4.Transform(new System.Numerics.Vector4((float)w.X, (float)w.Y, (float)w.Z, 1), view), proj);
            var u = (clip.X / clip.W + 1) / 2 * 1920;
            var v = (1 - clip.Y / clip.W) / 2 * 1080;
            Assert.Equal(px.X, u, 1);
            Assert.Equal(px.Y, v, 1);
            Assert.InRange(clip.Z / clip.W, -1, 1);
        }
    }

    [Fact]
    public void ARayFindsTheNearestTriangleOfAMesh()
    {
        // Two parallel unit quads at z = 1 and z = 2.
        float[] pos = [-1, -1, 1, 1, -1, 1, 1, 1, 1, -1, 1, 1, -1, -1, 2, 1, -1, 2, 1, 1, 2, -1, 1, 2];
        uint[] idx = [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7];
        var mesh = new MeshData(pos, [], [], idx);
        var rc = new MeshRaycaster(mesh);
        var hit = rc.Cast(new Ray(new Vec3d(0.2, 0.3, 0), Vec3d.UnitZ))!.Value;
        Assert.Equal(1, hit.Distance, 9);
        Assert.InRange(hit.Triangle, 0, 1);
        var far = rc.Cast(new Ray(new Vec3d(0.2, 0.3, 0), Vec3d.UnitZ), triangleFilter: t => t >= 2)!.Value;
        Assert.Equal(2, far.Distance, 9);
        Assert.Null(rc.Cast(new Ray(new Vec3d(5, 5, 0), Vec3d.UnitZ)));
        Assert.True(rc.Occluded(new Ray(new Vec3d(0, 0, 0), Vec3d.UnitZ), 1.5));
        var sub = mesh.Subset(ImmutableArray.Create(2, 3));
        Assert.Equal(4, sub.VertexCount);
        Assert.Equal(2, sub.TriangleCount);
    }

    [Fact]
    public void LensShiftMovesTheImageTheWayTheDatasheetSays()
    {
        // A projector straight ahead of a wall: shifting the image right and up lands a wall point that was at
        // the raster's centre to the left of and below the raster's centre.
        var pose = Pose.LookAt(new Vec3d(0, 0, -5), Vec3d.Zero, Vec3d.UnitY);
        var shifted = Intrinsics.FromThrowRatio(1920, 1080, 1.5, 0.1, 0.2);
        var p = CameraModel.Project(Vec3d.Zero, shifted, LensDistortion.None, pose)!.Value;
        Assert.True(p.X < 960, "the image moved right, so a fixed wall point sits left of the raster's centre");
        Assert.True(p.Y > 540, "the image moved up, so a fixed wall point sits below the raster's centre");
    }
}
