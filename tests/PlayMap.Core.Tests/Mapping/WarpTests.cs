using System.Collections.Immutable;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Mapping;

/// <summary>The 2D output warp: the lattice through its nodes, the corner pin's homography over it, the pin
/// warp's residual on top; inversion, resampling and hit-testing for the editor; the tessellated mesh.</summary>
public class WarpTests
{
    private static WarpLattice RandomLattice(int columns, int rows, WarpInterpolation mode, ulong seed, double amplitude = 0.03)
    {
        var rng = new Pcg32(seed);
        var offsets = Enumerable.Range(0, columns * rows)
            .Select(_ => new Vec2d(rng.Range(-amplitude, amplitude), rng.Range(-amplitude, amplitude))).ToImmutableArray();
        var zeros = ImmutableArray.CreateRange(Enumerable.Repeat(Vec2d.Zero, columns * rows));
        return new WarpLattice(columns, rows, offsets, mode, zeros, zeros);
    }

    private static readonly CornerPin Keystone = new(new Vec2d(0.05, 0.02), new Vec2d(-0.08, 0.06), new Vec2d(-0.02, -0.04), new Vec2d(0.07, -0.01));

    [Fact]
    public void ADisabledOrRestingWarpIsTheIdentityQuad()
    {
        var none = WarpMeshBuilder.Build(OutputWarp.None);
        Assert.Equal(4, none.VertexCount);
        Assert.Equal(none.TexCoords, none.Positions);
        Assert.Equal([0u, 1, 2, 0, 2, 3], none.Indices);

        var resting = new OutputWarp(true, CornerPin.None, WarpLattice.AtRest(9, 7));
        var mesh = WarpMeshBuilder.Build(resting);
        Assert.Equal(4, mesh.VertexCount);
        Assert.Equal(mesh.TexCoords, mesh.Positions);

        var disabled = new OutputWarp(false, Keystone, RandomLattice(5, 5, WarpInterpolation.CatmullRom, 1));
        Assert.Equal(new Vec2d(0.3, 0.7), WarpMeshBuilder.Evaluate(disabled, 0.3, 0.7));
        Assert.True(new WarpField(disabled).IsIdentity);
    }

    [Fact]
    public void ALatticeAtRestInEveryModeIsTheIdentityInsideEveryCell()
    {
        foreach (var mode in Enum.GetValues<WarpInterpolation>())
        {
            var surface = new LatticeSurface(WarpLattice.AtRest(6, 4, mode));
            for (var i = 0; i <= 20; i++)
                for (var j = 0; j <= 20; j++)
                {
                    var p = surface.Evaluate(i / 20.0, j / 20.0);
                    Assert.Equal(i / 20.0, p.X, 12);
                    Assert.Equal(j / 20.0, p.Y, 12);
                }
        }
    }

    [Fact]
    public void ACornerPinMapsTheCornersExactlyAndKeepsStraightLinesStraight()
    {
        var warp = new OutputWarp(true, Keystone, null);
        var field = new WarpField(warp);
        var (tl, tr, br, bl) = QuadHomography.Corners(Keystone);
        AssertNear(tl, field.Evaluate(0, 0), 1e-12);
        AssertNear(tr, field.Evaluate(1, 0), 1e-12);
        AssertNear(br, field.Evaluate(1, 1), 1e-12);
        AssertNear(bl, field.Evaluate(0, 1), 1e-12);
        Assert.True(field.PinIsValid);

        var rng = new Pcg32(7);
        for (var line = 0; line < 50; line++)
        {
            var a = new Vec2d(rng.NextDouble(), rng.NextDouble());
            var b = new Vec2d(rng.NextDouble(), rng.NextDouble());
            var pa = field.Evaluate(a.X, a.Y);
            var pb = field.Evaluate(b.X, b.Y);
            var dir = (pb - pa).Normalized();
            for (var k = 1; k < 10; k++)
            {
                var c = Vec2d.Lerp(a, b, k / 10.0);
                var pc = field.Evaluate(c.X, c.Y);
                // Distance of the image of a point on the segment from the image segment's line.
                Assert.True(Math.Abs(Vec2d.Cross(dir, pc - pa)) < 1e-12, $"line {line} point {k} left the line");
            }
        }

        // The mesh's rows are straight too: every vertex of a row lies on the line through its ends.
        var mesh = WarpMeshBuilder.Build(warp);
        Assert.Equal(32, mesh.GridColumns);
        var vw = mesh.GridColumns + 1;
        for (var row = 0; row <= mesh.GridRows; row += 8)
        {
            var p0 = new Vec2d(mesh.Positions[row * vw * 2], mesh.Positions[row * vw * 2 + 1]);
            var p1 = new Vec2d(mesh.Positions[(row * vw + vw - 1) * 2], mesh.Positions[(row * vw + vw - 1) * 2 + 1]);
            var d = (p1 - p0).Normalized();
            for (var x = 1; x < vw - 1; x++)
            {
                var p = new Vec2d(mesh.Positions[(row * vw + x) * 2], mesh.Positions[(row * vw + x) * 2 + 1]);
                Assert.True(Math.Abs(Vec2d.Cross(d, p - p0)) < 1e-6);
            }
        }
    }

