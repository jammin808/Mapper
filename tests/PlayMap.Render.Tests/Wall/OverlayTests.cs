using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Render.Engine;
using PlayMap.Render.Passes;

namespace PlayMap.Render.Tests.Wall;

/// <summary>The edit overlay: a quad's handles land where the output's warp puts its corners (with the content, not where the
/// raster had them), and while the show is armed the output is byte for byte what it is with no overlay at all.</summary>
[Collection("gl")]
public class OverlayTests(GlFixture gl)
{
    private const int PW = 320;
    private const int PH = 240;
    private static readonly (int R, int G, int B) Cyan = (0x3E, 0xC1, 0xF3);

    private static readonly OutputWarp Pinned = new(true,
        new CornerPin(new Vec2d(0.08, 0.05), new Vec2d(-0.04, 0.03), new Vec2d(0, 0), new Vec2d(0.02, -0.04)), null);

    private static Project Scene3(OutputWarp warp)
    {
        var lamp = PixelSurfaceRenderTests.Lamp();
        var output = Scene.SourceOutput("op1", SourceRef.None, PW, PH) with { Mode = OutputMode.Projector, ProjectorId = lamp.Id, Warp = warp };
        var surface = PixelSurfaceRenderTests.QuadSurface(PixelSurfaceRenderTests.Keystone(), SourceRef.Solid("#000000"));
        return Scene.Project([], [output], stage: Stage.Empty with { Surfaces = [surface], Projectors = [lamp] });
    }

    /// <summary>One frame of a fresh renderer (so the dither's frame is the same in every render), read back.</summary>
    private byte[] Render(Project project, OutputWarp warp, OverlayState? overlay, bool armed, bool withPass)
    {
        return gl.Run(c =>
        {
            using var engine = new RenderEngine(c.Backend, new RenderEngineOptions(GpuTiming: false));
            using var pass = new OverlayPass(c.Backend);
            var stage = new TestStage();
            if (warp.Enabled)
                stage.Warps["op1"] = WarpMeshBuilder.Build(warp);
            var inputs = Scene.Inputs(project, 1, stage: stage) with
            {
                Overlay = overlay,
                Armed = armed,
                Passes = withPass ? [pass] : [],
            };
            pass.Prepare(in inputs);
            engine.Render(inputs);
            var px = new byte[PW * PH * 4];
            Assert.True(engine.ReadOutput("op1", px));
            Assert.Empty(c.Device.TakeErrors());
            return px;
        });
    }

    private static readonly OverlayState Editing = new(true, [], OverlayLayers.Outlines | OverlayLayers.Handles, null, 0, "", 0);

    [Fact]
    public void TheHandlesAreDrawnThroughTheOutputsWarp()
    {
        var project = Scene3(Pinned);
        var px = Render(project, Pinned, Editing, armed: false, withPass: true);
        var field = new WarpField(Pinned);
        var quad = PixelSurfaceRenderTests.Keystone();
        for (var k = 0; k < 4; k++)
        {
            var corner = PixelQuadMath.Corner(quad, k);
            var at = OverlayPass.ToFinal(field, 0, corner, PW, PH);
            Pixels.Near(Cyan, Pixels.At(px, PW, (int)at.X, (int)at.Y), 3, $"corner {k} through the warp at {at}");
        }
        // Where the raster had the top-left corner, before the warp, the wall shows no handle.
        var raw = new Vec2d(quad.TopLeft.X * PW, quad.TopLeft.Y * PH);
        var warped = OverlayPass.ToFinal(field, 0, quad.TopLeft, PW, PH);
        Assert.True((raw - warped).Length > 12, $"the warp moves the corner {(raw - warped).Length:0.0} px");
        var there = Pixels.At(px, PW, (int)raw.X, (int)raw.Y);
        Assert.True(there.R + there.G + there.B < 30, $"no handle where the unwarped corner was: {there}");
        // An edge's midpoint through the warp is on the outline too: the lines bend with the content.
        var shape = PixelQuadShape.Of(quad)!;
        var mid = OverlayPass.ToFinal(field, 0, shape.Evaluate(0.5, 0), PW, PH);
        Pixels.Near(Cyan, Pixels.At(px, PW, (int)Math.Floor(mid.X), (int)Math.Floor(mid.Y)), 3, "the top edge's midpoint");
    }

    [Fact]
    public void TheWarpTheHandlesFollowIsTheWarpThePictureTakes()
    {
        // The picture alone, lit white through the warp: each corner the overlay computes is the warped picture's own corner (lit
        // just inside it towards the quad's centre, dark just outside), so the handles above sit on the content they edit.
        var lamp = PixelSurfaceRenderTests.Lamp();
        var output = Scene.SourceOutput("op1", SourceRef.None, PW, PH) with { Mode = OutputMode.Projector, ProjectorId = lamp.Id, Warp = Pinned };
        var quad = PixelSurfaceRenderTests.Keystone();
        var lit = Scene.Project([], [output], stage: Stage.Empty with
        {
            Surfaces = [PixelSurfaceRenderTests.QuadSurface(quad, SourceRef.Solid("#FFFFFF"))],
            Projectors = [lamp],
        });
        var px = Render(lit, Pinned, null, armed: false, withPass: false);
        var field = new WarpField(Pinned);
        var shape = PixelQuadShape.Of(quad)!;
        var centre = OverlayPass.ToFinal(field, 0, shape.Evaluate(0.5, 0.5), PW, PH);
        for (var k = 0; k < 4; k++)
        {
            var at = OverlayPass.ToFinal(field, 0, PixelQuadMath.Corner(quad, k), PW, PH);
            var inward = (centre - at) * (1 / (centre - at).Length);
            var inside = at + inward * 6;
            var outside = at - inward * 6;
            Assert.True(Pixels.At(px, PW, (int)inside.X, (int)inside.Y).R > 200, $"corner {k}: the picture is lit just inside {at}");
            Assert.True(Pixels.At(px, PW, (int)outside.X, (int)outside.Y).R < 30, $"corner {k}: the picture is dark just outside {at}");
        }
    }

