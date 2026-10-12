using System.Collections.Immutable;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Vision;

/// <summary>An 8-bit RGB raster: rows top to bottom, R G B per pixel, <see cref="Stride"/> bytes from one row's
/// start to the next (≥ 3 × <see cref="Width"/>; padding is allowed and never read). Pixel (x, y) covers the
/// continuous square [x, x+1) × [y, y+1), the convention of <see cref="CameraModel"/>.</summary>
public sealed class ImageRgb8
{
    public ImageRgb8(int width, int height, int stride, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * 3);
        if (data.Length < (long)stride * (height - 1) + width * 3)
            throw new ArgumentException("The data is shorter than the raster.", nameof(data));
        Width = width;
        Height = height;
        Stride = stride;
        Data = data;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte[] Data { get; }

    /// <summary>A black raster with no row padding.</summary>
    public static ImageRgb8 Create(int width, int height) => new(width, height, width * 3, new byte[width * height * 3]);

    public ReadOnlySpan<byte> Row(int y) => Data.AsSpan(y * Stride, Width * 3);

    public Span<byte> MutableRow(int y) => Data.AsSpan(y * Stride, Width * 3);

    /// <summary>Luma by BT.601 weights in fixed point ((77 R + 150 G + 29 B + 128) ≫ 8).</summary>
    public ImageGray8 ToGray()
    {
        var gray = ImageGray8.Create(Width, Height);
        for (var y = 0; y < Height; y++)
        {
            var src = Row(y);
            var dst = gray.MutableRow(y);
            for (var x = 0; x < Width; x++)
                dst[x] = (byte)((77 * src[x * 3] + 150 * src[x * 3 + 1] + 29 * src[x * 3 + 2] + 128) >> 8);
        }
        return gray;
    }
}

/// <summary>An 8-bit single-channel raster (the drift watch's and the corner finder's input); same layout rules
/// as <see cref="ImageRgb8"/> with one byte per pixel.</summary>
public sealed class ImageGray8
{
    public ImageGray8(int width, int height, int stride, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);
        if (data.Length < (long)stride * (height - 1) + width)
            throw new ArgumentException("The data is shorter than the raster.", nameof(data));
        Width = width;
        Height = height;
        Stride = stride;
        Data = data;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte[] Data { get; }

    public static ImageGray8 Create(int width, int height) => new(width, height, width, new byte[width * height]);

    public ReadOnlySpan<byte> Row(int y) => Data.AsSpan(y * Stride, Width);

    public Span<byte> MutableRow(int y) => Data.AsSpan(y * Stride, Width);
}

/// <summary>What a vision step looks at: the picture and, when a scan made it, the 3D point under every pixel —
/// an organised cloud on the picture's own grid (NaN where nothing was decoded), with normals when known. The
/// camera image alone cannot separate two surfaces of one colour; the points can.</summary>
public sealed class VisionFrame
{
    public VisionFrame(ImageRgb8 image, PointCloud? points = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (points is not null && (points.GridWidth != image.Width || points.GridHeight != image.Height))
            throw new ArgumentException("The points must be organised on the picture's grid.", nameof(points));
        Image = image;
        Points = points;
    }

    public ImageRgb8 Image { get; }
    public PointCloud? Points { get; }
    public bool HasGeometry => Points is not null;

    public static implicit operator VisionFrame(ImageRgb8 image) => new(image);
}

/// <summary>A model's own low-resolution mask logits over its input frame, kept with a mask so the next click can
/// refine it (SAM-class decoders read them back as the previous mask). <see cref="SourceKey"/> names the model
/// and the picture they came from; a producer ignores logits that are not its own.</summary>
public sealed record MaskLogits(int Width, int Height, float[] Values, string SourceKey);

