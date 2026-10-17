using System.Security.Cryptography;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Vision;
using PlayMap.Vision.Imaging;
using PlayMap.Vision.Models;
using PlayMap.Vision.Onnx;

namespace PlayMap.Vision.Segmentation;

/// <summary>How a SAM-class export expects its inputs. The encoder's input size is read from the model when it is
/// fixed; these values stand in when it is symbolic.</summary>
public sealed record SamProfile(ModelInputSpec Input, bool PadPointWhenNoBox, int MaskInputDivisor = 4, float MaskThreshold = 0f)
{
    /// <summary>SAM 2 / 2.1: the picture stretched to 1024², ImageNet normalisation, no padding point.</summary>
    public static readonly SamProfile Sam2 = new(ModelInputSpec.ImageNet(1024, 1024, FitMode.Stretch), false);

    /// <summary>SAM 1: the long side to 1024, padded right and bottom, a padding point when there is no box.</summary>
    public static readonly SamProfile Sam1 = new(ModelInputSpec.ImageNet(1024, 1024, FitMode.LetterboxTopLeft), true);
}

/// <summary>Clicks and boxes to masks with a SAM-class encoder + prompt decoder pair. The encoder runs once per
/// picture (its outputs cached by the picture's SHA-256, the last two pictures kept); each click runs only the
/// decoder. Decoder inputs are bound by name: any input an encoder output shares its name with gets that output
/// (image embeddings, high-resolution features), plus the prompt's points and labels (1 positive, 0 negative,
/// 2/3 box corners, −1 padding), the previous mask's low-resolution logits with its flag when the export keeps
/// them (refinement: the next click improves the last answer rather than starting again), and the original size.
/// An input it does not know is refused when the model is opened, not guessed at. Masks come back best first,
/// upsampled through the exact inverse of the encoder's fit, with soft edges from the logits.</summary>
public sealed class SamSegmenter : ISegmenter, IDisposable
{
    private static readonly string[] EmbeddingNames = ["image_embed", "image_embeddings", "embeddings"];

    private readonly OnnxSession _encoder;
    private readonly OnnxSession _decoder;
    private readonly SamProfile _profile;
    private readonly ModelInputSpec _input;
    private readonly string _encoderInput;
    private readonly Dictionary<string, string> _bindings = new(StringComparer.Ordinal);
    private readonly string _masksOutput;
    private readonly string? _scoresOutput;
    private readonly string? _lowResOutput;
    private readonly int _maskInputWidth;
    private readonly int _maskInputHeight;
    private readonly SemaphoreSlim _encodeGate = new(1, 1);
    private readonly LinkedList<(string Key, IReadOnlyDictionary<string, TensorData> Outputs)> _cache = new();
    private int _encoderRuns;

    /// <summary>Takes ownership of both sessions.</summary>
    public SamSegmenter(OnnxSession encoder, OnnxSession decoder, SamProfile profile, string id)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrEmpty(id);
        _encoder = encoder;
        _decoder = decoder;
        _profile = profile;
        Id = id;

        var image = encoder.Inputs.FirstOrDefault(i => i.Dimensions.Length == 4 && i.Dim(1, 3) == 3)
            ?? throw new InvalidOperationException("The encoder has no N×3×H×W picture input.");
        _encoderInput = image.Name;
        _input = profile.Input.WithSize((int)image.Dim(3, profile.Input.Width), (int)image.Dim(2, profile.Input.Height));

        foreach (var input in decoder.Inputs)
        {
            var name = input.Name;
            if (encoder.HasOutput(name))
                _bindings[name] = name;
            else if (EmbeddingNames.Contains(name) && encoder.Outputs.FirstOrDefault(o => EmbeddingNames.Contains(o.Name)) is { } emb)
                _bindings[name] = emb.Name;
            else if (name is not ("point_coords" or "point_labels" or "mask_input" or "has_mask_input" or "orig_im_size"))
                throw new InvalidOperationException("The decoder input " + name + " is not one PlayMap knows how to feed.");
        }
        if (!decoder.HasInput("point_coords") || !decoder.HasInput("point_labels"))
            throw new InvalidOperationException("The decoder takes no points.");
        if (_bindings.Count == 0)
            throw new InvalidOperationException("No decoder input takes an encoder output.");

