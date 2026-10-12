using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Vision;
using PlayMap.Vision.Imaging;
using PlayMap.Vision.Onnx;
using PlayMap.Vision.Segmentation;

namespace PlayMap.Vision.Tests;

/// <summary>The SAM path end to end on a real ONNX Runtime session, with a tiny encoder/decoder pair the test
/// writes itself in the SAM 2 export's shape: the encoder runs once per picture, a click comes back as a mask
/// where the decoder put it (through the exact inverse of the encoder's fit), the previous answer's logits feed
/// the next click only when they are this model's on this picture, and the prompt encodes as the decoders expect.</summary>
public class SamSegmenterTests
{
    // Model frame 32×32; the decoder's mask and previous-mask grids are 8×8 (a quarter, as SAM's 256 of 1024).
    // Its mask is a disc of radius 8 model pixels around the LAST point, plus the previous logits (+1) when the
    // flag says they are there, plus ten times the difference of the two encoder outputs' means — zero only
    // when both are fed from the same encoded picture.
    internal static byte[] Encoder() => new OnnxModelBuilder()
        .Input("image", 1, 3, 32, 32)
        .Output("image_embed", 1, 3, 4, 4)
        .Output("high_res_feats_0", 1, 3, 8, 8)
        .Node("AveragePool", ["image"], ["image_embed"], ("kernel_shape", new long[] { 8, 8 }), ("strides", new long[] { 8, 8 }))
        .Node("AveragePool", ["image"], ["high_res_feats_0"], ("kernel_shape", new long[] { 4, 4 }), ("strides", new long[] { 4, 4 }))
        .Build();

    internal static byte[] Decoder(bool withExtraInput = false)
    {
        var gx = new float[64];
        var gy = new float[64];
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
            {
                gx[y * 8 + x] = (x + 0.5f) * 4;
                gy[y * 8 + x] = (y + 0.5f) * 4;
            }
        var b = new OnnxModelBuilder()
            .Input("image_embed", 1, 3, 4, 4)
            .Input("high_res_feats_0", 1, 3, 8, 8)
            .Input("point_coords", 1, -1, 2)
            .Input("point_labels", 1, -1)
            .Input("mask_input", 1, 1, 8, 8)
            .Input("has_mask_input", 1);
        if (withExtraInput)
            b.Input("mystery", 1);
        return b
            .Output("masks", 1, 1, 8, 8)
            .Output("iou_predictions", 1, 1)
            .Constant("gx", gx, 1, 1, 8, 8)
            .Constant("gy", gy, 1, 1, 8, 8)
            .Constant("r2", [64f], 1)
            .Constant("one", [1f], 1)
            .Constant("ten", [10f], 1)
            .Constant("zero", [0f], 1)
            .Constant("iou", [0.9f], 1, 1)
            .ConstantInt64("last_s", [-1], 1)
            .ConstantInt64("last_e", [1L << 30], 1)
            .ConstantInt64("ax1", [1], 1)
            .ConstantInt64("ax2", [2], 1)
            .ConstantInt64("i0", [0], 1)
            .ConstantInt64("i1", [1], 1)
            .ConstantInt64("i2", [2], 1)
            .Node("Slice", ["point_coords", "last_s", "last_e", "ax1"], ["last"])
            .Node("Slice", ["last", "i0", "i1", "ax2"], ["px"])
            .Node("Slice", ["last", "i1", "i2", "ax2"], ["py"])
            .Node("Sub", ["gx", "px"], ["dx"])
            .Node("Sub", ["gy", "py"], ["dy"])
            .Node("Mul", ["dx", "dx"], ["dx2"])
            .Node("Mul", ["dy", "dy"], ["dy2"])
            .Node("Add", ["dx2", "dy2"], ["d2"])
            .Node("Sub", ["r2", "d2"], ["inside"])
            .Node("Div", ["inside", "r2"], ["t"])
            .Node("Tanh", ["t"], ["disc"])
            .Node("Add", ["mask_input", "one"], ["prev1"])
            .Node("Mul", ["has_mask_input", "prev1"], ["prev"])
            .Node("ReduceMean", ["image_embed"], ["m0"])
            .Node("ReduceMean", ["high_res_feats_0"], ["m1"])
            .Node("Sub", ["m0", "m1"], ["md"])
            .Node("Mul", ["md", "ten"], ["bias"])
            .Node("Add", ["disc", "prev"], ["s1"])
            .Node("Add", ["s1", "bias"], ["masks"])
            .Node("ReduceMean", ["point_labels"], ["lm"])
            .Node("Mul", ["lm", "zero"], ["l0"])
            .Node("Add", ["iou", "l0"], ["iou_predictions"])
            .Build();
    }

    private static SamSegmenter Open(string id = "tiny-sam") =>
        new(OnnxSession.Open(Encoder(), OnnxSessionSettings.CpuOnly), OnnxSession.Open(Decoder(), OnnxSessionSettings.CpuOnly),
            SamProfile.Sam2 with { Input = ModelInputSpec.ImageNet(1024, 1024, FitMode.Stretch) }, id);

    private static ImageRgb8 Picture(ulong seed)
    {
        var img = ImageRgb8.Create(64, 64);
        var rng = new Pcg32(seed);
        for (var i = 0; i < img.Data.Length; i++)
            img.Data[i] = (byte)rng.NextUInt();
        return img;
    }

