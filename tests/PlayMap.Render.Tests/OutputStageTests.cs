using PlayMap.Core.Mapping;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Render.Engine;

namespace PlayMap.Render.Tests;

/// <summary>The output stage: an identity warp gives back the picture's own codes, a warp moves a marker
/// where its geometry says, the blend map multiplies the signal and the pedestal adds to it, the trims change
/// values as their formula says, masks and rotation land where they should, and blackout is exact black.</summary>
[Collection("gpu")]
public class OutputStageTests(BackendFixture backends)
{
    private const int W = 64;
    private const int H = 36;

    private static readonly SourceRef Media = SourceRef.Media("md1");

    /// <summary>A still RGBA picture as a source (generation 1 forever).</summary>
    private static FuncSources Still(byte[] rgba) =>
        new((_, _) => new ArrayLease(PixelLayout.Rgba8, W, H, 1, ColorMatrix.Bt709, ColorRange.Full, ((byte[])rgba.Clone(), W * 4, W, H)));

    private static byte[] Noise()
    {
        var rgba = new byte[W * H * 4];
        var rng = new Pcg32(42);
        for (var i = 0; i < rgba.Length; i++)
            rgba[i] = (byte)rng.NextInt(256);
        for (var i = 3; i < rgba.Length; i += 4)
            rgba[i] = 255;
        return rgba;
    }

    private static byte[] Marker(int x0, int y0, int size = 4)
    {
        var rgba = new byte[W * H * 4];
        for (var i = 3; i < rgba.Length; i += 4)
            rgba[i] = 255;
        for (var y = y0; y < y0 + size; y++)
            for (var x = x0; x < x0 + size; x++)
                for (var c = 0; c < 3; c++)
                    rgba[(y * W + x) * 4 + c] = 255;
        return rgba;
    }

    private byte[] RenderOutput(Output output, ISourceProvider? sources = null, TestStage? stage = null, bool blackout = false)
    {
        var project = Scene.Project([], [output]);
        return backends.Run(leg =>
        {
            using var engine = new RenderEngine(leg.Device, new RenderEngineOptions(GpuTiming: false));
            var inputs = Scene.Inputs(project, 1, sources, stage) with { Blackout = blackout };
            engine.Render(inputs);
            var px = new byte[output.Width * output.Height * 4];
            Assert.True(engine.ReadOutput(output.Id, px));
            Assert.Empty(leg.TakeErrors());
            return px;
        });
    }

    [Fact]
    public void AnIdentityWarpGivesBackThePicturesOwnCodes()
    {
        var rgba = Noise();
        var px = RenderOutput(Scene.SourceOutput("op1", Media, W, H), Still(rgba));
        for (var i = 0; i < W * H; i++)
        {
            Assert.Equal(rgba[i * 4], px[i * 4 + 2]);
            Assert.Equal(rgba[i * 4 + 1], px[i * 4 + 1]);
            Assert.Equal(rgba[i * 4 + 2], px[i * 4]);
        }
    }

    [Fact]
    public void AWarpMovesAMarkerWhereItsGeometrySays()
    {
        var stage = new TestStage();
        // The picture squeezed to the middle half of the raster.
        stage.Warps["op1"] = new WarpMeshGeometry([0.25f, 0.25f, 0.75f, 0.25f, 0.75f, 0.75f, 0.25f, 0.75f], [0, 0, 1, 0, 1, 1, 0, 1], [0, 1, 2, 0, 2, 3], 2, 2);
        var px = RenderOutput(Scene.SourceOutput("op1", Media, W, H), Still(Marker(16, 8)), stage);
        // Marker centre (18, 10) of 64×36 → normalised (0.28, 0.28) → 0.25 + 0.5 × that → pixel (25, 14).
        Assert.True(Pixels.At(px, W, 25, 14).R > 200, "the marker moved");
        Assert.Equal((0, 0, 0), Pixels.At(px, W, 18, 10));
        Assert.Equal((0, 0, 0), Pixels.At(px, W, 2, 2));
    }

    [Fact]
    public void ABlendWeightOfAHalfHalvesAWhiteFieldInSignal()
    {
        var stage = new TestStage();
        stage.Blends["op1"] = new BlendMap("op1", 8, 8, Enumerable.Repeat(0.5f, 64).ToArray(), new float[64]);
        var px = RenderOutput(Scene.SourceOutput("op1", SourceRef.Solid("#FFFFFF"), W, H), stage: stage);
        Pixels.Near((128, 128, 128), Pixels.At(px, W, 30, 20), 1, "half weight");
    }

