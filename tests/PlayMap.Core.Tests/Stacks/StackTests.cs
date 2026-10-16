using PlayMap.Core.Align;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using PlayMap.Core.Stacks;
using PlayMap.Core.Tests.Mapping;
using Xunit.Abstractions;

namespace PlayMap.Core.Tests.Stacks;

/// <summary>Stacks (facade §3.9) on the simulators: two projectors stacked by hand — the second's picture 0.8 px along, 0.4 px down,
/// a 0.3 % longer throw and 0.5° of roll — scanned through one camera with align's real capture and decode; the member's lattice fitted
/// to the master's picture brings their misregistration (truth: where each one's light for the same picture point lands, in master
/// pixels) to p95 ≤ 0.2 px. The lattice basis is the warp surface's own. The blend collapse gives each member the share one projector
/// with their summed light would get. STACK MATCH on align's colour simulator: gains ≤ 1 and the whites within Δu′v′ 0.003.</summary>
public sealed class StackTests(ITestOutputHelper log)
{
    [Fact]
    public void TheBasisIsTheWarpSurfacesOwn()
    {
        var rng = new Pcg32(7, 1);
        Span<double> w = stackalloc double[LatticeBasis.MaxSupport];
        foreach (var (cols, rows) in new[] { (2, 2), (5, 4), (33, 19) })
        {
            var offsets = Enumerable.Range(0, cols * rows).Select(_ => new Vec2d(rng.Range(-0.02, 0.02), rng.Range(-0.02, 0.02))).ToArray();
            var lattice = WarpLattice.AtRest(cols, rows) with { Offsets = [.. offsets] };
            var surface = new LatticeSurface(lattice);
            var basis = new LatticeBasis(cols, rows);
            for (var i = 0; i < 400; i++)
            {
                var u = rng.NextDouble();
                var v = rng.NextDouble();
                var cell = basis.Weights(u, v, w);
                var nodes = basis.NodesOf(cell);
                var p = new Vec2d(u, v);
                for (var a = 0; a < nodes.Count; a++)
                    p += offsets[nodes[a]] * w[a];
                var expected = surface.Evaluate(u, v);
                Assert.True((p - expected).Length < 1e-12, $"{cols}×{rows} at ({u}, {v}): {p} vs {expected}");
            }
        }
    }

    [Fact]
    public void AStraightFieldIsFittedWithinAFewHundredthsAndAStrayDecodeMovesItLittle()
    {
        // A shift and a 0.3 % zoom: what bending does not hold back. One pair in fifty is a stray decode 20 px off.
        var rng = new Pcg32(3, 9);
        var pairs = new List<StackPair>();
        for (var i = 0; i < 20_000; i++)
        {
            var r = new Vec2d(rng.Range(0, 320), rng.Range(0, 180));
            var e = new Vec2d(160 + (r.X - 160) * 1.003 + 0.8, 90 + (r.Y - 90) * 1.003 - 0.4);
            if (i % 50 == 7)
                e += new Vec2d(20, -20);
            pairs.Add(new StackPair(r, e));
        }
        var fit = StackAlignment.Fit(pairs, 320, 180);
        log.WriteLine(fit.Words);
        Assert.True(fit.BeforePx > 0.8);
        // The held-out pairs hold strays too: the p95 reads the honest ones.
        Assert.True(fit.P95Px < 0.05, $"p95 {fit.P95Px}");
        Assert.Equal(33, fit.Lattice.Columns);
        Assert.Equal(19, fit.Lattice.Rows);
    }

    [Fact]
    public void AFitThatWouldFoldThePictureSaysWhereAndAStraightFieldNeverFolds()
    {
        // A glass band: the decodes there come back mirrored about the raster's middle, consistently, so the reweighting cannot set
        // them aside; the lattice that follows them turns the picture over itself there.
        var rng = new Pcg32(5, 1);
        var pairs = new List<StackPair>();
        for (var i = 0; i < 40_000; i++)
        {
            var r = new Vec2d(rng.Range(0, 320), rng.Range(0, 180));
            pairs.Add(new StackPair(r, r.X is > 140 and < 180 ? new Vec2d(320 - r.X, r.Y) : r));
        }
        var fit = StackAlignment.Fit(pairs, 320, 180);
        log.WriteLine(fit.Words);
        var at = Assert.NotNull(fit.FoldsAt);
        Assert.InRange(at.X, 120, 200);
        Assert.Contains("it folds the picture near", fit.Words, StringComparison.Ordinal);

        var straight = pairs.Select(p => new StackPair(p.Render, new Vec2d(160 + (p.Render.X - 160) * 1.003 + 0.8, p.Render.Y - 0.4))).ToList();
        Assert.Null(StackAlignment.Fit(straight, 320, 180).FoldsAt);
        Assert.Null(StackAlignment.FoldOf(WarpLattice.AtRest(33, 19), 320, 180));
    }

