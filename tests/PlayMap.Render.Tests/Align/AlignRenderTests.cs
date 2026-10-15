using System.Numerics;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Render.Engine;
using PlayMap.Render.Passes;

namespace PlayMap.Render.Tests.Align;

/// <summary>The measured correction on the GPU (llvmpipe), read back: a measured grid equal to a known keystone draws what the CPU
/// reference draws within one code (the grid read bilinearly, the picture sampled where it says), a node the camera did not see is
/// black; a γ of 2.2 with gains of one changes nothing, bit for bit, with or without an identity grid; a measured blend works in light
/// through the output's γ; a measured γ alone turns the content's transfer into the output's.</summary>
[Collection("gl")]
public class AlignRenderTests(GlFixture gl)
{
    private const int W = 64;
    private const int H = 36;

    private static readonly SourceRef Media = SourceRef.Media("md1");

    private sealed class Stages(Dictionary<string, MeasuredStage> stages) : IMeasuredStages
    {
        public MeasuredStage? For(string outputId) => stages.GetValueOrDefault(outputId);
    }

    private static FuncSources Still(byte[] rgba) =>
        new((_, _) => new ArrayLease(PixelLayout.Rgba8, W, H, 1, ColorMatrix.Bt709, ColorRange.Full, ((byte[])rgba.Clone(), W * 4, W, H)));