    [Fact]
    public void TheBlackPedestalAddsLightToBlack()
    {
        var stage = new TestStage();
        stage.Blends["op1"] = new BlendMap("op1", 4, 4, Enumerable.Repeat(1f, 16).ToArray(), Enumerable.Repeat(0.1f, 16).ToArray());
        var px = RenderOutput(Scene.SourceOutput("op1", SourceRef.Solid("#000000"), W, H), stage: stage);
        Pixels.Near((26, 26, 26), Pixels.At(px, W, 30, 20), 1, "pedestal");
    }

    [Fact]
    public void ColourTrimGainsAndGammaChangeValuesAsTheirFormulaSays()
    {
        var gain = RenderOutput(Scene.SourceOutput("op1", SourceRef.Solid("#FFFFFF"), W, H, ColorTrim.Neutral with { GainR = 0.5 }));
        Pixels.Near((128, 255, 255), Pixels.At(gain, W, 10, 10), 1, "red gain");
        var gamma = RenderOutput(Scene.SourceOutput("op1", SourceRef.Solid("#808080"), W, H, ColorTrim.Neutral with { Gamma = 2 }));
        var expected = (int)Math.Round(Math.Pow(128 / 255.0, 2) * 255);
        Pixels.Near((expected, expected, expected), Pixels.At(gamma, W, 10, 10), 1, "gamma 2");
        var bright = RenderOutput(Scene.SourceOutput("op1", SourceRef.Solid("#FFFFFF"), W, H, ColorTrim.Neutral with { Brightness = 0.25 }));
        Pixels.Near((64, 64, 64), Pixels.At(bright, W, 10, 10), 1, "brightness");
        var lift = RenderOutput(Scene.SourceOutput("op1", SourceRef.Solid("#000000"), W, H, ColorTrim.Neutral with { Lift = 0.2 }));
        Pixels.Near((51, 51, 51), Pixels.At(lift, W, 10, 10), 1, "lift");
    }

    [Fact]
    public void BlackoutIsExactBlackWhateverTheTrims()
    {
        var px = RenderOutput(Scene.SourceOutput("op1", SourceRef.Solid("#FFFFFF"), W, H, ColorTrim.Neutral with { Lift = 0.5 }), blackout: true);
        Assert.All(px.Where((_, i) => i % 4 != 3), b => Assert.Equal(0, b));
    }

    [Fact]
    public void AnOutputMaskDarkensWhatItDoesNotCover()
    {
        var stage = new TestStage();
        var cover = new float[16 * 4];
        for (var y = 0; y < 4; y++)
            for (var x = 8; x < 16; x++)
                cover[y * 16 + x] = 1;
        stage.Masks["op1"] = new CoverageMap(16, 4, cover);
        var px = RenderOutput(Scene.SourceOutput("op1", SourceRef.Solid("#FFFFFF"), W, H), stage: stage);
        Assert.Equal((0, 0, 0), Pixels.At(px, W, 4, 18));
        Assert.Equal((255, 255, 255), Pixels.At(px, W, 60, 18));
    }

    [Fact]
    public void AHalfTurnPutsTheTopLeftMarkerBottomRight()
    {
        var px = RenderOutput(Scene.SourceOutput("op1", Media, W, H, rotation: 180), Still(Marker(0, 0)));
        Assert.Equal((255, 255, 255), Pixels.At(px, W, W - 2, H - 2));
        Assert.Equal((0, 0, 0), Pixels.At(px, W, 1, 1));
    }

    [Fact]
    public void ALensLookupResamplesThePicture()
    {
        var stage = new TestStage();
        // A mirror: each output pixel samples the picture's opposite column.
        var uv = new float[W * H * 2];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                uv[(y * W + x) * 2] = 1 - (x + 0.5f) / W;
                uv[(y * W + x) * 2 + 1] = (y + 0.5f) / H;
            }
        }
        stage.Lenses["op1"] = new LensLookup(W, H, uv);
        var px = RenderOutput(Scene.SourceOutput("op1", Media, W, H), Still(Marker(0, 0)), stage);
        Assert.Equal((255, 255, 255), Pixels.At(px, W, W - 2, 1));
        Assert.Equal((0, 0, 0), Pixels.At(px, W, 1, 1));
    }
}
