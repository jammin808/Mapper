using System.Collections.Immutable;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Engine.Rendering;

namespace PlayMap.Engine.Tests.Rendering;

/// <summary>The stage's maps over the project snapshot, with no GPU: an unchanged input hands back the same
/// object (so the renderer uploads nothing again), a moved warp node rebuilds that output's warp alone, the
/// automatic blend waits for a quiet stage and keeps its last good maps meanwhile, a blend BLEND SOLVE made is
/// taken as it is, and masks, manual edges and lens lookups come out at their sizes. The blend's quiet time runs on a clock the
/// test moves, so what has not been solved yet is not solved however late the test's own steps run.</summary>
public sealed class StageResourcesTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>Plays the render thread: hands the snapshot over, waits for the builder, takes its tables.</summary>
    private static async Task Settle(StageResources resources, ProjectSnapshot snap)
    {
        resources.Observe(snap);
        await resources.WhenSettledAsync(snap.Revision).WaitAsync(Patience);
        resources.Observe(snap);
        Assert.Equal(snap.Revision, resources.Revision);
    }

    private static async Task Until(Func<bool> condition, StageResources resources, ProjectSnapshot snap)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waited.Elapsed < Patience, "timed out");
            await Task.Delay(20);
            resources.Observe(snap);
        }
    }

    /// <summary>Moves the test's clock through the blend's quiet time once the blender waits on it: a tick short of it the wait
    /// stands and nothing is solved, and the last tick ends it.</summary>
    private static async Task PassTheQuietTime(ManualTime time, TimeSpan quiet, StageResources resources, ProjectSnapshot snap)
    {
        await Until(() => time.Waiting == 1, resources, snap);
        var solves = resources.BlendSolves;
        time.Advance(quiet - TimeSpan.FromTicks(1));
        Assert.Equal(1, time.Waiting);
        Assert.Equal(solves, resources.BlendSolves);
        time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(0, time.Waiting);
    }

    internal static Project WithWarps(Project p) => p with
    {
        Outputs = p.Outputs.Select(o => o with { Warp = new OutputWarp(true, CornerPin.None with { TopLeft = new Vec2d(0.05, 0.02) }, WarpLattice.AtRest(4, 4)) }).ToImmutableArray(),
    };

    [Fact]
    public async Task AMovedWarpNodeRebuildsThatOutputsWarpAndNothingElse()
    {
        var store = new ProjectStore(WithWarps(BlendSolveTests.TwoProjectorsOnAWall()));
        var meshes = new StageMeshes(() => null);
        await using var resources = new StageResources(meshes);
        await Settle(resources, store.Current);
        var left = resources.Warp("op_left");
        var right = resources.Warp("op_right");
        var wall = resources.Mesh("ms_wall");
        Assert.NotNull(left);
        Assert.NotNull(wall);
        Assert.Equal(2, resources.WarpBuilds);
        Assert.Equal(1, meshes.Loads);

        store.Apply("node", p => p with
        {
            Outputs = p.Outputs.Update("op_left", o => o with { Warp = o.Warp with { Lattice = o.Warp.Lattice! with { Offsets = o.Warp.Lattice.Offsets.SetItem(5, new Vec2d(0.03, -0.01)) } } }),
        });
        await Settle(resources, store.Current);

        Assert.NotSame(left, resources.Warp("op_left"));
        Assert.Same(right, resources.Warp("op_right"));
        Assert.Same(wall, resources.Mesh("ms_wall"));
        Assert.Equal(3, resources.WarpBuilds);
        Assert.Equal(1, meshes.Loads);
    }

    [Fact]
    public async Task TheAutomaticBlendWaitsForAQuietStageAndKeepsItsLastGoodMaps()
    {
        var time = new ManualTime();
        var quiet = TimeSpan.FromMilliseconds(150);
        var store = new ProjectStore(BlendSolveTests.TwoProjectorsOnAWall());
        await using var resources = new StageResources(new StageMeshes(() => null), StageResourceOptions.Default with { BlendDebounce = quiet, Time = time });
        await Settle(resources, store.Current);
        Assert.Null(resources.Blend("op_left"));
        await PassTheQuietTime(time, quiet, resources, store.Current);
        await Until(() => resources.Blend("op_left") is not null, resources, store.Current);
        var first = resources.Blend("op_left");
        Assert.Equal(1, resources.BlendSolves);
        Assert.Contains("2 projectors blended in 3D", resources.BlendWords, StringComparison.Ordinal);

        store.Apply("brighter", p => p.WithStage(s => s with { Projectors = s.Projectors.Update("pj_right", x => x with { Lumens = 8000 }) }));
        await Settle(resources, store.Current);
        Assert.Same(first, resources.Blend("op_left"));
        await PassTheQuietTime(time, quiet, resources, store.Current);
        await Until(() => !ReferenceEquals(first, resources.Blend("op_left")), resources, store.Current);
        Assert.Equal(2, resources.BlendSolves);

        // An output that stops blending automatically drops its automatic map.
        store.Apply("manual", p => p with { Outputs = p.Outputs.Update("op_left", o => o with { Blend = o.Blend with { Auto3D = false } }) });
        await Settle(resources, store.Current);
        Assert.Null(resources.Blend("op_left"));
        Assert.Null(resources.LastProblem);
    }

    [Fact]
    public async Task ABlendTheVerbSolvedIsTakenAsItIsWithoutSolvingAgain()
    {
        var project = BlendSolveTests.TwoProjectorsOnAWall();
        var store = new ProjectStore(project);
        var meshes = new StageMeshes(() => null);
        // The quiet time never passes: the clock it runs on is never moved, so the only blend there can be is the one offered.
        await using var resources = new StageResources(meshes, StageResourceOptions.Default with { BlendDebounce = TimeSpan.FromSeconds(30), Time = new ManualTime() });
        await Settle(resources, store.Current);
        var inputs = BlendInputs.Of(project);
        var offered = new BlendSolveResult(inputs, inputs.Solve(meshes), store.Current.Revision);
        resources.Offer(offered);
        await Until(() => resources.Blend("op_right") is not null, resources, store.Current);
        Assert.Same(offered.Solution.For("op_right")!.Map, resources.Blend("op_right"));
        Assert.Equal(0, resources.BlendSolves);
    }

    [Fact]
    public async Task MasksEdgesAndLensesComeOutAtTheirSizes()
    {
        var project = BlendSolveTests.TwoProjectorsOnAWall();
        var mask = new Mask("mk_1", "Window", BezierPath.Rectangle(0.25, 0.25, 0.5, 0.5), MaskMode.Add, 4, false, true);
        project = project with
        {
            Outputs = project.Outputs
                .Update("op_left", o => o with { Masks = [mask] })
                .Update("op_right", o => o with { Blend = o.Blend with { Auto3D = false, Edges = new EdgeBlend(300, 0, 0, 0) } }),
            Stage = project.Stage with
            {
                Surfaces = project.Stage.Surfaces.Update("sf_wall", s => s with { Masks = [mask with { Id = "mk_2" }] }),
                Projectors = project.Stage.Projectors.Update("pj_left", p => p with { Lens = new LensDistortion(-0.05, 0.01, 0, 0, 0) }),
            },
        };
        var store = new ProjectStore(project);
        await using var resources = new StageResources(new StageMeshes(() => null),
            StageResourceOptions.Default with { BlendDebounce = TimeSpan.FromSeconds(30), SurfaceMaskSize = 256 });
        await Settle(resources, store.Current);

        var outputMask = resources.OutputMask("op_left")!;
        Assert.Equal((960, 540), (outputMask.Width, outputMask.Height));
        Assert.Equal(1f, outputMask.At(480, 270), 3);
        Assert.Equal(0f, outputMask.At(10, 10), 3);
        Assert.Null(resources.OutputMask("op_right"));
        var surfaceMask = resources.SurfaceMask("sf_wall")!;
        Assert.Equal(256, surfaceMask.Width);
        var edges = resources.Blend("op_right")!;
        Assert.Equal(480, edges.Width);
        Assert.True(edges.WeightAt(0, 100) < 0.1f && edges.WeightAt(400, 100) > 0.99f, "the left edge fades in");
        var lens = resources.LensPredistortion("op_left")!;
        Assert.Equal((480, 270), (lens.Width, lens.Height));
        Assert.Null(resources.LensPredistortion("op_right"));
    }

    [Fact]
    public async Task AMeshFileThatIsMissingSaysWhyAndTheRestStillBuilds()
    {
        var project = BlendSolveTests.TwoProjectorsOnAWall();
        project = project.WithStage(s => s with
        {
            Meshes = s.Meshes.Add(new MeshAsset("ms_scan", "Scan", MeshOrigin.Scanned, "scans/missing.ply", "", "", ParamSet.Empty, 0, 0)),
        });
        var folder = Path.Combine(Path.GetTempPath(), "playmap-stage-tests");
        var meshes = new StageMeshes(() => folder);
        var store = new ProjectStore(project);
        await using var resources = new StageResources(meshes);
        await Settle(resources, store.Current);
        Assert.NotNull(resources.Mesh("ms_wall"));
        Assert.Null(resources.Mesh("ms_scan"));
        Assert.Contains("is missing", meshes.Problem("ms_scan"), StringComparison.Ordinal);
    }
}