    private static (double X, double Y, int Area) Stats(SegmentationMask m)
    {
        double sx = 0, sy = 0;
        var n = 0;
        for (var y = 0; y < m.Height; y++)
            for (var x = 0; x < m.Width; x++)
                if (m.Contains(x, y))
                {
                    sx += x + 0.5;
                    sy += y + 0.5;
                    n++;
                }
        return (sx / n, sy / n, n);
    }

    [Fact]
    public async Task AClickComesBackAsTheDecodersMaskInPicturePixels()
    {
        using var sam = Open();
        // The model's fixed 32×32 input overrides the profile's 1024.
        var r = await sam.SegmentAsync(Picture(1), SegmentationPrompt.Click(20, 30), CancellationToken.None);
        var m = Assert.Single(r.Masks);
        var (cx, cy, area) = Stats(m);
        Assert.InRange(cx, 18.5, 21.5);
        Assert.InRange(cy, 28.5, 31.5);
        // Radius 8 model pixels is 16 picture pixels: π·16² ≈ 804 (an 8×8 logit grid upsampled, so loosely).
        Assert.InRange(area, 640, 980);
        Assert.True(m.Contains(20, 30));
        Assert.False(m.Contains(55, 55));
        Assert.Equal(0.9, m.Score, 5);
        Assert.Equal(SegmenterCapabilities.Points | SegmenterCapabilities.Box | SegmenterCapabilities.Refinement, sam.Capabilities);
        Assert.Equal(ExecutionProvider.Cpu, sam.Providers.Encoder);
        Assert.NotNull(m.Logits);
        Assert.Equal(8, m.Logits.Width);
    }

    [Fact]
    public async Task TheEncoderRunsOncePerPicture()
    {
        using var sam = Open();
        var a = Picture(1);
        await sam.SegmentAsync(a, SegmentationPrompt.Click(10, 10), CancellationToken.None);
        await sam.SegmentAsync(a, SegmentationPrompt.Click(40, 40), CancellationToken.None);
        Assert.Equal(1, sam.EncoderRuns);
        await sam.SegmentAsync(Picture(2), SegmentationPrompt.Click(40, 40), CancellationToken.None);
        Assert.Equal(2, sam.EncoderRuns);
        // A copy of the first picture is the same picture.
        var copy = new ImageRgb8(64, 64, 64 * 3, (byte[])a.Data.Clone());
        await sam.SegmentAsync(copy, SegmentationPrompt.Click(10, 10), CancellationToken.None);
        Assert.Equal(2, sam.EncoderRuns);
    }

    [Fact]
    public async Task TheNextClickRefinesThePreviousAnswerOnlyWhenItIsThisModelsOnThisPicture()
    {
        using var sam = Open();
        var img = Picture(3);
        var first = await sam.SegmentAsync(img, SegmentationPrompt.Click(14, 14), CancellationToken.None);
        Assert.False(first.Best!.Contains(50, 50));
        var prompt = SegmentationPrompt.Click(14, 14).WithClick(new Vec2d(50, 50), positive: true, previous: first.Best);
        var refined = (await sam.SegmentAsync(img, prompt, CancellationToken.None)).Best!;
        Assert.True(refined.Contains(14, 14), "the previous logits carry the first region");
        Assert.True(refined.Contains(50, 50));

        var fresh = (await sam.SegmentAsync(img, prompt with { Previous = null }, CancellationToken.None)).Best!;
        Assert.False(fresh.Contains(14, 14));
        Assert.True(fresh.Contains(50, 50));

        // Logits from another picture are ignored, not misapplied.
        var other = (await sam.SegmentAsync(Picture(4), prompt, CancellationToken.None)).Best!;
        Assert.False(other.Contains(14, 14));
    }

    [Fact]
    public void ThePromptEncodesAsTheDecodersExpect()
    {
        var map = ImageToModel.For(64, 64, 32, 32, FitMode.Stretch);
        var prompt = SegmentationPrompt.Click(10, 20).WithClick(new Vec2d(30, 40), false, null) with { Box = new RectD(8, 8, 16, 16) };
        var (coords, labels, count) = SamSegmenter.EncodePoints(prompt, map, padWhenNoBox: true);
        Assert.Equal(4, count);
        Assert.Equal([5f, 10f, 15f, 20f, 4f, 4f, 12f, 12f], coords);
        Assert.Equal([1f, 0f, 2f, 3f], labels);
        var (padded, padLabels, n) = SamSegmenter.EncodePoints(SegmentationPrompt.Click(10, 20), map, padWhenNoBox: true);
        Assert.Equal(2, n);
        Assert.Equal([5f, 10f, 0f, 0f], padded);
        Assert.Equal([1f, -1f], padLabels);
    }

    [Fact]
    public async Task APhraseOrNothingIsRefusedAndAnUnknownDecoderInputFailsAtOpen()
    {
        using var sam = Open();
        var r = await sam.SegmentAsync(Picture(1), SegmentationPrompt.ForText("windows"), CancellationToken.None);
        Assert.Null(r.Best);
        Assert.Contains("phrase", r.Words, StringComparison.Ordinal);
        using var enc = OnnxSession.Open(Encoder(), OnnxSessionSettings.CpuOnly);
        using var dec = OnnxSession.Open(Decoder(withExtraInput: true), OnnxSessionSettings.CpuOnly);
        var ex = Assert.Throws<InvalidOperationException>(() => new SamSegmenter(enc, dec, SamProfile.Sam2, "x"));
        Assert.Contains("mystery", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledClickThrows()
    {
        using var sam = Open();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sam.SegmentAsync(Picture(1), SegmentationPrompt.Click(5, 5), cts.Token));
    }
}
