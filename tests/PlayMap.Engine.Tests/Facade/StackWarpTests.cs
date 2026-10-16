using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Engine.Rendering;

namespace PlayMap.Engine.Tests.Facade;

/// <summary>The stage's warp meshes carry a member's measured lattice (no GPU): an output whose correction holds a lattice gets the mesh
/// of that lattice under its (resting) warp, the output beside it keeps none, an edit elsewhere hands back the same mesh, and a new
/// lattice builds a new one.</summary>
public sealed class StackWarpTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static async Task Settle(StageResources resources, ProjectSnapshot snap)
    {
        resources.Observe(snap);
        await resources.WhenSettledAsync(snap.Revision).WaitAsync(Patience);
        resources.Observe(snap);
        Assert.Equal(snap.Revision, resources.Revision);
    }

    private static WarpLattice Shifted(double dx) =>
        WarpLattice.AtRest(5, 4) with { Offsets = [.. Enumerable.Repeat(new Vec2d(dx, 0), 20)] };

    private static Project WithLattice(Project p, WarpLattice lattice) => p with
    {
        Outputs = p.Outputs.Update("op00000002", o => o with { Measured = new MeasuredCorrection(null, lattice, null, null, 0.03, StackKit.Scan, "sk00000001", "", 1) }),
    };

    [Fact]
    public async Task AMembersMeasuredLatticeIsTheFirstLayerOfItsWarpMesh()
    {
        var lattice = Shifted(0.01);
        var store = new ProjectStore(WithLattice(StackKit.Project(StackKit.Sim()), lattice));
        await using var resources = new StageResources(new StageMeshes(() => null));
        await Settle(resources, store.Current);

        Assert.Null(resources.Warp("op00000001"));
        var mesh = resources.Warp("op00000002");
        Assert.NotNull(mesh);
        var expected = WarpMeshBuilder.Build(OutputWarp.None, measured: lattice);
        Assert.Equal(expected.Positions, mesh.Positions);
        Assert.Equal(expected.TexCoords, mesh.TexCoords);
        // Every vertex sits one hundredth of the raster right of where it is sampled.
        for (var k = 0; k < mesh.Positions.Length; k += 2)
            Assert.Equal(mesh.TexCoords[k] + 0.01f, mesh.Positions[k], 5);

        store.Apply("rename", p => p with { Outputs = p.Outputs.Update("op00000001", o => o with { Name = "Left" }) });
        await Settle(resources, store.Current);
        Assert.Same(mesh, resources.Warp("op00000002"));

        store.Apply("realign", p => WithLattice(p, Shifted(-0.02)));
        await Settle(resources, store.Current);
        Assert.NotSame(mesh, resources.Warp("op00000002"));
        Assert.Equal(resources.Warp("op00000002")!.TexCoords[0] - 0.02f, resources.Warp("op00000002")!.Positions[0], 5);
    }
}
