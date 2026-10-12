using System.Collections.Immutable;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Render.Engine;
using PlayMap.Render.Generators.Skia;

namespace PlayMap.Render.Tests;

/// <summary>Canvas layers blend as their modes say; the projector pass puts a surface where the camera model
/// says, lays a UV checker on a box face the right way up, masks in UV space and draws the surface-aware
/// effects from the geometry.</summary>
[Collection("gl")]
public class CanvasAndStageTests(GlFixture gl)
{
    private const int W = 64;
    private const int H = 36;

    private static Output CanvasOutput(string canvasId, int w, int h) =>
        Scene.SourceOutput("op1", SourceRef.Canvas(canvasId), w, h) with { Mode = OutputMode.Canvas, SourceRect = new RectD(0, 0, w, h) };

    private static Layer L(string id, string hex, LayerBlend blend, double opacity = 1, RectD? rect = null) =>
        new(id, id, SourceRef.Solid(hex), rect ?? new RectD(0, 0, W, H), opacity, blend, true);

    private (int R, int G, int B) CanvasPixel(ImmutableArray<Layer> layers, int x = 30, int y = 18, string background = "#000000")
    {
        var canvas = new Canvas("cv1", "Canvas", W, H, background, layers);
        var project = Scene.Project([], [CanvasOutput("cv1", W, H)], [canvas]);
        return gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: false));
            engine.Render(Scene.Inputs(project));
            var px = new byte[W * H * 4];
            Assert.True(engine.ReadOutput("op1", px));
            Assert.Empty(c.Device.TakeErrors());
            return Pixels.At(px, W, x, y);
        });
    }

    [Fact]
    public void CanvasLayersBlendAsTheirModesSay()
    {
        Pixels.Near((128, 128, 128), CanvasPixel([L("a", "#FFFFFF", LayerBlend.Normal, 0.5)]), 1, "normal at half");
        Pixels.Near((160, 160, 160), CanvasPixel([L("a", "#808080", LayerBlend.Normal), L("b", "#404040", LayerBlend.Add, 0.5)]), 1, "add");
        Pixels.Near((64, 64, 64), CanvasPixel([L("a", "#808080", LayerBlend.Normal), L("b", "#808080", LayerBlend.Multiply)]), 1, "multiply");
        Pixels.Near((192, 192, 192), CanvasPixel([L("a", "#808080", LayerBlend.Normal), L("b", "#808080", LayerBlend.Screen)]), 1, "screen");
    }

    [Fact]
    public void ALayerLandsInItsRectangleOverTheBackground()
    {
        ImmutableArray<Layer> layers = [L("a", "#FF0000", LayerBlend.Normal, 1, new RectD(32, 0, 32, 18))];
        Assert.Equal((255, 0, 0), CanvasPixel(layers, 40, 5, "#0000FF"));
        Assert.Equal((0, 0, 255), CanvasPixel(layers, 10, 5, "#0000FF"));
        Assert.Equal((0, 0, 255), CanvasPixel(layers, 40, 30, "#0000FF"));
    }

    // ---- the projector pass ----

    private const int PW = 160;
    private const int PH = 90;

    private static MeshData Quad() => new(
        [-0.5f, -0.5f, 0, 0.5f, -0.5f, 0, 0.5f, 0.5f, 0, -0.5f, 0.5f, 0],
        [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1],
        [0, 1, 1, 1, 1, 0, 0, 0],
        [0, 1, 2, 0, 2, 3]);

    /// <summary>A unit box centred on the origin; every face has UVs 0..1 with v down the face as seen from outside, y up.</summary>
    private static MeshData Box()
    {
        var pos = new List<float>();
        var nor = new List<float>();
        var uv = new List<float>();
        var idx = new List<uint>();
        void Face(Vec3d n, Vec3d right, Vec3d up)
        {
            var c = n * 0.5;
            Vec3d[] corners = [c - right * 0.5 + up * 0.5, c + right * 0.5 + up * 0.5, c + right * 0.5 - up * 0.5, c - right * 0.5 - up * 0.5];
            float[] uvs = [0, 0, 1, 0, 1, 1, 0, 1];
            var b = (uint)(pos.Count / 3);
            for (var i = 0; i < 4; i++)
            {
                pos.AddRange([(float)corners[i].X, (float)corners[i].Y, (float)corners[i].Z]);
                nor.AddRange([(float)n.X, (float)n.Y, (float)n.Z]);
                uv.AddRange([uvs[i * 2], uvs[i * 2 + 1]]);
            }
            idx.AddRange([b, b + 2, b + 1, b, b + 3, b + 2]);
        }
        Face(new Vec3d(0, 0, 1), new Vec3d(1, 0, 0), new Vec3d(0, 1, 0));
        Face(new Vec3d(0, 0, -1), new Vec3d(-1, 0, 0), new Vec3d(0, 1, 0));
        Face(new Vec3d(1, 0, 0), new Vec3d(0, 0, -1), new Vec3d(0, 1, 0));
        Face(new Vec3d(-1, 0, 0), new Vec3d(0, 0, 1), new Vec3d(0, 1, 0));
        Face(new Vec3d(0, 1, 0), new Vec3d(1, 0, 0), new Vec3d(0, 0, -1));
        Face(new Vec3d(0, -1, 0), new Vec3d(1, 0, 0), new Vec3d(0, 0, 1));
        return new MeshData([.. pos], [.. nor], [.. uv], [.. idx]);
    }

    private static Projector Lamp(Pose pose) =>
        new("pj1", "Lamp", Intrinsics.FromThrowRatio(PW, PH, 1.2), LensDistortion.None, pose, 0, "#FF4000", false, [], SolveLocks.None, 0);

    private static Surface Surf(string meshId, SurfaceContent content, Transform3? transform = null) =>
        new("sf1", "Surface", meshId, transform ?? Transform3.Identity, [], content, 1, [], true, false, "#3EC1F3", []);

    private byte[] ProjectorView(Projector lamp, Surface surface, MeshData mesh, ImmutableArray<Generator>? generators = null, TestStage? stage = null, ImmutableArray<Viewpoint>? viewpoints = null)
    {
        stage ??= new TestStage();
        stage.Meshes[surface.MeshId] = mesh;
        var output = Scene.SourceOutput("op1", SourceRef.None, PW, PH) with { Mode = OutputMode.Projector, ProjectorId = lamp.Id };
        var st = Stage.Empty with { Surfaces = [surface], Projectors = [lamp], Viewpoints = viewpoints ?? [] };
        var project = Scene.Project(generators ?? [], [output], stage: st);
        return gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: false));
            engine.Render(Scene.Inputs(project, 2.5, stage: stage));
            var px = new byte[PW * PH * 4];
            Assert.True(engine.ReadOutput("op1", px));
            Assert.Empty(c.Device.TakeErrors());
            return px;
        });
    }

    [Fact]
    public void AQuadLandsWhereTheCameraModelProjectsItsCorners()
    {
        var pose = Pose.LookAt(new Vec3d(0.3, 0.2, 2.5), new Vec3d(0, 0, 0), new Vec3d(0, 1, 0));
        var lamp = Lamp(pose);
        var px = ProjectorView(lamp, Surf("ms1", SurfaceContent.Empty with { Source = SourceRef.Solid("#FFFFFF") }), Quad());
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var corner in new[] { new Vec3d(-0.5, -0.5, 0), new Vec3d(0.5, -0.5, 0), new Vec3d(0.5, 0.5, 0), new Vec3d(-0.5, 0.5, 0) })
        {
            var p = CameraModel.Project(corner, lamp.Intrinsics, lamp.Lens, lamp.Pose)!.Value;
            (minX, minY, maxX, maxY) = (Math.Min(minX, p.X), Math.Min(minY, p.Y), Math.Max(maxX, p.X), Math.Max(maxY, p.Y));
            // The rasterised corner: the lit pixel nearest the projected corner is within one pixel on each axis.
            var best = double.MaxValue;
            for (var y = (int)p.Y - 4; y <= (int)p.Y + 4; y++)
                for (var x = (int)p.X - 4; x <= (int)p.X + 4; x++)
                    if (x >= 0 && y >= 0 && x < PW && y < PH && Pixels.At(px, PW, x, y).R > 128)
                        best = Math.Min(best, Math.Max(Math.Abs(x + 0.5 - p.X), Math.Abs(y + 0.5 - p.Y)));
            Assert.True(best <= 1.0, $"corner {corner} projects to {p}; the nearest lit pixel is {best:0.00} px away");
        }
        // And nothing is lit outside the projected corners' box by more than a pixel.
        for (var y = 0; y < PH; y++)
            for (var x = 0; x < PW; x++)
                if (Pixels.At(px, PW, x, y).R > 128)
                    Assert.True(x + 0.5 >= minX - 1 && x + 0.5 <= maxX + 1 && y + 0.5 >= minY - 1 && y + 0.5 <= maxY + 1, $"pixel ({x}, {y}) lit outside the quad");
        Assert.Equal((0, 0, 0), Pixels.At(px, PW, 1, 1));
    }

    [Fact]
    public void ATopLeftMarkerInTheContentLandsAtTheMeshesUvOrigin()
    {
        // UVs have their origin at the picture's top-left with v down, like every texture here: no flip.
        // The quad's UV (0, 0) is its world corner (−0.5, +0.5); the content's top-left 8×8 pixels are white.
        const int w = 64, h = 36;
        var rgba = new byte[w * h * 4];
        for (var i = 3; i < rgba.Length; i += 4)
            rgba[i] = 255;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                rgba[(y * w + x) * 4] = rgba[(y * w + x) * 4 + 1] = rgba[(y * w + x) * 4 + 2] = 255;
        var sources = new FuncSources((_, _) => new ArrayLease(PlayMap.Core.Media.PixelLayout.Rgba8, w, h, 1,
            PlayMap.Core.Media.ColorMatrix.Bt709, PlayMap.Core.Media.ColorRange.Full, ((byte[])rgba.Clone(), w * 4, w, h)));
        var pose = Pose.LookAt(new Vec3d(0, 0, 2.5), new Vec3d(0, 0, 0), new Vec3d(0, 1, 0));
        var lamp = Lamp(pose);
        var stage = new TestStage();
        stage.Meshes["ms1"] = Quad();
        var output = Scene.SourceOutput("op1", SourceRef.None, PW, PH) with { Mode = OutputMode.Projector, ProjectorId = lamp.Id };
        var surface = Surf("ms1", SurfaceContent.Empty with { Source = SourceRef.Media("md1") });
        var project = Scene.Project([], [output], stage: Stage.Empty with { Surfaces = [surface], Projectors = [lamp] });
        var px = gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: false));
            engine.Render(Scene.Inputs(project, 1, sources, stage));
            var bytes = new byte[PW * PH * 4];
            Assert.True(engine.ReadOutput("op1", bytes));
            return bytes;
        });
        (double X, double Y, bool Lit)[] probes = [(-0.47, 0.47, true), (0.47, 0.47, false), (0.47, -0.47, false), (-0.47, -0.47, false)];
        foreach (var (x, y, lit) in probes)
        {
            var p = CameraModel.Project(new Vec3d(x, y, 0), lamp.Intrinsics, lamp.Lens, lamp.Pose)!.Value;
            var r = Pixels.At(px, PW, (int)p.X, (int)p.Y).R;
            Assert.True(lit ? r > 200 : r < 40, $"world ({x}, {y}) → pixel {p}: red {r}");
        }
    }

    [Fact]
    public void AUvCheckerLandsTopLeftFirstOnABoxFace()
    {
        var pose = Pose.LookAt(new Vec3d(0, 0, 3), new Vec3d(0, 0, 0), new Vec3d(0, 1, 0));
        var lamp = Lamp(pose);
        var checker = Scene.Gen("pattern.uvchecker", ParamSet.Empty.With("labels", false), "gn_uv");
        var px = ProjectorView(lamp, Surf("ms1", SurfaceContent.Empty with { Source = SourceRef.Generator("gn_uv") }), Box(), [checker]);
        var rows = UvCheckerPattern.RowsFor(8, PW, PH);
        (double U, double V, int Col, int Row)[] probes = [(0.06, 0.1, 0, 0), (0.94, 0.1, 7, 0), (0.06, 0.9, 0, rows - 1), (0.55, 0.4, 4, 1)];
        foreach (var (u, v, col, row) in probes)
        {
            var world = new Vec3d(-0.5 + u, 0.5 - v, 0.5);
            var p = CameraModel.Project(world, lamp.Intrinsics, lamp.Lens, lamp.Pose)!.Value;
            var (r, g, b) = UvCheckerPattern.CellColor(col, row, 8);
            Pixels.Near((r, g, b), Pixels.At(px, PW, (int)p.X, (int)p.Y), 24, $"cell {UvCheckerPattern.Label(col, row)} at uv ({u}, {v})");
        }
    }

    [Fact]
    public void ASurfaceMaskDarkensItsUncoveredUvs()
    {
        var pose = Pose.LookAt(new Vec3d(0, 0, 2.5), new Vec3d(0, 0, 0), new Vec3d(0, 1, 0));
        var lamp = Lamp(pose);
        var stage = new TestStage();
        stage.SurfaceMasks["sf1"] = new CoverageMap(2, 1, [0f, 1f]);
        var px = ProjectorView(lamp, Surf("ms1", SurfaceContent.Empty with { Source = SourceRef.Solid("#FFFFFF") }), Quad(), stage: stage);
        var left = CameraModel.Project(new Vec3d(-0.4, 0, 0), lamp.Intrinsics, lamp.Lens, lamp.Pose)!.Value;
        var right = CameraModel.Project(new Vec3d(0.4, 0, 0), lamp.Intrinsics, lamp.Lens, lamp.Pose)!.Value;
        Assert.True(Pixels.At(px, PW, (int)left.X, (int)left.Y).R < 40, "masked half");
        Assert.True(Pixels.At(px, PW, (int)right.X, (int)right.Y).R > 215, "covered half");
    }

    [Fact]
    public void ContentProjectedFromAViewpointLandsAsThatViewpointSawIt()
    {
        var pose = Pose.LookAt(new Vec3d(0, 0, 3), new Vec3d(0, 0, 0), new Vec3d(0, 1, 0));
        var lamp = Lamp(pose);
        var viewpoint = new Viewpoint("vp1", "Audience", lamp.Intrinsics, pose);
        var checker = Scene.Gen("pattern.uvchecker", ParamSet.Empty.With("labels", false), "gn_uv");
        var content = new SurfaceContent(SurfaceMapping.Projection, SourceRef.Generator("gn_uv"), new RectD(0, 0, 1, 1), "vp1");
        var px = ProjectorView(lamp, Surf("ms1", content), Box(), [checker], viewpoints: [viewpoint]);
        // Seen from the viewpoint itself, a projected picture is the picture: the face's centre shows the
        // checker cell at the raster's centre.
        var rows = UvCheckerPattern.RowsFor(8, PW, PH);
        var col = (int)(0.53 * 8);
        var row = (int)(0.47 * rows);
        var (r, g, b) = UvCheckerPattern.CellColor(col, row, 8);
        Pixels.Near((r, g, b), Pixels.At(px, PW, (int)(0.53 * PW), (int)(0.47 * PH)), 24, "projected");
    }

    [Fact]
    public void SurfaceAwareEffectsDrawFromTheGeometry()
    {
        var pose = Pose.LookAt(new Vec3d(1.5, 1.2, 2.5), new Vec3d(0, 0, 0), new Vec3d(0, 1, 0));
        var lamp = Lamp(pose);
        foreach (var kind in new[] { "effect.sweep", "effect.outline", "effect.relight" })
        {
            var fx = Scene.Gen(kind, ParamSet.Empty, "gn_fx");
            var px = ProjectorView(lamp, Surf("ms1", SurfaceContent.Empty with { Source = SourceRef.Generator("gn_fx") }), Box(), [fx]);
            var lit = 0;
            for (var i = 0; i < PW * PH; i++)
                if (px[i * 4] + px[i * 4 + 1] + px[i * 4 + 2] > 30)
                    lit++;
            Assert.True(lit > 20, $"{kind} lit only {lit} pixels");
        }
    }
}