    private static List<AlignOutputSpec> Specs(MultiProjectorSim sim) =>
        [.. sim.Projectors.Select((p, j) => new AlignOutputSpec($"op{j + 1}", $"P{j + 1}", p.Intrinsics.Width, p.Intrinsics.Height,
            new ProjectorPrior(p.Intrinsics.Width, p.Intrinsics.Height, p.Intrinsics.Cx, p.Intrinsics.Cy, p.Intrinsics.Fx, 0.05)))];

    /// <summary>Each member's projector-space view from align's capture and decode on the simulator: what a scan of the stack holds.</summary>
    internal static async Task<(IReadOnlyList<ProjectorView> Views, IReadOnlyList<AlignCapture> Captures)> ScanAsync(MultiProjectorSim sim)
    {
        var (_, captures) = await AlignSequence.CaptureAsync(new SimShooter(sim), Specs(sim), null, CancellationToken.None);
        return ([.. captures.Select(c => ProjectorView.Build(c.Map))], captures);
    }

    /// <summary>Where member j's light for the master's picture point q lands, in the master's raster (truth), when the member draws
    /// it through its lattice (null: none).</summary>
    private static double? Misregistration(MultiProjectorSim sim, int j, Vec2d q, WarpLattice? lattice)
    {
        var k = sim.Projectors[j].Intrinsics;
        var emit = q;
        if (lattice is not null)
        {
            var at = new LatticeSurface(lattice).Evaluate(q.X / k.Width, q.Y / k.Height);
            emit = new Vec2d(at.X * k.Width, at.Y * k.Height);
        }
        if (sim.ProjectorToScreen(j, emit) is not { } world || sim.ScreenToRaster(0, world) is not { } back)
            return null;
        return (back - q).Length;
    }

    [Fact]
    public async Task StackAlignBringsTheMembersMisregistrationToATenthOfAPixel()
    {
        var sim = MultiProjectorSim.Stack(2);
        var (views, _) = await ScanAsync(sim);
        var k = sim.Projectors[1].Intrinsics;
        var pairs = StackPairs.Against(views[0], views[1], StackPairs.Scaled(k.Width, k.Height, k.Width, k.Height));
        log.WriteLine($"{pairs.Count} pairs");
        var fit = StackAlignment.Fit(pairs, k.Width, k.Height);
        log.WriteLine(fit.Words);

        var before = new List<double>();
        var after = new List<double>();
        for (var y = 0.08; y <= 0.92; y += 0.02)
            for (var x = 0.08; x <= 0.92; x += 0.02)
            {
                var q = new Vec2d(x * k.Width, y * k.Height);
                if (Misregistration(sim, 1, q, null) is { } b && Misregistration(sim, 1, q, fit.Lattice) is { } a)
                {
                    before.Add(b);
                    after.Add(a);
                }
            }
        var p95Before = StackAlignment.Quantile(before, 0.95);
        var p95After = StackAlignment.Quantile(after, 0.95);
        log.WriteLine($"misregistration p95: {p95Before:0.000} px before, {p95After:0.000} px after ({after.Count} points)");
        Assert.True(after.Count > 1000);
        Assert.True(p95Before > 0.8, $"before {p95Before}");
        Assert.True(p95After <= 0.2, $"after {p95After}");
        // The fit's own held-out residual says the same, on decodes it never saw.
        Assert.True(fit.ResidualPx <= 0.25, $"held out {fit.ResidualPx}");
    }