    [Fact]
    public void ArmedTheOutputIsByteIdenticalToNoOverlay()
    {
        var project = Scene3(Pinned);
        var none = Render(project, Pinned, null, armed: false, withPass: false);
        var armed = Render(project, Pinned, Editing, armed: true, withPass: true);
        var editing = Render(project, Pinned, Editing, armed: false, withPass: true);
        Assert.Equal(none, armed);
        Assert.NotEqual(none, editing);
        // An overlay that names another output, or is off, draws nothing here either.
        Assert.Equal(none, Render(project, Pinned, Editing with { Outputs = ["op_other"] }, armed: false, withPass: true));
        Assert.Equal(none, Render(project, Pinned, OverlayState.Off, armed: false, withPass: true));
    }

    [Fact]
    public void ACursorComesBackWithItsLayer()
    {
        var project = Scene3(OutputWarp.None);
        System.Collections.Immutable.ImmutableArray<WallCursor> cursors = [new WallCursor("s1", "Desk", "op1", new Vec2d(0.25, 0.75), [], "#FF8800", 0, long.MaxValue)];
        var on = new OverlayState(true, [], OverlayLayers.Cursor | OverlayLayers.Outlines, null, 0, "", 0);
        var (first, off, back) = gl.Run(c =>
        {
            using var engine = new RenderEngine(c.Backend, new RenderEngineOptions(GpuTiming: false));
            using var pass = new OverlayPass(c.Backend);
            byte[] Frame(OverlayState state)
            {
                var inputs = Scene.Inputs(project, 1, stage: new TestStage()) with { Overlay = state, Cursors = cursors, Passes = [pass] };
                pass.Prepare(in inputs);
                engine.Render(inputs);
                var bytes = new byte[PW * PH * 4];
                Assert.True(engine.ReadOutput("op1", bytes));
                return bytes;
            }
            // The same cursors throughout: on, the layer off, then on again.
            return (Frame(on), Frame(on with { Layers = OverlayLayers.Outlines }), Frame(on with { Layers = on.Layers }));
        });
        var arm = (X: (int)(0.25 * PW) + 15, Y: (int)(0.75 * PH));
        Pixels.Near((0xFF, 0x88, 0x00), Pixels.At(first, PW, arm.X, arm.Y), 3, "the cursor at first");
        Assert.True(Pixels.At(off, PW, arm.X, arm.Y).R < 30, "no cursor with its layer off");
        Pixels.Near((0xFF, 0x88, 0x00), Pixels.At(back, PW, arm.X, arm.Y), 3, "the cursor with its layer back on");
    }

    [Fact]
    public void ACursorIsDrawnOnTheWallInItsColour()
    {
        var project = Scene3(OutputWarp.None);
        var cursor = new WallCursor("s1", "Desk", "op1", new Vec2d(0.25, 0.75), [new Vec2d(0.8, 0.2)], "#FF8800", 0, long.MaxValue);
        var px = gl.Run(c =>
        {
            using var engine = new RenderEngine(c.Backend, new RenderEngineOptions(GpuTiming: false));
            using var pass = new OverlayPass(c.Backend);
            var inputs = Scene.Inputs(project, 1, stage: new TestStage()) with
            {
                Overlay = new OverlayState(true, [], OverlayLayers.Cursor, null, 0, "", 0),
                Cursors = [cursor],
                Passes = [pass],
            };
            pass.Prepare(in inputs);
            engine.Render(inputs);
            var bytes = new byte[PW * PH * 4];
            Assert.True(engine.ReadOutput("op1", bytes));
            return bytes;
        });
        var at = new Vec2d(0.25 * PW, 0.75 * PH);
        // An arm, beyond the gap; the centre itself is the gap.
        Pixels.Near((0xFF, 0x88, 0x00), Pixels.At(px, PW, (int)at.X + 15, (int)at.Y), 3, "the right arm");
        Pixels.Near((0xFF, 0x88, 0x00), Pixels.At(px, PW, (int)at.X, (int)at.Y - 15), 3, "the upper arm");
        Assert.True(Pixels.At(px, PW, (int)at.X, (int)at.Y).G < 0x88 || Pixels.At(px, PW, (int)at.X, (int)at.Y).R < 0xFF);
        // The click so far, a dot.
        Pixels.Near((0xFF, 0x88, 0x00), Pixels.At(px, PW, (int)(0.8 * PW), (int)(0.2 * PH)), 3, "the trail's dot");
    }
}