/// <summary>A mask over a picture's grid: coverage 0 (outside) … 255 (inside), soft values on an anti-aliased
/// edge; inside means coverage ≥ 128. <see cref="Score"/> is the producer's confidence in 0..1.</summary>
public sealed class SegmentationMask
{
    public SegmentationMask(int width, int height, byte[] coverage, double score, string label, MaskLogits? logits = null)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (coverage.Length != width * height)
            throw new ArgumentException("One coverage byte per pixel.", nameof(coverage));
        Width = width;
        Height = height;
        Coverage = coverage;
        Score = score;
        Label = label;
        Logits = logits;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Coverage { get; }
    public double Score { get; }
    public string Label { get; }
    public MaskLogits? Logits { get; }

    public bool Contains(int x, int y) => Coverage[y * Width + x] >= 128;

    /// <summary>The number of inside pixels.</summary>
    public int Area()
    {
        var n = 0;
        foreach (var c in Coverage)
            if (c >= 128)
                n++;
        return n;
    }

    /// <summary>The bounding rectangle of the inside pixels in pixel units; empty when nothing is inside.</summary>
    public RectD Bounds()
    {
        int minX = Width, minY = Height, maxX = -1, maxY = -1;
        for (var y = 0; y < Height; y++)
        {
            var row = y * Width;
            for (var x = 0; x < Width; x++)
            {
                if (Coverage[row + x] < 128)
                    continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return maxX < 0 ? default : new RectD(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    public SegmentationMask WithCoverage(byte[] coverage, double score) => new(Width, Height, coverage, score, Label, null);

    public SegmentationMask WithLabel(string label) => new(Width, Height, Coverage, Score, label, Logits);
}

/// <summary>What the operator asked for: clicks on what they want (positive) and on what they do not (negative),
/// a box around it, a phrase naming it ("windows", "the left column"), and the previous answer to refine.
/// Coordinates are pixels of the picture (continuous, (0, 0) the top-left corner).</summary>
public sealed record SegmentationPrompt(
    ImmutableArray<Vec2d> Positive,
    ImmutableArray<Vec2d> Negative,
    RectD? Box,
    string? Text,
    SegmentationMask? Previous)
{
    public static readonly SegmentationPrompt Empty = new([], [], null, null, null);

    public static SegmentationPrompt Click(double x, double y) => Empty with { Positive = [new Vec2d(x, y)] };

    public static SegmentationPrompt ForBox(RectD box) => Empty with { Box = box };

    public static SegmentationPrompt ForText(string text) => Empty with { Text = text };

    public ImmutableArray<Vec2d> PositivePoints => Positive.IsDefault ? [] : Positive;
    public ImmutableArray<Vec2d> NegativePoints => Negative.IsDefault ? [] : Negative;
    public bool HasPoints => !PositivePoints.IsEmpty || !NegativePoints.IsEmpty;
    public bool HasBox => Box is { IsEmpty: false };
    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    /// <summary>One more click, refining the answer that came back from the last one.</summary>
    public SegmentationPrompt WithClick(Vec2d point, bool positive, SegmentationMask? previous) => positive
        ? this with { Positive = PositivePoints.Add(point), Previous = previous }
        : this with { Negative = NegativePoints.Add(point), Previous = previous };
}

/// <summary>Masks best first, and the words that say why there are none when there are none.</summary>
public sealed record SegmentationResult(ImmutableArray<SegmentationMask> Masks, string Words)
{
    public SegmentationMask? Best => Masks.IsDefaultOrEmpty ? null : Masks[0];

    public static SegmentationResult Refused(string words) => new([], words);
}

/// <summary>What a segmenter can take.</summary>
[Flags]
public enum SegmenterCapabilities
{
    None = 0,
    Points = 1,
    Box = 2,
    Text = 4,
    /// <summary>Reads the previous answer's logits so a click refines rather than restarts.</summary>
    Refinement = 8,
}

/// <summary>Click-, box- or phrase-to-mask. The classical one is always present; a model-backed one is added when
/// its files are installed. Runs off the render thread; a cancelled call throws
/// <see cref="OperationCanceledException"/>, a prompt it cannot take comes back refused with the reason.</summary>
public interface ISegmenter
{
    string Id { get; }
    SegmenterCapabilities Capabilities { get; }
    Task<SegmentationResult> SegmentAsync(VisionFrame frame, SegmentationPrompt prompt, CancellationToken cancellationToken);
}

/// <summary>What a depth value means.</summary>
public enum DepthScaleKind
{
    /// <summary>Larger is nearer, up to an unknown scale and shift (disparity-like; most monocular models).</summary>
    RelativeInverse,
    /// <summary>Larger is farther, up to an unknown scale.</summary>
    Relative,
    /// <summary>Metres along the optical axis.</summary>
    Metric,
}

/// <summary>A depth map on a picture's grid (row-major, NaN where unknown).</summary>
public sealed class DepthEstimate
{
    public DepthEstimate(int width, int height, float[] values, DepthScaleKind kind)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != width * height)
            throw new ArgumentException("One value per pixel.", nameof(values));
        Width = width;
        Height = height;
        Values = values;
        Kind = kind;
    }

    public int Width { get; }
    public int Height { get; }
    public float[] Values { get; }
    public DepthScaleKind Kind { get; }
}

/// <summary>Depth from one photograph (for previs; never for calibration — structured light measures).</summary>
public interface IDepthEstimator
{
    string Id { get; }
    Task<DepthEstimate> EstimateAsync(ImageRgb8 image, CancellationToken cancellationToken);
}

/// <summary>A point worth pinning: a sub-pixel location and how distinct it is (larger is stronger).</summary>
public readonly record struct FeaturePoint(Vec2d Pixel, double Strength);

/// <summary>Finds distinct, well-spread points in a picture: corners classically, learned keypoints when a model
/// is installed. Strongest first.</summary>
public interface IFeatureSuggester
{
    string Id { get; }
    Task<IReadOnlyList<FeaturePoint>> SuggestAsync(ImageGray8 image, int maxCount, CancellationToken cancellationToken);
}

public enum VisionTask
{
    /// <summary>Clicks and boxes to masks (SAM-class encoder + prompt decoder).</summary>
    PromptSegmentation,
    /// <summary>A phrase to every matching instance.</summary>
    TextSegmentation,
    /// <summary>Monocular depth.</summary>
    Depth,
}

public enum LicenceKind
{
    /// <summary>Apache-2.0, MIT and the like: commercial shows are fine.</summary>
    Permissive,
    /// <summary>CC BY-NC and the like: no paid shows. Never a default.</summary>
    NonCommercial,
    /// <summary>The publisher's own terms, read and accepted on their own.</summary>
    Custom,
}

/// <summary>A model's licence as the operator is shown it before anything is downloaded.</summary>
public sealed record VisionLicence(string Id, string Name, string Url, LicenceKind Kind, string Summary)
{
    public bool AllowsCommercialUse => Kind != LicenceKind.NonCommercial;
}

/// <summary>One file of a model. <see cref="Sha256"/> is lower-case hex, or null when the catalogue has not
/// verified it — a file without a checksum is never installed.</summary>
public sealed record VisionModelFile(string Role, string FileName, string Url, string? Sha256, long SizeBytes);

/// <summary>A model the node can install: what it does, its licence, its files and the input it expects.</summary>
public sealed record VisionModelInfo(
    string Id,
    string Name,
    VisionTask Task,
    VisionLicence Licence,
    ImmutableArray<VisionModelFile> Files,
    int InputWidth,
    int InputHeight,
    /// <summary>The publisher requires its own agreement (a gated download) beyond reading the licence here.</summary>
    bool RequiresSeparateAgreement,
    string Notes)
{
    /// <summary>Every file carries a checksum the catalogue has checked against the publisher.</summary>
    public bool IsVerified => !Files.IsDefaultOrEmpty && Files.All(f => f.Sha256 is { Length: 64 });

    public long TotalBytes => Files.IsDefaultOrEmpty ? 0 : Files.Sum(f => f.SizeBytes);

    public VisionModelFile? File(string role) => Files.IsDefaultOrEmpty ? null : Files.FirstOrDefault(f => f.Role == role);
}