        _masksOutput = decoder.Outputs.FirstOrDefault(o => o.Name == "masks")?.Name
            ?? decoder.Outputs.FirstOrDefault(o => o.Dimensions.Length == 4)?.Name
            ?? throw new InvalidOperationException("The decoder has no mask output.");
        _scoresOutput = decoder.Outputs.FirstOrDefault(o => o.Name is "iou_predictions" or "scores" or "iou_scores")?.Name
            ?? decoder.Outputs.FirstOrDefault(o => o.Dimensions.Length == 2)?.Name;
        _lowResOutput = decoder.Outputs.FirstOrDefault(o => o.Name == "low_res_masks")?.Name;
        var mi = decoder.Input("mask_input");
        _maskInputWidth = (int)(mi?.Dim(3, _input.Width / profile.MaskInputDivisor) ?? _input.Width / profile.MaskInputDivisor);
        _maskInputHeight = (int)(mi?.Dim(2, _input.Height / profile.MaskInputDivisor) ?? _input.Height / profile.MaskInputDivisor);
        Capabilities = SegmenterCapabilities.Points | SegmenterCapabilities.Box |
            (decoder.HasInput("mask_input") ? SegmenterCapabilities.Refinement : SegmenterCapabilities.None);
    }

    public string Id { get; }
    public SegmenterCapabilities Capabilities { get; }

    /// <summary>How many times the encoder has run (once per new picture).</summary>
    public int EncoderRuns => Volatile.Read(ref _encoderRuns);

    /// <summary>The execution provider each half runs on.</summary>
    public (ExecutionProvider Encoder, ExecutionProvider Decoder) Providers => (_encoder.Provider, _decoder.Provider);

    /// <summary>Opens an encoder and decoder pair from files.</summary>
    public static SamSegmenter Open(string encoderPath, string decoderPath, SamProfile profile, OnnxSessionSettings settings, string id)
    {
        var encoder = OnnxSession.Open(encoderPath, settings);
        try
        {
            var decoder = OnnxSession.Open(decoderPath, settings);
            try
            {
                return new SamSegmenter(encoder, decoder, profile, id);
            }
            catch
            {
                decoder.Dispose();
                throw;
            }
        }
        catch
        {
            encoder.Dispose();
            throw;
        }
    }

    /// <summary>Opens an installed catalogue model (roles "encoder" and "decoder"); null with the reason when it is
    /// not installed or will not open — the classical segmenter stands in.</summary>
    public static SamSegmenter? TryOpen(ModelStore store, string modelId, SamProfile profile, OnnxSessionSettings settings, out string words)
    {
        ArgumentNullException.ThrowIfNull(store);
        var installed = store.Installed(modelId);
        if (installed?.PathOf("encoder") is not { } enc || installed.PathOf("decoder") is not { } dec)
        {
            words = modelId + " is not installed";
            return null;
        }
        try
        {
            var s = Open(enc, dec, profile, settings, modelId);
            words = "opened " + modelId + " on " + s.Providers.Encoder;
            return s;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or IOException or NotSupportedException)
        {
            words = modelId + " would not open: " + ex.Message;
            return null;
        }
    }

    /// <summary>Runs on the compute threads from the start: hashing and fitting the picture are pixel work too.</summary>
    public Task<SegmentationResult> SegmentAsync(VisionFrame frame, SegmentationPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(prompt);
        return SegmentCoreAsync(frame, prompt, cancellationToken);
    }

    private async Task<SegmentationResult> SegmentCoreAsync(VisionFrame frame, SegmentationPrompt prompt, CancellationToken cancellationToken)
    {
        await PlayMap.Core.Threading.Compute.Hop();
        if (prompt.PositivePoints.IsEmpty && !prompt.HasBox)
            return SegmentationResult.Refused(prompt.HasText
                ? Id + " cannot read a phrase; click on the surface, or install a text-prompt model"
                : "a click or a box on what to select is needed");
        var image = frame.Image;
        var key = Id + ":" + PictureKey(image);
        var embedding = await EncodeAsync(image, key, cancellationToken).ConfigureAwait(false);
        await PlayMap.Core.Threading.Compute.Hop();
        var map = embedding.Map;

        var (coords, labels, count) = EncodePoints(prompt, map, _profile.PadPointWhenNoBox);
        var inputs = new List<TensorBuffer>();
        try
        {
            foreach (var (decoderInput, encoderOutput) in _bindings)
            {
                var t = embedding.Outputs[encoderOutput];
                var buffer = new TensorBuffer(decoderInput, [.. t.Shape]);
                inputs.Add(buffer);
                t.Values.AsSpan(0, buffer.Length).CopyTo(buffer.Span);
            }
            var pc = new TensorBuffer("point_coords", 1, count, 2);
            inputs.Add(pc);
            coords.AsSpan().CopyTo(pc.Span);
            var pl = new TensorBuffer("point_labels", 1, count);
            inputs.Add(pl);
            labels.AsSpan().CopyTo(pl.Span);
            if (_decoder.HasInput("mask_input"))
            {
                var mi = new TensorBuffer("mask_input", 1, 1, _maskInputHeight, _maskInputWidth);
                inputs.Add(mi);
                var has = PreviousLogits(prompt.Previous, key, mi.Span);
                if (_decoder.HasInput("has_mask_input"))
                {
                    var hm = TensorBuffer.Scalar("has_mask_input", has ? 1 : 0, 1);
                    inputs.Add(hm);
                }
            }
            if (_decoder.HasInput("orig_im_size"))
            {
                var os = new TensorBuffer("orig_im_size", 2);
                inputs.Add(os);
                os.Span[0] = image.Height;
                os.Span[1] = image.Width;
            }
            var wanted = new List<string> { _masksOutput };
            if (_scoresOutput is not null)
                wanted.Add(_scoresOutput);
            if (_lowResOutput is not null)
                wanted.Add(_lowResOutput);
            var outputs = await _decoder.RunAsync(inputs, wanted, cancellationToken).ConfigureAwait(false);
            await PlayMap.Core.Threading.Compute.Hop();
            return Decode(outputs, image, map, key, prompt.Text ?? "");
        }
        finally
        {
            foreach (var t in inputs)
                t.Dispose();
        }
    }

    private sealed record Embedding(IReadOnlyDictionary<string, TensorData> Outputs, ImageToModel Map);

    private async Task<Embedding> EncodeAsync(ImageRgb8 image, string key, CancellationToken ct)
    {
        await _encodeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PlayMap.Core.Threading.Compute.Hop();
            var map = ImageToModel.For(image.Width, image.Height, _input.Width, _input.Height, _input.Fit);
            for (var node = _cache.First; node is not null; node = node.Next)
            {
                if (node.Value.Key != key)
                    continue;
                _cache.Remove(node);
                _cache.AddFirst(node);
                return new Embedding(node.Value.Outputs, Snapped(image, map));
            }
            using var tensor = new TensorBuffer(_encoderInput, 1, 3, _input.Height, _input.Width);
            var filled = ModelInput.Fill(image, _input, tensor.Span);
            var outputs = await _encoder.RunAsync([tensor], [.. _encoder.Outputs.Select(o => o.Name)], ct).ConfigureAwait(false);
            Interlocked.Increment(ref _encoderRuns);
            _cache.AddFirst((key, outputs));
            while (_cache.Count > 2)
                _cache.RemoveLast();
            return new Embedding(outputs, filled);
        }
        finally
        {
            _encodeGate.Release();
        }
    }

    /// <summary>The map the fill would produce (its content rectangle snapped to whole model pixels).</summary>
    private ImageToModel Snapped(ImageRgb8 image, ImageToModel map)
    {
        var x0 = (int)Math.Round(map.OffsetX);
        var y0 = (int)Math.Round(map.OffsetY);
        var w = Math.Clamp((int)Math.Round(image.Width * map.ScaleX), 1, _input.Width - x0);
        var h = Math.Clamp((int)Math.Round(image.Height * map.ScaleY), 1, _input.Height - y0);
        return new ImageToModel(w / (double)image.Width, h / (double)image.Height, x0, y0, _input.Width, _input.Height, image.Width, image.Height);
    }

    /// <summary>The picture's identity: SHA-256 over its size and pixel rows (row padding excluded).</summary>
    internal static string PictureKey(ImageRgb8 image)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> size = stackalloc byte[8];
        BitConverter.TryWriteBytes(size, image.Width);
        BitConverter.TryWriteBytes(size[4..], image.Height);
        hash.AppendData(size);
        for (var y = 0; y < image.Height; y++)
            hash.AppendData(image.Row(y));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>The prompt as decoder points in model pixels: positives (1), negatives (0), the box's corners
    /// (2 top-left, 3 bottom-right), and a padding point (−1) when the export wants one and there is no box.</summary>
    internal static (float[] Coords, float[] Labels, int Count) EncodePoints(SegmentationPrompt prompt, ImageToModel map, bool padWhenNoBox)
    {
        var pts = new List<(Vec2d P, float L)>();
        foreach (var p in prompt.PositivePoints)
            pts.Add((map.ToModel(p), 1));
        foreach (var p in prompt.NegativePoints)
            pts.Add((map.ToModel(p), 0));
        if (prompt.HasBox)
        {
            var b = prompt.Box!.Value;
            pts.Add((map.ToModel(new Vec2d(b.X, b.Y)), 2));
            pts.Add((map.ToModel(new Vec2d(b.Right, b.Bottom)), 3));
        }
        else if (padWhenNoBox)
            pts.Add((Vec2d.Zero, -1));
        var coords = new float[pts.Count * 2];
        var labels = new float[pts.Count];
        for (var i = 0; i < pts.Count; i++)
        {
            coords[i * 2] = (float)pts[i].P.X;
            coords[i * 2 + 1] = (float)pts[i].P.Y;
            labels[i] = pts[i].L;
        }
        return (coords, labels, pts.Count);
    }

    /// <summary>Fills the decoder's previous-mask input from the last answer when it is this model's, on this
    /// picture; true when it did (otherwise the input stays zero and its flag says so).</summary>
    private bool PreviousLogits(SegmentationMask? previous, string key, Span<float> dst)
    {
        if (previous?.Logits is not { } logits || logits.SourceKey != key)
            return false;
        if (logits.Width == _maskInputWidth && logits.Height == _maskInputHeight)
            logits.Values.AsSpan(0, dst.Length).CopyTo(dst);
        else
            Resampler.Channel(logits.Values, logits.Width, logits.Height, new RectD(0, 0, logits.Width, logits.Height), dst, _maskInputWidth, _maskInputHeight);
        return true;
    }

    private SegmentationResult Decode(IReadOnlyDictionary<string, TensorData> outputs, ImageRgb8 image, ImageToModel map, string key, string label)
    {
        var masks = outputs[_masksOutput];
        if (masks.Shape.Length != 4)
            return SegmentationResult.Refused("the decoder's masks are not N×K×H×W");
        int k = (int)masks.Dim(1), mh = (int)masks.Dim(2), mw = (int)masks.Dim(3);
        var scores = _scoresOutput is not null ? outputs[_scoresOutput].Values : null;
        var lowRes = _lowResOutput is not null ? outputs[_lowResOutput] : null;
        int w = image.Width, h = image.Height;
        // A mask at the picture's own size is already there; otherwise it covers the model's input frame and
        // the picture is its content rectangle, scaled to the mask's grid.
        var fullSize = mh == h && mw == w;
        var region = fullSize
            ? new RectD(0, 0, w, h)
            : new RectD(map.OffsetX * mw / map.ModelWidth, map.OffsetY * mh / map.ModelHeight,
                w * map.ScaleX * mw / map.ModelWidth, h * map.ScaleY * mh / map.ModelHeight);
        var list = new List<SegmentationMask>(k);
        var plane = new float[w * h];
        for (var m = 0; m < k; m++)
        {
            var src = masks.Values.AsSpan(m * mh * mw, mh * mw);
            if (fullSize)
                src.CopyTo(plane);
            else
                Resampler.Channel(src, mw, mh, region, plane, w, h);
            var coverage = new byte[w * h];
            var t = _profile.MaskThreshold;
            for (var i = 0; i < plane.Length; i++)
            {
                var v = plane[i] - t;
                // Inside exactly where the logit clears the threshold; the logit's size softens the edge.
                coverage[i] = v >= 0 ? (byte)Math.Min(255f, 128f + v * 32f) : (byte)Math.Max(0f, 127f + v * 32f);
            }
            var score = scores is not null && m < scores.Length ? Math.Clamp(scores[m], 0, 1) : 0.5;
            list.Add(new SegmentationMask(w, h, coverage, score, label, LogitsFor(m, src, mw, mh, fullSize, lowRes, map, key)));
        }
        list.Sort((a, b) => b.Score.CompareTo(a.Score));
        return new SegmentationResult([.. list], list.Count == 0 ? "the decoder returned no mask" : "");
    }

    /// <summary>The logits to refine from next time, on the decoder's previous-mask grid over the model frame.</summary>
    private MaskLogits LogitsFor(int m, ReadOnlySpan<float> src, int mw, int mh, bool fullSize, TensorData? lowRes, ImageToModel map, string key)
    {
        var dst = new float[_maskInputWidth * _maskInputHeight];
        if (lowRes is { Shape.Length: 4 } lr && (int)lr.Dim(2) == _maskInputHeight && (int)lr.Dim(3) == _maskInputWidth)
        {
            lr.Values.AsSpan(m * dst.Length, dst.Length).CopyTo(dst);
        }
        else if (!fullSize && mw == _maskInputWidth && mh == _maskInputHeight)
        {
            src.CopyTo(dst);
        }
        else if (!fullSize)
        {
            Resampler.Channel(src, mw, mh, new RectD(0, 0, mw, mh), dst, _maskInputWidth, _maskInputHeight);
        }
        else
        {
            // From the picture's grid back to the model frame: sample the picture for each grid cell's centre;
            // cells in the letterbox padding read as strongly outside.
            for (var y = 0; y < _maskInputHeight; y++)
            {
                for (var x = 0; x < _maskInputWidth; x++)
                {
                    var p = map.ToImage(new Vec2d((x + 0.5) * map.ModelWidth / _maskInputWidth, (y + 0.5) * map.ModelHeight / _maskInputHeight));
                    int px = (int)Math.Floor(p.X), py = (int)Math.Floor(p.Y);
                    dst[y * _maskInputWidth + x] = px >= 0 && py >= 0 && px < mw && py < mh ? src[py * mw + px] : -20f;
                }
            }
        }
        return new MaskLogits(_maskInputWidth, _maskInputHeight, dst, key);
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
        _encodeGate.Dispose();
    }
}
