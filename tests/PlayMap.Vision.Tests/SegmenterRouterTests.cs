using PlayMap.Core.Numerics;
using PlayMap.Core.Vision;
using PlayMap.Vision.Segmentation;

namespace PlayMap.Vision.Tests;

/// <summary>The router always answers: a model that fits takes the prompt and its mask is snapped to the scan's
/// edges (logits kept), a model that throws or finds nothing hands over, a prompt no model can take goes to the
/// classical path, and the words say who answered.</summary>
public class SegmenterRouterTests
{
    private static readonly BoxScene Scene = BoxScene.Build();

    /// <summary>A stand-in model: returns the box top shifted a few pixels (as a model's edge wanders), throws,
    /// or finds nothing.</summary>
    private sealed class FakeModel(string id, SegmenterCapabilities caps, string mode) : ISegmenter
    {
        public string Id => id;
        public SegmenterCapabilities Capabilities => caps;
        public int Calls { get; private set; }

        public Task<SegmentationResult> SegmentAsync(VisionFrame frame, SegmentationPrompt prompt, CancellationToken cancellationToken)
        {
            Calls++;
            if (mode == "throw")
                throw new InvalidOperationException("device lost");
            if (mode == "empty")
                return Task.FromResult(SegmentationResult.Refused("no instance of that"));
            int w = frame.Image.Width, h = frame.Image.Height;
            var top = Scene.FaceMask(BoxScene.Top);
            var shifted = new bool[w * h];
            for (var y = 0; y < h; y++)
                for (var x = 3; x < w; x++)
                    shifted[y * w + x] = top[y * w + x - 3];
            var logits = new MaskLogits(2, 2, [1, 2, 3, 4], id + ":k");
            return Task.FromResult(new SegmentationResult([new SegmentationMask(w, h, MaskOps.ToCoverage(shifted), 0.8, prompt.Text ?? "", logits)], ""));
        }
    }

    private static SegmentationPrompt ClickTop()
    {
        var c = Scene.Project(new Vec3d(0, 0.3, 0));
        return SegmentationPrompt.Click(c.X, c.Y);
    }

    [Fact]
    public async Task AFittingModelAnswersAndItsMaskIsSnappedToTheScan()
    {
        var model = new FakeModel("sam", SegmenterCapabilities.Points | SegmenterCapabilities.Box | SegmenterCapabilities.Refinement, "ok");
        var router = new SegmenterRouter([model], new ClassicalSegmenter(), DepthSnapOptions.Default);
        var r = await router.SegmentAsync(Scene.Frame, ClickTop(), CancellationToken.None);
        Assert.StartsWith("by sam, snapped", r.Words, StringComparison.Ordinal);
        Assert.InRange(MaskOps.IoU(MaskOps.FromCoverage(r.Best!.Coverage), Scene.FaceMask(BoxScene.Top)), 0.95, 1.0);
        Assert.Equal("sam:k", r.Best.Logits!.SourceKey);
        Assert.True(router.Capabilities.HasFlag(SegmenterCapabilities.Refinement));
    }

    [Fact]
    public async Task AFailingOrEmptyModelHandsOverToTheClassicalPath()
    {
        var broken = new FakeModel("sam", SegmenterCapabilities.Points, "throw");
        var empty = new FakeModel("other", SegmenterCapabilities.Points, "empty");
        var router = new SegmenterRouter([broken, empty], new ClassicalSegmenter(), DepthSnapOptions.Default);
        var r = await router.SegmentAsync(Scene.Frame, ClickTop(), CancellationToken.None);
        Assert.Equal(1, broken.Calls);
        Assert.Equal(1, empty.Calls);
        Assert.StartsWith("by classical", r.Words, StringComparison.Ordinal);
        Assert.Contains("sam failed: device lost", r.Words, StringComparison.Ordinal);
        Assert.Contains("other: no instance of that", r.Words, StringComparison.Ordinal);
        Assert.InRange(MaskOps.IoU(MaskOps.FromCoverage(r.Best!.Coverage), Scene.FaceMask(BoxScene.Top)), 0.95, 1.0);
    }

    [Fact]
    public async Task APhraseGoesOnlyToATextModelAndOtherwiseIsRefusedWithTheReason()
    {
        var points = new FakeModel("sam", SegmenterCapabilities.Points, "ok");
        var router = new SegmenterRouter([points], new ClassicalSegmenter(), null);
        var refused = await router.SegmentAsync(Scene.Frame, SegmentationPrompt.ForText("box"), CancellationToken.None);
        Assert.Equal(0, points.Calls);
        Assert.Null(refused.Best);
        Assert.Contains("phrase", refused.Words, StringComparison.Ordinal);

        var text = new FakeModel("sam3", SegmenterCapabilities.Text, "ok");
        var withText = new SegmenterRouter([points, text], new ClassicalSegmenter(), null);
        var r = await withText.SegmentAsync(Scene.Frame, SegmentationPrompt.ForText("box"), CancellationToken.None);
        Assert.Equal("by sam3", r.Words);
        Assert.Equal("box", r.Best!.Label);
        Assert.True(SegmenterRouter.Fits(SegmenterCapabilities.Points | SegmenterCapabilities.Box, SegmentationPrompt.Click(1, 1) with { Box = new PlayMap.Core.Model.RectD(0, 0, 4, 4) }));
        Assert.False(SegmenterRouter.Fits(SegmenterCapabilities.Points, SegmentationPrompt.ForBox(new PlayMap.Core.Model.RectD(0, 0, 4, 4))));
    }
}
