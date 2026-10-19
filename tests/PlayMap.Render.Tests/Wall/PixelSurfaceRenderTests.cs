using PlayMap.Core.Content;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Render.Engine;
using PlayMap.Render.Generators.Skia;

namespace PlayMap.Render.Tests.Wall;

/// <summary>A pixel quad in the projector pass: its edges land where its corners say to within half a pixel, its picture is the
/// homography's (no affine seam along the diagonal), and a quad with no source shows the UV checker.</summary>
[Collection("gl")]
public class PixelSurfaceRenderTests(GlFixture gl)
{
    private const int PW = 320;
    private const int PH = 240;

    internal static Projector Lamp() =>
        new("pj1", "Lamp", Intrinsics.FromThrowRatio(PW, PH, 1.2), LensDistortion.None,
            Pose.LookAt(new Vec3d(0, 0, 3), new Vec3d(0, 0, 0), new Vec3d(0, 1, 0)), 0, "#FF4000", false, [], SolveLocks.None, 0);

    internal static Surface QuadSurface(PixelQuad quad, SourceRef source, string id = "sf_q") =>
        new Surface(id, "Quad", "", Transform3.Identity, [], SurfaceContent.Empty with { Source = source }, 1, [], true, false, "#3EC1F3", [])
        { Pixel = quad };

    /// <summary>A strongly perspective quad: its centre is far from its corners' mean.</summary>
    internal static PixelQuad Keystone() =>
        new("pj1", new Vec2d(0.10, 0.12), new Vec2d(0.92, 0.30), new Vec2d(0.86, 0.70), new Vec2d(0.14, 0.90), null);

    private byte[] Render(Surface surface, Generator? generator = null)
    {
        var lamp = Lamp();
        var output = Scene.SourceOutput("op1", SourceRef.None, PW, PH) with { Mode = OutputMode.Projector, ProjectorId = lamp.Id };
        var project = Scene.Project(generator is null ? [] : [generator], [output], stage: Stage.Empty with { Surfaces = [surface], Projectors = [lamp] });
        return gl.Run(c =>
        {
            using var engine = new RenderEngine(c.Backend, new RenderEngineOptions(GpuTiming: false));
            engine.Render(Scene.Inputs(project, 1, stage: new TestStage()));
            var px = new byte[PW * PH * 4];
            Assert.True(engine.ReadOutput("op1", px));
            Assert.Empty(c.Device.TakeErrors());
            return px;
        });
    }

    [Fact]
    public void AQuadsEdgesLandOnTheRasterWithinHalfAPixel()
    {
        var quad = Keystone();
        var px = Render(QuadSurface(quad, SourceRef.Solid("#FFFFFF")));
        Span<Vec2d> poly =
        [
            new(quad.TopLeft.X * PW, quad.TopLeft.Y * PH), new(quad.TopRight.X * PW, quad.TopRight.Y * PH),
            new(quad.BottomRight.X * PW, quad.BottomRight.Y * PH), new(quad.BottomLeft.X * PW, quad.BottomLeft.Y * PH),
        ];
        var checkedPixels = 0;
        for (var y = 0; y < PH; y++)
            for (var x = 0; x < PW; x++)
            {
                var c = new Vec2d(x + 0.5, y + 0.5);
                var (inside, distance) = Inside(poly, c);
                if (distance <= 0.5)
                    continue;
                var r = Pixels.At(px, PW, x, y).R;
                Assert.True(inside ? r > 250 : r < 5, $"pixel ({x}, {y}) {(inside ? "inside" : "outside")} by {distance:0.00} px reads {r}");
                checkedPixels++;
            }
        Assert.True(checkedPixels > PW * PH * 9 / 10);
    }