    [Fact]
    public void ACatmullRomLatticePassesThroughEveryNodeAndSoDoTheOtherModes()
    {
        foreach (var mode in Enum.GetValues<WarpInterpolation>())
        {
            var lattice = RandomLattice(7, 5, mode, 3);
            if (mode == WarpInterpolation.Bezier)
                lattice = WarpMeshBuilder.WithHandles(lattice, 9, new Vec2d(0.01, -0.02), new Vec2d(0.015, 0.005));
            var surface = new LatticeSurface(lattice);
            for (var j = 0; j < 5; j++)
                for (var i = 0; i < 7; i++)
                {
                    var rest = surface.Rest(i, j);
                    AssertNear(rest + lattice.Offset(i, j), surface.Evaluate(rest.X, rest.Y), 1e-12);
                }
        }
    }

    [Fact]
    public void ABezierLatticeWithZeroHandlesEqualsTheCatmullRomSurfaceAndHandlesBendIt()
    {
        var cr = RandomLattice(5, 6, WarpInterpolation.CatmullRom, 5);
        var bz = cr with { Interpolation = WarpInterpolation.Bezier };
        var a = new LatticeSurface(cr);
        var b = new LatticeSurface(bz);
        var bent = new LatticeSurface(WarpMeshBuilder.WithHandles(bz, 12, new Vec2d(0.02, 0.01), Vec2d.Zero));
        var maxBend = 0.0;
        for (var i = 0; i <= 40; i++)
            for (var j = 0; j <= 40; j++)
            {
                AssertNear(a.Evaluate(i / 40.0, j / 40.0), b.Evaluate(i / 40.0, j / 40.0), 1e-12);
                maxBend = Math.Max(maxBend, (bent.Evaluate(i / 40.0, j / 40.0) - a.Evaluate(i / 40.0, j / 40.0)).Length);
            }
        Assert.True(maxBend > 1e-3, $"a handle should bend the surface, moved {maxBend}");
    }

    [Fact]
    public void NeighbouringCellsMeetWithMatchingSlope()
    {
        var surface = new LatticeSurface(RandomLattice(5, 5, WarpInterpolation.CatmullRom, 9, 0.05));
        const double eps = 1e-9;
        for (var cell = 1; cell < 4; cell++)
            for (var v = 0.05; v < 1; v += 0.1)
            {
                var u = cell / 4.0;
                var (_, leftU, leftV) = surface.EvaluateWithDerivatives(u - eps, v);
                var (_, rightU, rightV) = surface.EvaluateWithDerivatives(u + eps, v);
                AssertNear(leftU, rightU, 1e-6);
                AssertNear(leftV, rightV, 1e-6);
            }
    }