/// <summary>A warp or mask edit reaches the air while a solve holds every compute thread: the stage's tables are built on a thread of
/// their own, never queued behind the compute threads (where a job waits for every one of them), and the builder's loops run on it.</summary>
[Collection(ComputeHeldCollection.Name)]
public sealed class StageTablesBesideComputeTests
{
    [Fact]
    public async Task TheTablesAreBuiltWhileEveryComputeThreadIsHeldByASolve()
    {
        var store = new ProjectStore(StageResourcesTests.WithWarps(BlendSolveTests.TwoProjectorsOnAWall()));
        await using var resources = new StageResources(new StageMeshes(() => null));
        using var held = new ManualResetEventSlim(false);
        var holding = Enumerable.Range(0, Core.Threading.Compute.Threads).Select(_ => Core.Threading.Compute.RunAsync(() => held.Wait())).ToList();
        try
        {
            var queued = Core.Threading.Compute.RunAsync(() => 1);
            resources.Observe(store.Current);
            await resources.WhenSettledAsync(store.Current.Revision).WaitAsync(TimeSpan.FromMinutes(1));
            resources.Observe(store.Current);
            Assert.Equal(store.Current.Revision, resources.Revision);
            Assert.NotNull(resources.Warp("op_left"));
            Assert.NotNull(resources.Warp("op_right"));
            Assert.False(queued.IsCompleted, "a job queued on the held compute threads ran");
        }
        finally
        {
            held.Set();
            await Task.WhenAll(holding);
        }
    }
}

/// <summary>Tests that hold the compute threads run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ComputeHeldCollection
{
    public const string Name = "Compute threads held";
}
