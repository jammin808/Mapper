using PlayMap.Core.Mapping;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Render.Engine;

namespace PlayMap.Render.Tests;

/// <summary>What an output shows instead of its content: a pattern over the outputs it names and no others,
/// a raw frame that reaches the raster code for code (past the trim, the warp and a freeze, never past
/// blackout), a playlist routed to a picture the renderer draws itself, and the stage's objects uploaded
/// once per instance.</summary>
[Collection("gl")]
public class OverrideTests(GlFixture gl)
{
    private sealed class RawOn(string outputId, SourceRef source) : IRawOutputs
    {
        public SourceRef? For(string id) => id == outputId ? source : null;
    }

    private sealed class Router(SourceRef routed) : ISourceProvider
    {
        public VideoFrameLease? Acquire(SourceRef source, long leaderNs) => null;

        public SourceRef? Route(SourceRef source, long leaderNs) => source.Kind == SourceKind.Playlist ? routed : null;
    }

    [Fact]
    public void APatternCoversOnlyTheOutputsItNames()
    {
        var project = Scene.Project([Scene.Gen("pattern.flat", ParamSet.Empty.With("level", 50.0))],
            [Scene.SourceOutput("op1", SourceRef.Solid("#0000FF"), 32, 18), Scene.SourceOutput("op2", SourceRef.Solid("#00FF00"), 32, 18)]);
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: false));
            var px = new byte[32 * 18 * 4];
            engine.Render(Scene.Inputs(project) with { PatternOverride = SourceRef.Generator("gn_test"), PatternOutputs = ["op2"] });
            Assert.True(engine.ReadOutput("op1", px));
            Assert.Equal((0, 0, 255), Pixels.At(px, 32, 4, 4));
            Assert.True(engine.ReadOutput("op2", px));
            Assert.Equal((128, 128, 128), Pixels.At(px, 32, 4, 4));
        });
    }

    [Fact]
    public void ARawFrameReachesTheRasterCodeForCodeAndBlackoutStillWins()
    {
        const int w = 16, h = 8;
        var gray = new byte[w * h];
        for (var i = 0; i < gray.Length; i++)
            gray[i] = (byte)(i * 7 % 256);
        var raw = SourceRef.Live("lv_scan");
        var trim = ColorTrim.Neutral with { Gamma = 2, GainR = 0.5 };
        var project = Scene.Project([], [Scene.SourceOutput("op1", SourceRef.Solid("#FF0000"), w, h, trim, rotation: 180),
            Scene.SourceOutput("op2", SourceRef.Solid("#00FF00"), w, h)]);
        var sources = new FuncSources((s, _) => s == raw ? new ArrayLease(PixelLayout.Gray8, w, h, 1, ColorMatrix.Bt709, ColorRange.Full, (gray, w, w, h)) : null);
        var warp = new TestStage();
        warp.Warps["op1"] = WarpMeshBuilder.Build(new OutputWarp(true, CornerPin.None with { TopLeft = new Core.Numerics.Vec2d(0.2, 0.2) }, null));
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: false));
            var px = new byte[w * h * 4];
            engine.Render(Scene.Inputs(project, sources: sources, stage: warp) with { Raw = new RawOn("op1", raw), Freeze = true });
            Assert.True(engine.ReadOutput("op1", px));
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var g = gray[y * w + x];
                    Assert.Equal((g, g, g), Pixels.At(px, w, x, y));
                }
            // Every other output keeps the show (its first frame is drawn even while frozen).
            Assert.True(engine.ReadOutput("op2", px));
            Assert.Equal((0, 255, 0), Pixels.At(px, w, 3, 3));
            engine.Render(Scene.Inputs(project, sources: sources) with { Raw = new RawOn("op1", raw), Blackout = true });
            Assert.True(engine.ReadOutput("op1", px));
            Assert.Equal((0, 0, 0), Pixels.At(px, w, 15, 7));
            // Back to the show: a freeze holds the show's picture, and a raw frame was never the show's.
            engine.Render(Scene.Inputs(project, sources: sources) with { Raw = new RawOn("op1", raw) });
            engine.Render(Scene.Inputs(project, sources: sources) with { Freeze = true });
            Assert.True(engine.ReadOutput("op1", px));
            Assert.True(Pixels.At(px, w, 8, 4).R > 0 && Pixels.At(px, w, 8, 4).G == 0, "the show is up again");
        });
    }

    [Fact]
    public void APlaylistRoutedToAGeneratorDrawsTheGeneratorAndNoneIsBlack()
    {
        var project = Scene.Project([Scene.Gen("pattern.flat", ParamSet.Empty.With("level", 100.0))],
            [Scene.SourceOutput("op1", SourceRef.Playlist("pl_show"), 16, 8)]);
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: false));
            var px = new byte[16 * 8 * 4];
            engine.Render(Scene.Inputs(project, sources: new Router(SourceRef.Generator("gn_test"))));
            Assert.True(engine.ReadOutput("op1", px));
            Assert.Equal((255, 255, 255), Pixels.At(px, 16, 8, 4));
            engine.Render(Scene.Inputs(project, sources: new Router(SourceRef.None)));
            Assert.True(engine.ReadOutput("op1", px));
            Assert.Equal((0, 0, 0), Pixels.At(px, 16, 8, 4));
        });
    }

    [Fact]
    public void AWarpInstanceIsUploadedOnceAndANewOneOnceMore()
    {
        var project = Scene.Project([], [Scene.SourceOutput("op1", SourceRef.Solid("#336699"), 32, 18)]);
        var stage = new TestStage();
        stage.Warps["op1"] = WarpMeshBuilder.Build(new OutputWarp(true, CornerPin.None with { TopRight = new Core.Numerics.Vec2d(-0.1, 0) }, null));
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: false));
            for (var f = 0; f < 3; f++)
                engine.Render(Scene.Inputs(project, stage: stage));
            Assert.Equal(1, engine.WarpUploads);
            stage.Warps["op1"] = WarpMeshBuilder.Build(new OutputWarp(true, CornerPin.None with { TopRight = new Core.Numerics.Vec2d(-0.2, 0) }, null));
            engine.Render(Scene.Inputs(project, stage: stage));
            Assert.Equal(2, engine.WarpUploads);
            Assert.Equal(0, engine.MeshUploads);
        });
    }
}