    [Fact]
    public void InvertTakesEveryOutputPointBackToTheContentPointThatLandsThere()
    {
        var pins = PinWarp.None with { Pins = [new WarpPin(new Vec2d(0.4, 0.5), new Vec2d(0.43, 0.48))] };
        var warp = new OutputWarp(true, Keystone, RandomLattice(5, 5, WarpInterpolation.CatmullRom, 11, 0.02));
        var field = new WarpField(warp, pins);
        var rng = new Pcg32(13);
        for (var k = 0; k < 300; k++)
        {
            var uv = new Vec2d(rng.NextDouble(), rng.NextDouble());
            var p = field.Evaluate(uv.X, uv.Y);
            var back = field.Invert(p.X, p.Y);
            Assert.NotNull(back);
            AssertNear(uv, back.Value, 1e-8);
        }
        // The pin alone inverts in closed form, outside the picture too.
        var pinOnly = new WarpField(new OutputWarp(true, Keystone, null));
        var q = pinOnly.Evaluate(1.2, -0.1);
        AssertNear(new Vec2d(1.2, -0.1), pinOnly.Invert(q.X, q.Y)!.Value, 1e-10);
    }

    [Fact]
    public void ResamplingKeepsTheShape()
    {
        // A smooth bow, the kind a lens or a curved screen asks for.
        var offsets = new List<Vec2d>();
        for (var j = 0; j < 5; j++)
            for (var i = 0; i < 5; i++)
            {
                var u = i / 4.0;
                var v = j / 4.0;
                offsets.Add(new Vec2d(0.03 * Math.Sin(Math.PI * v) * (u - 0.5), 0.02 * Math.Sin(Math.PI * u) * Math.Sin(Math.PI * v)));
            }
        var zeros = ImmutableArray.CreateRange(Enumerable.Repeat(Vec2d.Zero, 25));
        foreach (var mode in new[] { WarpInterpolation.CatmullRom, WarpInterpolation.Bezier })
        {
            var lattice = new WarpLattice(5, 5, [.. offsets], mode, zeros, zeros);
            var fine = WarpMeshBuilder.Resample(lattice, 17, 17);
            Assert.Equal(17 * 17, fine.Offsets.Length);
            Assert.Equal(mode, fine.Interpolation);
            var max = MaxDifference(new LatticeSurface(lattice), new LatticeSurface(fine));
            Assert.True(max < 2e-4, $"{mode}: resampled surface moved {max}");
        }
        // Linear refines exactly on a multiple of the old density.
        var linear = RandomLattice(3, 3, WarpInterpolation.Linear, 17);
        Assert.True(MaxDifference(new LatticeSurface(linear), new LatticeSurface(WarpMeshBuilder.Resample(linear, 5, 9))) < 1e-12);
    }

    [Fact]
    public void APinWarpLandsEveryPinExactlyAndItsPullDecaysTowardsTheFrame()
    {
        var pin = new WarpPin(new Vec2d(0.5, 0.5), new Vec2d(0.56, 0.47));
        var warp = PinWarp.None with { Pins = [pin, new WarpPin(new Vec2d(0.2, 0.3), new Vec2d(0.21, 0.3))] };
        var field = warp.Compile()!;
        AssertNear(pin.To, field.Apply(pin.From), 1e-10);
        AssertNear(new Vec2d(0.21, 0.3), field.Apply(new Vec2d(0.2, 0.3)), 1e-10);

        // Along the line from the main pin to the right edge the pull falls away, to nothing at the frame.
        var previous = double.MaxValue;
        for (var x = 0.5; x <= 1.0001; x += 0.05)
        {
            var d = field.Displacement(new Vec2d(x, 0.5)).Length;
            Assert.True(d < previous + 1e-12, $"pull grew at x={x}");
            previous = d;
        }
        Assert.True(field.Displacement(new Vec2d(1, 0.5)).Length < 1e-9);
        Assert.True(field.Displacement(new Vec2d(0.98, 0.5)).Length < 0.01 * pin.Displacement.Length * 10);

        // Through the whole warp the pin lands where it was dragged, and the inverse finds the point shown there.
        var output = new WarpField(new OutputWarp(true, CornerPin.None, null), warp);
        AssertNear(pin.To, output.Evaluate(0.5, 0.5), 1e-10);
        AssertNear(pin.From, field.Inverse(pin.To), 1e-9);
        var added = warp.WithPin(new Vec2d(0.7, 0.7), new Vec2d(0.72, 0.7));
        AssertNear(new Vec2d(0.72, 0.7), added.Compile()!.Apply(added.Pins[^1].From), 1e-9);
    }