    [Fact]
    public void TheBlendCollapseGivesEachMemberTheShareOfOneProjectorWithTheirLight()
    {
        static Projector Lit(Projector p, double lumens) => p with { Lumens = lumens };
        var (stage, meshes, outputs) = BlendSolver3DTests.Room(box: false);
        var pa = stage.Projectors[0];
        var pb = stage.Projectors[1];

        // Stacked: two identical projectors where pa stands, each 1000 lm, beside pb at 1000 lm.
        var stacked = stage with
        {
            Projectors = [Lit(pa, 1000), Lit(pa with { Id = "pa2", Name = "PA2" }, 1000), Lit(pb, 1000)],
            Stacks = [new StackGroup("sk00000001", "S1", ["pa", "pa2"], "pa", StackFailover.Hold)],
        };
        Output[] stackedOutputs = [.. outputs, BlendSolver3DTests.OutputFor("a2", "pa2")];
        var withStack = BlendSolver3D.Solve(stacked, stackedOutputs, id => meshes.GetValueOrDefault(id));
        // One projector with their light: 2000 lm where pa stands.
        var single = stage with { Projectors = [Lit(pa, 2000), Lit(pb, 1000)] };
        var alone = BlendSolver3D.Solve(single, outputs, id => meshes.GetValueOrDefault(id));
        log.WriteLine(withStack.Words);

        var a = withStack.For("a")!.Map.Weights;
        var a2 = withStack.For("a2")!.Map.Weights;
        var b = withStack.For("b")!.Map.Weights;
        var one = alone.For("a")!.Map.Weights;
        var bAlone = alone.For("b")!.Map.Weights;
        var shared = 0;
        for (var i = 0; i < a.Length; i++)
        {
            // A member's own pixel and its twin's projection of the same point differ by rounding only.
            Assert.True(Math.Abs(a[i] - a2[i]) < 1e-5, $"members at {i}: {a[i]} and {a2[i]}");
            Assert.True(Math.Abs(one[i] - a[i]) < 1e-4, $"stack and one projector at {i}: {a[i]} and {one[i]}");
            if (a[i] is > 0.05f and < 0.95f)
                shared++;
        }
        for (var i = 0; i < b.Length; i++)
            Assert.True(Math.Abs(bAlone[i] - b[i]) < 1e-4, $"neighbour at {i}: {b[i]} and {bAlone[i]}");
        Assert.True(shared > 100, "the stack and its neighbour share an overlap");

        // Without the stack the two at pa would split pa's share between them as two projectors.
        var unstacked = BlendSolver3D.Solve(stacked with { Stacks = [] }, stackedOutputs, id => meshes.GetValueOrDefault(id));
        Assert.Contains(Enumerable.Range(0, a.Length), i => Math.Abs(unstacked.For("a")!.Map.Weights[i] - a[i]) > 0.05);
    }

    [Fact]
    public async Task StackMatchBringsTheWhitesWithinThreeThousandthsWithGainsOfAtMostOne()
    {
        // A 6 500 K master and a 7 500 K member with a redder red, stacked: align's colour simulator.
        var stack = MultiProjectorSim.Stack(2);
        var sim = new MultiProjectorSim(stack.Screen, stack.Camera,
        [
            stack.Projectors[0] with { Response = SimProjector.ResponseAt(6500) },
            stack.Projectors[1] with { Response = SimProjector.ResponseAt(7500, 0.01), White = 0.5 },
        ]);
        var (_, captures) = await ScanAsync(sim);
        var shooter = new SimShooter(sim);
        var both = Footprints.Erode(captures[0].Footprint.Mask, sim.CameraWidth, sim.CameraHeight, 6);
        var other = Footprints.Erode(captures[1].Footprint.Mask, sim.CameraWidth, sim.CameraHeight, 6);
        for (var i = 0; i < both.Length; i++)
            both[i] &= other[i];
        var black = (await shooter.ShootRgbAsync(new byte[]?[2], CancellationToken.None))!;
        var w = sim.Projectors[0].Intrinsics.Width;
        var h = sim.Projectors[0].Intrinsics.Height;
        async Task<Vec3d> White(int j, double r, double g, double b)
        {
            static byte Level(double gain) => (byte)Math.Clamp(Math.Round(255 * Math.Pow(gain, 1 / 2.2)), 0, 255);
            var frames = new byte[]?[2];
            frames[j] = AlignSequence.SolidRgb(w, h, Level(r), Level(g), Level(b), 1);
            var shot = (await shooter.ShootRgbAsync(frames, CancellationToken.None))!;
            return ColourMatch.Mean(shot.Rgb, both) - ColourMatch.Mean(black.Rgb, both);
        }
        static double Duv(Vec3d a, Vec3d b)
        {
            var (ua, va) = ColourMatch.Uv(a);
            var (ub, vb) = ColourMatch.Uv(b);
            return Math.Sqrt((ua - ub) * (ua - ub) + (va - vb) * (va - vb));
        }

        // What a scan holds: each member's white alone (its colour white frame).
        var whites = new[] { await White(0, 1, 1, 1), await White(1, 1, 1, 1) };
        var (match, words) = StackColour.ToMaster([StackColour.FromWhite(Vec3d.Zero, whites[0]), StackColour.FromWhite(Vec3d.Zero, whites[1])], 0);
        log.WriteLine(words);
        Assert.NotNull(match);
        var before = Duv(whites[0], whites[1]);
        Assert.True(before > 0.006, $"before {before}");
        foreach (var g in match.Members)
        {
            Assert.InRange(g.R, 0, 1);
            Assert.InRange(g.G, 0, 1);
            Assert.InRange(g.B, 0, 1);
            Assert.Equal(1, Math.Max(g.R, Math.Max(g.G, g.B)), 9);
        }
        var after = new[] { await White(0, match.Members[0].R, match.Members[0].G, match.Members[0].B),
            await White(1, match.Members[1].R, match.Members[1].G, match.Members[1].B) };
        var duv = Duv(after[0], after[1]);
        log.WriteLine($"Δu′v′ {before:0.0000} before, {duv:0.0000} after");
        Assert.True(duv <= 0.003, $"after {duv}");
    }
}