    /// <summary>A smooth picture: red across, green down, blue fixed.</summary>
    private static byte[] Gradient()
    {
        var rgba = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var i = (y * W + x) * 4;
                rgba[i] = (byte)(x * 255 / (W - 1));
                rgba[i + 1] = (byte)(y * 255 / (H - 1));
                rgba[i + 2] = 96;
                rgba[i + 3] = 255;
            }
        }
        return rgba;
    }

    private static byte[] Noise()
    {
        var rgba = new byte[W * H * 4];
        var rng = new Pcg32(9);
        for (var i = 0; i < rgba.Length; i++)
            rgba[i] = (byte)rng.NextInt(256);
        for (var i = 3; i < rgba.Length; i += 4)
            rgba[i] = 255;
        return rgba;
    }

    /// <summary>A keystone: output position (s, t) → canvas uv.</summary>
    private static Vec2d Keystone(double s, double t) =>
        new(0.08 + 0.84 * s + 0.05 * s * t - 0.03 * t, 0.06 + 0.8 * t + 0.04 * s * s);

    private static (int, int, float[]) Grid(int columns, int rows, Func<double, double, Vec2d> f, Func<int, int, bool>? valid = null)
    {
        var values = new float[columns * rows * 3];
        for (var k = 0; k < rows; k++)
        {
            for (var i = 0; i < columns; i++)
            {
                var uv = f((double)i / (columns - 1), (double)k / (rows - 1));
                var n = (k * columns + i) * 3;
                values[n] = (float)uv.X;
                values[n + 1] = (float)uv.Y;
                values[n + 2] = valid is null || valid(i, k) ? 1 : 0;
            }
        }
        return (columns, rows, values);
    }

    private byte[] Render(Output output, ISourceProvider? sources, Func<Gpu.GlDevice, MeasuredStage?> stage)
    {
        var project = Scene.Project([], [output]);
        return gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: false));
            using var measured = stage(c.Device);
            var inputs = Scene.Inputs(project, 1, sources) with
            {
                Measured = measured is null ? null : new Stages(new() { [output.Id] = measured }),
            };
            engine.Render(inputs);
            var px = new byte[output.Width * output.Height * 4];
            Assert.True(engine.ReadOutput(output.Id, px));
            Assert.Empty(c.Device.TakeErrors());
            return px;
        });
    }

    [Fact]
    public void AMeasuredGridDrawsWhereTheCpuReferenceSays()
    {
        var picture = Gradient();
        const int columns = 33, rows = 19;
        var grid = Grid(columns, rows, Keystone, (i, _) => i < columns - 3);
        var px = Render(Scene.SourceOutput("op1", Media, W, H), Still(picture),
            d => MeasuredStage.Upload(d, grid, null, Vector4.One, MeasuredStage.ContentGamma, 1));
        var compared = 0;
        var blackSeen = 0;
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var (uv, valid) = Bilinear(grid, (x + 0.5) / W * (columns - 1), (y + 0.5) / H * (rows - 1));
                var got = Pixels.At(px, W, x, y);
                if (valid < 0.5)
                {
                    Assert.Equal((0, 0, 0), got);
                    blackSeen++;
                    continue;
                }
                var want = SamplePicture(picture, uv);
                Pixels.Near(want, got, 1, $"pixel ({x}, {y})");
                compared++;
            }
        }
        Assert.True(compared > W * H / 2 && blackSeen > 0, $"{compared} compared, {blackSeen} black where the camera saw nothing");
    }

    [Fact]
    public void AGammaOfTwoPointTwoWithGainsOfOneIsBitExact()
    {
        var picture = Noise();
        var output = Scene.SourceOutput("op1", Media, W, H);
        var plain = Render(output, Still(picture), _ => null);
        var gammaOnly = Render(output, Still(picture), d => MeasuredStage.Upload(d, null, null, Vector4.One, MeasuredStage.ContentGamma, 1));
        var identityGrid = Render(output, Still(picture),
            d => MeasuredStage.Upload(d, Grid(33, 19, (s, t) => new Vec2d(s, t)), null, Vector4.One, MeasuredStage.ContentGamma, 1));
        Assert.Equal(plain, gammaOnly);
        Assert.Equal(plain, identityGrid);
        for (var i = 0; i < W * H; i++)
            Assert.Equal(picture[i * 4], plain[i * 4 + 2]);
    }

    [Fact]
    public void AMeasuredBlendWorksInLightThroughTheOutputsGamma()
    {
        var blendHalf = (4, 4, Enumerable.Range(0, 16).SelectMany(_ => new[] { 0.5f, 0f, 1f }).ToArray());
        var white = Render(Scene.SourceOutput("op1", SourceRef.Solid("#FFFFFF"), W, H), null,
            d => MeasuredStage.Upload(d, null, blendHalf, Vector4.One, 2.6f, 1));
        var want = (int)Math.Round(MeasuredStage.Reference(1f, (0.5f, 0f), 1f, 2.6f) * 255);
        Assert.InRange(want, 194, 197);
        Pixels.Near((want, want, want), Pixels.At(white, W, 30, 20), 1, "half the white in light through γ 2.6");

        var pedestal = (4, 4, Enumerable.Range(0, 16).SelectMany(_ => new[] { 1f, 0.01f, 1f }).ToArray());
        var black = Render(Scene.SourceOutput("op1", SourceRef.Solid("#000000"), W, H), null,
            d => MeasuredStage.Upload(d, null, pedestal, Vector4.One, 2.2f, 1));
        var lifted = (int)Math.Round(MeasuredStage.Reference(0f, (1f, 0.01f), 1f, 2.2f) * 255);
        Pixels.Near((lifted, lifted, lifted), Pixels.At(black, W, 30, 20), 1, "a pedestal of 1 % of the white");
        var off = Render(Scene.SourceOutput("op1", SourceRef.Solid("#000000"), W, H), null,
            d => MeasuredStage.Upload(d, null, pedestal, Vector4.One, 2.2f, 0));
        Assert.Equal((0, 0, 0), Pixels.At(off, W, 30, 20));
    }

    [Fact]
    public void AMeasuredGammaAloneTurnsTheContentsTransferIntoTheOutputs()
    {
        var px = Render(Scene.SourceOutput("op1", SourceRef.Solid("#808080"), W, H), null,
            d => MeasuredStage.Upload(d, null, null, Vector4.One, 2.6f, 1));
        var want = (int)Math.Round(MeasuredStage.Reference(128 / 255f, null, 1f, 2.6f) * 255);
        Assert.InRange(want, 140, 150);
        Pixels.Near((want, want, want), Pixels.At(px, W, 20, 10), 1, "the content's 2.2 out through γ 2.6");
    }

    /// <summary>The grid read as the shader reads it: node coordinates, bilinear, edges clamped.</summary>
    private static (Vec2d Uv, double Valid) Bilinear((int Columns, int Rows, float[] Values) g, double px, double py)
    {
        px = Math.Clamp(px, 0, g.Columns - 1);
        py = Math.Clamp(py, 0, g.Rows - 1);
        var i0 = (int)Math.Floor(px);
        var k0 = (int)Math.Floor(py);
        var i1 = Math.Min(i0 + 1, g.Columns - 1);
        var k1 = Math.Min(k0 + 1, g.Rows - 1);
        var fx = px - i0;
        var fy = py - k0;
        double At(int i, int k, int c) => g.Values[(k * g.Columns + i) * 3 + c];
        double L(int c) => (At(i0, k0, c) * (1 - fx) + At(i1, k0, c) * fx) * (1 - fy) + (At(i0, k1, c) * (1 - fx) + At(i1, k1, c) * fx) * fy;
        return (new Vec2d(L(0), L(1)), L(2));
    }

    /// <summary>The picture where uv falls, bilinear between texel centres, edges clamped (as the GPU's linear filter).</summary>
    private static (int R, int G, int B) SamplePicture(byte[] rgba, Vec2d uv)
    {
        var fx = Math.Clamp(uv.X * W - 0.5, 0, W - 1);
        var fy = Math.Clamp(uv.Y * H - 0.5, 0, H - 1);
        var x0 = (int)Math.Floor(fx);
        var y0 = (int)Math.Floor(fy);
        var x1 = Math.Min(x0 + 1, W - 1);
        var y1 = Math.Min(y0 + 1, H - 1);
        var tx = fx - x0;
        var ty = fy - y0;
        int C(int c) => (int)Math.Round(
            (rgba[(y0 * W + x0) * 4 + c] * (1 - tx) + rgba[(y0 * W + x1) * 4 + c] * tx) * (1 - ty)
            + (rgba[(y1 * W + x0) * 4 + c] * (1 - tx) + rgba[(y1 * W + x1) * 4 + c] * tx) * ty);
        return (C(0), C(1), C(2));
    }
}
