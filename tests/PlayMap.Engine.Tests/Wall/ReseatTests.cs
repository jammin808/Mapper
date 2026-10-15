using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Wall;

/// <summary>What a camera re-solve carries with the pose (calibops M8, minimal): a pixel quad on the knocked projector is re-seated so
/// its corners and lattice nodes light the wall spots they lit before (within half a pixel after a 16 px knock); the pins keep their
/// world points and take the new pose's pixels; the wall surface, its masks and the other projectors' quads are untouched; and the
/// projector blends automatically, so its blend is solved again off the lane.</summary>
public sealed class ReseatTests(ITestOutputHelper output)
{
    private static Vec3d OnWall(Ray ray) => ray.At((5 - ray.Origin.Z) / ray.Direction.Z);

    [Fact]
    public async Task AQuadRelightsItsWallSpotsAndPinsFollowThePose()
    {
        var p = CameraStage.With(WallRig.ProjectorsWithPins(3, noisePx: 0.1));
        // The project stands exactly where the projectors truly do (a fresh calibration), with a quad drawn on P2 and one on P1.
        p = p.WithStage(st => st with
        {
            Projectors = [.. st.Projectors.Select((pj, n) => pj with { Pose = WallRig.TruePose(n) })],
            Surfaces = st.Surfaces
                .Add(QuadOn("sf_quad2", "pj_test2", WarpLattice.AtRest(3, 3)))
                .Add(QuadOn("sf_quad1", "pj_test1", null)),
        });
        await using var rig = new WallRig(p);
        foreach (var pj in rig.Project.Stage.Projectors)
            rig.Engine.SimulatedStage.Knock(pj, Vec3d.Zero, Vec3d.Zero);
        var before = rig.Project;
        var old = rig.Engine.SimulatedStage.TruthOf(before.FindProjector("pj_test2")!);
        // A knock that moves P2's picture about 16 px on the wall.
        rig.Engine.SimulatedStage.Knock(before.FindProjector("pj_test2")!, new Vec3d(0, 16.0 / 2880, 0), Vec3d.Zero);
        var knocked = rig.Engine.SimulatedStage.TruthOf(before.FindProjector("pj_test2")!);

        var quad = before.FindSurface("sf_quad2")!.Pixel!;
        var points = Points(quad);
        var spots = points.Select(pt => OnWall(CameraModel.PixelRay(pt, old.Intrinsics, old.Lens, old.Pose))).ToList();
        var stale = spots.Select((x, i) => Vec2d.Distance(CameraModel.Project(x, knocked.Intrinsics, knocked.Lens, knocked.Pose)!.Value, points[i])).Max();
        output.WriteLine($"the knock moves the quad's spots {stale:0.0} px");
        Assert.InRange(stale, 12, 24);

        var reply = WallRig.Json(await rig.Ok("CALIBRATION RESOLVE ALL CAMERA cm_sim"));
        output.WriteLine(reply.GetProperty("words").GetString());
        Assert.True(reply.GetProperty("projectors")[0].GetProperty("applied").GetBoolean());

        // The quad's corners and nodes, as re-seated, light the spots they lit before.
        var moved = rig.Project.FindSurface("sf_quad2")!.Pixel!;
        var now = Points(moved);
        var worst = 0.0;
        for (var i = 0; i < now.Count; i++)
        {
            var lit = OnWall(CameraModel.PixelRay(now[i], knocked.Intrinsics, knocked.Lens, knocked.Pose));
            var off = Vec2d.Distance(CameraModel.Project(lit, old.Intrinsics, old.Lens, old.Pose)!.Value, points[i]);
            worst = Math.Max(worst, off);
        }
        output.WriteLine($"re-seated: the corners and nodes land {worst:0.000} px from their old spots");
        Assert.True(worst <= 0.5, $"{worst} px");

        // Pins keep their world points and take the new pose's pixels.
        var pj2 = rig.Project.FindProjector("pj_test2")!;
        var oldPins = before.FindProjector("pj_test2")!.Pins;
        Assert.Equal(oldPins.Length, pj2.Pins.Length);
        for (var i = 0; i < oldPins.Length; i++)
        {
            Assert.Equal(oldPins[i].World, pj2.Pins[i].World);
            Assert.Equal(CameraModel.Project(oldPins[i].World, pj2.Intrinsics, pj2.Lens, pj2.Pose)!.Value, pj2.Pins[i].Pixel);
        }

        // Nothing else moved: the wall surface (masks and all), P1's quad, P1 and P3.
        Assert.Equal(before.FindSurface("sf_wall"), rig.Project.FindSurface("sf_wall"));
        Assert.Equal(before.FindSurface("sf_quad1"), rig.Project.FindSurface("sf_quad1"));
        Assert.Same(before.FindProjector("pj_test1"), rig.Project.FindProjector("pj_test1"));
        Assert.Same(before.FindProjector("pj_test3"), rig.Project.FindProjector("pj_test3"));

        // The projector blends automatically: its blend is solved again off the lane.
        Assert.Contains("solving the blend", reply.GetProperty("blend").GetString(), StringComparison.Ordinal);
        var solved = await rig.Engine.WhenSolved;
        Assert.NotNull(solved);
        Assert.Equal("done", solved.State);
    }

    private static Surface QuadOn(string id, string projectorId, WarpLattice? lattice) =>
        new(id, id, "", Transform3.Identity, [], SurfaceContent.Empty, 1, [], true, false, "#FF8800", [])
        {
            Pixel = new PixelQuad(projectorId, new Vec2d(0.3, 0.3), new Vec2d(0.7, 0.32), new Vec2d(0.68, 0.7), new Vec2d(0.31, 0.68), lattice),
        };

    /// <summary>A quad's corners and lattice nodes in projector px.</summary>
    private static List<Vec2d> Points(PixelQuad q)
    {
        var list = new List<Vec2d> { q.TopLeft, q.TopRight, q.BottomRight, q.BottomLeft };
        if (q.Lattice is { } l && PixelQuadShape.Of(q) is { } shape)
            for (var r = 0; r < l.Rows; r++)
                for (var c = 0; c < l.Columns; c++)
                    list.Add(PixelQuadMath.Node(shape, c, r));
        return [.. list.Select(v => new Vec2d(v.X * WallRig.W, v.Y * WallRig.H))];
    }
}
