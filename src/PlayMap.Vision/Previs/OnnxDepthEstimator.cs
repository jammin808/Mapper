using PlayMap.Core.Model;
using PlayMap.Core.Vision;
using PlayMap.Vision.Imaging;
using PlayMap.Vision.Models;
using PlayMap.Vision.Onnx;

namespace PlayMap.Vision.Previs;

/// <summary>How a monocular depth export expects its input and what its output means. The input size is read
/// from the model when fixed.</summary>
public sealed record DepthProfile(ModelInputSpec Input, DepthScaleKind Kind)
{
    /// <summary>Depth Anything (relative): 518², stretched, ImageNet normalisation, disparity-like output.</summary>
    public static readonly DepthProfile DepthAnything = new(ModelInputSpec.ImageNet(518, 518, FitMode.Stretch), DepthScaleKind.RelativeInverse);
}

/// <summary>A monocular depth model through ONNX Runtime: the photo is fitted into the model's input, the first
/// output (N×H×W or N×1×H×W) is mapped back through the inverse fit to the photo's own grid.</summary>
public sealed class OnnxDepthEstimator : IDepthEstimator, IDisposable
{
    private readonly OnnxSession _session;
    private readonly DepthProfile _profile;
    private readonly ModelInputSpec _input;
    private readonly string _inputName;
    private readonly string _outputName;

    /// <summary>Takes ownership of the session.</summary>
    public OnnxDepthEstimator(OnnxSession session, DepthProfile profile, string id)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrEmpty(id);
        _session = session;
        _profile = profile;
        Id = id;
        var image = session.Inputs.FirstOrDefault(i => i.Dimensions.Length == 4 && i.Dim(1, 3) == 3)
            ?? throw new InvalidOperationException("The depth model has no N×3×H×W picture input.");
        if (session.Inputs.Length != 1)
            throw new InvalidOperationException("The depth model takes more than a picture.");
        _inputName = image.Name;
        _input = profile.Input.WithSize((int)image.Dim(3, profile.Input.Width), (int)image.Dim(2, profile.Input.Height));
        _outputName = session.Outputs[0].Name;
    }

    public string Id { get; }

    public ExecutionProvider Provider => _session.Provider;

    public static OnnxDepthEstimator? TryOpen(ModelStore store, string modelId, DepthProfile profile, OnnxSessionSettings settings, out string words)
    {
        ArgumentNullException.ThrowIfNull(store);
        var path = store.Installed(modelId)?.PathOf("model");
        if (path is null)
        {
            words = modelId + " is not installed";
            return null;
        }
        OnnxSession? session = null;
        try
        {
            session = OnnxSession.Open(path, settings);
            var e = new OnnxDepthEstimator(session, profile, modelId);
            session = null;
            words = "opened " + modelId + " on " + e.Provider;
            return e;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or IOException or NotSupportedException)
        {
            words = modelId + " would not open: " + ex.Message;
            return null;
        }
        finally
        {
            session?.Dispose();
        }
    }

    public async Task<DepthEstimate> EstimateAsync(ImageRgb8 image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ImageToModel map;
        IReadOnlyDictionary<string, TensorData> outputs;
        using (var tensor = new TensorBuffer(_inputName, 1, 3, _input.Height, _input.Width))
        {
            map = ModelInput.Fill(image, _input, tensor.Span);
            outputs = await _session.RunAsync([tensor], [_outputName], cancellationToken).ConfigureAwait(false);
        }
        var o = outputs[_outputName];
        var shape = o.Shape;
        if (shape.Length < 2)
            throw new InvalidOperationException("The depth output is not a map.");
        int oh = (int)shape[^2], ow = (int)shape[^1];
        // The output covers the model's input frame; the photo is its content rectangle scaled to the output grid.
        var region = new RectD(map.OffsetX * ow / map.ModelWidth, map.OffsetY * oh / map.ModelHeight,
            image.Width * map.ScaleX * ow / map.ModelWidth, image.Height * map.ScaleY * oh / map.ModelHeight);
        var values = new float[image.Width * image.Height];
        Resampler.Channel(o.Values.AsSpan(0, ow * oh), ow, oh, region, values, image.Width, image.Height);
        return new DepthEstimate(image.Width, image.Height, values, _profile.Kind);
    }

    public void Dispose() => _session.Dispose();
}
