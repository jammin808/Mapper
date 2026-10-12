using PlayMap.Core.Vision;
using PlayMap.Vision.Imaging;
using PlayMap.Vision.Onnx;
using PlayMap.Vision.Previs;

namespace PlayMap.Vision.Tests;

/// <summary>Previs never fails: with no model, or a model that throws, a flat plane at the given distance; with
/// a model, a grid mesh whose median depth is that distance, shaped by the map and torn at silhouettes. The ONNX
/// depth path runs a real session on a model the test writes.</summary>
public class DepthPrevisTests
{
    private static ImageRgb8 Ramp(int w, int h)
    {
        var img = ImageRgb8.Create(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var v = (byte)(40 + 200 * x / (w - 1));
                img.MutableRow(y)[x * 3] = v;
                img.MutableRow(y)[x * 3 + 1] = v;
                img.MutableRow(y)[x * 3 + 2] = v;
            }
        return img;
    }

    private sealed class FakeDepth(Func<int, int, int, int, float> value, DepthScaleKind kind, bool fail = false) : IDepthEstimator
    {
        public string Id => "fake";

        public Task<DepthEstimate> EstimateAsync(ImageRgb8 image, CancellationToken cancellationToken)
        {
            if (fail)
                throw new InvalidOperationException("out of video memory");
            var v = new float[image.Width * image.Height];
            for (var y = 0; y < image.Height; y++)
                for (var x = 0; x < image.Width; x++)
                    v[y * image.Width + x] = value(x, y, image.Width, image.Height);
            return Task.FromResult(new DepthEstimate(image.Width, image.Height, v, kind));
        }
    }

    [Fact]
    public async Task WithNoModelPrevisIsAFlatPlaneAtTheDistance()
    {
        var r = await DepthPrevis.BuildAsync(Ramp(160, 90), null, PrevisOptions.Default with { DistanceMetres = 4, GridColumns = 32 }, CancellationToken.None);
        Assert.False(r.FromModel);
        Assert.Contains("flat plane at 4 m", r.Words, StringComparison.Ordinal);
        Assert.Equal(32 * 18 * 2, r.Mesh.TriangleCount);
        for (var v = 0; v < r.Mesh.VertexCount; v++)
            Assert.Equal(-4, r.Mesh.Position(v).Z, 5);
        // The plane fills the camera's view: its corners project to the photo's corners.
        var (min, max) = r.Mesh.Bounds();
        var halfWidth = 4 * Math.Tan(30 * Math.PI / 180);
        Assert.Equal(-halfWidth, min.X, 3);
        Assert.Equal(halfWidth, max.X, 3);
        Assert.True(r.Mesh.HasNormals && r.Mesh.HasUvs);
    }

    [Fact]
    public async Task AModelThatFailsStillGivesAPlaneAndSaysWhy()
    {
        var r = await DepthPrevis.BuildAsync(Ramp(64, 48), new FakeDepth((_, _, _, _) => 1, DepthScaleKind.Relative, fail: true), null, CancellationToken.None);
        Assert.False(r.FromModel);
        Assert.Contains("out of video memory", r.Words, StringComparison.Ordinal);
        Assert.True(r.Mesh.TriangleCount > 0);
    }

    [Fact]
    public async Task ARelativeMapIsScaledSoItsMedianSitsAtTheDistance()
    {
        // Disparity rising to the right: the right is nearer.
        var depth = new FakeDepth((x, _, w, _) => 1f + x / (float)w, DepthScaleKind.RelativeInverse);
        var r = await DepthPrevis.BuildAsync(Ramp(128, 64), depth, PrevisOptions.Default with { DistanceMetres = 5, GridColumns = 32 }, CancellationToken.None);
        Assert.True(r.FromModel);
        Assert.Equal(32 * 16 * 2, r.Mesh.TriangleCount);
        var zs = Enumerable.Range(0, r.Mesh.VertexCount).Select(v => -r.Mesh.Position(v).Z).Order().ToArray();
        Assert.InRange(zs[zs.Length / 2], 4.75, 5.25);
        Assert.True(-r.Mesh.Position(8 * 33 + 1).Z > -r.Mesh.Position(8 * 33 + 31).Z, "the left is farther");
    }

    [Fact]
    public async Task ADepthStepTearsTheMeshAtTheSilhouette()
    {
        // A near box on the right half in front of a far wall: one column of cells spans the step.
        var depth = new FakeDepth((x, _, w, _) => x < w / 2 ? 1f : 4f, DepthScaleKind.RelativeInverse);
        var r = await DepthPrevis.BuildAsync(Ramp(128, 64), depth, PrevisOptions.Default with { GridColumns = 32 }, CancellationToken.None);
        Assert.Equal(31 * 16 * 2, r.Mesh.TriangleCount);
    }

    /// <summary>A depth model in the Depth Anything export's shape: N×3×H×W in, N×H×W out (here the channel
    /// mean, so brighter reads nearer).</summary>
    private static byte[] DepthModel() => new OnnxModelBuilder()
        .Input("pixel_values", 1, 3, 16, 16)
        .Output("predicted_depth", 1, 16, 16)
        .Node("ReduceMean", ["pixel_values"], ["predicted_depth"], ("axes", new long[] { 1 }), ("keepdims", 0))
        .Build();

    [Fact]
    public async Task TheOnnxDepthPathMapsTheModelsOutputBackToThePhoto()
    {
        using var estimator = new OnnxDepthEstimator(OnnxSession.Open(DepthModel(), OnnxSessionSettings.CpuOnly),
            DepthProfile.DepthAnything with { Input = ModelInputSpec.ImageNet(518, 518, FitMode.Stretch) with { Mean = [0, 0, 0], Std = [1, 1, 1] } }, "tiny-depth");
        var photo = Ramp(64, 32);
        var d = await estimator.EstimateAsync(photo, CancellationToken.None);
        Assert.Equal(64, d.Width);
        Assert.Equal(32, d.Height);
        Assert.Equal(DepthScaleKind.RelativeInverse, d.Kind);
        // Normalisation off: the output is brightness / 255, resampled back to the photo's grid.
        Assert.InRange(d.Values[16 * 64 + 32], 0.5, 0.7);
        Assert.True(d.Values[16 * 64 + 60] > d.Values[16 * 64 + 4]);

        var previs = await DepthPrevis.BuildAsync(photo, estimator, PrevisOptions.Default with { GridColumns = 16, TearRatio = 10 }, CancellationToken.None);
        Assert.True(previs.FromModel);
        Assert.Contains("tiny-depth", previs.Words, StringComparison.Ordinal);
        // Brighter is nearer: the right edge of the mesh is closer to the camera than the left.
        Assert.True(-previs.Mesh.Position(4 * 17 + 16).Z < -previs.Mesh.Position(4 * 17).Z);
    }
}