    [Fact]
    public void ACheckerIsProjectivelyCorrectAcrossTheDiagonal()
    {
        const int Cols = 8;
        var rows = UvCheckerPattern.RowsFor(Cols, PW, PH);
        var checker = Scene.Gen("pattern.uvchecker", ParamSet.Empty.With("labels", false).With("divisions", (double)Cols), "gn_uv");
        var quad = Keystone();
        var px = Render(QuadSurface(quad, SourceRef.Generator(checker.Id)), checker);
        var shape = PixelQuadShape.Of(quad)!;
        var probes = 0;
        var affineWrong = 0;
        for (var j = 0; j < 40; j++)
            for (var i = 0; i < 40; i++)
            {
                double u = (i + 0.5) / 40, v = (j + 0.5) / 40;
                var cu = u * Cols - Math.Floor(u * Cols);
                var cv = v * rows - Math.Floor(v * rows);
                if (cu is < 0.2 or > 0.8 || cv is < 0.2 or > 0.8)
                    continue;
                var at = shape.Evaluate(u, v);
                var (x, y) = ((int)(at.X * PW), (int)(at.Y * PH));
                var (cr, cg, cb) = UvCheckerPattern.CellColor((int)(u * Cols), (int)(v * rows), Cols);
                Pixels.Near((cr, cg, cb), Pixels.At(px, PW, x, y), 6, $"(u, v) = ({u:0.000}, {v:0.000}) at ({x}, {y})");
                probes++;
                // Where two affine triangles split along TL–BR would have sampled this pixel instead.
                var affine = AffineUv(quad, new Vec2d((x + 0.5) / PW, (y + 0.5) / PH));
                if ((int)(affine.X * Cols) != (int)(u * Cols) || (int)(affine.Y * rows) != (int)(v * rows))
                    affineWrong++;
            }
        Assert.True(probes > 200, $"{probes} probes");
        Assert.True(affineWrong > 20, $"the quad is perspective enough that an affine split lands {affineWrong} probes in another cell");
    }

    [Fact]
    public void ANewQuadWithNoSourceShowsTheUvChecker()
    {
        var quad = new PixelQuad("pj1", new Vec2d(0.05, 0.05), new Vec2d(0.95, 0.05), new Vec2d(0.95, 0.95), new Vec2d(0.05, 0.95), null);
        var px = Render(QuadSurface(quad, SourceRef.None));
        const int Cols = 8;
        var rows = UvCheckerPattern.RowsFor(Cols, 1024, 1024);
        var shape = PixelQuadShape.Of(quad)!;
        for (var row = 0; row < rows; row++)
            for (var col = 0; col < Cols; col++)
            {
                // Near a cell's corner, clear of its centred label.
                double u = (col + 0.12) / Cols, v = (row + 0.12) / rows;
                var at = shape.Evaluate(u, v);
                var (cr, cg, cb) = UvCheckerPattern.CellColor(col, row, Cols);
                Pixels.Near((cr, cg, cb), Pixels.At(px, PW, (int)(at.X * PW), (int)(at.Y * PH)), 8, $"cell {UvCheckerPattern.Label(col, row)}");
            }
        // Outside the quad the projector is black.
        Assert.True(Pixels.At(px, PW, 2, 2).R < 5);
    }

    /// <summary>Inside the polygon, and the distance to its nearest edge.</summary>
    private static (bool Inside, double Distance) Inside(ReadOnlySpan<Vec2d> poly, Vec2d p)
    {
        var inside = false;
        var best = double.MaxValue;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            var a = poly[j];
            var b = poly[i];
            if ((b.Y > p.Y) != (a.Y > p.Y) && p.X < (a.X - b.X) * (p.Y - b.Y) / (a.Y - b.Y) + b.X)
                inside = !inside;
            var ab = b - a;
            var t = Math.Clamp(Vec2d.Dot(p - a, ab) / ab.LengthSquared, 0, 1);
            best = Math.Min(best, (a + ab * t - p).Length);
        }
        return (inside, best);
    }

    /// <summary>The (u, v) two affine triangles (TL-TR-BR and TL-BR-BL) would give a raster point.</summary>
    private static Vec2d AffineUv(PixelQuad q, Vec2d p)
    {
        var upper = Barycentric(p, q.TopLeft, q.TopRight, q.BottomRight);
        if (upper.X >= 0 && upper.Y >= 0 && upper.Z >= 0)
            return new Vec2d(upper.Y + upper.Z, upper.Z);
        var lower = Barycentric(p, q.TopLeft, q.BottomRight, q.BottomLeft);
        return new Vec2d(lower.Y, lower.Y + lower.Z);
    }

    private static Vec3d Barycentric(Vec2d p, Vec2d a, Vec2d b, Vec2d c)
    {
        var d = Vec2d.Cross(b - a, c - a);
        var wb = Vec2d.Cross(p - a, c - a) / d;
        var wc = Vec2d.Cross(b - a, p - a) / d;
        return new Vec3d(1 - wb - wc, wb, wc);
    }
}