    [Fact]
    public void AThinPlateSplineReproducesAnAffineMapExactly()
    {
        var rng = new Pcg32(19);
        var centres = Enumerable.Range(0, 12).Select(_ => new Vec2d(rng.NextDouble(), rng.NextDouble())).ToList();
        static Vec2d Affine(Vec2d p) => new(0.1 + 0.9 * p.X - 0.2 * p.Y, -0.3 + 0.4 * p.X + 1.1 * p.Y);
        var tps = new ThinPlateSpline(centres, centres.Select(Affine).ToList());
        for (var k = 0; k < 50; k++)
        {
            var p = new Vec2d(rng.Range(-0.5, 1.5), rng.Range(-0.5, 1.5));
            AssertNear(Affine(p), tps.Evaluate(p), 1e-8);
        }
        // Coincident centres do not break it: the field stays finite.
        var dup = new ThinPlateSpline([new Vec2d(0, 0), new Vec2d(0, 0), new Vec2d(1, 0), new Vec2d(0, 1)],
            [new Vec2d(1, 0), new Vec2d(0, 1), Vec2d.Zero, Vec2d.Zero]);
        Assert.True(double.IsFinite(dup.Evaluate(new Vec2d(0.3, 0.3)).X));
    }

    [Fact]
    public void TheNodeUnderTheCursorIsFoundWithinItsReachInPixels()
    {
        var lattice = WarpMeshBuilder.WithNode(WarpLattice.AtRest(5, 5), 12, new Vec2d(0.02, 0.01));
        var warp = new OutputWarp(true, CornerPin.None, lattice);
        var nodes = WarpMeshBuilder.NodePositions(warp);
        AssertNear(new Vec2d(0.52, 0.51), nodes[12], 1e-12);
        Assert.Equal(12, WarpMeshBuilder.NearestNode(warp, new Vec2d(0.52 + 5.0 / 1920, 0.51), 8, 1920, 1080));
        Assert.Equal(-1, WarpMeshBuilder.NearestNode(warp, new Vec2d(0.52 + 20.0 / 1920, 0.51), 8, 1920, 1080));
        Assert.Equal(0, WarpMeshBuilder.NearestNode(warp, new Vec2d(0.001, 0.001), 8, 1920, 1080));
    }

    [Fact]
    public void TheMeshPutsAVertexOnEveryNodeAndSamplesThePictureEvenly()
    {
        var lattice = RandomLattice(5, 3, WarpInterpolation.CatmullRom, 23);
        var warp = new OutputWarp(true, Keystone, lattice);
        var mesh = WarpMeshBuilder.Build(warp, subdivisionsPerCell: 8, minGrid: 32);
        Assert.Equal(32, mesh.GridColumns);
        Assert.Equal(32, mesh.GridRows);
        Assert.Equal(33 * 33, mesh.VertexCount);
        Assert.Equal(32 * 32 * 6, mesh.Indices.Length);
        var nodes = WarpMeshBuilder.NodePositions(warp);
        // Node (i, j) is vertex (8i, 16j): 4 cells × 8 across, 2 cells × 16 down.
        for (var j = 0; j < 3; j++)
            for (var i = 0; i < 5; i++)
            {
                var v = j * 16 * 33 + i * 8;
                Assert.Equal(nodes[j * 5 + i].X, mesh.Positions[v * 2], 5);
                Assert.Equal(nodes[j * 5 + i].Y, mesh.Positions[v * 2 + 1], 5);
                Assert.Equal(i / 4.0, mesh.TexCoords[v * 2], 6);
                Assert.Equal(j / 2.0, mesh.TexCoords[v * 2 + 1], 6);
            }
    }

    private static double MaxDifference(LatticeSurface a, LatticeSurface b)
    {
        var max = 0.0;
        for (var i = 0; i <= 64; i++)
            for (var j = 0; j <= 64; j++)
                max = Math.Max(max, (a.Evaluate(i / 64.0, j / 64.0) - b.Evaluate(i / 64.0, j / 64.0)).Length);
        return max;
    }

    private static void AssertNear(Vec2d expected, Vec2d actual, double tolerance) =>
        Assert.True((expected - actual).Length <= tolerance, $"expected {expected}, got {actual} (|Δ| = {(expected - actual).Length:E2})");
}
